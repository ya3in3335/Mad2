using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Security;

namespace Veltrix.Desktop.Core.Workforce.Terminals;

/// <summary>Creates the adapter of a registered terminal.</summary>
public interface ITerminalAdapterFactory
{
    ITerminalAdapter Create(WfDevice device, TerminalConnection connection);
}

/// <summary>Terminal options kept in <see cref="WfDevice.ConfigJson"/>.</summary>
public sealed record TerminalOptions
{
    public string? CertificateSha256 { get; init; }
    public string? RemoteDeviceId { get; init; }
}

public sealed class TerminalAdapterFactory(TimeProvider time) : ITerminalAdapterFactory
{
    public ITerminalAdapter Create(WfDevice device, TerminalConnection connection) => device.Manufacturer switch
    {
        TerminalManufacturers.Simulated => new SimulatedTerminalAdapter(SimulatedTerminals.GetOrCreate(device.Id, time), connection),
        TerminalManufacturers.Hikvision => new HikvisionIsapiAdapter(connection, time, device.TimeZoneOffsetMinutes ?? (int)time.LocalTimeZone.GetUtcOffset(time.GetUtcNow()).TotalMinutes),
        TerminalManufacturers.Suprema => new SupremaBioStar2Adapter(connection, time),
        TerminalManufacturers.ZKTeco => new ZkTecoSdkAdapter(connection) { OffsetMinutes = device.TimeZoneOffsetMinutes ?? (int)time.LocalTimeZone.GetUtcOffset(time.GetUtcNow()).TotalMinutes },
        _ => throw TerminalException.Unsupported("Unknown terminal manufacturer: " + device.Manufacturer),
    };

    public static TerminalOptions Options(WfDevice d)
    {
        if (string.IsNullOrWhiteSpace(d.ConfigJson)) return new TerminalOptions();
        try { return JsonSerializer.Deserialize<TerminalOptions>(d.ConfigJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new TerminalOptions(); }
        catch (JsonException) { return new TerminalOptions(); }
    }
}

/// <summary>Terminal passwords at rest: protected bytes (base64) — the plaintext is never stored.</summary>
public static class TerminalSecrets
{
    public static string Protect(IDataProtector protector, string plaintext) => Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(plaintext)));

    public static string? Unprotect(IDataProtector protector, string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return null;
        try { return Encoding.UTF8.GetString(protector.Unprotect(Convert.FromBase64String(protectedBase64))); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
    }
}

/// <summary>
/// DPAPI with LocalMachine scope: the desktop app (user) and the connector service (LocalSystem) on THIS PC can decrypt; another PC cannot.
/// Windows only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MachineDataProtector : IDataProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VELTRIX OS terminal credentials v1");
    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.LocalMachine);
    public byte[] Unprotect(byte[] ciphertext) => ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.LocalMachine);
}

/// <summary>Single-writer coordination of terminal ingestion between the desktop app and the connector service.</summary>
public interface IIngestionLock
{
    /// <summary>Null when another process holds the lock after <paramref name="wait"/>.</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(TimeSpan wait, CancellationToken ct = default);
}

/// <summary>In-process lock (tests, or a single process).</summary>
public sealed class InProcessIngestionLock : IIngestionLock
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IAsyncDisposable?> TryAcquireAsync(TimeSpan wait, CancellationToken ct = default) =>
        await _gate.WaitAsync(wait, ct) ? new Releaser(_gate) : null;

    private sealed class Releaser(SemaphoreSlim gate) : IAsyncDisposable
    {
        private int _done;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Machine-wide named mutex (<c>Global\</c> namespace, so the LocalSystem service in session 0 and the app in the user session share it).
/// Mutexes are thread-affine, so each acquisition owns a dedicated thread that waits, holds and releases. The ACL lets authenticated users
/// open a mutex first created by the service. A crashed holder leaves an abandoned mutex, which the next waiter acquires normally.
/// </summary>
public sealed class NamedMutexIngestionLock(string name = NamedMutexIngestionLock.DefaultName) : IIngestionLock
{
    public const string DefaultName = @"Global\VeltrixOS.TerminalIngestion";

    public Task<IAsyncDisposable?> TryAcquireAsync(TimeSpan wait, CancellationToken ct = default)
    {
        var acquired = new TaskCompletionSource<IAsyncDisposable?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            Mutex? mutex = null;
            try
            {
                mutex = Open();
                bool owned;
                try { owned = mutex.WaitOne(wait); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) { acquired.TrySetResult(null); return; }
                acquired.TrySetResult(new Holder(release));
                release.Wait();
                mutex.ReleaseMutex();
            }
            catch (Exception ex) { acquired.TrySetException(ex); }
            finally { mutex?.Dispose(); }
        }) { IsBackground = true, Name = "VeltrixIngestionLock" };
        thread.Start();
        return acquired.Task; // bounded by `wait`; not cancelled midway so an acquired mutex is never orphaned
    }

    private Mutex Open()
    {
        if (OperatingSystem.IsWindows()) return OpenWindows();
        return new Mutex(false, name.Replace(@"Global\", "", StringComparison.Ordinal));
    }

    [SupportedOSPlatform("windows")]
    private Mutex OpenWindows()
    {
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Allow));
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), MutexRights.FullControl, AccessControlType.Allow));
        try { return MutexAcl.Create(false, name, out _, security); }
        catch (UnauthorizedAccessException) { return Mutex.OpenExisting(name); }
    }

    private sealed class Holder(ManualResetEventSlim release) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            release.Set();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Hand-off between the desktop app and the connector service: the app (user session) publishes where its database lives; the service
/// (LocalSystem) reads it on every start / tick. Kept in a machine-wide folder the installer makes writable for users. No secrets inside.
/// </summary>
public sealed record ConnectorConfig(string DatabasePath, DateTimeOffset UpdatedAt, string? AppVersion = null)
{
    public const string FileName = "connector.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VELTRIX", "Connector");

    public static ConnectorConfig? Read(string directory)
    {
        try
        {
            var path = Path.Combine(directory, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<ConnectorConfig>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Best effort (false when the folder is missing or not writable, e.g. the service was never installed).</summary>
    public bool TryWrite(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return false;
            var path = Path.Combine(directory, FileName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
