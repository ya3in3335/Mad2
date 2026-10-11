using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Diagnostics;

namespace Veltrix.Desktop.Core.Cameras;

/// <summary>Grabs one JPEG frame from a camera into a file. Returns false (never throws for camera/network problems) when no image was saved.</summary>
public interface ISnapshotCapturer
{
    Task<bool> CaptureAsync(CameraConfig camera, string destinationPath, CancellationToken ct);
}

/// <summary>Snapshot files under the app data folder: <c>Snapshots/yyyy/MM/{saleId}.jpg</c>, with age-based retention.</summary>
public sealed class SnapshotStore(string appDataRoot, TimeProvider time)
{
    public const string FolderName = "Snapshots";

    public string Root { get; } = Path.Combine(appDataRoot, FolderName);

    /// <summary>Path relative to the app data root (stored in the database), in the shop's local calendar.</summary>
    public string RelativePathFor(Guid saleId, DateTimeOffset at)
    {
        var local = TimeZoneInfo.ConvertTime(at, time.LocalTimeZone);
        return Path.Combine(FolderName, local.ToString("yyyy", CultureInfo.InvariantCulture), local.ToString("MM", CultureInfo.InvariantCulture),
            saleId.ToString("D", CultureInfo.InvariantCulture) + ".jpg");
    }

    public string FullPath(string relativePath) => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Root)!, relativePath));

    /// <summary>Deletes .jpg files older than <paramref name="retentionDays"/> (by file time) and the folders left empty. Returns the number deleted.</summary>
    public int Cleanup(int retentionDays)
    {
        if (!Directory.Exists(Root)) return 0;
        var cutoff = time.GetUtcNow().UtcDateTime - TimeSpan.FromDays(Math.Max(1, retentionDays));
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(Root, "*.jpg", SearchOption.AllDirectories).ToList())
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;
                File.Delete(file);
                deleted++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        foreach (var dir in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList())
        {
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return deleted;
    }
}

/// <summary>
/// Links a cashier-camera photo to each completed POS sale. Capture runs in the background with a timeout: it never blocks
/// nor fails the sale, and any camera problem is only logged.
/// </summary>
public sealed class SaleSnapshotService(Func<LocalDb> db, CameraRepository cameras, SnapshotStore store, ISnapshotCapturer capturer, TimeProvider time,
    Func<Settings.CameraSettings> settings, AppLog? log = null)
{
    public static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(12);

    /// <summary>Starts the capture and returns immediately. The returned task (for tests) completes when the photo is linked or abandoned; it never faults.</summary>
    public Task CaptureForSaleInBackground(Guid saleId)
    {
        if (saleId == Guid.Empty || !settings().SnapshotOnSale) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            try { await CaptureForSaleAsync(saleId); }
            catch (Exception e) { log?.Warn("Sale snapshot failed", e); }
        });
    }

    private async Task CaptureForSaleAsync(Guid saleId)
    {
        using var cts = new CancellationTokenSource(CaptureTimeout);
        var cam = await cameras.CashierCameraAsync(cts.Token);
        if (cam is null) return;
        var now = time.GetUtcNow();
        var relative = store.RelativePathFor(saleId, now);
        var full = store.FullPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        bool ok;
        try { ok = await capturer.CaptureAsync(cam, full, cts.Token); }
        catch (OperationCanceledException) { ok = false; }
        if (!ok || !File.Exists(full)) { log?.Info($"Sale snapshot skipped: camera '{cam.Name}' did not return an image"); return; }
        await using var ctx = db();
        var existing = await ctx.SaleSnapshots.FindAsync([saleId], CancellationToken.None);
        if (existing is null) ctx.SaleSnapshots.Add(new LocalSaleSnapshot { SaleId = saleId, CameraId = cam.Id, RelativePath = relative, TakenAt = now });
        else { existing.RelativePath = relative; existing.TakenAt = now; existing.CameraId = cam.Id; }
        await ctx.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Full path of the sale's photo, or null when none was taken or it was removed by retention.</summary>
    public async Task<string?> FindAsync(Guid saleId, CancellationToken ct = default)
    {
        await using var ctx = db();
        var row = await ctx.SaleSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.SaleId == saleId, ct);
        if (row is null) return null;
        var full = store.FullPath(row.RelativePath);
        return File.Exists(full) ? full : null;
    }

    /// <summary>Retention: deletes old photo files and their links. Returns the number of files deleted.</summary>
    public async Task<int> CleanupAsync(CancellationToken ct = default)
    {
        var days = settings().RetentionDays;
        var deleted = store.Cleanup(days);
        var cutoff = time.GetUtcNow() - TimeSpan.FromDays(Math.Max(1, days));
        await using var ctx = db();
        var stale = (await ctx.SaleSnapshots.ToListAsync(ct)).Where(s => s.TakenAt < cutoff).ToList();
        if (stale.Count > 0)
        {
            ctx.SaleSnapshots.RemoveRange(stale);
            await ctx.SaveChangesAsync(ct);
        }
        return deleted;
    }
}

/// <summary>JPEG from the camera's own HTTP snapshot endpoint (Hikvision ISAPI, Dahua CGI) with digest/basic auth, LAN hosts only.</summary>
public sealed class HttpSnapshotCapturer : ISnapshotCapturer
{
    public async Task<bool> CaptureAsync(CameraConfig camera, string destinationPath, CancellationToken ct)
    {
        var url = RtspUrlBuilder.SnapshotUrl(camera);
        if (url is null || await LanAddress.ResolveLocalAsync(camera.Host, ct) is null) return false;
        var uri = new Uri(url);
        var credentials = new CredentialCache();
        if (camera.Username.Length > 0)
        {
            var nc = new NetworkCredential(camera.Username, camera.Password);
            var root = new Uri(uri.GetLeftPart(UriPartial.Authority));
            credentials.Add(root, "Digest", nc);
            credentials.Add(root, "Basic", nc);
        }
        // LAN only: no proxy (it could forward to the internet), no redirects (they could point outside the LAN).
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, Credentials = credentials, ConnectTimeout = TimeSpan.FromSeconds(4) };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        try
        {
            using var response = await http.GetAsync(uri, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is not ("image/jpeg" or "image/jpg")) return false;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) return false;
            var tmp = destinationPath + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, destinationPath, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException) { return false; }
    }
}

/// <summary>Tries each capturer in turn until one saves an image.</summary>
public sealed class FallbackSnapshotCapturer(params ISnapshotCapturer[] capturers) : ISnapshotCapturer
{
    public async Task<bool> CaptureAsync(CameraConfig camera, string destinationPath, CancellationToken ct)
    {
        foreach (var c in capturers)
        {
            ct.ThrowIfCancellationRequested();
            if (await c.CaptureAsync(camera, destinationPath, ct)) return true;
        }
        return false;
    }
}
