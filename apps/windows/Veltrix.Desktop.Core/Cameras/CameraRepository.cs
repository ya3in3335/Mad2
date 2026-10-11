using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;

namespace Veltrix.Desktop.Core.Cameras;

/// <summary>Camera list in the local database; credentials are encrypted on write and decrypted on read.</summary>
public sealed class CameraRepository(Func<LocalDb> db, CredentialProtector protector, TimeProvider time)
{
    public async Task<IReadOnlyList<CameraConfig>> ListAsync(CancellationToken ct = default)
    {
        await using var ctx = db();
        var rows = await ctx.Cameras.AsNoTracking().ToListAsync(ct);
        return rows.OrderBy(r => r.SortOrder).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).Select(ToConfig).ToList();
    }

    /// <summary>The enabled camera with the cashier role (first by order), if any.</summary>
    public async Task<CameraConfig?> CashierCameraAsync(CancellationToken ct = default) =>
        (await ListAsync(ct)).FirstOrDefault(c => c.Enabled && c.IsCashier);

    /// <summary>Validates and saves (insert when <see cref="CameraConfig.Id"/> is empty). Returns the saved camera.</summary>
    public async Task<CameraConfig> SaveAsync(CameraConfig cam, CancellationToken ct = default)
    {
        Validate(cam);
        await using var ctx = db();
        var row = cam.Id == Guid.Empty ? null : await ctx.Cameras.FindAsync([cam.Id], ct);
        if (row is null)
        {
            row = new LocalCamera
            {
                Id = cam.Id == Guid.Empty ? Guid.NewGuid() : cam.Id, CreatedAt = time.GetUtcNow(),
                SortOrder = cam.SortOrder != 0 ? cam.SortOrder : (await ctx.Cameras.Select(c => (int?)c.SortOrder).MaxAsync(ct) ?? 0) + 1,
            };
            ctx.Cameras.Add(row);
        }
        row.Name = cam.Name.Trim();
        row.Brand = cam.Brand.ToString();
        row.Host = cam.Brand == CameraBrand.Generic && RtspUrlBuilder.TryGetEndpoint(cam.CustomUrl ?? "", out var h, out _) ? h : cam.Host.Trim();
        row.Port = cam.Port;
        row.HttpPort = cam.HttpPort;
        row.UsernameProtected = protector.Protect(cam.Username);
        row.PasswordProtected = protector.Protect(cam.Password);
        row.CustomUrlProtected = cam.Brand == CameraBrand.Generic ? protector.Protect(cam.CustomUrl?.Trim()) : null;
        row.Channel = cam.Channel;
        row.Stream = CameraConfig.StreamText(cam.Stream);
        row.Role = cam.Role == CameraRoles.Cashier ? CameraRoles.Cashier : CameraRoles.General;
        row.Enabled = cam.Enabled;
        await ctx.SaveChangesAsync(ct);
        return ToConfig(row);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = db();
        if (await ctx.Cameras.FindAsync([id], ct) is { } row)
        {
            ctx.Cameras.Remove(row);
            await ctx.SaveChangesAsync(ct);
        }
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> with a localization key (cam.err.*) when the camera is not valid.</summary>
    public static void Validate(CameraConfig cam)
    {
        if (string.IsNullOrWhiteSpace(cam.Name)) throw new InvalidOperationException("cam.err.name");
        if (cam.Brand == CameraBrand.Generic)
        {
            if (!RtspUrlBuilder.TryGetEndpoint(cam.CustomUrl ?? "", out var host, out _) ||
                !(cam.CustomUrl!.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) || cam.CustomUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("cam.err.url");
            if (!LanAddress.IsAcceptableHostText(host)) throw new InvalidOperationException("cam.err.notLan");
            return;
        }
        if (string.IsNullOrWhiteSpace(cam.Host)) throw new InvalidOperationException("cam.err.host");
        if (!LanAddress.IsAcceptableHostText(cam.Host)) throw new InvalidOperationException("cam.err.notLan");
        if (cam.Port is < 1 or > 65535 || cam.HttpPort is < 1 or > 65535) throw new InvalidOperationException("cam.err.port");
        if (cam.Channel is < 1 or > 256) throw new InvalidOperationException("cam.err.channel");
    }

    private CameraConfig ToConfig(LocalCamera r) => new()
    {
        Id = r.Id, Name = r.Name, Brand = CameraConfig.ParseBrand(r.Brand), Host = r.Host, Port = r.Port, HttpPort = r.HttpPort,
        Username = protector.Unprotect(r.UsernameProtected), Password = protector.Unprotect(r.PasswordProtected),
        CustomUrl = r.CustomUrlProtected is null ? null : protector.Unprotect(r.CustomUrlProtected),
        Channel = r.Channel, Stream = CameraConfig.ParseStream(r.Stream), Role = r.Role, Enabled = r.Enabled, SortOrder = r.SortOrder,
    };
}
