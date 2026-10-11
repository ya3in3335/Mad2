using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Veltrix.Desktop.Core.Security;

namespace Veltrix.Desktop.Core.Licensing;

/// <summary>
/// Where the device binding lives. On Windows: %ProgramData%\VELTRIX\binding.dat and HKLM\SOFTWARE\VELTRIX\Binding, both
/// writable by administrators only (written by the elevated installer via <c>VeltrixOS.exe --bind</c>); tests use memory.
/// </summary>
public interface IDeviceBindingStore
{
    /// <summary>Stable names of the binding locations (at least two for redundancy).</summary>
    IReadOnlyList<string> Locations { get; }
    byte[]? Read(string location);
    /// <summary>Returns false when the location cannot be written (e.g. access denied).</summary>
    bool Write(string location, byte[] data);
}

public sealed record DeviceBindingRecord(int V, Guid Iid, string Fingerprint, DateTimeOffset BoundAt);

public enum BindingStatus
{
    /// <summary>A valid binding for this computer exists.</summary>
    Bound,
    /// <summary><see cref="DeviceBindingManager.Bind"/> (elevated installer) created or replaced the binding.</summary>
    NewlyBound,
    /// <summary>No binding (e.g. program files copied to another PC without the vendor's installer).</summary>
    NotInstalled,
    /// <summary>A binding exists but belongs to another computer (copied) or could not be decrypted here.</summary>
    WrongDevice,
    /// <summary>A binding was modified or the locations disagree.</summary>
    Tampered,
    /// <summary>The binding could not be written to every location.</summary>
    StorageFailed,
    /// <summary><see cref="DeviceBindingManager.Bind"/> was called without administrator rights.</summary>
    NotElevated,
}

public sealed record BindingResult(BindingStatus Status, DeviceBindingRecord? Record)
{
    public bool IsLicensed => Status is BindingStatus.Bound or BindingStatus.NewlyBound;
}

/// <summary>
/// Device binding created at install time. The vendor installs the app personally; the elevated installer runs
/// <c>VeltrixOS.exe --bind</c>, which calls <see cref="Bind"/>: a record (new installation id + machine fingerprint) is encrypted
/// with a machine-scoped protector (DPAPI LocalMachine on Windows), authenticated with an HMAC whose key is itself
/// machine-protected, and written to admin-only locations. The normal (non-elevated) app only calls <see cref="Verify"/>: it never
/// creates or repairs a binding. A copy on another computer, a missing binding or a tampered record is refused. This deters
/// casual copying; an administrator with the program files can still run --bind (see LICENSE_ACTIVATION_GUIDE.md).
/// </summary>
public sealed class DeviceBindingManager(IDeviceBindingStore store, IDataProtector machineProtector, string fingerprint, TimeProvider time,
    Func<bool> isElevated)
{
    public const int Version = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public BindingResult? Current { get; private set; }

    /// <summary>True once <see cref="Verify"/> succeeded: the app may open local data (standalone mode).</summary>
    public bool IsBound => Current?.IsLicensed == true;

    /// <summary>Read-only check at every start: at least one location present, all present ones valid for this machine and equal.</summary>
    public BindingResult Verify()
    {
        var (status, record, _) = ReadAll();
        Current = new BindingResult(status, record);
        return Current;
    }

    /// <summary>
    /// Installer path (<c>--bind</c>), administrators only. Keeps a valid existing binding (repairing missing locations) or
    /// replaces a missing/unusable one (vendor re-install, e.g. after a hardware change). Every location must be written.
    /// </summary>
    public BindingResult Bind()
    {
        if (!isElevated()) return Current = new BindingResult(BindingStatus.NotElevated, null);
        var (status, record, blob) = ReadAll();
        if (status != BindingStatus.Bound || record is null || blob is null)
        {
            record = new DeviceBindingRecord(Version, Guid.NewGuid(), fingerprint, time.GetUtcNow());
            blob = Seal(record);
            status = BindingStatus.NewlyBound;
        }
        foreach (var loc in store.Locations)
        {
            if (store.Read(loc) is { } existing && existing.AsSpan().SequenceEqual(blob)) continue;
            bool ok;
            try { ok = store.Write(loc, blob); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ok = false; }
            if (!ok) return Current = new BindingResult(BindingStatus.StorageFailed, null);
        }
        return Current = new BindingResult(status, record);
    }

    private (BindingStatus, DeviceBindingRecord?, byte[]?) ReadAll()
    {
        var present = new List<byte[]>();
        foreach (var loc in store.Locations)
        {
            byte[]? data;
            try { data = store.Read(loc); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { data = null; }
            if (data is { Length: > 0 }) present.Add(data);
        }
        if (present.Count == 0) return (BindingStatus.NotInstalled, null, null);

        DeviceBindingRecord? record = null;
        foreach (var data in present)
        {
            var (s, r) = Open(data);
            if (s != BindingStatus.Bound) return (s, null, null);
            if (record is not null && record != r) return (BindingStatus.Tampered, null, null);
            record = r;
        }
        return (BindingStatus.Bound, record, present[0]);
    }

    /// <summary>{k: protected HMAC key, d: protected record, m: HMAC-SHA256(key, record)}.</summary>
    public byte[] Seal(DeviceBindingRecord record)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        var macKey = RandomNumberGenerator.GetBytes(32);
        var envelope = new Envelope(Convert.ToBase64String(machineProtector.Protect(macKey)), Convert.ToBase64String(machineProtector.Protect(plain)),
            Convert.ToBase64String(HMACSHA256.HashData(macKey, plain)));
        return JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
    }

    private (BindingStatus, DeviceBindingRecord?) Open(byte[] data)
    {
        Envelope? env;
        try { env = JsonSerializer.Deserialize<Envelope>(data, Json); }
        catch (JsonException) { return (BindingStatus.Tampered, null); }
        if (env is null || env.K is null || env.D is null || env.M is null) return (BindingStatus.Tampered, null);

        byte[] macKey, plain, mac;
        try
        {
            mac = Convert.FromBase64String(env.M);
            macKey = machineProtector.Unprotect(Convert.FromBase64String(env.K));
            plain = machineProtector.Unprotect(Convert.FromBase64String(env.D));
        }
        catch (FormatException) { return (BindingStatus.Tampered, null); }
        catch (CryptographicException) { return (BindingStatus.WrongDevice, null); } // another machine's DPAPI key

        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(macKey, plain), mac)) return (BindingStatus.Tampered, null);
        DeviceBindingRecord? record;
        try { record = JsonSerializer.Deserialize<DeviceBindingRecord>(plain, Json); }
        catch (JsonException) { return (BindingStatus.Tampered, null); }
        if (record is null || record.V != Version || record.Iid == Guid.Empty) return (BindingStatus.Tampered, null);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(record.Fingerprint), Encoding.UTF8.GetBytes(fingerprint)))
            return (BindingStatus.WrongDevice, null);
        return (BindingStatus.Bound, record);
    }

    private sealed record Envelope(string? K, string? D, string? M);
}
