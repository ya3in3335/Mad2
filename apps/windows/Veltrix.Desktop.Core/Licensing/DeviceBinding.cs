using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Veltrix.Desktop.Core.Security;

namespace Veltrix.Desktop.Core.Licensing;

/// <summary>
/// Where the device binding and the installer's one-time "fresh install" token live. On Windows the binding is kept in two
/// places (%ProgramData%\VELTRIX\binding.dat and HKLM\SOFTWARE\VELTRIX\Binding, with per-user fallbacks when those are not
/// writable); tests use an in-memory implementation.
/// </summary>
public interface IDeviceBindingStore
{
    /// <summary>Stable names of the binding locations (at least two for redundancy).</summary>
    IReadOnlyList<string> Locations { get; }
    byte[]? Read(string location);
    /// <summary>Returns false when the location cannot be written (e.g. access denied).</summary>
    bool Write(string location, byte[] data);
    /// <summary>The token written by the installer, or null when there is none (never installed, or already consumed).</summary>
    string? ReadInstallToken();
    /// <summary>Consumes the token. Returns false when it could not be deleted.</summary>
    bool DeleteInstallToken();
}

/// <summary>The binding record: which installation is bound to which machine, and when.</summary>
public sealed record DeviceBindingRecord(int V, Guid Iid, string Fingerprint, DateTimeOffset BoundAt, string? Tok = null);

public enum BindingStatus
{
    /// <summary>A valid binding for this computer exists.</summary>
    Bound,
    /// <summary>First run after a vendor installation: the binding was just created and the install token consumed.</summary>
    NewlyBound,
    /// <summary>No binding and no install token (e.g. program files copied from another PC).</summary>
    NotInstalled,
    /// <summary>A binding exists but belongs to another computer (copied) or could not be decrypted here.</summary>
    WrongDevice,
    /// <summary>A binding was modified or the locations disagree.</summary>
    Tampered,
    /// <summary>The binding could not be written to any location.</summary>
    StorageFailed,
}

public sealed record BindingResult(BindingStatus Status, DeviceBindingRecord? Record)
{
    public bool IsLicensed => Status is BindingStatus.Bound or BindingStatus.NewlyBound;
}

/// <summary>
/// Trust-on-first-use device binding. The vendor installs the app personally; the installer leaves a one-time token. On the
/// first start the app binds itself to this computer (fingerprint + new installation id), stores the record encrypted with a
/// machine-scoped protector (DPAPI LocalMachine on Windows) plus an HMAC whose key is itself machine-protected, and deletes the
/// token. Every start re-verifies; a copy on another computer, a deleted binding without a fresh token or a tampered record is
/// refused. This deters casual copying; an administrator with tools can still tamper (see LICENSE_ACTIVATION_GUIDE.md).
/// </summary>
public sealed class DeviceBindingManager(IDeviceBindingStore store, IDataProtector machineProtector, string fingerprint, TimeProvider time)
{
    public const int Version = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public BindingResult? Current { get; private set; }

    /// <summary>True once <see cref="Verify"/> succeeded: the app may open local data (standalone mode).</summary>
    public bool IsBound => Current?.IsLicensed == true;

    public BindingResult Verify()
    {
        var result = VerifyCore();
        Current = result;
        return result;
    }

    private BindingResult VerifyCore()
    {
        var present = new List<(string Location, byte[] Data)>();
        foreach (var loc in store.Locations)
        {
            byte[]? data;
            try { data = store.Read(loc); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { data = null; }
            if (data is { Length: > 0 }) present.Add((loc, data));
        }

        var token = store.ReadInstallToken();
        if (present.Count == 0) return token is null ? new BindingResult(BindingStatus.NotInstalled, null) : Bind(token);

        DeviceBindingRecord? record = null;
        var status = BindingStatus.Bound;
        foreach (var (_, data) in present)
        {
            var (s, r) = Open(data);
            if (s != BindingStatus.Bound) { status = s; record = null; break; }
            if (record is not null && record != r) { status = BindingStatus.Tampered; record = null; break; }
            record = r;
        }

        if (record is null)
        {
            // A fresh vendor (re)installation on this computer may replace an unusable binding (e.g. after a hardware change).
            return token is null ? new BindingResult(status, null) : Bind(token);
        }

        // Repair any missing location from the valid copy.
        var valid = present[0].Data;
        foreach (var loc in store.Locations.Where(l => present.All(p => p.Location != l))) store.Write(loc, valid);
        if (token is not null) store.DeleteInstallToken(); // vendor re-install on the same computer: keep the existing binding
        return new BindingResult(BindingStatus.Bound, record);
    }

    private BindingResult Bind(string token)
    {
        var record = new DeviceBindingRecord(Version, Guid.NewGuid(), fingerprint, time.GetUtcNow(),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant());
        var blob = Seal(record);
        var written = 0;
        foreach (var loc in store.Locations)
        {
            try { if (store.Write(loc, blob)) written++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (written == 0) return new BindingResult(BindingStatus.StorageFailed, null);
        store.DeleteInstallToken();
        return new BindingResult(BindingStatus.NewlyBound, record);
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
