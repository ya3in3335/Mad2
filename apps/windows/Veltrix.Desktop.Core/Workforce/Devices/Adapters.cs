using System.Net.Sockets;
using System.Text;
using Veltrix.Desktop.Core.Data;

namespace Veltrix.Desktop.Core.Workforce.Devices;

/// <summary>
/// In-memory terminal for tests and demos. Simulates card / QR / PIN / on-device fingerprint matches, disconnections (events are kept in the
/// terminal's offline log and delivered on reconnection, like real terminals) and door relays. It is NOT real hardware.
/// </summary>
public sealed class VirtualTerminal(Guid deviceId, TimeProvider time) : DeviceBase(deviceId, time), IAccessController
{
    private readonly List<DeviceScan> _offlineLog = [];
    private readonly HashSet<Guid> _unlocked = [];
    private long _counter;

    public override DeviceCapabilities Capabilities => DeviceCapabilities.Card | DeviceCapabilities.Qr | DeviceCapabilities.Pin | DeviceCapabilities.Fingerprint
        | DeviceCapabilities.DoorRelay | DeviceCapabilities.StoresOfflineLogs;
    public bool EmergencyActive { get; private set; }
    public IReadOnlyCollection<Guid> UnlockedDoors => _unlocked;
    public List<string> Commands { get; } = [];
    public int OfflineCount => _offlineLog.Count;

    public override Task ConnectAsync(CancellationToken ct = default)
    {
        SetState(DeviceConnectionState.Connected);
        var pending = _offlineLog.ToList();
        _offlineLog.Clear();
        foreach (var s in pending) Raise(s);
        return Task.CompletedTask;
    }

    public override Task DisconnectAsync()
    {
        SetState(DeviceConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public override Task<DeviceTestResult> TestConnectionAsync(CancellationToken ct = default) =>
        Task.FromResult(new DeviceTestResult(State == DeviceConnectionState.Connected, "virtual terminal " + State));

    /// <summary>Simulates a link failure (the terminal keeps logging offline).</summary>
    public void SimulateDisconnect(string reason = "link lost") => Fault(new IOException(reason));

    public DeviceScan SimulateCard(string uid, string? type = null) => Emit(CredentialKinds.Card, uid, type);
    public DeviceScan SimulateQr(string token, string? type = null) => Emit(CredentialKinds.Qr, token, type);
    public DeviceScan SimulatePin(string employeeNo, string pin, string? type = null) => Emit(CredentialKinds.Pin, employeeNo + ":" + pin, type);
    /// <summary>On-device fingerprint match: only the terminal user id is reported.</summary>
    public DeviceScan SimulateFingerprintMatch(string terminalUserId, string? type = null) => Emit(CredentialKinds.TerminalUser, terminalUserId, type);

    /// <summary>Re-sends an already delivered event (terminals re-push logs after reconnecting) — must be ignored by idempotency.</summary>
    public void Replay(DeviceScan scan) => Raise(scan);

    private DeviceScan Emit(string kind, string value, string? type)
    {
        var scan = new DeviceScan(DeviceId, "VT-" + Interlocked.Increment(ref _counter).ToString(System.Globalization.CultureInfo.InvariantCulture), kind, value, type, Time.GetUtcNow());
        if (State == DeviceConnectionState.Connected) Raise(scan);
        else _offlineLog.Add(scan);
        return scan;
    }

    public Task UnlockAsync(Guid doorId, TimeSpan duration, CancellationToken ct = default)
    {
        _unlocked.Add(doorId);
        Commands.Add($"unlock {doorId} {duration.TotalSeconds}s");
        return Task.CompletedTask;
    }

    public Task RelockAsync(Guid doorId, CancellationToken ct = default)
    {
        if (EmergencyActive) return Task.CompletedTask; // never relock while the emergency unlock is active
        _unlocked.Remove(doorId);
        Commands.Add($"relock {doorId}");
        return Task.CompletedTask;
    }

    public Task EmergencyUnlockAllAsync(CancellationToken ct = default)
    {
        EmergencyActive = true;
        Commands.Add("emergency-unlock-all");
        return Task.CompletedTask;
    }
}

public sealed record KeyboardWedgeConfig
{
    /// <summary>Minimum characters of a valid UID.</summary>
    public int MinLength { get; init; } = 4;
    /// <summary>Max milliseconds between keystrokes (readers type fast; slower input is treated as human typing and discarded). 0 = no check.</summary>
    public int MaxInterKeyMilliseconds { get; init; } = 80;
    /// <summary>Text starting with this prefix is reported as a QR token (2D scanners in keyboard mode).</summary>
    public string QrPrefix { get; init; } = "VQR:";
}

/// <summary>
/// USB HID "keyboard wedge" RFID/NFC/QR readers (the most common, driver-less kind): the reader types the UID followed by Enter. The UI forwards
/// keystrokes from a focused capture field to <see cref="Feed(char)"/>; fast bursts ending with Enter become scans.
/// </summary>
public sealed class KeyboardWedgeCardReader(Guid deviceId, KeyboardWedgeConfig config, TimeProvider time) : DeviceBase(deviceId, time)
{
    private readonly StringBuilder _buffer = new();
    private DateTimeOffset _lastKey;

    public override DeviceCapabilities Capabilities => DeviceCapabilities.Card | DeviceCapabilities.Qr;

    public override Task ConnectAsync(CancellationToken ct = default)
    {
        SetState(DeviceConnectionState.Connected);
        return Task.CompletedTask;
    }

    public override Task DisconnectAsync()
    {
        SetState(DeviceConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public override Task<DeviceTestResult> TestConnectionAsync(CancellationToken ct = default) =>
        Task.FromResult(new DeviceTestResult(true, "Keyboard-wedge readers need no connection: present a card to verify the reader types its UID."));

    /// <summary>Feeds one keystroke. Returns the scan when Enter completes a valid UID.</summary>
    public DeviceScan? Feed(char c)
    {
        var now = Time.GetUtcNow();
        if (config.MaxInterKeyMilliseconds > 0 && _buffer.Length > 0 && (now - _lastKey).TotalMilliseconds > config.MaxInterKeyMilliseconds) _buffer.Clear();
        _lastKey = now;
        if (c is '\r' or '\n')
        {
            var text = _buffer.ToString().Trim();
            _buffer.Clear();
            if (text.Length < config.MinLength || State != DeviceConnectionState.Connected) return null;
            var qr = config.QrPrefix.Length > 0 && text.StartsWith(config.QrPrefix, StringComparison.Ordinal);
            var scan = new DeviceScan(DeviceId, Guid.NewGuid().ToString("N"), qr ? CredentialKinds.Qr : CredentialKinds.Card,
                qr ? text[config.QrPrefix.Length..] : text, null, now);
            Raise(scan);
            return scan;
        }
        if (!char.IsControl(c)) _buffer.Append(c);
        return null;
    }

    public DeviceScan? Feed(string text)
    {
        DeviceScan? last = null;
        foreach (var c in text) last = Feed(c) ?? last;
        return last;
    }
}

/// <summary>Line-oriented byte transport (serial port in production, fakes in tests).</summary>
public interface ILineTransport : IDisposable
{
    void Open();
    /// <summary>Blocks until a full line arrives; returns null when the transport is closed.</summary>
    string? ReadLine();
}

public sealed record SerialLineConfig
{
    public string PortName { get; init; } = "COM3";
    public int BaudRate { get; init; } = 9600;
    public int DataBits { get; init; } = 8;
    /// <summary>None, Odd, Even, Mark, Space.</summary>
    public string Parity { get; init; } = "None";
    /// <summary>One, OnePointFive, Two.</summary>
    public string StopBits { get; init; } = "One";
    public string NewLine { get; init; } = "\r\n";
    public LineProtocol Protocol { get; init; } = new();
}

/// <summary>System.IO.Ports transport (RS-232 / USB-CDC readers that send one newline-terminated UID per scan).</summary>
public sealed class SerialPortTransport(SerialLineConfig c) : ILineTransport
{
    private readonly System.IO.Ports.SerialPort _port = new(c.PortName, c.BaudRate, Enum.Parse<System.IO.Ports.Parity>(c.Parity, true), c.DataBits,
        Enum.Parse<System.IO.Ports.StopBits>(c.StopBits, true)) { NewLine = c.NewLine, ReadTimeout = System.IO.Ports.SerialPort.InfiniteTimeout };

    public void Open() => _port.Open();

    public string? ReadLine()
    {
        try { return _port.IsOpen ? _port.ReadLine() : null; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException) { return null; }
    }

    public void Dispose() => _port.Dispose();
}

/// <summary>Serial (COM port) card reader: newline-delimited UIDs (optionally "uid,type,id,ts" — see <see cref="LineProtocol"/>).</summary>
public sealed class SerialLineReader(Guid deviceId, SerialLineConfig config, TimeProvider time, Func<SerialLineConfig, ILineTransport>? transportFactory = null)
    : DeviceBase(deviceId, time)
{
    private ILineTransport? _transport;
    private Task? _loop;

    public override DeviceCapabilities Capabilities => DeviceCapabilities.Card;
    public Task? ReadLoop => _loop;

    public override Task ConnectAsync(CancellationToken ct = default)
    {
        SetState(DeviceConnectionState.Connecting);
        try
        {
            _transport = (transportFactory ?? (c => new SerialPortTransport(c)))(config);
            _transport.Open();
        }
        catch (Exception ex)
        {
            Fault(ex);
            throw;
        }
        SetState(DeviceConnectionState.Connected);
        var t = _transport;
        _loop = Task.Run(() =>
        {
            try
            {
                while (t.ReadLine() is { } line)
                    if (config.Protocol.Parse(line, DeviceId, Time.GetUtcNow()) is { } scan) Raise(scan);
                SetState(DeviceConnectionState.Disconnected);
            }
            catch (Exception ex) { Fault(ex); }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public override async Task DisconnectAsync()
    {
        _transport?.Dispose();
        _transport = null;
        if (_loop is { } l) await Task.WhenAny(l, Task.Delay(2000));
        SetState(DeviceConnectionState.Disconnected);
    }

    public override async Task<DeviceTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        if (State == DeviceConnectionState.Connected) return new(true, config.PortName + " open");
        try
        {
            using var t = (transportFactory ?? (c => new SerialPortTransport(c)))(config);
            t.Open();
            await Task.CompletedTask;
            return new(true, config.PortName + " opened successfully");
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }
}

public sealed record TcpLineConfig
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 5005;
    public int ConnectTimeoutMs { get; init; } = 3000;
    /// <summary>Reconnect automatically after a link failure (0 = off).</summary>
    public int ReconnectDelayMs { get; init; } = 5000;
    public LineProtocol Protocol { get; init; } = new();
}

/// <summary>
/// Generic TCP text terminal / bridge: connects to host:port and reads newline-terminated records parsed by <see cref="LineProtocol"/>
/// (default <c>uid[,type[,eventId[,iso-timestamp]]]</c>). Reconnects after link failures.
/// </summary>
public sealed class TcpLineTerminal(Guid deviceId, TcpLineConfig config, TimeProvider time) : DeviceBase(deviceId, time)
{
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public override DeviceCapabilities Capabilities => DeviceCapabilities.Card | DeviceCapabilities.Qr | DeviceCapabilities.Pin;

    public override async Task ConnectAsync(CancellationToken ct = default)
    {
        await DisconnectAsync();
        _cts = new CancellationTokenSource();
        var client = await OpenAsync(ct);
        SetState(DeviceConnectionState.Connected);
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(client, token), CancellationToken.None);
    }

    private async Task<TcpClient> OpenAsync(CancellationToken ct)
    {
        SetState(DeviceConnectionState.Connecting);
        var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(config.ConnectTimeoutMs);
        try
        {
            await client.ConnectAsync(config.Host, config.Port, timeout.Token);
            return client;
        }
        catch (Exception ex)
        {
            client.Dispose();
            Fault(ex);
            throw;
        }
    }

    private async Task RunAsync(TcpClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using (client)
                using (var reader = new StreamReader(client.GetStream(), Encoding.UTF8))
                {
                    while (await reader.ReadLineAsync(ct) is { } line)
                        if (config.Protocol.Parse(line, DeviceId, Time.GetUtcNow()) is { } scan) Raise(scan);
                }
                if (ct.IsCancellationRequested) break;
                Fault(new IOException("connection closed by peer"));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Fault(ex); }
            if (config.ReconnectDelayMs <= 0) break;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(config.ReconnectDelayMs, ct);
                    client = await OpenAsync(ct);
                    SetState(DeviceConnectionState.Connected);
                    break;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception) { /* Fault already raised; retry */ }
            }
        }
    }

    public override async Task DisconnectAsync()
    {
        if (_cts is { } c)
        {
            await c.CancelAsync();
            if (_loop is { } l) await Task.WhenAny(l, Task.Delay(2000));
            c.Dispose();
        }
        _cts = null;
        _loop = null;
        SetState(DeviceConnectionState.Disconnected);
    }

    public override async Task<DeviceTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(config.ConnectTimeoutMs);
            await client.ConnectAsync(config.Host, config.Port, timeout.Token);
            return new(true, $"{config.Host}:{config.Port} reachable");
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }
}
