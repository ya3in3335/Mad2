using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;

namespace Veltrix.Desktop.Core.Diagnostics;

/// <summary>Minimal rolling file log. Every line is redacted before it is written.</summary>
public sealed class AppLog(string directory, TimeProvider time)
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private readonly object _gate = new();

    public string Directory { get; } = directory;
    public string CurrentFile => Path.Combine(Directory, "veltrix.log");

    public void Info(string message) => Write("INF", message, null);
    public void Warn(string message, Exception? e = null) => Write("WRN", message, e);
    public void Error(string message, Exception? e = null) => Write("ERR", message, e);

    private void Write(string level, string message, Exception? e)
    {
        var line = $"{time.GetUtcNow():O} {level} {Redactor.Redact(message)}{(e is null ? "" : " | " + Redactor.Redact(e.GetType().Name + ": " + e.Message))}";
        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var fi = new FileInfo(CurrentFile);
                if (fi.Exists && fi.Length > MaxBytes) File.Move(CurrentFile, Path.Combine(Directory, "veltrix.1.log"), overwrite: true);
                File.AppendAllText(CurrentFile, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

public sealed record DiagnosticsInfo(string AppVersion, string OsVersion, string Runtime, string Language, string Theme, string? ServerUrl,
    Guid InstallationId, Guid? DeviceId, string LicenseState, int? LicenseDaysRemaining, DateTimeOffset? OfflineUntil, bool Online);

/// <summary>Builds a support bundle (zip) containing only redacted text: no tokens, keys, certificates or customer data.</summary>
public static class DiagnosticsBundle
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task WriteAsync(Stream output, DiagnosticsInfo info, string? logDirectory, LocalDb? db, CancellationToken ct)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        await AddAsync(zip, "system.json", Redactor.Redact(JsonSerializer.Serialize(info with { ServerUrl = MaskHost(info.ServerUrl) }, Json)), ct);

        if (db is not null)
        {
            var outbox = await db.Outbox.GroupBy(e => new { e.Type, e.Status }).Select(g => new { g.Key.Type, g.Key.Status, Count = g.Count() }).ToListAsync(ct);
            var failures = await db.Outbox.Where(e => e.LastError != null).OrderByDescending(e => e.Seq).Take(50)
                .Select(e => new { e.Seq, e.Type, e.Status, e.Attempts, e.LastError, e.ClientTime }).ToListAsync(ct);
            var lastSync = await db.GetValueAsync(LocalDb.Keys.LastSyncAt, ct);
            var counts = new { products = await db.Products.CountAsync(ct), customers = await db.Customers.CountAsync(ct), sales = await db.Sales.CountAsync(ct) };
            await AddAsync(zip, "sync.json", Redactor.Redact(JsonSerializer.Serialize(new { lastSync, counts, outbox, failures }, Json)), ct);
        }

        if (logDirectory is not null && System.IO.Directory.Exists(logDirectory))
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(logDirectory, "*.log"))
            {
                string text;
                try
                {
                    await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs);
                    text = await reader.ReadToEndAsync(ct);
                }
                catch (IOException) { continue; }
                await AddAsync(zip, "logs/" + Path.GetFileName(file), Redactor.Redact(text), ct);
            }
        }
    }

    public static string FileName(DateTimeOffset now) => "veltrix-diagnostics-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".zip";

    private static string? MaskHost(string? url) =>
        url is null ? null : Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Host}{(u.IsDefaultPort ? "" : ":" + u.Port)}" : "[invalid]";

    private static async Task AddAsync(ZipArchive zip, string name, string content, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var s = entry.Open();
        await s.WriteAsync(Encoding.UTF8.GetBytes(content), ct);
    }
}
