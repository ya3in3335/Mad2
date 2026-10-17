using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Veltrix.Desktop.Core.Workforce.Terminals;

/// <summary>Runs one request through the 32-bit ZKTeco bridge process (VeltrixZkBridge.exe) and returns its JSON reply.</summary>
public interface IZkBridgeRunner
{
    Task<string> RunAsync(string requestJson, CancellationToken ct);
}

/// <summary>
/// ZKTeco terminals through the OFFICIAL ZKTeco Standalone SDK (zkemkeeper.dll 6.2.4.11, 32-bit COM), driven by the 32-bit bridge
/// VeltrixZkBridge.exe because the app and the connector service are 64-bit. The SDK is installed by the user from ZKTeco's package
/// (tools/zkteco/Install-ZktecoSdk.ps1); it is not bundled.
/// STATUS: calls follow the SDK's own type library — NOT yet verified on a real ZKTeco terminal.
/// Read-only: the attendance log is read, never cleared. The terminal has no event ids, so the cursor is the device-local time of the last
/// event and overlap at that second is removed by the ingestion hash. In/out keys are kept in the raw data only (many terminals report 0 for
/// every punch), so the direction is derived by the attendance engine.
/// </summary>
public sealed class ZkTecoSdkAdapter(TerminalConnection connection, IZkBridgeRunner? runner = null, int timeoutSeconds = 120) : ITerminalAdapter
{
    public const string Status = "Official ZKTeco Standalone SDK via 32-bit bridge — NOT verified on hardware";
    public const int DefaultPort = 4370;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IZkBridgeRunner _runner = runner ?? new ProcessZkBridgeRunner(TimeSpan.FromSeconds(timeoutSeconds));

    public string Manufacturer => TerminalManufacturers.ZKTeco;
    public TerminalCapabilities Capabilities => TerminalCapabilities.EventImport | TerminalCapabilities.DeviceInfo | TerminalCapabilities.ClockRead
        | TerminalCapabilities.Fingerprint | TerminalCapabilities.Face | TerminalCapabilities.Card | TerminalCapabilities.Pin;

    private sealed record Request(string Op, string Host, int Port, int CommKey, int MachineNumber, string? Since);
    private sealed record Log(string EnrollNumber, string LocalTime, int VerifyMode, int InOutMode, int WorkCode);
    private sealed record Reply(bool Ok, string? ErrorKind, string? Error, string? Serial, string? Firmware, string? Product, string? DeviceTime, List<Log>? Logs);

    /// <summary>The communication key (numeric "Comm Key" set on the terminal) is stored as the terminal password.</summary>
    private int CommKey => string.IsNullOrEmpty(connection.Password) ? 0
        : int.TryParse(connection.Password, NumberStyles.None, CultureInfo.InvariantCulture, out var k) ? k
        : throw new TerminalException(TerminalErrorKind.AuthFailed, "ZKTeco communication key must be numeric.");

    private int MachineNumber => int.TryParse(connection.RemoteDeviceId, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 1;

    private async Task<Reply> CallAsync(string op, string? since, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connection.Host)) throw new TerminalException(TerminalErrorKind.Unreachable, "No host configured.");
        var json = JsonSerializer.Serialize(new Request(op, connection.Host.Trim(), connection.Port ?? DefaultPort, CommKey, MachineNumber, since), Json);
        string output;
        try { output = await _runner.RunAsync(json, ct); }
        catch (TerminalException) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TerminalException(TerminalErrorKind.Unreachable, "ZKTeco bridge timed out."); }
        Reply reply;
        try { reply = JsonSerializer.Deserialize<Reply>(output, Json) ?? throw new JsonException("empty"); }
        catch (JsonException e) { throw new TerminalException(TerminalErrorKind.ProtocolError, "ZKTeco bridge: malformed reply (" + e.Message + ")", e); }
        if (!reply.Ok)
            throw new TerminalException(Enum.TryParse<TerminalErrorKind>(reply.ErrorKind, out var k) ? k : TerminalErrorKind.ProtocolError, "ZKTeco: " + reply.Error);
        return reply;
    }

    public async Task ValidateConnectionAsync(CancellationToken ct = default) => await CallAsync("info", null, ct);

    public async Task<TerminalDeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default) => ToInfo(await CallAsync("info", null, ct));

    private TerminalDeviceInfo ToInfo(Reply r) => new(Manufacturer, r.Product, r.Serial, r.Firmware, ParseDeviceTime(r.DeviceTime));

    /// <summary>Device clock without zone: interpreted in the connection's offset (the terminal's registered offset), resolved by the caller.</summary>
    public int? OffsetMinutes { get; init; }

    private DateTimeOffset? ParseDeviceTime(string? s) =>
        s is not null && OffsetMinutes is { } off && DateTime.TryParseExact(s, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? new DateTimeOffset(t, TimeSpan.FromMinutes(off)) : null;

    public async Task<TerminalImportPage> ImportEventsAsync(string? cursor, DateTimeOffset? since, int maxEvents, CancellationToken ct = default)
    {
        // The SDK always reads the whole log; the bridge filters from the cursor second (inclusive: same-second events are deduplicated later).
        var r = await CallAsync("logs", cursor, ct);
        var events = (r.Logs ?? []).OrderBy(l => l.LocalTime, StringComparer.Ordinal).Select(ToEvent).ToList();
        var next = events.Count > 0 ? events[^1].DeviceLocalTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) : cursor;
        return new TerminalImportPage(events, next, false);
    }

    private TerminalEvent ToEvent(Log l) => new(null, l.EnrollNumber, null,
        DateTime.ParseExact(l.LocalTime, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), null, Method(l.VerifyMode), VerifyResults.Success, null,
        new Dictionary<string, string>
        {
            ["verifyMode"] = l.VerifyMode.ToString(CultureInfo.InvariantCulture),
            ["inOutMode"] = l.InOutMode.ToString(CultureInfo.InvariantCulture),
            ["workCode"] = l.WorkCode.ToString(CultureInfo.InvariantCulture),
        });

    /// <summary>Only the common verify codes are named (0 password, 1 fingerprint, 2 card, 15 face); others stay "unknown" with the raw code kept.</summary>
    public static string Method(int verifyMode) => verifyMode switch
    {
        0 => VerifyMethods.Pin,
        1 => VerifyMethods.Finger,
        2 => VerifyMethods.Card,
        15 => VerifyMethods.Face,
        _ => VerifyMethods.Unknown,
    };

    public IAsyncEnumerable<TerminalEvent> StreamEventsAsync(CancellationToken ct = default) => throw TerminalException.Unsupported("ZKTeco: polling only.");

    public async Task<TerminalHealth> HealthAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try { await CallAsync("info", null, ct); return new TerminalHealth(true, sw.Elapsed, null); }
        catch (TerminalException e) { return new TerminalHealth(false, null, e.Message); }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Starts VeltrixZkBridge.exe (looked up next to the app / service), writes the request on stdin and reads the reply.</summary>
public sealed class ProcessZkBridgeRunner(TimeSpan timeout) : IZkBridgeRunner
{
    public const string ExeName = "VeltrixZkBridge.exe";

    public static string? Locate()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var p in new[] { Path.Combine(baseDir, "ZkBridge", ExeName), Path.Combine(baseDir, "..", "ZkBridge", ExeName), Path.Combine(baseDir, ExeName) })
            if (File.Exists(p)) return Path.GetFullPath(p);
        return null;
    }

    public async Task<string> RunAsync(string requestJson, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw TerminalException.Unsupported("ZKTeco SDK: Windows only.");
        var exe = Locate() ?? throw TerminalException.Unsupported("ZKTeco bridge (" + ExeName + ") is not installed.");
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo(exe)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            },
        };
        p.Start();
        await p.StandardInput.WriteAsync(requestJson.AsMemory(), ct);
        p.StandardInput.Close();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var output = await p.StandardOutput.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            return output;
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
            throw;
        }
    }
}
