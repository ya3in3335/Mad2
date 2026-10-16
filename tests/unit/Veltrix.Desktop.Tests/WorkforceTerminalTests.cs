using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Security;
using Veltrix.Desktop.Core.Standalone;
using Veltrix.Desktop.Core.Workforce;
using Veltrix.Desktop.Core.Workforce.Terminals;

namespace Veltrix.Desktop.Tests;

/// <summary>Physical-terminal ingestion, exercised against the SIMULATED terminal only (no real hardware).</summary>
public sealed class WorkforceTerminalTests : IAsyncLifetime
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly WorkforceTests.ManualTime _time = new(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero)); // Monday 09:00 UTC
    private static readonly WorkforceActor Owner = new("owner", LocalRoles.Owner);

    private LocalDb Db() => new(new DbContextOptionsBuilder<LocalDb>().UseSqlite(_conn).Options);
    private WorkforceService Wf() => new(Db, _time, PasswordHasher.MinimumIterations) { TerminalProtector = new XorProtector() };

    public async Task InitializeAsync()
    {
        await _conn.OpenAsync();
        await using var db = Db();
        await db.InitializeAsync();
    }

    public async Task DisposeAsync() => await _conn.DisposeAsync();

    /// <summary>Test-only reversible protector (DPAPI is Windows-only).</summary>
    private sealed class XorProtector : IDataProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.Select(b => (byte)(b ^ 0x5A)).Prepend((byte)1).ToArray();
        public byte[] Unprotect(byte[] ciphertext) => ciphertext.Skip(1).Select(b => (byte)(b ^ 0x5A)).ToArray();
    }

    private async Task<(WorkforceService Wf, WfDevice Device, SimulatedTerminal Sim)> SetupAsync(TerminalInput? input = null, string password = "sim")
    {
        var wf = Wf();
        var d = await wf.UpsertTerminalAsync(Owner, null, input ?? new TerminalInput("Gate", TerminalManufacturers.Simulated) { Password = password });
        var sim = new SimulatedTerminal(_time, "SIM-T1", 60, "sim");
        SimulatedTerminals.Register(d.Id, sim);
        return (wf, d, sim);
    }

    private async Task<LocalEmployee> EmployeeAsync(WorkforceService wf, string name, string? terminalUser, Guid deviceId, string? workplace = null)
    {
        var e = await wf.UpsertEmployeeAsync(Owner, null, new EmployeeProfileInput(name, 30000, new DateOnly(2026, 1, 1)) { Workplace = workplace });
        if (terminalUser is not null) await wf.MapTerminalUserAsync(Owner, e.Id, deviceId, terminalUser);
        return e;
    }

    /// <summary>Effective (non-voided) punches of an employee, chronological.</summary>
    private async Task<List<WfAttendanceEvent>> EffectiveAsync(Guid employeeId)
    {
        await using var db = Db();
        var all = await db.WfAttendanceEvents.AsNoTracking().Where(e => e.EmployeeId == employeeId).ToListAsync();
        var voided = all.Where(e => e.Type == PunchTypes.Void).Select(e => e.CompensatesEventId).ToHashSet();
        return all.Where(e => e.Type != PunchTypes.Void && !voided.Contains(e.Id)).OrderBy(e => e.OccurredTicks).ThenBy(e => e.Seq).ToList();
    }

    private static DateTime Local(int day, int h, int m) => new(2026, 10, day, h, m, 0); // terminal wall clock (UTC+1)

    [Fact]
    public async Task Sync_imports_punches_and_is_idempotent()
    {
        var (wf, d, sim) = await SetupAsync();
        var e = await EmployeeAsync(wf, "Amine", "7", d.Id);
        sim.Punch("7", deviceLocalTime: Local(12, 8, 0));
        sim.Punch("7", deviceLocalTime: Local(12, 9, 55));

        var r = await wf.SyncTerminalAsync(d.Id, ConnectorOrigins.Manual);
        Assert.True(r.Ok, r.Error);
        Assert.Equal((2, 2, 0), (r.Received, r.Accepted, r.ToReview));
        var ev = await EffectiveAsync(e.Id);
        Assert.Equal([PunchTypes.In, PunchTypes.Out], ev.Select(x => x.Type));
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 7, 0, 0, TimeSpan.Zero), ev[0].OccurredAtUtc); // 08:00 at UTC+1

        _time.Advance(TimeSpan.FromHours(5));
        sim.InjectDuplicate(sim.Punch("7", deviceLocalTime: Local(12, 13, 0)));
        var r2 = await wf.SyncTerminalAsync(d.Id, ConnectorOrigins.Manual);
        Assert.Equal((1, 1), (r2.Accepted, r2.Duplicates));
        var r3 = await wf.SyncTerminalAsync(d.Id, ConnectorOrigins.Manual); // nothing new
        Assert.Equal(0, r3.Accepted);
        Assert.Equal(3, (await EffectiveAsync(e.Id)).Count);

        // Replaying an already-buffered page (crash before the cursor moved) is harmless.
        var replay = await wf.BufferTerminalEventsAsync(d, null, [new TerminalEvent("1", "7", null, Local(12, 8, 0), 60, VerifyMethods.Finger, VerifyResults.Success, null, new Dictionary<string, string> { ["sim"] = "1" })], null);
        Assert.Equal((0, 1), (replay.New, replay.Duplicates));
    }

    [Fact]
    public async Task Unknown_user_goes_to_review_and_mapping_resolves_all_their_events()
    {
        var (wf, d, sim) = await SetupAsync();
        var e = await EmployeeAsync(wf, "Sara", null, d.Id);
        sim.Punch("42", deviceLocalTime: Local(12, 8, 0));
        sim.Punch("42", deviceLocalTime: Local(12, 9, 0));

        var r = await wf.SyncTerminalAsync(d.Id, ConnectorOrigins.Manual);
        Assert.Equal((0, 2), (r.Accepted, r.ToReview));
        var queue = await wf.ReviewQueueAsync(Owner);
        Assert.All(queue, q => Assert.Equal(ReviewReasons.UnknownUser, q.ReviewReason));

        await wf.ResolveReviewByMappingAsync(Owner, queue[0].Seq, e.Id);
        Assert.Empty(await wf.ReviewQueueAsync(Owner));
        Assert.Equal(2, (await EffectiveAsync(e.Id)).Count);

        sim.Punch("42", deviceLocalTime: Local(12, 9, 50)); // now mapped: automatic
        Assert.Equal(1, (await wf.SyncTerminalAsync(d.Id, ConnectorOrigins.Manual)).Accepted);
    }

    [Fact]
    public async Task Conflicting_duplicate_is_kept_for_review_and_rejection_needs_a_reason()
    {
        var (wf, d, sim) = await SetupAsync();
        var e = await EmployeeAsync(wf, "Karim", "1", d.Id);
        var original = sim.Punch("1", deviceLocalTime: Local(12, 8, 0));
        await wf.SyncTerminalAsync(d.Id, ConnectorOrigins.Manual);
        sim.InjectConflict(original, Local(12, 8, 30));

        var r = await wf.SyncTerminalAsync(d.Id, ConnectorOrigins.Manual);
        Assert.Equal(1, r.ToReview);
        var item = Assert.Single(await wf.ReviewQueueAsync(Owner));
        Assert.Equal(ReviewReasons.Conflict, item.ReviewReason);
        await Assert.ThrowsAnyAsync<Exception>(() => wf.RejectReviewAsync(Owner, item.Seq, ""));
        await wf.RejectReviewAsync(Owner, item.Seq, "re-numbered log");
        Assert.Single(await EffectiveAsync(e.Id));
        Assert.Single(await wf.ReviewQueueAsync(Owner, includeClosed: true)); // kept, never deleted
    }

    [Fact]
    public async Task Offline_terminal_keeps_events_and_next_sync_imports_them()
    {
        var (wf, d, sim) = await SetupAsync();
        var e = await EmployeeAsync(wf, "Nadia", "3", d.Id);
        sim.Punch("3", deviceLocalTime: Local(12, 8, 0));
        sim.Online = false;
        var r = await wf.SyncTerminalAsync(d.Id, ConnectorOrigins.Manual);
        Assert.False(r.Ok);
        Assert.Equal(TerminalErrorKind.Unreachable, r.ErrorKind);
        await using (var db = Db())
        {
            var dev = await db.WfDevices.SingleAsync(x => x.Id == d.Id);
            Assert.Equal(("Error", 1), (dev.LastSyncStatus, dev.ErrorCount));
        }

        sim.Online = true;
        var r2 = await wf.SyncTerminalAsync(d.Id, ConnectorOrigins.Manual);
        Assert.True(r2.Ok);
        Assert.Single(await EffectiveAsync(e.Id));
    }

    [Fact]
    public async Task Clock_drift_within_tolerance_is_corrected_beyond_goes_to_review()
    {
        var (wf, d, sim) = await SetupAsync();
        var e = await EmployeeAsync(wf, "Yacine", "5", d.Id);
        sim.ClockDrift = TimeSpan.FromSeconds(120); // terminal runs 2 min fast
        sim.Punch("5"); // terminal clock 10:02 local = 09:02 UTC device time
        await wf.SyncTerminalAsync(d.Id, ConnectorOrigins.Manual);
        Assert.Equal(_time.GetUtcNow(), Assert.Single(await EffectiveAsync(e.Id)).OccurredAtUtc);

        sim.ClockDrift = TimeSpan.FromMinutes(30);
        _time.Advance(TimeSpan.FromHours(2));
        sim.Punch("5");
        var r = await wf.SyncTerminalAsync(d.Id, ConnectorOrigins.Manual);
        Assert.Equal((0, 1), (r.Accepted, r.ToReview));
        var item = Assert.Single(await wf.ReviewQueueAsync(Owner));
        Assert.Equal(ReviewReasons.ClockDrift, item.ReviewReason);
        Assert.Equal(_time.GetUtcNow(), item.SuggestedUtc);
        await wf.AcceptReviewAsync(Owner, item.Seq, item.SuggestedUtc);
        Assert.Equal(2, (await EffectiveAsync(e.Id)).Count);
    }

    [Fact]
    public async Task Late_out_of_order_event_rederives_directions()
    {
        var (wf, d, sim) = await SetupAsync();
        var e = await EmployeeAsync(wf, "Lina", "9", d.Id);
        sim.Punch("9", deviceLocalTime: Local(12, 9, 0));
        await wf.SyncTerminalAsync(d.Id, ConnectorOrigins.Manual);
        Assert.Equal([PunchTypes.In], (await EffectiveAsync(e.Id)).Select(x => x.Type));

        sim.Punch("9", deviceLocalTime: Local(12, 8, 0)); // delivered late, but earlier
        await wf.SyncTerminalAsync(d.Id, ConnectorOrigins.Manual);
        var ev = await EffectiveAsync(e.Id);
        Assert.Equal([PunchTypes.In, PunchTypes.Out], ev.Select(x => x.Type));
        Assert.Equal(7, ev[0].OccurredAtUtc.Hour);
    }

    [Fact]
    public async Task Overnight_shift_pairs_across_midnight()
    {
        var (wf, d, sim) = await SetupAsync();
        var e = await EmployeeAsync(wf, "Night", "11", d.Id);
        _time.Set(new DateOnly(2026, 10, 13), 6, 0);
        sim.Punch("11", deviceLocalTime: Local(12, 22, 0));
        sim.Punch("11", deviceLocalTime: Local(13, 6, 0));
        var r = await wf.SyncTerminalAsync(d.Id, ConnectorOrigins.Manual);
        Assert.Equal(2, r.Accepted);
        Assert.Equal([PunchTypes.In, PunchTypes.Out], (await EffectiveAsync(e.Id)).Select(x => x.Type));
    }

    [Fact]
    public async Task Failed_verification_is_logged_not_punched_and_branch_mismatch_is_reviewed()
    {
        var (wf, d, sim) = await SetupAsync(new TerminalInput("Oran gate", TerminalManufacturers.Simulated) { Password = "sim", Branch = "Oran" });
        var e = await EmployeeAsync(wf, "Algiers staff", "2", d.Id, workplace: "Alger");
        sim.Punch("2", deviceLocalTime: Local(12, 8, 0), verifyResult: VerifyResults.Failed);
        sim.Punch("2", deviceLocalTime: Local(12, 8, 1));
        var r = await wf.SyncTerminalAsync(d.Id, ConnectorOrigins.Manual);
        Assert.Equal((0, 1, 1), (r.Accepted, r.ToReview, r.Ignored));
        Assert.Equal(ReviewReasons.BranchMismatch, Assert.Single(await wf.ReviewQueueAsync(Owner)).ReviewReason);
        Assert.Empty(await EffectiveAsync(e.Id));
        Assert.Equal(2, (await wf.TerminalEventsAsync(Owner, d.Id)).Count); // both kept with provenance
    }

    [Fact]
    public async Task Password_is_stored_protected_and_wrong_password_or_serial_is_refused()
    {
        var (wf, d, sim) = await SetupAsync(password: "wrong");
        await using (var db = Db())
        {
            var dev = await db.WfDevices.SingleAsync(x => x.Id == d.Id);
            Assert.NotNull(dev.SecretProtected);
            Assert.DoesNotContain("wrong", dev.SecretProtected!, StringComparison.Ordinal);
        }
        Assert.Equal(TerminalErrorKind.AuthFailed, (await wf.SyncTerminalAsync(d.Id, ConnectorOrigins.Manual)).ErrorKind);

        await wf.UpsertTerminalAsync(Owner, d.Id, new TerminalInput("Gate", TerminalManufacturers.Simulated) { Password = "sim" });
        Assert.True((await wf.SyncTerminalAsync(d.Id, ConnectorOrigins.Manual)).Ok); // serial SIM-T1 pinned
        sim.SerialNumber = "SIM-OTHER"; // swapped device on the same address
        var r = await wf.SyncTerminalAsync(d.Id, ConnectorOrigins.Manual);
        Assert.Equal(TerminalErrorKind.AuthFailed, r.ErrorKind);
    }

    [Fact]
    public async Task Removed_terminal_keeps_history_and_is_no_longer_polled()
    {
        var (wf, d, sim) = await SetupAsync();
        await EmployeeAsync(wf, "Old", "4", d.Id);
        sim.Punch("4", deviceLocalTime: Local(12, 8, 0));
        await wf.SyncTerminalAsync(d.Id, ConnectorOrigins.Manual);
        Assert.True(await wf.RemoveDeviceAsync(Owner, d.Id));
        Assert.Empty(await wf.ActiveTerminalsAsync());
        Assert.Single(await wf.TerminalEventsAsync(Owner, d.Id));
        Assert.NotEmpty(await wf.ImportBatchesAsync(Owner, d.Id));
    }

    [Fact]
    public async Task Connector_loop_syncs_writes_heartbeat_and_backs_off_on_failure()
    {
        var (wf, d, sim) = await SetupAsync();
        var e = await EmployeeAsync(wf, "Loop", "8", d.Id);
        var loop = new TerminalConnectorLoop(new TerminalSyncCoordinator(wf, new InProcessIngestionLock(), ConnectorOrigins.Service), _time,
            new ConnectorOptions { EnableStreaming = false, Version = "test" });

        sim.Punch("8", deviceLocalTime: Local(12, 8, 0));
        Assert.Equal(1, await loop.TickAsync());
        Assert.Single(await EffectiveAsync(e.Id));
        var hb = await wf.ConnectorHeartbeatAsync();
        Assert.Equal((ConnectorOrigins.Service, 1), (hb!.Origin, hb.Devices));
        Assert.Equal(0, await loop.TickAsync()); // not due before its poll interval

        sim.Online = false;
        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(1, await loop.TickAsync());
        Assert.Equal(1, loop.Failures[d.Id]);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromSeconds(5), loop.NextDue(d.Id));
        _time.Advance(TimeSpan.FromSeconds(5));
        await loop.TickAsync();
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromSeconds(10), loop.NextDue(d.Id)); // exponential backoff
        Assert.Equal(TimeSpan.FromMinutes(5), loop.Backoff(30));

        sim.Online = true;
        sim.Punch("8", deviceLocalTime: Local(12, 10, 0));
        _time.Advance(TimeSpan.FromSeconds(10));
        await loop.TickAsync();
        Assert.Equal(0, loop.Failures[d.Id]);
        Assert.Equal(2, (await EffectiveAsync(e.Id)).Count);

        // The app defers to a live service.
        var app = new TerminalConnectorLoop(new TerminalSyncCoordinator(wf, new InProcessIngestionLock(), ConnectorOrigins.App), _time,
            new ConnectorOptions { Origin = ConnectorOrigins.App, DeferToServiceWhenFresherThan = TimeSpan.FromMinutes(2) });
        Assert.Equal(0, await app.TickAsync());
    }

    [Fact]
    public async Task Single_writer_lock_returns_busy_while_held()
    {
        var (wf, d, _) = await SetupAsync();
        var gate = new InProcessIngestionLock();
        var coordinator = new TerminalSyncCoordinator(wf, gate, ConnectorOrigins.App) { LockWait = TimeSpan.FromMilliseconds(50) };
        await using (await gate.TryAcquireAsync(TimeSpan.Zero))
            Assert.Equal("wf.term.busy", (await coordinator.SyncAsync(d.Id)).Error);
        Assert.True((await coordinator.SyncAsync(d.Id)).Ok);
    }

    [Fact]
    public async Task ZKTeco_reports_sdk_missing_instead_of_pretending()
    {
        var wf = Wf();
        var d = await wf.UpsertTerminalAsync(Owner, null, new TerminalInput("ZK", TerminalManufacturers.ZKTeco) { Host = "192.168.1.201", Port = 4370 });
        var r = await wf.SyncTerminalAsync(d.Id, ConnectorOrigins.Manual);
        Assert.Equal(TerminalErrorKind.Unsupported, r.ErrorKind);
        Assert.Contains("SDK", r.Error, StringComparison.Ordinal);
    }
}
