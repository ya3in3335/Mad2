using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Standalone;
using Veltrix.Desktop.Core.Workforce;
using Veltrix.Desktop.Core.Workforce.Devices;

namespace Veltrix.Desktop.Tests;

/// <summary>Workforce hardware framework and access control: virtual terminal, wedge / serial / TCP readers, persisted queue, access decisions.</summary>
public sealed class WorkforceDeviceTests : IAsyncLifetime
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly WorkforceTests.ManualTime _time = new(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero)); // Monday 09:00
    private static readonly WorkforceActor Owner = new("owner", LocalRoles.Owner);

    private LocalDb Db() => new(new DbContextOptionsBuilder<LocalDb>().UseSqlite(_conn).Options);
    private WorkforceService Wf() => new(Db, _time, PasswordHasher.MinimumIterations);

    public async Task InitializeAsync()
    {
        await _conn.OpenAsync();
        await using var db = Db();
        await db.InitializeAsync();
    }

    public async Task DisposeAsync() => await _conn.DisposeAsync();

    private async Task<LocalEmployee> EmployeeAsync(WorkforceService wf, string name) =>
        await wf.UpsertEmployeeAsync(Owner, null, new EmployeeProfileInput(name, 30000, new DateOnly(2026, 1, 1)));

    private async Task<int> EventCountAsync()
    {
        await using var db = Db();
        return await db.WfAttendanceEvents.CountAsync();
    }

    // ------------------------------------------------------------------ access control

    [Fact]
    public async Task Access_decision_with_schedule_anti_passback_and_emergency_unlock()
    {
        var wf = Wf();
        var e = await EmployeeAsync(wf, "Door user");
        await wf.AssignCardAsync(Owner, e.Id, "CAFE0001");
        var relayCfg = await wf.UpsertDeviceAsync(Owner, null, new DeviceInput("Relay", DeviceKinds.Virtual));
        var relay = new VirtualTerminal(relayCfg.Id, _time);
        wf.RegisterController(relay);
        var zone = await wf.UpsertZoneAsync(Owner, null, "Warehouse", AntiPassbackModes.Hard);
        var entry = await wf.UpsertDoorAsync(Owner, null, "Entry", zone.Id, "In", relayCfg.Id);
        var exit = await wf.UpsertDoorAsync(Owner, null, "Exit", zone.Id, "Out", relayCfg.Id);
        await wf.UpsertAccessGroupAsync(Owner, null, "Staff", [entry.Id, exit.Id], [new AccessWindow(1, "08:00", "18:00")], memberIds: [e.Id]);

        var r1 = await wf.RequestAccessAsync(entry.Id, CredentialKinds.Card, "CAFE0001");
        Assert.Equal((true, AccessReasons.Granted), (r1.Decision.Granted, r1.Decision.Reason));
        Assert.Contains(entry.Id, relay.UnlockedDoors);
        var r2 = await wf.RequestAccessAsync(entry.Id, CredentialKinds.Card, "CAFE0001");
        Assert.Equal((false, AccessReasons.AntiPassback), (r2.Decision.Granted, r2.Decision.Reason));
        Assert.True((await wf.RequestAccessAsync(exit.Id, CredentialKinds.Card, "CAFE0001")).Decision.Granted);
        Assert.Equal(AccessReasons.UnknownCredential, (await wf.RequestAccessAsync(entry.Id, CredentialKinds.Card, "DEADBEEF")).Decision.Reason);
        _time.Set(new DateOnly(2026, 10, 12), 19, 0);
        Assert.Equal(AccessReasons.OutsideSchedule, (await wf.RequestAccessAsync(entry.Id, CredentialKinds.Card, "CAFE0001")).Decision.Reason);
        _time.Set(new DateOnly(2026, 10, 13), 9, 0); // Tuesday: no window
        Assert.Equal(AccessReasons.OutsideSchedule, (await wf.RequestAccessAsync(entry.Id, CredentialKinds.Card, "CAFE0001")).Decision.Reason);

        // Soft anti-passback: violation is logged but access granted.
        await wf.UpsertZoneAsync(Owner, zone.Id, "Warehouse", AntiPassbackModes.Soft);
        _time.Set(new DateOnly(2026, 10, 19), 9, 0);
        await wf.RequestAccessAsync(entry.Id, CredentialKinds.Card, "CAFE0001");
        var soft = await wf.RequestAccessAsync(entry.Id, CredentialKinds.Card, "CAFE0001");
        Assert.Equal((true, AccessReasons.GrantedSoftAntiPassback, true), (soft.Decision.Granted, soft.Decision.Reason, soft.Decision.AntiPassbackViolation));

        // Holidays close the door unless the group allows them.
        await wf.AddHolidayAsync(Owner, new DateOnly(2026, 10, 26), "Closed");
        _time.Set(new DateOnly(2026, 10, 26), 9, 0);
        Assert.Equal(AccessReasons.Holiday, (await wf.RequestAccessAsync(exit.Id, CredentialKinds.Card, "CAFE0001")).Decision.Reason);

        // Suspended employees are denied.
        await wf.UpsertEmployeeAsync(Owner, e.Id, new EmployeeProfileInput("Door user", 30000, new DateOnly(2026, 1, 1)) { Status = EmployeeStatuses.Suspended });
        _time.Set(new DateOnly(2026, 11, 2), 9, 0);
        Assert.Equal(AccessReasons.EmployeeInactive, (await wf.RequestAccessAsync(exit.Id, CredentialKinds.Card, "CAFE0001")).Decision.Reason);

        var denials = await new WorkforceReports(wf, Db).AccessDenialsAsync(Owner, DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        Assert.Equal(6, denials.Count);
        Assert.DoesNotContain(denials, d => d.Credential.Contains("CAFE0001")); // credentials are masked in logs
        Assert.Contains("Reason", WorkforceReports.ToCsv(denials));

        // Emergency: unlock everything; normal relock is then ignored. There is no lock command at all.
        var supervisor = new WorkforceActor("sup", LocalRoles.AttendanceSupervisor);
        Assert.Equal(1, await wf.EmergencyUnlockAllAsync(supervisor));
        Assert.True(relay.EmergencyActive);
        await relay.RelockAsync(entry.Id);
        Assert.Contains(entry.Id, relay.UnlockedDoors);
        Assert.Equal(["EmergencyUnlockAllAsync", "RelockAsync", "UnlockAsync", "get_DeviceId"], typeof(IAccessController).GetMethods().Select(m => m.Name).Order(StringComparer.Ordinal));
        Assert.Contains(await wf.AccessLogsAsync(Owner, DateTimeOffset.MinValue, DateTimeOffset.MaxValue), l => l.Reason == AccessReasons.EmergencyUnlock && l.Actor == "sup");
    }

    [Fact]
    public void Weekly_schedule_windows_can_cross_midnight()
    {
        IReadOnlyList<AccessWindow> night = [new AccessWindow(5, "22:00", "06:00")]; // Friday night
        Assert.True(AccessEngine.InSchedule(night, new DateTime(2026, 10, 16, 23, 0, 0)));
        Assert.True(AccessEngine.InSchedule(night, new DateTime(2026, 10, 17, 3, 0, 0))); // Saturday 03:00
        Assert.False(AccessEngine.InSchedule(night, new DateTime(2026, 10, 17, 7, 0, 0)));
        Assert.False(AccessEngine.InSchedule(night, new DateTime(2026, 10, 15, 23, 0, 0)));
        Assert.True(AccessEngine.InSchedule([], new DateTime(2026, 10, 15, 23, 0, 0)));
        Assert.Equal("****0001", AccessEngine.Mask("CAFE0001"));
    }

    // ------------------------------------------------------------------ virtual terminal, queue, idempotency

    [Fact]
    public async Task Virtual_terminal_disconnect_reconnect_and_idempotent_persisted_queue()
    {
        var wf = Wf();
        var e = await EmployeeAsync(wf, "Finger");
        var cfg = await wf.UpsertDeviceAsync(Owner, null, new DeviceInput("Lobby terminal", DeviceKinds.Virtual));
        await wf.MapTerminalUserAsync(Owner, e.Id, cfg.Id, "17", biometricEnrolled: true);
        await wf.AssignCardAsync(Owner, e.Id, "0A0B0C0D");
        await using (var db = Db())
        {
            var creds = await db.WfCredentials.AsNoTracking().Where(c => c.EmployeeId == e.Id).ToListAsync();
            Assert.Contains(creds, c => c.Kind == CredentialKinds.BiometricEnrollment && c.DeviceId == cfg.Id && c.Value == "enrolled"); // flag only, no template
        }

        await using var hub = new DeviceHub(wf);
        var vt = new VirtualTerminal(cfg.Id, _time);
        await hub.AttachAsync(vt);
        vt.SimulateFingerprintMatch("17");
        await hub.DrainAsync();
        Assert.Equal(1, await EventCountAsync());

        vt.SimulateDisconnect();
        await hub.DrainAsync();
        _time.Advance(TimeSpan.FromHours(8));
        var offline = vt.SimulateCard("0a0b0c0d");
        Assert.Equal(1, vt.OfflineCount);
        Assert.Equal(1, await EventCountAsync()); // nothing arrives while disconnected
        await vt.ConnectAsync();
        await hub.DrainAsync();
        Assert.Equal(2, await EventCountAsync());
        vt.Replay(offline); // terminal re-pushes its log
        await hub.DrainAsync();
        Assert.Equal(2, await EventCountAsync());

        await using (var db = Db())
        {
            var dev = await db.WfDevices.AsNoTracking().SingleAsync();
            Assert.Equal((1, "link lost"), (dev.ErrorCount, dev.LastError));
            Assert.NotNull(dev.LastSeenAt);
            var events = await db.WfAttendanceEvents.AsNoTracking().OrderBy(x => x.Seq).ToListAsync();
            Assert.Equal([(PunchTypes.In, PunchSources.Terminal), (PunchTypes.Out, PunchSources.Card)], events.Select(x => (x.Type, x.Source)));
            Assert.Equal(2, await db.WfDeviceInbox.CountAsync(i => i.Status == "Processed"));
        }
        var activity = await new WorkforceReports(wf, Db).DeviceActivityAsync(Owner, DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        Assert.Equal((2, 2, 1), (activity.Single().Received, activity.Single().Processed, activity.Single().ErrorCount));
    }

    [Fact]
    public async Task Queued_events_survive_a_restart_and_are_processed_exactly_once()
    {
        var wf = Wf();
        var e = await EmployeeAsync(wf, "Queue");
        await wf.AssignCardAsync(Owner, e.Id, "ABCDEF01");
        var dev = await wf.UpsertDeviceAsync(Owner, null, new DeviceInput("Reader", DeviceKinds.Serial, Direction: PunchTypes.In));
        var scan = new DeviceScan(dev.Id, "EVT-1", CredentialKinds.Card, "ABCDEF01", null, _time.GetUtcNow());
        Assert.True(await wf.EnqueueDeviceEventAsync(scan));
        Assert.False(await wf.EnqueueDeviceEventAsync(scan)); // same device event id
        // "Power failure": the process stops before processing. A fresh service picks the queue up.
        var restarted = Wf();
        Assert.Equal(new DeviceProcessingResult(1, 0), await restarted.ProcessPendingDeviceEventsAsync());
        Assert.Equal(new DeviceProcessingResult(0, 0), await restarted.ProcessPendingDeviceEventsAsync());
        Assert.Equal(1, await EventCountAsync());

        // Crash between the punch and marking the queue item: re-processing must not double the punch.
        var scan2 = scan with { DeviceEventId = "EVT-2", OccurredAtUtc = _time.GetUtcNow().AddHours(4) };
        await wf.PunchByCredentialAsync(scan2.CredentialKind, scan2.CredentialValue, PunchTypes.Out, PunchSources.Card, dev.Id, "EVT-2", scan2.OccurredAtUtc);
        await wf.EnqueueDeviceEventAsync(scan2);
        Assert.Equal(new DeviceProcessingResult(1, 0), await wf.ProcessPendingDeviceEventsAsync());
        Assert.Equal(2, await EventCountAsync());

        await wf.EnqueueDeviceEventAsync(scan with { DeviceEventId = "EVT-3", CredentialValue = "00000000" });
        Assert.Equal(new DeviceProcessingResult(0, 1), await wf.ProcessPendingDeviceEventsAsync());
        Assert.Contains("unknown_credential", (await wf.DeviceInboxAsync(Owner, "Rejected")).Single().Result);
        Assert.True((await wf.VerifyChainAsync(Owner)).IsValid);
    }

    [Fact]
    public async Task Door_bound_device_routes_scans_to_access_control()
    {
        var wf = Wf();
        var e = await EmployeeAsync(wf, "Gate");
        await wf.AssignCardAsync(Owner, e.Id, "11110000");
        var zone = await wf.UpsertZoneAsync(Owner, null, "Site");
        var dev = await wf.UpsertDeviceAsync(Owner, null, new DeviceInput("Gate reader", DeviceKinds.Virtual));
        var door = await wf.UpsertDoorAsync(Owner, null, "Gate", zone.Id, "In", dev.Id);
        await wf.UpsertDeviceAsync(Owner, dev.Id, new DeviceInput("Gate reader", DeviceKinds.Virtual, DoorId: door.Id));
        await wf.UpsertAccessGroupAsync(Owner, null, "All", [door.Id], [], memberIds: [e.Id]);
        await using var hub = new DeviceHub(wf);
        var vt = new VirtualTerminal(dev.Id, _time);
        await hub.AttachAsync(vt);
        vt.SimulateCard("11110000");
        vt.SimulateCard("99990000");
        await hub.DrainAsync();
        Assert.Contains(door.Id, vt.UnlockedDoors);
        var logs = await wf.AccessLogsAsync(Owner, DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        Assert.Equal([true, false], logs.Select(l => l.Granted));
        Assert.Equal(0, await EventCountAsync());
    }

    // ------------------------------------------------------------------ real adapters

    [Fact]
    public async Task Keyboard_wedge_reader_turns_fast_bursts_into_scans()
    {
        var reader = new KeyboardWedgeCardReader(Guid.NewGuid(), new KeyboardWedgeConfig(), _time);
        var scans = new List<DeviceScan>();
        reader.Scanned += (_, s) => scans.Add(s);
        Assert.Null(reader.Feed("0A1B2C3D\r")); // not connected
        await reader.ConnectAsync();
        Assert.Equal("0A1B2C3D", reader.Feed("0A1B2C3D\r")!.CredentialValue);
        Assert.Equal(CredentialKinds.Qr, reader.Feed("VQR:VQ1.abc123\n")!.CredentialKind);
        reader.Feed("12");
        _time.Advance(TimeSpan.FromMilliseconds(500)); // human typing speed: buffer discarded
        Assert.Null(reader.Feed("34\r"));
        Assert.Equal(2, scans.Count);
    }

    [Fact]
    public async Task Serial_line_reader_parses_newline_delimited_uids()
    {
        var transport = new FakeLineTransport();
        var reader = new SerialLineReader(Guid.NewGuid(), new SerialLineConfig { PortName = "COM9" }, _time, _ => transport);
        var scans = new ConcurrentQueue<DeviceScan>();
        reader.Scanned += (_, s) => scans.Enqueue(s);
        await reader.ConnectAsync();
        transport.Lines.Add("04A2B3C4");
        transport.Lines.Add("DEADBEEF,out,SER-9");
        transport.Lines.CompleteAdding();
        await reader.ReadLoop!;
        Assert.Equal(["04A2B3C4", "DEADBEEF"], scans.Select(s => s.CredentialValue));
        Assert.Equal((PunchTypes.Out, "SER-9"), (scans.Last().PunchType, scans.Last().DeviceEventId));
        Assert.Equal(DeviceConnectionState.Disconnected, reader.State);
        Assert.True((await reader.TestConnectionAsync()).Ok);
    }

    [Fact]
    public async Task Tcp_line_terminal_reads_records_and_reports_link_loss()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var terminal = new TcpLineTerminal(Guid.NewGuid(), new TcpLineConfig { Port = port, ReconnectDelayMs = 0 }, TimeProvider.System);
        var scans = new ConcurrentQueue<DeviceScan>();
        var faulted = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        terminal.Scanned += (_, s) => scans.Enqueue(s);
        terminal.Faulted += (_, ex) => faulted.TrySetResult(ex);
        Assert.True((await terminal.TestConnectionAsync()).Ok);
        (await listener.AcceptTcpClientAsync()).Dispose(); // the test probe

        var connect = terminal.ConnectAsync();
        using (var server = await listener.AcceptTcpClientAsync())
        {
            await connect;
            await using var w = new StreamWriter(server.GetStream()) { AutoFlush = true };
            await w.WriteAsync("04A2B3C4,in,EVT1,2026-10-12T08:00:00Z\nBADBEEF1;o;EVT2\n");
        }
        var ex = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("closed", ex.Message);
        Assert.Equal(DeviceConnectionState.Faulted, terminal.State);
        Assert.Equal([("04A2B3C4", PunchTypes.In, "EVT1"), ("BADBEEF1", PunchTypes.Out, "EVT2")], scans.Select(s => (s.CredentialValue, s.PunchType!, s.DeviceEventId)));
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero), scans.First().OccurredAtUtc);
        await terminal.DisposeAsync();
        listener.Stop();
        Assert.False((await new TcpLineTerminal(Guid.NewGuid(), new TcpLineConfig { Port = port, ConnectTimeoutMs = 500 }, TimeProvider.System).TestConnectionAsync()).Ok);
    }

    [Fact]
    public async Task Vendor_terminals_are_declared_but_not_faked()
    {
        var wf = Wf();
        var cfg = await wf.UpsertDeviceAsync(Owner, null, new DeviceInput("ZK gate", DeviceKinds.Vendor, "{\"vendor\":\"ZKTeco\"}"));
        var ex = Assert.Throws<NotSupportedException>(() => DeviceFactory.Create(cfg, _time));
        Assert.Contains("SDK", ex.Message);
        await using var hub = new DeviceHub(wf);
        var test = await hub.TestAsync(cfg);
        Assert.False(test.Ok);
        Assert.Contains("NOT implemented", test.Message);
        var vt = await hub.TestAsync(await wf.UpsertDeviceAsync(Owner, null, new DeviceInput("V", DeviceKinds.Virtual)));
        Assert.False(vt.Ok); // a virtual terminal that is not connected says so
    }

    private sealed class FakeLineTransport : ILineTransport
    {
        public BlockingCollection<string> Lines { get; } = [];
        public void Open() { }
        public string? ReadLine() => Lines.TryTake(out var l, Timeout.Infinite) ? l : null;
        public void Dispose() => Lines.CompleteAdding();
    }
}
