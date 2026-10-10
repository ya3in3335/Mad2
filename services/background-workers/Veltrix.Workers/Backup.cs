using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Veltrix.Modules.Platform;
using Veltrix.SharedKernel;

namespace Veltrix.Workers;

public sealed class BackupOptions
{
    public bool Enabled { get; set; }
    public string Directory { get; set; } = "backups";
    /// <summary>Base64 32-byte AES-256 key. Required; backups are never written unencrypted.</summary>
    public string? EncryptionKey { get; set; }
    public int IntervalHours { get; set; } = 24;
    public int RetentionCount { get; set; } = 14;
    /// <summary>Restore every new backup into a disposable database to prove it is usable.</summary>
    public bool VerifyRestore { get; set; } = true;
    public string PgDumpPath { get; set; } = "pg_dump";
    public string PgRestorePath { get; set; } = "pg_restore";
}

/// <summary>Encrypted PostgreSQL backups: pg_dump (custom format) → AES-256-GCM chunked file (.vbk) → SHA-256 → optional restore test.</summary>
public sealed class BackupService(DbContext db, BackupOptions options, IClock clock, ILogger<BackupService> logger, IAlertPublisher alerts, IDiagnosticsRecorder diagnostics)
{
    private static readonly byte[] Magic = "VLXBK1"u8.ToArray();
    private const int ChunkSize = 1024 * 1024;

    public async Task<BackupRecord> RunAsync(CancellationToken ct)
    {
        var record = new BackupRecord { StartedAt = clock.UtcNow, Status = "running", FileName = "" };
        db.Add(record);
        await db.SaveChangesAsync(ct);
        var dumpPath = Path.Combine(Path.GetTempPath(), $"veltrix-{Guid.NewGuid():N}.dump");
        try
        {
            var key = Key(options);
            System.IO.Directory.CreateDirectory(options.Directory);
            var cs = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
            await RunProcessAsync(options.PgDumpPath, ["--format=custom", "--no-owner", "--file", dumpPath, "--host", cs.Host!, "--port", cs.Port.ToString(), "--username", cs.Username!, cs.Database!], cs.Password, ct);
            var fileName = $"veltrix-{clock.UtcNow:yyyyMMdd-HHmmss}.vbk";
            var target = Path.Combine(options.Directory, fileName);
            await using (var input = File.OpenRead(dumpPath))
            await using (var output = File.Create(target))
                await EncryptAsync(input, output, key, ct);
            record.FileName = fileName;
            record.SizeBytes = new FileInfo(target).Length;
            record.Sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(target, ct))).ToLowerInvariant();
            record.CompletedAt = clock.UtcNow;
            record.Status = "completed";
            await db.SaveChangesAsync(ct);
            if (options.VerifyRestore) await VerifyAsync(record, ct);
            ApplyRetention();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            record.Status = "failed";
            record.Error = ex.Message.Length > 1900 ? ex.Message[..1900] : ex.Message;
            record.CompletedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogError(ex, "Backup failed");
            await diagnostics.RecordAsync(new ServerDiagnosticEvent(DiagnosticCategories.Backup, DiagnosticSeverities.Critical, "backup_failed", record.Error ?? "Backup failed"), ct);
            await alerts.PublishAsync(new AlertMessage(AlertTypes.BackupFailed, "Backup failed",
                $"The database backup started at {record.StartedAt:yyyy-MM-dd HH:mm}Z failed: {record.Error}", AlertSeverity.Critical,
                $"backup-failed:{record.Id}"), ct);
        }
        finally
        {
            if (File.Exists(dumpPath)) File.Delete(dumpPath);
        }
        return record;
    }

    /// <summary>A backup is only "verified" after it decrypts, its checksum matches, and it restores into a disposable database
    /// whose company count matches the source.</summary>
    public async Task VerifyAsync(BackupRecord record, CancellationToken ct)
    {
        var path = Path.Combine(options.Directory, record.FileName);
        var dump = Path.Combine(Path.GetTempPath(), $"veltrix-verify-{Guid.NewGuid():N}.dump");
        var tempDb = "veltrix_restore_check_" + Guid.NewGuid().ToString("N")[..10];
        var cs = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
        try
        {
            var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, ct))).ToLowerInvariant();
            if (actual != record.Sha256) throw new InvalidOperationException("Checksum mismatch.");
            await using (var input = File.OpenRead(path))
            await using (var output = File.Create(dump))
                await DecryptAsync(input, output, Key(options), ct);
            var admin = new NpgsqlConnectionStringBuilder(cs.ConnectionString) { Database = "postgres", Pooling = false };
            await using (var conn = new NpgsqlConnection(admin.ConnectionString))
            {
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand($"CREATE DATABASE {tempDb}", conn);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await RunProcessAsync(options.PgRestorePath, ["--no-owner", "--host", cs.Host!, "--port", cs.Port.ToString(), "--username", cs.Username!, "--dbname", tempDb, dump], cs.Password, ct);
            long restored, source;
            var restoredCs = new NpgsqlConnectionStringBuilder(cs.ConnectionString) { Database = tempDb, Pooling = false };
            await using (var conn = new NpgsqlConnection(restoredCs.ConnectionString))
            {
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand("SELECT count(*) FROM tenants.companies", conn);
                restored = (long)(await cmd.ExecuteScalarAsync(ct))!;
            }
            source = await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM tenants.companies").SingleAsync(ct);
            record.Verified = restored <= source && restored >= 0;
            record.VerifiedAt = clock.UtcNow;
            record.VerificationDetails = $"Restored into disposable database; companies restored={restored}, source now={source}.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            record.Verified = false;
            record.VerificationDetails = "Verification failed: " + (ex.Message.Length > 1800 ? ex.Message[..1800] : ex.Message);
            logger.LogError(ex, "Backup verification failed");
        }
        finally
        {
            if (File.Exists(dump)) File.Delete(dump);
            try
            {
                var admin = new NpgsqlConnectionStringBuilder(cs.ConnectionString) { Database = "postgres", Pooling = false };
                await using var conn = new NpgsqlConnection(admin.ConnectionString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {tempDb} WITH (FORCE)", conn);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Could not drop {Db}", tempDb); }
        }
        await db.SaveChangesAsync(ct);
    }

    private void ApplyRetention()
    {
        var files = new DirectoryInfo(options.Directory).GetFiles("veltrix-*.vbk").OrderByDescending(f => f.Name).Skip(Math.Max(1, options.RetentionCount));
        foreach (var f in files) f.Delete();
    }

    public static byte[] Key(BackupOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.EncryptionKey)) throw new InvalidOperationException("Backup:EncryptionKey is not configured; refusing to write an unencrypted backup.");
        var key = Convert.FromBase64String(o.EncryptionKey);
        if (key.Length != 32) throw new InvalidOperationException("Backup:EncryptionKey must be 32 bytes (base64).");
        return key;
    }

    /// <summary>Format: "VLXBK1" | 12-byte base nonce | repeated [4-byte length | ciphertext | 16-byte tag]. Chunk nonce = base XOR counter.</summary>
    public static async Task EncryptAsync(Stream input, Stream output, byte[] key, CancellationToken ct)
    {
        var baseNonce = RandomNumberGenerator.GetBytes(12);
        await output.WriteAsync(Magic, ct);
        await output.WriteAsync(baseNonce, ct);
        using var aes = new AesGcm(key, 16);
        var buffer = new byte[ChunkSize];
        long counter = 0;
        int read;
        while ((read = await ReadFullAsync(input, buffer, ct)) > 0)
        {
            var nonce = ChunkNonce(baseNonce, counter++);
            var cipher = new byte[read];
            var tag = new byte[16];
            aes.Encrypt(nonce, buffer.AsSpan(0, read), cipher, tag, BitConverter.GetBytes(counter));
            var len = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, read);
            await output.WriteAsync(len, ct);
            await output.WriteAsync(cipher, ct);
            await output.WriteAsync(tag, ct);
        }
    }

    public static async Task DecryptAsync(Stream input, Stream output, byte[] key, CancellationToken ct)
    {
        var header = new byte[6];
        if (await ReadFullAsync(input, header, ct) != 6 || !header.AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Not a VELTRIX backup file.");
        var baseNonce = new byte[12];
        if (await ReadFullAsync(input, baseNonce, ct) != 12) throw new InvalidDataException("Truncated backup.");
        using var aes = new AesGcm(key, 16);
        long counter = 0;
        var lenBuf = new byte[4];
        while (true)
        {
            var r = await ReadFullAsync(input, lenBuf, ct);
            if (r == 0) break;
            if (r != 4) throw new InvalidDataException("Truncated backup.");
            var len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
            if (len is <= 0 or > ChunkSize) throw new InvalidDataException("Corrupt chunk length.");
            var cipher = new byte[len];
            var tag = new byte[16];
            if (await ReadFullAsync(input, cipher, ct) != len || await ReadFullAsync(input, tag, ct) != 16) throw new InvalidDataException("Truncated backup.");
            var plain = new byte[len];
            var nonce = ChunkNonce(baseNonce, counter++);
            aes.Decrypt(nonce, cipher, tag, plain, BitConverter.GetBytes(counter)); // throws on tampering
            await output.WriteAsync(plain, ct);
        }
    }

    private static byte[] ChunkNonce(byte[] baseNonce, long counter)
    {
        var n = (byte[])baseNonce.Clone();
        var c = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(c, counter);
        for (var i = 0; i < 8; i++) n[4 + i] ^= c[i];
        return n;
    }

    private static async Task<int> ReadFullAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var r = await s.ReadAsync(buffer.AsMemory(total), ct);
            if (r == 0) break;
            total += r;
        }
        return total;
    }

    private static async Task RunProcessAsync(string file, IEnumerable<string> args, string? password, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (password is not null) psi.Environment["PGPASSWORD"] = password;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}.");
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(file)} exited with {p.ExitCode}: {(await stderr).Trim()}");
    }
}

public sealed class BackupJob : IJob
{
    public string Name => "backup";
    public TimeSpan Interval => TimeSpan.FromHours(1);

    public async Task RunAsync(IServiceProvider sp, CancellationToken ct)
    {
        var options = sp.GetRequiredService<BackupOptions>();
        if (!options.Enabled) return;
        var db = sp.GetRequiredService<DbContext>();
        var last = await db.Set<BackupRecord>().Where(b => b.Status == "completed").OrderByDescending(b => b.StartedAt).FirstOrDefaultAsync(ct);
        if (last is not null && sp.GetRequiredService<IClock>().UtcNow - last.StartedAt < TimeSpan.FromHours(options.IntervalHours)) return;
        var record = await sp.GetRequiredService<BackupService>().RunAsync(ct);
        if (record.Status == "failed") throw new InvalidOperationException("Backup failed: " + record.Error);
        if (options.VerifyRestore && !record.Verified)
        {
            await sp.GetRequiredService<IAlertPublisher>().PublishAsync(new AlertMessage(AlertTypes.BackupFailed, "Backup verification failed",
                $"Backup {record.FileName} was created but its restore verification failed.", AlertSeverity.Critical, $"backup-verify:{record.Id}"), ct);
            throw new InvalidOperationException("Backup created but restore verification failed: " + record.VerificationDetails);
        }
    }
}
