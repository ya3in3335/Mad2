using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Veltrix.Desktop.Core.Security;

namespace Veltrix.Desktop.Core.Cameras;

public enum CameraBrand { Hikvision, Dahua, Generic }

public enum CameraStream { Main, Sub }

public static class CameraRoles
{
    public const string General = "general";
    public const string Cashier = "cashier";
}

/// <summary>A camera with decrypted credentials (in memory only). IP cameras use channel 1; DVR/NVR inputs use their channel number.</summary>
public sealed record CameraConfig
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public CameraBrand Brand { get; init; } = CameraBrand.Hikvision;
    public string Host { get; init; } = "";
    public int Port { get; init; } = 554;
    /// <summary>Web port, used for the JPEG snapshot endpoint (Hikvision ISAPI / Dahua CGI).</summary>
    public int HttpPort { get; init; } = 80;
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public int Channel { get; init; } = 1;
    public CameraStream Stream { get; init; } = CameraStream.Sub;
    public string Role { get; init; } = CameraRoles.General;
    /// <summary>Generic brand only: full rtsp:// (or http:// MJPEG) URL; credentials are inserted when not already present.</summary>
    public string? CustomUrl { get; init; }
    public bool Enabled { get; init; } = true;
    public int SortOrder { get; init; }

    public bool IsCashier => Role == CameraRoles.Cashier;

    public static int DefaultPort(CameraBrand brand) => 554;

    public static CameraBrand ParseBrand(string? text) => Enum.TryParse<CameraBrand>(text, true, out var b) ? b : CameraBrand.Generic;
    public static CameraStream ParseStream(string? text) => string.Equals(text, "main", StringComparison.OrdinalIgnoreCase) ? CameraStream.Main : CameraStream.Sub;
    public static string StreamText(CameraStream s) => s == CameraStream.Main ? "main" : "sub";
}

/// <summary>RTSP and snapshot URL presets for Hikvision and Dahua (IP cameras and DVR/NVR channels) plus generic custom URLs.</summary>
public static class RtspUrlBuilder
{
    /// <summary>
    /// Hikvision: <c>rtsp://u:p@host:554/Streaming/Channels/101</c> (channel 1 main; 102 = sub; channel 12 main = 1201).
    /// Dahua: <c>rtsp://u:p@host:554/cam/realmonitor?channel=1&amp;subtype=0</c> (0 = main, 1 = sub).
    /// Credentials are percent-encoded; with <paramref name="includeCredentials"/> false they are left out (the player receives them as options instead).
    /// </summary>
    public static string Build(CameraConfig cam, CameraStream stream, bool includeCredentials = true)
    {
        if (cam.Brand == CameraBrand.Generic) return BuildGeneric(cam, includeCredentials);
        var channel = Math.Max(1, cam.Channel);
        var auth = includeCredentials ? UserInfo(cam.Username, cam.Password) : "";
        var host = HostPart(cam.Host);
        var port = cam.Port is > 0 and < 65536 ? cam.Port : 554;
        return cam.Brand switch
        {
            CameraBrand.Hikvision => string.Create(CultureInfo.InvariantCulture,
                $"rtsp://{auth}{host}:{port}/Streaming/Channels/{channel}0{(stream == CameraStream.Main ? 1 : 2)}"),
            _ => string.Create(CultureInfo.InvariantCulture,
                $"rtsp://{auth}{host}:{port}/cam/realmonitor?channel={channel}&subtype={(stream == CameraStream.Main ? 0 : 1)}"),
        };
    }

    /// <summary>HTTP JPEG snapshot endpoint (digest/basic auth, no credentials in the URL), or null for generic cameras.</summary>
    public static string? SnapshotUrl(CameraConfig cam)
    {
        var channel = Math.Max(1, cam.Channel);
        var port = cam.HttpPort is > 0 and < 65536 ? cam.HttpPort : 80;
        var host = HostPart(cam.Host);
        return cam.Brand switch
        {
            CameraBrand.Hikvision => string.Create(CultureInfo.InvariantCulture, $"http://{host}:{port}/ISAPI/Streaming/channels/{channel}01/picture"),
            CameraBrand.Dahua => string.Create(CultureInfo.InvariantCulture, $"http://{host}:{port}/cgi-bin/snapshot.cgi?channel={channel}"),
            _ => null,
        };
    }

    /// <summary>The URL with any user-info replaced by <c>***</c>, for logs and on-screen previews.</summary>
    public static string Redact(string url)
    {
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return url;
        var start = scheme + 3;
        var end = url.IndexOfAny(['/', '?', '#'], start);
        var authority = end < 0 ? url[start..] : url[start..end];
        var at = authority.LastIndexOf('@');
        return at < 0 ? url : url[..start] + "***@" + url[(start + at + 1)..];
    }

    /// <summary>Host (and port) of an absolute URL, for LAN validation and connection tests.</summary>
    public static bool TryGetEndpoint(string url, out string host, out int port)
    {
        host = "";
        port = 0;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return false;
        host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host.Trim('[', ']') : uri.Host;
        port = uri.IsDefaultPort || uri.Port <= 0 ? (uri.Scheme == "rtsp" ? 554 : uri.Scheme == "https" ? 443 : 80) : uri.Port;
        return true;
    }

    private static string BuildGeneric(CameraConfig cam, bool includeCredentials)
    {
        var url = (cam.CustomUrl ?? "").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        var hasUserInfo = !string.IsNullOrEmpty(uri.UserInfo);
        if (!includeCredentials) return hasUserInfo ? StripUserInfo(url) : url;
        if (hasUserInfo || cam.Username.Length == 0) return url;
        var start = url.IndexOf("://", StringComparison.Ordinal) + 3;
        return url[..start] + UserInfo(cam.Username, cam.Password) + url[start..];
    }

    private static string StripUserInfo(string url)
    {
        var start = url.IndexOf("://", StringComparison.Ordinal) + 3;
        var end = url.IndexOfAny(['/', '?', '#'], start);
        var authority = end < 0 ? url[start..] : url[start..end];
        var at = authority.LastIndexOf('@');
        return at < 0 ? url : url[..start] + url[(start + at + 1)..];
    }

    private static string UserInfo(string user, string pass)
    {
        if (string.IsNullOrEmpty(user)) return "";
        return Uri.EscapeDataString(user) + (string.IsNullOrEmpty(pass) ? "" : ":" + Uri.EscapeDataString(pass)) + "@";
    }

    private static string HostPart(string host)
    {
        host = host.Trim();
        return IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 && !host.StartsWith('[') ? $"[{host}]" : host;
    }
}

/// <summary>
/// LAN-only policy for cameras: hosts must be (or resolve exclusively to) private, link-local or loopback addresses —
/// 10/8, 172.16/12, 192.168/16, 169.254/16, fc00::/7, fe80::/10. Public addresses are refused so the feature never reaches the internet.
/// </summary>
public static class LanAddress
{
    public static bool IsLocal(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80);
        return false;
    }

    /// <summary>Syntactic check without DNS: an IP literal must be local; a name is accepted for now (resolution is checked before connecting).</summary>
    public static bool IsAcceptableHostText(string? host)
    {
        host = host?.Trim().Trim('[', ']') ?? "";
        if (host.Length == 0 || host.Length > 253 || host.Any(c => char.IsWhiteSpace(c) || c is '/' or '@' or '?' or '#')) return false;
        return !IPAddress.TryParse(host, out var ip) || IsLocal(ip);
    }

    /// <summary>
    /// Resolves <paramref name="host"/> and returns a local address to connect to, or null when the host is unknown or any resolved address is public
    /// (a name that resolves to a public address is refused outright, so DNS cannot redirect the feature to the internet).
    /// </summary>
    public static async Task<IPAddress?> ResolveLocalAsync(string host, CancellationToken ct, Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null)
    {
        host = host.Trim().Trim('[', ']');
        if (!IsAcceptableHostText(host)) return null;
        if (IPAddress.TryParse(host, out var literal)) return IsLocal(literal) ? literal : null;
        IPAddress[] addresses;
        try { addresses = await (resolver ?? Dns.GetHostAddressesAsync)(host, ct); }
        catch (SocketException) { return null; }
        if (addresses.Length == 0 || addresses.Any(a => !IsLocal(a))) return null;
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
    }
}

/// <summary>Encrypts camera credentials for the database with the app's <see cref="IDataProtector"/> (DPAPI, current user, on Windows).</summary>
public sealed class CredentialProtector(IDataProtector protector)
{
    private const string Prefix = "dp1:";

    public string? Protect(string? plaintext) =>
        string.IsNullOrEmpty(plaintext) ? null : Prefix + Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(plaintext)));

    /// <summary>Empty when absent or not decryptable (e.g. database copied from another Windows account).</summary>
    public string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal)) return "";
        try { return Encoding.UTF8.GetString(protector.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]))); }
        catch (Exception e) when (e is FormatException or System.Security.Cryptography.CryptographicException) { return ""; }
    }
}
