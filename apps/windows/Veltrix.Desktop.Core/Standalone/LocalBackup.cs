using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;

namespace Veltrix.Desktop.Core.Standalone;

/// <summary>Everything a standalone installation needs to continue on another device.</summary>
public sealed record BackupPayload(int Version, DateTimeOffset CreatedAt, string? TenantId, string? TenantName, string UsersJson,
    List<LocalProduct> Products, List<LocalCustomer> Customers, List<LocalStock> Stock, List<LocalRegister> Registers, List<LocalShift> Shifts,
    List<LocalSale> Sales, List<LocalSaleLine> SaleLines, List<KeyValue> Values);

/// <summary>
/// Encrypted local backup. File layout: <c>"VLXBAK01" | iterations (int32 LE) | salt (16) | nonce (12) | tag (16) | ciphertext</c>.
/// The AES-256-GCM key is derived from the owner's password with PBKDF2-SHA256; the header is authenticated as associated data,
/// and the plaintext is gzip-compressed JSON. A wrong password or any tampering fails authentication and nothing is restored.
/// </summary>
public static class LocalBackup
{
    public const int FormatVersion = 1;
    public const string FileExtension = ".vlxbak";
    private static readonly byte[] Magic = "VLXBAK01"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int HeaderLength = 8 + 4 + 16;

    public static byte[] Encrypt(BackupPayload payload, string password, int iterations = PasswordHasher.DefaultIterations)
    {
        using var raw = new MemoryStream();
        using (var gz = new GZipStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(gz, payload, Json);
        var plain = raw.ToArray();

        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var header = new byte[HeaderLength];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), iterations);
        salt.CopyTo(header, 12);
        var key = DeriveKey(password, salt, iterations);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(nonce, plain, cipher, tag, header);
        CryptographicOperations.ZeroMemory(key);
        return [.. header, .. nonce, .. tag, .. cipher];
    }

    /// <summary>Decrypts a backup. Throws <see cref="InvalidDataException"/> for a foreign file and
    /// <see cref="AuthenticationTagMismatchException"/> for a wrong password or a modified file.</summary>
    public static BackupPayload Decrypt(byte[] file, string password)
    {
        if (file.Length < HeaderLength + 28 || !file.AsSpan(0, 8).SequenceEqual(Magic)) throw new InvalidDataException("Not a VELTRIX backup file.");
        var iterations = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(8));
        if (iterations is < PasswordHasher.MinimumIterations or > 10_000_000) throw new InvalidDataException("Unsupported backup parameters.");
        var salt = file.AsSpan(12, 16).ToArray();
        var nonce = file.AsSpan(HeaderLength, 12);
        var tag = file.AsSpan(HeaderLength + 12, 16);
        var cipher = file.AsSpan(HeaderLength + 28);
        var plain = new byte[cipher.Length];
        var key = DeriveKey(password, salt, iterations);
        try
        {
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(nonce, cipher, tag, plain, file.AsSpan(0, HeaderLength));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        using var gz = new GZipStream(new MemoryStream(plain), CompressionMode.Decompress);
        var payload = JsonSerializer.Deserialize<BackupPayload>(gz, Json) ?? throw new InvalidDataException("Empty backup.");
        if (payload.Version > FormatVersion) throw new InvalidDataException("This backup was made by a newer version.");
        return payload;
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>Snapshot of the local database (excluding the sync outbox/cursor, which belong to online mode).</summary>
    public static async Task<BackupPayload> CaptureAsync(LocalDb db, string usersJson, string? tenantId, string? tenantName, DateTimeOffset now, CancellationToken ct = default) =>
        new(FormatVersion, now, tenantId, tenantName, usersJson,
            await db.Products.AsNoTracking().ToListAsync(ct), await db.Customers.AsNoTracking().ToListAsync(ct), await db.Stock.AsNoTracking().ToListAsync(ct),
            await db.Registers.AsNoTracking().ToListAsync(ct), await db.Shifts.AsNoTracking().ToListAsync(ct), await db.Sales.AsNoTracking().ToListAsync(ct),
            await db.SaleLines.AsNoTracking().ToListAsync(ct),
            await db.Values.AsNoTracking().Where(v => v.Key != LocalDb.Keys.SchemaVersion && !v.Key.StartsWith("sync.")).ToListAsync(ct));

    /// <summary>Replaces all local business data with the backup, atomically.</summary>
    public static async Task RestoreAsync(LocalDb db, BackupPayload p, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.SaleLines.RemoveRange(db.SaleLines);
        db.Sales.RemoveRange(db.Sales);
        db.Shifts.RemoveRange(db.Shifts);
        db.Stock.RemoveRange(db.Stock);
        db.Registers.RemoveRange(db.Registers);
        db.Customers.RemoveRange(db.Customers);
        db.Products.RemoveRange(db.Products);
        db.ParkedCarts.RemoveRange(db.ParkedCarts);
        db.Outbox.RemoveRange(db.Outbox);
        db.Values.RemoveRange(db.Values.Where(v => v.Key != LocalDb.Keys.SchemaVersion));
        await db.SaveChangesAsync(ct);
        db.Products.AddRange(p.Products);
        db.Customers.AddRange(p.Customers);
        db.Stock.AddRange(p.Stock);
        db.Registers.AddRange(p.Registers);
        db.Shifts.AddRange(p.Shifts);
        db.Sales.AddRange(p.Sales);
        db.SaleLines.AddRange(p.SaleLines);
        db.Values.AddRange(p.Values.Where(v => v.Key != LocalDb.Keys.SchemaVersion));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
