using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Veltrix.Desktop.Core.Data;

namespace Veltrix.Desktop.Core.Workforce.Devices;

[Flags]
public enum DeviceCapabilities
{
    None = 0,
    Card = 1,
    Qr = 2,
    Pin = 4,
    /// <summary>Fingerprint matched ON the device; only the terminal user id reaches VELTRIX (no template).</summary>
    Fingerprint = 8,
    /// <summary>Face matched ON the device; only the terminal user id reaches VELTRIX (no template).</summary>
    Face = 16,
    DoorRelay = 32,
    /// <summary>Keeps events while offline and delivers them on reconnection.</summary>
    StoresOfflineLogs = 64,
}

public enum DeviceConnectionState { Disconnected, Connecting, Connected, Faulted }

/// <summary>
/// A credential presented at a device. <see cref="CredentialKind"/> is a <see cref="CredentialKinds"/> value: Card (UID), Qr (raw token),
/// Pin ("EMPLOYEENO:PIN"), TerminalUser (user id after on-device biometric / PIN match).
/// </summary>
public sealed record DeviceScan(Guid DeviceId, string DeviceEventId, string CredentialKind, string CredentialValue, string? PunchType, DateTimeOffset OccurredAtUtc);

public sealed record DeviceTestResult(bool Ok, string Message);

/// <summary>An attendance / identification device. Implementations raise <see cref="Scanned"/>; VELTRIX persists every scan before processing it.</summary>
public interface IAttendanceDevice : IAsyncDisposable
{
    Guid DeviceId { get; }
    DeviceCapabilities Capabilities { get; }
    DeviceConnectionState State { get; }
    event EventHandler<DeviceScan>? Scanned;
    event EventHandler<DeviceConnectionState>? StateChanged;
    event EventHandler<Exception>? Faulted;
    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();
    Task<DeviceTestResult> TestConnectionAsync(CancellationToken ct = default);
}

/// <summary>
/// Door relay abstraction. SAFETY: the software only ever sends UNLOCK (timed pulse or emergency unlock-all) and the normal RELOCK at the end of a
/// pulse; there is deliberately NO "lock during alarm" command. Free egress must be guaranteed by fail-safe hardware wiring (fail-safe locks,
/// request-to-exit buttons, break-glass and fire-alarm interface cutting lock power) independent of this software.
/// </summary>
public interface IAccessController
{
    Guid DeviceId { get; }
    Task UnlockAsync(Guid doorId, TimeSpan duration, CancellationToken ct = default);
    /// <summary>Normal relock after an unlock pulse. Must be ignored by implementations while an emergency unlock is active.</summary>
    Task RelockAsync(Guid doorId, CancellationToken ct = default);
    Task EmergencyUnlockAllAsync(CancellationToken ct = default);
}

public static class VendorTerminals
{
    /// <summary>Shown when a vendor terminal is configured.</summary>
    public const string Notice =
        "Vendor terminals (ZKTeco, Hikvision, Suprema, Anviz and others) speak proprietary protocols. They require the vendor's official SDK or " +
        "protocol documentation and are NOT implemented yet: the IAttendanceDevice / IAccessController interfaces are ready for an adapter. " +
        "Many such terminals can meanwhile export events through a generic TCP/serial line output (use the TcpLine or Serial adapters).";
}

/// <summary>Generic line protocol: a regex with named groups <c>uid</c> (required), <c>type</c>, <c>id</c>, <c>ts</c> (ISO-8601).</summary>
public sealed record LineProtocol
{
    public const string DefaultPattern = @"^(?<uid>[^,;]+)(?:[,;](?<type>[A-Za-z0-9]*))?(?:[,;](?<id>[^,;]*))?(?:[,;](?<ts>.+))?$";
    public string Pattern { get; init; } = DefaultPattern;
    public string CredentialKind { get; init; } = CredentialKinds.Card;

    public DeviceScan? Parse(string line, Guid deviceId, DateTimeOffset now)
    {
        var m = Regex.Match(line.Trim(), Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        if (!m.Success || m.Groups["uid"].Value.Trim() is not { Length: > 0 } uid) return null;
        var id = m.Groups["id"].Success && m.Groups["id"].Value.Trim().Length > 0 ? m.Groups["id"].Value.Trim() : Guid.NewGuid().ToString("N");
        var at = m.Groups["ts"].Success && DateTimeOffset.TryParse(m.Groups["ts"].Value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts)
            ? ts.ToUniversalTime() : now;
        return new DeviceScan(deviceId, id, CredentialKind, uid, MapType(m.Groups["type"].Value), at);
    }

    public static string? MapType(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "in" or "i" or "0" or "checkin" => PunchTypes.In,
        "out" or "o" or "1" or "checkout" => PunchTypes.Out,
        "breakstart" or "bs" or "2" => PunchTypes.BreakStart,
        "breakend" or "be" or "3" => PunchTypes.BreakEnd,
        _ => null,
    };
}

/// <summary>Builds adapters from persisted device configuration.</summary>
public static class DeviceFactory
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static T Config<T>(WfDevice d) where T : new() =>
        string.IsNullOrWhiteSpace(d.ConfigJson) ? new T() : JsonSerializer.Deserialize<T>(d.ConfigJson, Json) ?? new T();

    public static IAttendanceDevice Create(WfDevice d, TimeProvider time) => d.Kind switch
    {
        DeviceKinds.Virtual => new VirtualTerminal(d.Id, time),
        DeviceKinds.KeyboardWedge => new KeyboardWedgeCardReader(d.Id, Config<KeyboardWedgeConfig>(d), time),
        DeviceKinds.Serial => new SerialLineReader(d.Id, Config<SerialLineConfig>(d), time),
        DeviceKinds.TcpLine => new TcpLineTerminal(d.Id, Config<TcpLineConfig>(d), time),
        DeviceKinds.Vendor => throw new NotSupportedException(VendorTerminals.Notice),
        _ => throw new NotSupportedException("Unknown device kind: " + d.Kind),
    };
}

/// <summary>Shared plumbing for adapters.</summary>
public abstract class DeviceBase(Guid deviceId, TimeProvider time) : IAttendanceDevice
{
    public Guid DeviceId { get; } = deviceId;
    public abstract DeviceCapabilities Capabilities { get; }
    public DeviceConnectionState State { get; private set; } = DeviceConnectionState.Disconnected;
    protected TimeProvider Time { get; } = time;
    public event EventHandler<DeviceScan>? Scanned;
    public event EventHandler<DeviceConnectionState>? StateChanged;
    public event EventHandler<Exception>? Faulted;

    protected void SetState(DeviceConnectionState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(this, s);
    }

    protected void Raise(DeviceScan scan) => Scanned?.Invoke(this, scan);
    protected void Fault(Exception ex)
    {
        SetState(DeviceConnectionState.Faulted);
        Faulted?.Invoke(this, ex);
    }

    public abstract Task ConnectAsync(CancellationToken ct = default);
    public abstract Task DisconnectAsync();
    public abstract Task<DeviceTestResult> TestConnectionAsync(CancellationToken ct = default);
    public virtual async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        GC.SuppressFinalize(this);
    }
}
