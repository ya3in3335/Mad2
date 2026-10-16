using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Veltrix.Desktop.Core.Workforce.Terminals;

/// <summary>
/// Fully functional SIMULATED terminal (tests, demos, training) — NOT real hardware. It keeps an append-only event log like a real
/// pointeuse and can inject the failure modes ingestion must survive: clock drift, disconnects / transient faults, out-of-order events,
/// duplicates (same vendor id) and conflicting duplicates (same vendor id, different data).
/// </summary>
public sealed class SimulatedTerminal(TimeProvider pcClock, string serialNumber = "SIM-0001", int utcOffsetMinutes = 60, string password = "sim")
{
    private readonly List<TerminalEvent> _log = [];
    private readonly List<Channel<TerminalEvent>> _subscribers = [];
    private readonly object _gate = new();
    private long _nextId = 1;
    private readonly Random _random = new(42);

    public string SerialNumber { get; set; } = serialNumber;
    public string Model { get; set; } = "VELTRIX-SIM-T1";
    public string Firmware { get; set; } = "sim-1.0";
    public string Password { get; set; } = password;
    public int UtcOffsetMinutes { get; set; } = utcOffsetMinutes;
    /// <summary>Device clock − true time.</summary>
    public TimeSpan ClockDrift { get; set; }
    /// <summary>When false every call fails with Unreachable (link down / terminal off). Events keep being logged on the terminal.</summary>
    public bool Online { get; set; } = true;
    /// <summary>Number of next calls that fail with Unreachable (transient faults → reconnect with backoff).</summary>
    public int FailNextCalls { get; set; }
    /// <summary>Whether events carry the UTC offset (some vendors report only wall-clock time).</summary>
    public bool ReportsOffset { get; set; } = true;
    public bool SupportsStream { get; set; } = true;
    /// <summary>Demo mode: every import adds a punch for one of these terminal users.</summary>
    public IReadOnlyList<string> DemoUsers { get; set; } = [];
    public int Calls { get; private set; }

    public int Count { get { lock (_gate) return _log.Count; } }

    /// <summary>Current terminal wall-clock time (true time + drift, in the terminal offset).</summary>
    public DateTime DeviceNow => (pcClock.GetUtcNow() + ClockDrift).ToOffset(TimeSpan.FromMinutes(UtcOffsetMinutes)).DateTime;

    /// <summary>A user presents finger / face / card on the terminal (time = terminal clock unless given).</summary>
    public TerminalEvent Punch(string? userId, string method = VerifyMethods.Finger, string? punchType = null, DateTime? deviceLocalTime = null, string? cardNo = null,
        string verifyResult = VerifyResults.Success)
    {
        lock (_gate)
        {
            var e = new TerminalEvent((_nextId++).ToString(CultureInfo.InvariantCulture), userId, cardNo, Truncate(deviceLocalTime ?? DeviceNow),
                ReportsOffset ? UtcOffsetMinutes : null, method, verifyResult, punchType,
                new Dictionary<string, string> { ["sim"] = "1" });
            Append(e);
            return e;
        }
    }

    /// <summary>Re-appends an existing event unchanged (terminals re-deliver logs).</summary>
    public void InjectDuplicate(TerminalEvent e)
    {
        lock (_gate) Append(e);
    }

    /// <summary>Appends an event with the same vendor id but different data (corrupted / re-numbered log).</summary>
    public TerminalEvent InjectConflict(TerminalEvent original, DateTime newLocalTime)
    {
        var e = original with { DeviceLocalTime = Truncate(newLocalTime) };
        lock (_gate) Append(e);
        return e;
    }

    private void Append(TerminalEvent e)
    {
        _log.Add(e);
        foreach (var s in _subscribers) s.Writer.TryWrite(e);
    }

    private static DateTime Truncate(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Unspecified);

    internal void Call()
    {
        Calls++;
        if (!Online) throw new TerminalException(TerminalErrorKind.Unreachable, "Simulated terminal offline.");
        if (FailNextCalls > 0)
        {
            FailNextCalls--;
            throw new TerminalException(TerminalErrorKind.Unreachable, "Simulated transient network failure.");
        }
    }

    internal TerminalImportPage Page(string? cursor, int max)
    {
        lock (_gate)
        {
            if (DemoUsers.Count > 0) Punch(DemoUsers[_random.Next(DemoUsers.Count)]);
            var from = int.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? Math.Clamp(c, 0, _log.Count) : 0;
            var take = Math.Min(Math.Max(1, max), _log.Count - from);
            var events = _log.GetRange(from, take);
            var next = from + take;
            return new TerminalImportPage(events, next.ToString(CultureInfo.InvariantCulture), next < _log.Count);
        }
    }

    internal Channel<TerminalEvent> Subscribe()
    {
        var ch = Channel.CreateUnbounded<TerminalEvent>();
        lock (_gate) _subscribers.Add(ch);
        return ch;
    }

    internal void Unsubscribe(Channel<TerminalEvent> ch)
    {
        lock (_gate) _subscribers.Remove(ch);
        ch.Writer.TryComplete();
    }

    /// <summary>Drops the link: open streams fail, calls fail until <see cref="Online"/> is set again.</summary>
    public void Disconnect()
    {
        Online = false;
        lock (_gate)
            foreach (var s in _subscribers) s.Writer.TryComplete(new TerminalException(TerminalErrorKind.Unreachable, "Simulated link lost."));
    }
}

/// <summary><see cref="ITerminalAdapter"/> over a <see cref="SimulatedTerminal"/>.</summary>
public sealed class SimulatedTerminalAdapter(SimulatedTerminal terminal, TerminalConnection connection) : ITerminalAdapter
{
    public string Manufacturer => TerminalManufacturers.Simulated;

    public TerminalCapabilities Capabilities => TerminalCapabilities.EventImport | TerminalCapabilities.DeviceInfo | TerminalCapabilities.ClockRead
        | TerminalCapabilities.Fingerprint | TerminalCapabilities.Face | TerminalCapabilities.Card | TerminalCapabilities.Pin | TerminalCapabilities.VendorEventIds
        | (terminal.SupportsStream ? TerminalCapabilities.EventStream : 0);

    public Task ValidateConnectionAsync(CancellationToken ct = default)
    {
        terminal.Call();
        if (!string.Equals(connection.Password ?? "", terminal.Password, StringComparison.Ordinal))
            throw new TerminalException(TerminalErrorKind.AuthFailed, "Simulated terminal: wrong password.");
        return Task.CompletedTask;
    }

    public async Task<TerminalDeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default)
    {
        await ValidateConnectionAsync(ct);
        var clock = new DateTimeOffset(terminal.DeviceNow, TimeSpan.FromMinutes(terminal.UtcOffsetMinutes));
        return new TerminalDeviceInfo(Manufacturer, terminal.Model, terminal.SerialNumber, terminal.Firmware, clock);
    }

    public async Task<TerminalImportPage> ImportEventsAsync(string? cursor, DateTimeOffset? since, int maxEvents, CancellationToken ct = default)
    {
        await ValidateConnectionAsync(ct);
        return terminal.Page(cursor, maxEvents);
    }

    public async IAsyncEnumerable<TerminalEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!terminal.SupportsStream) throw TerminalException.Unsupported("Simulated terminal: streaming disabled.");
        await ValidateConnectionAsync(ct);
        var ch = terminal.Subscribe();
        try
        {
            await foreach (var e in ch.Reader.ReadAllAsync(ct)) yield return e;
        }
        finally { terminal.Unsubscribe(ch); }
    }

    public Task<TerminalHealth> HealthAsync(CancellationToken ct = default) =>
        Task.FromResult(terminal.Online ? new TerminalHealth(true, TimeSpan.Zero, "simulated") : new TerminalHealth(false, null, "simulated terminal offline"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Process-wide simulated terminals by device id (the demo terminal survives between syncs).</summary>
public static class SimulatedTerminals
{
    private static readonly ConcurrentDictionary<Guid, SimulatedTerminal> Terminals = new();

    public static SimulatedTerminal GetOrCreate(Guid deviceId, TimeProvider time) =>
        Terminals.GetOrAdd(deviceId, id => new SimulatedTerminal(time, "SIM-" + id.ToString("N")[..8].ToUpperInvariant()) { DemoUsers = ["1", "2", "3"] });

    public static void Register(Guid deviceId, SimulatedTerminal terminal) => Terminals[deviceId] = terminal;
}
