using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Veltrix.Desktop.Core.Workforce.Terminals;

/// <summary>
/// Vendor-neutral contract for PHYSICAL time-attendance terminals (pointeuses). Punching happens only on the terminal: VELTRIX imports the
/// terminal's event log (identity matched ON the device — fingerprint / face templates never leave it) and ingests the events.
/// </summary>
public interface ITerminalAdapter : IAsyncDisposable
{
    string Manufacturer { get; }
    TerminalCapabilities Capabilities { get; }

    /// <summary>Reachability + authentication with the stored credential. Throws <see cref="TerminalException"/>.</summary>
    Task ValidateConnectionAsync(CancellationToken ct = default);

    /// <summary>Model, serial, firmware and (when supported) the terminal clock.</summary>
    Task<TerminalDeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default);

    /// <summary>
    /// One page of events after <paramref name="cursor"/> (opaque, produced by this adapter; null = from <paramref name="since"/> or the beginning).
    /// Adapters may return events already seen (overlap is deduplicated by ingestion).
    /// </summary>
    Task<TerminalImportPage> ImportEventsAsync(string? cursor, DateTimeOffset? since, int maxEvents, CancellationToken ct = default);

    /// <summary>Real-time events (only when <see cref="TerminalCapabilities.EventStream"/>); otherwise throws Unsupported.</summary>
    IAsyncEnumerable<TerminalEvent> StreamEventsAsync(CancellationToken ct = default);

    Task<TerminalHealth> HealthAsync(CancellationToken ct = default);
}

[Flags]
public enum TerminalCapabilities
{
    None = 0,
    EventImport = 1,
    EventStream = 2,
    DeviceInfo = 4,
    /// <summary>Reports its clock, so the drift against the PC can be measured.</summary>
    ClockRead = 8,
    Fingerprint = 16,
    Face = 32,
    Card = 64,
    Pin = 128,
    /// <summary>Stable per-event id from the vendor (otherwise a deterministic hash is used for idempotency).</summary>
    VendorEventIds = 256,
}

public enum TerminalErrorKind { Unreachable, AuthFailed, Unsupported, ProtocolError }

/// <summary>Normalized adapter error. Vendor specifics stay in <see cref="Exception.Message"/>.</summary>
public sealed class TerminalException(TerminalErrorKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public TerminalErrorKind Kind { get; } = kind;
    public static TerminalException Unsupported(string message) => new(TerminalErrorKind.Unsupported, message);
}

public static class VerifyMethods
{
    public const string Finger = "finger";
    public const string Face = "face";
    public const string Card = "card";
    public const string Pin = "pin";
    public const string Unknown = "unknown";
}

public static class VerifyResults
{
    public const string Success = "success";
    public const string Failed = "failed";
    public const string Unknown = "unknown";
}

public sealed record TerminalDeviceInfo(string Manufacturer, string? Model, string? SerialNumber, string? Firmware, DateTimeOffset? DeviceClock = null);

public sealed record TerminalHealth(bool Reachable, TimeSpan? Latency, string? Message);

/// <summary>
/// A normalized terminal event. <see cref="DeviceLocalTime"/> is the terminal wall clock; <see cref="DeviceOffsetMinutes"/> its UTC offset
/// when the vendor reports one (else the registered terminal offset / workplace zone applies). <see cref="PunchType"/> is a VELTRIX punch
/// type when the terminal reports a direction (check-in / check-out keys), else null (toggle).
/// </summary>
public sealed record TerminalEvent(
    string? VendorEventId,
    string? DeviceUserId,
    string? CardNo,
    DateTime DeviceLocalTime,
    int? DeviceOffsetMinutes,
    string Method,
    string VerifyResult,
    string? PunchType = null,
    IReadOnlyDictionary<string, string>? Raw = null)
{
    /// <summary>Idempotency key: the vendor id when present, else SHA-256(device | user | card | local time | method).</summary>
    public string DedupKey(Guid deviceId) => VendorEventId is { Length: > 0 } v
        ? "v:" + v
        : "h:" + Hash(deviceId.ToString("N"), DeviceUserId ?? "", CardNo ?? "", DeviceLocalTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), Method)[..32];

    /// <summary>Hash of the normalized content (detects a vendor id re-used with different data).</summary>
    public string ContentHash() => Hash(DeviceUserId ?? "", CardNo ?? "", DeviceLocalTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        DeviceOffsetMinutes?.ToString(CultureInfo.InvariantCulture) ?? "", Method, VerifyResult, PunchType ?? "");

    private static string Hash(params string[] parts) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts))));
}

public sealed record TerminalImportPage(IReadOnlyList<TerminalEvent> Events, string? NextCursor, bool HasMore);

/// <summary>Connection settings handed to an adapter. <see cref="Password"/> is decrypted only in memory, just before use.</summary>
public sealed record TerminalConnection(string? Host, int? Port, string? Protocol, string? UserName, string? Password)
{
    public Uri BaseUri(int defaultHttpPort)
    {
        if (string.IsNullOrWhiteSpace(Host)) throw new TerminalException(TerminalErrorKind.Unreachable, "No host configured.");
        var https = string.Equals(Protocol, "https", StringComparison.OrdinalIgnoreCase);
        var port = Port ?? (https ? 443 : defaultHttpPort);
        return new UriBuilder(https ? "https" : "http", Host.Trim(), port).Uri;
    }
}

public static class TerminalManufacturers
{
    public const string Simulated = "Simulated";
    public const string Hikvision = "Hikvision";
    public const string Suprema = "Suprema";
    public const string ZKTeco = "ZKTeco";
    public static readonly IReadOnlyList<string> All = [Simulated, Hikvision, Suprema, ZKTeco];
}

/// <summary>Maps common vendor HTTP / socket failures to <see cref="TerminalErrorKind"/>.</summary>
public static class TerminalErrors
{
    public static TerminalException FromHttpStatus(int status, string context) => status switch
    {
        401 or 403 => new TerminalException(TerminalErrorKind.AuthFailed, $"{context}: authentication refused (HTTP {status})."),
        404 or 405 or 501 => new TerminalException(TerminalErrorKind.Unsupported, $"{context}: endpoint not supported by this terminal (HTTP {status})."),
        _ => new TerminalException(TerminalErrorKind.ProtocolError, $"{context}: unexpected HTTP {status}."),
    };

    public static async Task<T> GuardAsync<T>(Func<Task<T>> call, string context, CancellationToken ct)
    {
        try { return await call(); }
        catch (TerminalException) { throw; }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TerminalException(TerminalErrorKind.Unreachable, $"{context}: timeout", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is { } code) { throw FromHttpStatus((int)code, context); }
        catch (HttpRequestException ex) { throw new TerminalException(TerminalErrorKind.Unreachable, $"{context}: {ex.Message}", ex); }
        catch (System.Net.Sockets.SocketException ex) { throw new TerminalException(TerminalErrorKind.Unreachable, $"{context}: {ex.Message}", ex); }
        catch (TimeoutException ex) { throw new TerminalException(TerminalErrorKind.Unreachable, $"{context}: timeout", ex); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or System.Xml.XmlException or FormatException or InvalidDataException)
        {
            throw new TerminalException(TerminalErrorKind.ProtocolError, $"{context}: malformed response ({ex.Message})", ex);
        }
    }
}
