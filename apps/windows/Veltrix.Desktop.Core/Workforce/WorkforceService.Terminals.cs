using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Security;
using Veltrix.Desktop.Core.Workforce.Terminals;

namespace Veltrix.Desktop.Core.Workforce;

/// <summary>Registration of a physical terminal. <see cref="Password"/>: null keeps the stored one, "" clears it.</summary>
public sealed record TerminalInput(string Name, string Manufacturer)
{
    public string? Host { get; init; }
    public int? Port { get; init; }
    public string? Protocol { get; init; }
    public string? UserName { get; init; }
    public string? Password { get; init; }
    public string? Branch { get; init; }
    public string? Location { get; init; }
    public int? TimeZoneOffsetMinutes { get; init; }
    public int PollIntervalSeconds { get; init; } = 60;
    public bool ApplyDriftCorrection { get; init; } = true;
    public int DriftToleranceSeconds { get; init; } = 300;
    public string Direction { get; init; } = "Auto";
    public bool IsEnabled { get; init; } = true;
    public string? CertificateSha256 { get; init; }
    public string? RemoteDeviceId { get; init; }
}

public sealed record TerminalSyncResult(Guid DeviceId, bool Ok, string? Error, TerminalErrorKind? ErrorKind, int Received, int Accepted, int Duplicates, int ToReview,
    int Ignored, int? DriftSeconds, IReadOnlyList<DateOnly> AffectedDays, Guid? BatchId)
{
    public static TerminalSyncResult Busy(Guid id) => new(id, false, "wf.term.busy", null, 0, 0, 0, 0, 0, null, [], null);
}

public sealed record BufferResult(int Received, int New, int Duplicates);

public sealed record DiagnosticStep(string Step, bool Ok, string Message);

/// <summary>Heartbeat written by the connector service (or the app when it ingests itself).</summary>
public sealed record ConnectorHeartbeat(DateTimeOffset At, string Origin, string Machine, int ProcessId, string Version, int Devices, int Faulted);

public static class ReviewReasons
{
    public const string UnknownUser = "unknownUser";
    public const string ClockDrift = "clockDrift";
    public const string Future = "future";
    public const string TooOld = "tooOld";
    public const string Conflict = "conflict";
    public const string BranchMismatch = "branchMismatch";
    public const string Unverified = "unverified";
    public const string EmployeeInactive = "employeeInactive";
    public const string Rejected = "rejected";
}

public sealed partial class WorkforceService
{
    public const string ConnectorHeartbeatKey = "connector.heartbeat";
    /// <summary>Reason stamped on punches whose direction was inferred (toggle): they are re-derived when an earlier event arrives late.</summary>
    public const string AutoDirectionReason = "terminal:auto-direction";
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxBackdate = TimeSpan.FromDays(62);
    public const int ImportPageSize = 100;
    public const int MaxPagesPerSync = 50;

    /// <summary>Protector of terminal passwords (DPAPI LocalMachine on Windows). Required to store or use a terminal password.</summary>
    public IDataProtector? TerminalProtector { get; set; }
    private ITerminalAdapterFactory? _terminalFactory;
    public ITerminalAdapterFactory TerminalFactory { get => _terminalFactory ??= new TerminalAdapterFactory(time); set => _terminalFactory = value; }

    // ------------------------------------------------------------------ registry

    public Task<WfDevice> UpsertTerminalAsync(WorkforceActor actor, Guid? id, TerminalInput input, CancellationToken ct = default) => InTransactionAsync(async (db, now) =>
    {
        AuthorizeGlobal(actor, WorkforcePermissions.DevicesManage);
        Require(!string.IsNullOrWhiteSpace(input.Name), "wf.err.nameRequired");
        Require(TerminalManufacturers.All.Contains(input.Manufacturer), "wf.err.manufacturer");
        Require(input.Direction is "Auto" or PunchTypes.In or PunchTypes.Out, "wf.err.direction");
        Require(input.Port is null or (>= 1 and <= 65535), "wf.err.deviceConfig");
        Require(input.PollIntervalSeconds is >= 10 and <= 86400, "wf.err.pollInterval");
        Require(input.DriftToleranceSeconds is >= 0 and <= 86400, "wf.err.deviceConfig");
        Require(input.TimeZoneOffsetMinutes is null or (>= -840 and <= 840), "wf.err.deviceConfig");
        Require(input.Manufacturer == TerminalManufacturers.Simulated || !string.IsNullOrWhiteSpace(input.Host), "wf.err.hostRequired");
        var d = id is { } x ? await db.WfDevices.FindAsync([x], ct) ?? throw new InvalidOperationException("wf.err.unknown") : null;
        Require(d is null || !d.IsRemoved, "wf.err.deviceRemoved");
        if (d is null) db.WfDevices.Add(d = new WfDevice { Id = Guid.NewGuid(), CreatedAt = now, LastSyncStatus = "Never" });
        if (d.Manufacturer is { } old && old != input.Manufacturer) (d.SyncCursor, d.SerialNumber) = (null, null); // another terminal type: new identity
        d.Kind = DeviceKinds.Terminal;
        (d.Name, d.Manufacturer, d.Host, d.Port, d.Protocol, d.UserName) = (input.Name.Trim(), input.Manufacturer, O(input.Host), input.Port, O(input.Protocol), O(input.UserName));
        (d.Branch, d.Location, d.TimeZoneOffsetMinutes, d.PollIntervalSeconds) = (O(input.Branch), O(input.Location), input.TimeZoneOffsetMinutes, input.PollIntervalSeconds);
        (d.ApplyDriftCorrection, d.DriftToleranceSeconds, d.Direction, d.IsEnabled) = (input.ApplyDriftCorrection, input.DriftToleranceSeconds, input.Direction, input.IsEnabled);
        d.ConfigJson = JsonSerializer.Serialize(new TerminalOptions { CertificateSha256 = O(input.CertificateSha256), RemoteDeviceId = O(input.RemoteDeviceId) }, Json);
        if (input.Password is { } pw)
        {
            if (pw.Length == 0) d.SecretProtected = null;
            else
            {
                Require(TerminalProtector is not null, "wf.err.noProtector");
                d.SecretProtected = TerminalSecrets.Protect(TerminalProtector!, pw);
            }
        }
        db.WfDeviceLogs.Add(new WfDeviceLog { DeviceId = d.Id, At = now, Level = "Info", Message = "Configuration saved by " + actor.UserName });
        return d;
    }, ct);

    /// <summary>Removes a device from operation but keeps it and its whole history (events, batches, logs) for audit.</summary>
    public Task<bool> RemoveDeviceAsync(WorkforceActor actor, Guid id, CancellationToken ct = default) => InTransactionAsync(async (db, now) =>
    {
        AuthorizeGlobal(actor, WorkforcePermissions.DevicesManage);
        var d = await db.WfDevices.FindAsync([id], ct) ?? throw new InvalidOperationException("wf.err.unknownDevice");
        if (d.IsRemoved) return false;
        (d.IsRemoved, d.RemovedAt, d.IsEnabled) = (true, now, false);
        db.WfDeviceLogs.Add(new WfDeviceLog { DeviceId = d.Id, At = now, Level = "Warning", Message = "Removed by " + actor.UserName + " (history kept)" });
        return true;
    }, ct);

    public Task<bool> SetDeviceEnabledAsync(WorkforceActor actor, Guid id, bool enabled, CancellationToken ct = default) => InTransactionAsync(async (db, now) =>
    {
        AuthorizeGlobal(actor, WorkforcePermissions.DevicesManage);
        var d = await db.WfDevices.FindAsync([id], ct) ?? throw new InvalidOperationException("wf.err.unknownDevice");
        Require(!d.IsRemoved, "wf.err.deviceRemoved");
        d.IsEnabled = enabled;
        db.WfDeviceLogs.Add(new WfDeviceLog { DeviceId = d.Id, At = now, Level = "Info", Message = (enabled ? "Enabled by " : "Disabled by ") + actor.UserName });
        return enabled;
    }, ct);

    /// <summary>Terminals to poll (enabled, not removed).</summary>
    public async Task<IReadOnlyList<WfDevice>> ActiveTerminalsAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory();
        return await db.WfDevices.AsNoTracking().Where(d => d.Kind == DeviceKinds.Terminal && d.IsEnabled && !d.IsRemoved).ToListAsync(ct);
    }

    /// <summary>Adapter of a registered terminal with its decrypted credential (streaming, diagnostics).</summary>
    public async Task<ITerminalAdapter> OpenTerminalAsync(Guid deviceId, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var d = await db.WfDevices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == deviceId, ct) ?? throw new InvalidOperationException("wf.err.unknownDevice");
        return TerminalFactory.Create(d, ConnectionFor(d));
    }

    internal TerminalConnection ConnectionFor(WfDevice d)
    {
        var options = TerminalAdapterFactory.Options(d);
        var password = TerminalProtector is { } p ? TerminalSecrets.Unprotect(p, d.SecretProtected) : null;
        if (d.SecretProtected is not null && password is null && d.Manufacturer != TerminalManufacturers.Simulated)
            throw new TerminalException(TerminalErrorKind.AuthFailed, "Stored terminal credential cannot be decrypted on this PC (re-enter the password).");
        return new TerminalConnection(d.Host, d.Port, d.Protocol, d.UserName, password, options.CertificateSha256, options.RemoteDeviceId);
    }

    private async Task LogDeviceAsync(Guid deviceId, string level, string message, CancellationToken ct)
    {
        await using var db = dbFactory();
        db.WfDeviceLogs.Add(new WfDeviceLog { DeviceId = deviceId, At = time.GetUtcNow(), Level = level, Message = message.Length > 500 ? message[..500] : message });
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ sync

    /// <summary>"Sync now" from the UI (permission checked). Ingestion coordination is the caller's job (see <see cref="TerminalSyncCoordinator"/>).</summary>
    public Task<TerminalSyncResult> SyncTerminalAsync(WorkforceActor actor, Guid deviceId, string origin = "Manual", CancellationToken ct = default)
    {
        AuthorizeGlobal(actor, WorkforcePermissions.DevicesManage);
        return SyncTerminalAsync(deviceId, origin, ct);
    }

    /// <summary>
    /// Authenticates the registered terminal (stored credential + pinned serial), measures the clock drift, imports every new page into the
    /// persisted queue (with the cursor, atomically: offline-safe), then ingests the buffered events.
    /// </summary>
    public async Task<TerminalSyncResult> SyncTerminalAsync(Guid deviceId, string origin, CancellationToken ct = default)
    {
        WfDevice device;
        await using (var db = dbFactory())
            device = await db.WfDevices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, ct) ?? throw new InvalidOperationException("wf.err.unknownDevice");
        Require(device.Kind == DeviceKinds.Terminal && !device.IsRemoved, "wf.err.notTerminal");
        var batch = new WfImportBatch { Id = Guid.NewGuid(), DeviceId = deviceId, Origin = origin, StartedAt = time.GetUtcNow(), CursorBefore = device.SyncCursor };
        await using (var db = dbFactory()) { db.WfImportBatches.Add(batch); await db.SaveChangesAsync(ct); }
        int received = 0, duplicates = 0;
        int? drift = null;
        try
        {
            await using var adapter = TerminalFactory.Create(device, ConnectionFor(device));
            await adapter.ValidateConnectionAsync(ct);
            var info = await adapter.GetDeviceInfoAsync(ct);
            if (device.SerialNumber is { Length: > 0 } pinned && info.SerialNumber is { Length: > 0 } reported && !string.Equals(pinned, reported, StringComparison.OrdinalIgnoreCase))
                throw new TerminalException(TerminalErrorKind.AuthFailed, $"Terminal identity mismatch: registered serial {pinned}, device reports {reported}.");
            if (info.DeviceClock is { } clock) drift = (int)Math.Round((clock - time.GetUtcNow()).TotalSeconds);
            await UpdateDeviceAsync(deviceId, d =>
            {
                d.SerialNumber ??= info.SerialNumber;
                (d.Model, d.Firmware, d.LastConnectionAt, d.LastSeenAt) = (info.Model ?? d.Model, info.Firmware ?? d.Firmware, time.GetUtcNow(), time.GetUtcNow());
                d.Capabilities = adapter.Capabilities.ToString();
                if (drift is not null) (d.ClockDriftSeconds, d.ClockDriftMeasuredAt) = (drift, time.GetUtcNow());
            }, ct);
            if (drift is { } dr && Math.Abs(dr) > device.DriftToleranceSeconds)
                await LogDeviceAsync(deviceId, "Warning", $"Clock drift {dr}s exceeds tolerance {device.DriftToleranceSeconds}s: imported events go to review.", ct);
            device.ClockDriftSeconds = drift ?? device.ClockDriftSeconds;

            var cursor = device.SyncCursor;
            for (var page = 0; page < MaxPagesPerSync; page++)
            {
                var p = await adapter.ImportEventsAsync(cursor, null, ImportPageSize, ct);
                var b = await BufferTerminalEventsAsync(device, batch.Id, p.Events, p.NextCursor, ct);
                (received, duplicates, cursor) = (received + b.Received, duplicates + b.Duplicates, p.NextCursor);
                if (!p.HasMore) break;
            }
            var processed = await ProcessBufferedTerminalEventsAsync(deviceId, ct);
            await UpdateDeviceAsync(deviceId, d => { (d.LastSyncAt, d.LastSyncStatus) = (time.GetUtcNow(), "Ok"); d.ErrorCount = 0; d.LastError = null; }, ct);
            var result = new TerminalSyncResult(deviceId, true, null, null, received, processed.Accepted, duplicates + processed.Duplicates, processed.ToReview,
                processed.Ignored, drift, processed.AffectedDays, batch.Id);
            await FinishBatchAsync(batch.Id, cursor, result, null, ct);
            return result;
        }
        catch (TerminalException ex)
        {
            await UpdateDeviceAsync(deviceId, d => { d.ErrorCount++; d.LastError = ex.Kind + ": " + Trim(ex.Message); d.LastSyncStatus = "Error"; }, ct);
            await LogDeviceAsync(deviceId, "Error", ex.Kind + ": " + ex.Message, ct);
            // Whatever was already buffered is ingested; the rest stays on the terminal (cursor not advanced past it).
            var processed = await ProcessBufferedTerminalEventsAsync(deviceId, ct);
            var result = new TerminalSyncResult(deviceId, false, ex.Message, ex.Kind, received, processed.Accepted, duplicates + processed.Duplicates, processed.ToReview,
                processed.Ignored, drift, processed.AffectedDays, batch.Id);
            await FinishBatchAsync(batch.Id, null, result, ex.Kind + ": " + ex.Message, ct);
            return result;
        }
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;

    private async Task UpdateDeviceAsync(Guid id, Action<WfDevice> change, CancellationToken ct)
    {
        await using var db = dbFactory();
        if (await db.WfDevices.FindAsync([id], ct) is not { } d) return;
        change(d);
        await db.SaveChangesAsync(ct);
    }

    private async Task FinishBatchAsync(Guid id, string? cursorAfter, TerminalSyncResult r, string? error, CancellationToken ct)
    {
        await using var db = dbFactory();
        if (await db.WfImportBatches.FindAsync([id], ct) is not { } b) return;
        (b.FinishedAt, b.CursorAfter, b.Received, b.Accepted, b.Duplicates, b.ToReview, b.Ignored, b.DriftSeconds) =
            (time.GetUtcNow(), cursorAfter ?? b.CursorAfter, r.Received, r.Accepted, r.Duplicates, r.ToReview, r.Ignored, r.DriftSeconds);
        (b.Status, b.Error) = (error is null ? "Ok" : "Error", error is null ? null : Trim(error));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Stores terminal events in the persisted device queue BEFORE ingestion, with their original data and provenance, and advances the cursor in the
    /// same transaction. Idempotent: unique (device, vendor id | deterministic hash). Same vendor id with different data is kept as a conflict for review.
    /// </summary>
    public async Task<BufferResult> BufferTerminalEventsAsync(WfDevice device, Guid? batchId, IReadOnlyList<TerminalEvent> events, string? nextCursor, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var policy = WorkforcePolicy.FromJson(await db.GetValueAsync(WorkforcePolicy.Key, ct));
        var zone = Zone(policy);
        var now = time.GetUtcNow();
        var dev = await db.WfDevices.FirstAsync(d => d.Id == device.Id, ct);
        int fresh = 0, dups = 0;
        var seen = new HashSet<string>();
        foreach (var e in events)
        {
            var key = e.DedupKey(device.Id);
            var hash = e.ContentHash();
            var existing = await db.WfDeviceInbox.AsNoTracking().Where(i => i.DeviceId == device.Id && i.DeviceEventId == key).Select(i => i.ContentHash).ToListAsync(ct);
            string? reviewReason = null;
            if (existing.Count > 0 || !seen.Add(key))
            {
                if (existing.Count == 0 || existing[0] == hash) { dups++; continue; }
                key = key + "#c" + hash[..12]; // conflicting duplicate: kept once, for review
                if (await db.WfDeviceInbox.AnyAsync(i => i.DeviceId == device.Id && i.DeviceEventId == key, ct) || !seen.Add(key)) { dups++; continue; }
                reviewReason = ReviewReasons.Conflict;
            }
            var offset = e.DeviceOffsetMinutes ?? dev.TimeZoneOffsetMinutes ?? (int)zone.GetUtcOffset(DateTime.SpecifyKind(e.DeviceLocalTime, DateTimeKind.Unspecified)).TotalMinutes;
            var deviceUtc = new DateTimeOffset(DateTime.SpecifyKind(e.DeviceLocalTime, DateTimeKind.Unspecified), TimeSpan.FromMinutes(offset)).ToUniversalTime();
            var at = deviceUtc;
            int? applied = null;
            DateTimeOffset? suggested = null;
            if (dev.ClockDriftSeconds is { } drift && drift != 0)
            {
                if (Math.Abs(drift) > dev.DriftToleranceSeconds)
                {
                    reviewReason ??= ReviewReasons.ClockDrift;
                    suggested = deviceUtc.AddSeconds(-drift);
                }
                else if (dev.ApplyDriftCorrection) (at, applied) = (deviceUtc.AddSeconds(-drift), drift);
            }
            db.WfDeviceInbox.Add(new WfDeviceInbox
            {
                DeviceId = device.Id, DeviceEventId = key, BatchId = batchId, VendorEventId = e.VendorEventId, DeviceUserId = O(e.DeviceUserId), CardNo = O(e.CardNo),
                CredentialKind = e.DeviceUserId is { Length: > 0 } ? CredentialKinds.TerminalUser : CredentialKinds.Card,
                CredentialValue = O(e.DeviceUserId) ?? O(e.CardNo) ?? "",
                DeviceLocalTime = e.DeviceLocalTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), DeviceOffsetMinutes = e.DeviceOffsetMinutes,
                Method = e.Method, VerifyResult = e.VerifyResult, PunchType = e.PunchType, ContentHash = hash, DriftAppliedSeconds = applied, SuggestedUtc = suggested,
                RawJson = e.Raw is null ? null : JsonSerializer.Serialize(e.Raw, Json), OccurredAtUtc = at, ReceivedAt = now, Status = InboxStatuses.Buffered,
                ReviewReason = reviewReason,
            });
            fresh++;
        }
        if (nextCursor is not null) dev.SyncCursor = nextCursor;
        if (events.Count > 0) dev.LastSeenAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new BufferResult(events.Count, fresh, dups);
    }

    public sealed record IngestionSummary(int Accepted, int Duplicates, int ToReview, int Ignored, IReadOnlyList<DateOnly> AffectedDays);

    /// <summary>Ingests buffered terminal events (chronologically): validation, employee resolution, shift rules via the attendance engine.</summary>
    public async Task<IngestionSummary> ProcessBufferedTerminalEventsAsync(Guid? deviceId = null, CancellationToken ct = default)
    {
        List<long> seqs;
        await using (var db = dbFactory())
            seqs = (await db.WfDeviceInbox.AsNoTracking().Where(i => i.Status == InboxStatuses.Buffered && (deviceId == null || i.DeviceId == deviceId))
                .Select(i => new { i.Seq, i.OccurredAtUtc }).ToListAsync(ct)).OrderBy(i => i.OccurredAtUtc).ThenBy(i => i.Seq).Select(i => i.Seq).ToList();
        int accepted = 0, dups = 0, review = 0, ignored = 0;
        var days = new SortedSet<DateOnly>();
        foreach (var seq in seqs)
        {
            await using var db = dbFactory();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var row = await db.WfDeviceInbox.FirstAsync(i => i.Seq == seq, ct);
            if (row.Status != InboxStatuses.Buffered) continue; // processed concurrently
            var outcome = await IngestRowAsync(db, row, null, null, days, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            switch (outcome)
            {
                case InboxStatuses.Processed when row.Result?.StartsWith(PunchOutcomes.Duplicate, StringComparison.Ordinal) == true: dups++; break;
                case InboxStatuses.Processed: accepted++; break;
                case InboxStatuses.Review: review++; break;
                default: ignored++; break;
            }
        }
        return new IngestionSummary(accepted, dups, review, ignored, days.ToList());
    }

    /// <summary>One terminal row → punch / review / ignored, inside the caller's transaction. Override values come from an administrator's resolution.</summary>
    private async Task<string> IngestRowAsync(LocalDb db, WfDeviceInbox row, Guid? employeeOverride, DateTimeOffset? timeOverride, SortedSet<DateOnly> days, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        row.ProcessedAt = now;
        string Mark(string status, string result, string? reason = null)
        {
            (row.Status, row.Result) = (status, result);
            if (reason is not null) row.ReviewReason = reason;
            return status;
        }
        var admin = employeeOverride is not null || timeOverride is not null;
        if (!admin)
        {
            if (row.VerifyResult == VerifyResults.Failed) return Mark(InboxStatuses.Ignored, "verifyFailed");
            if (row.DeviceUserId is null && row.CardNo is null) return Mark(InboxStatuses.Ignored, "noIdentity");
            if (row.ReviewReason is { } preset) return Mark(InboxStatuses.Review, preset);
            if (row.OccurredAtUtc > now + FutureTolerance) return Mark(InboxStatuses.Review, ReviewReasons.Future, ReviewReasons.Future);
            if (row.OccurredAtUtc < now - MaxBackdate) return Mark(InboxStatuses.Review, ReviewReasons.TooOld, ReviewReasons.TooOld);
        }
        var device = await db.WfDevices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == row.DeviceId, ct);
        LocalEmployee? employee;
        if (employeeOverride is { } eid) employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eid, ct);
        else
        {
            employee = null;
            if (row.DeviceUserId is { } uid) (employee, _) = await ResolveCredentialAsync(db, CredentialKinds.TerminalUser, uid, row.DeviceId, ct);
            if (employee is null && row.CardNo is { } card) (employee, _) = await ResolveCredentialAsync(db, CredentialKinds.Card, card, row.DeviceId, ct);
        }
        if (employee is null) return Mark(InboxStatuses.Review, ReviewReasons.UnknownUser, ReviewReasons.UnknownUser);
        row.EmployeeId = employee.Id;
        if (!admin)
        {
            if (device?.Branch is { Length: > 0 } branch && employee.Workplace is { Length: > 0 } wp && !string.Equals(branch.Trim(), wp.Trim(), StringComparison.OrdinalIgnoreCase))
                return Mark(InboxStatuses.Review, ReviewReasons.BranchMismatch, ReviewReasons.BranchMismatch);
            if (row.VerifyResult != VerifyResults.Success) return Mark(InboxStatuses.Review, ReviewReasons.Unverified, ReviewReasons.Unverified);
        }
        var at = timeOverride ?? row.OccurredAtUtc;
        var type = row.PunchType ?? (device?.Direction is PunchTypes.In or PunchTypes.Out ? device.Direction : null);
        var r = await PunchCoreAsync(db, new PunchInput(employee.Id, type, PunchSources.Terminal, at, row.DeviceId, row.DeviceEventId,
            Reason: type is null ? AutoDirectionReason : null), admin ? null : "terminal", ct);
        if (r.Outcome == PunchOutcomes.Rejected)
            return Mark(InboxStatuses.Review, r.Outcome + ":" + r.Reason, r.Reason == AccessReasons.EmployeeInactive ? ReviewReasons.EmployeeInactive : r.Reason);
        row.AttendanceEventId = r.Event?.Id;
        if (r.IsRecorded && r.Event is { } ev)
        {
            var policy = WorkforcePolicy.FromJson(await db.GetValueAsync(WorkforcePolicy.Key, ct));
            days.Add(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(ev.OccurredAtUtc, Zone(policy)).DateTime));
            if (type is null) await RederiveDirectionsAfterAsync(db, ev, policy, now, days, ct);
        }
        return Mark(admin ? InboxStatuses.Resolved : InboxStatuses.Processed, r.Outcome + (r.Reason is null ? "" : ":" + r.Reason));
    }

    /// <summary>
    /// Out-of-order safety: a late event can change the In/Out toggle of the automatically-typed punches that follow it. Those are re-derived; a
    /// wrong one is voided (compensating event) and re-appended with the right direction — raw events are never edited. Shift rules / overtime
    /// are always computed on demand from the effective events, so affected days are recomputed (overtime approvals on changed minutes revert to pending).
    /// </summary>
    private async Task RederiveDirectionsAfterAsync(LocalDb db, WfAttendanceEvent late, WorkforcePolicy policy, DateTimeOffset now, SortedSet<DateOnly> days, CancellationToken ct)
    {
        var events = await EffectiveEventsAsync(db, late.EmployeeId, (late.OccurredAtUtc - TimeSpan.FromHours(24)).UtcTicks, (late.OccurredAtUtc + TimeSpan.FromHours(48)).UtcTicks, ct);
        WfAttendanceEvent? last = null;
        foreach (var e in events)
        {
            if (e.OccurredTicks <= late.OccurredTicks || e.Reason != AutoDirectionReason) { last = e; continue; }
            var expected = last is { Type: PunchTypes.In or PunchTypes.BreakEnd } && e.OccurredTicks - last.OccurredTicks < TimeSpan.FromHours(20).Ticks ? PunchTypes.Out : PunchTypes.In;
            if (expected == e.Type) { last = e; continue; }
            await AppendEventAsync(db, new WfAttendanceEvent
            {
                EmployeeId = e.EmployeeId, Type = PunchTypes.Void, Source = PunchSources.Terminal, OccurredAtUtc = e.OccurredAtUtc, CompensatesEventId = e.Id,
                CreatedBy = "terminal", Reason = "terminal:out-of-order recompute",
            }, policy, now, ct);
            last = await AppendEventAsync(db, new WfAttendanceEvent
            {
                EmployeeId = e.EmployeeId, Type = expected, Source = e.Source, DeviceId = e.DeviceId, DeviceEventId = e.DeviceEventId + "~r", OccurredAtUtc = e.OccurredAtUtc,
                CreatedBy = "terminal", Reason = AutoDirectionReason,
            }, policy, now, ct);
            days.Add(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(e.OccurredAtUtc, Zone(policy)).DateTime));
        }
    }

    // ------------------------------------------------------------------ review queue

    public async Task<IReadOnlyList<WfDeviceInbox>> ReviewQueueAsync(WorkforceActor actor, bool includeClosed = false, CancellationToken ct = default)
    {
        AuthorizeGlobal(actor, WorkforcePermissions.CorrectionsApprove);
        await using var db = dbFactory();
        return await db.WfDeviceInbox.AsNoTracking()
            .Where(i => i.ReviewReason != null && (i.Status == InboxStatuses.Review || includeClosed && (i.Status == InboxStatuses.Resolved || i.Status == InboxStatuses.Rejected)))
            .OrderBy(i => i.Seq).ToListAsync(ct);
    }

    /// <summary>Maps the unknown terminal user / card to an employee (future events resolve automatically), then accepts the event.</summary>
    public async Task<WfDeviceInbox> ResolveReviewByMappingAsync(WorkforceActor actor, long seq, Guid employeeId, string? note = null, CancellationToken ct = default)
    {
        AuthorizeGlobal(actor, WorkforcePermissions.CorrectionsApprove);
        WfDeviceInbox row;
        await using (var db = dbFactory()) row = await db.WfDeviceInbox.AsNoTracking().FirstOrDefaultAsync(i => i.Seq == seq, ct) ?? throw new InvalidOperationException("wf.err.unknown");
        Require(row.Status == InboxStatuses.Review, "wf.err.alreadyDecided");
        if (row.DeviceUserId is { } uid) await MapTerminalUserAsync(actor, employeeId, row.DeviceId, uid, ct: ct);
        else if (row.CardNo is { } card) await AssignCardAsync(actor, employeeId, card, ct: ct);
        var accepted = await AcceptReviewAsync(actor, seq, null, employeeId, note ?? "mapped", ct);
        // Other events of the same unknown user now resolve: re-queue and ingest them.
        await using (var db = dbFactory())
        {
            var others = await db.WfDeviceInbox.Where(i => i.DeviceId == row.DeviceId && i.Status == InboxStatuses.Review && i.ReviewReason == ReviewReasons.UnknownUser
                && i.DeviceUserId == row.DeviceUserId && i.CardNo == row.CardNo).ToListAsync(ct);
            foreach (var o in others) (o.Status, o.ReviewReason, o.Result) = (InboxStatuses.Buffered, null, null);
            await db.SaveChangesAsync(ct);
        }
        await ProcessBufferedTerminalEventsAsync(row.DeviceId, ct);
        return accepted;
    }

    /// <summary>Accepts a review item, optionally with a corrected time (UTC) and / or an explicit employee. Audited.</summary>
    public Task<WfDeviceInbox> AcceptReviewAsync(WorkforceActor actor, long seq, DateTimeOffset? correctedUtc, Guid? employeeId = null, string? note = null,
        CancellationToken ct = default) => InTransactionAsync(async (db, now) =>
    {
        AuthorizeGlobal(actor, WorkforcePermissions.CorrectionsApprove);
        var row = await db.WfDeviceInbox.FirstOrDefaultAsync(i => i.Seq == seq, ct) ?? throw new InvalidOperationException("wf.err.unknown");
        Require(row.Status == InboxStatuses.Review, "wf.err.alreadyDecided");
        var at = correctedUtc ?? row.SuggestedUtc ?? row.OccurredAtUtc;
        Require(at <= now + FutureTolerance, "wf.err.futureTime");
        var target = employeeId ?? row.EmployeeId;
        if (target is null)
        {
            var resolved = row.DeviceUserId is { } uid ? (await ResolveCredentialAsync(db, CredentialKinds.TerminalUser, uid, row.DeviceId, ct)).Employee
                : row.CardNo is { } c ? (await ResolveCredentialAsync(db, CredentialKinds.Card, c, row.DeviceId, ct)).Employee : null;
            target = resolved?.Id;
        }
        Require(target is not null, "wf.err.employeeRequired");
        var outcome = await IngestRowAsync(db, row, target, at, new SortedSet<DateOnly>(), ct);
        Require(outcome == InboxStatuses.Resolved, row.Result ?? "wf.err.unknown");
        (row.ResolvedBy, row.ResolvedAt, row.ResolutionNote) = (actor.UserName, now, O(note) ?? (correctedUtc is null ? "accepted" : "accepted with corrected time"));
        Audit(db, target.Value, actor, "terminal.review.accept", new { row.ReviewReason, original = row.OccurredAtUtc }, new { at, row.DeviceEventId, note }, now);
        db.WfDeviceLogs.Add(new WfDeviceLog { DeviceId = row.DeviceId, At = now, Level = "Info", Message = $"Review #{row.Seq} ({row.ReviewReason}) accepted by {actor.UserName}" });
        return row;
    }, ct);

    /// <summary>Rejects a review item with a mandatory reason (kept, never deleted). Audited.</summary>
    public Task<WfDeviceInbox> RejectReviewAsync(WorkforceActor actor, long seq, string reason, CancellationToken ct = default) => InTransactionAsync(async (db, now) =>
    {
        AuthorizeGlobal(actor, WorkforcePermissions.CorrectionsApprove);
        Require(!string.IsNullOrWhiteSpace(reason), "wf.err.reasonRequired");
        var row = await db.WfDeviceInbox.FirstOrDefaultAsync(i => i.Seq == seq, ct) ?? throw new InvalidOperationException("wf.err.unknown");
        Require(row.Status == InboxStatuses.Review, "wf.err.alreadyDecided");
        (row.Status, row.ResolvedBy, row.ResolvedAt, row.ResolutionNote, row.ProcessedAt) = (InboxStatuses.Rejected, actor.UserName, now, reason.Trim(), now);
        if (row.EmployeeId is { } eid) Audit(db, eid, actor, "terminal.review.reject", new { row.ReviewReason }, new { reason }, now);
        db.WfDeviceLogs.Add(new WfDeviceLog { DeviceId = row.DeviceId, At = now, Level = "Info", Message = $"Review #{row.Seq} ({row.ReviewReason}) rejected by {actor.UserName}: {Trim(reason)}" });
        return row;
    }, ct);

    // ------------------------------------------------------------------ device views

    public async Task<IReadOnlyList<WfDeviceInbox>> TerminalEventsAsync(WorkforceActor actor, Guid deviceId, int take = 500, CancellationToken ct = default)
    {
        AuthorizeGlobal(actor, WorkforcePermissions.AccessView);
        await using var db = dbFactory();
        return await db.WfDeviceInbox.AsNoTracking().Where(i => i.DeviceId == deviceId).OrderByDescending(i => i.Seq).Take(take).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<WfDeviceLog>> DeviceLogsAsync(WorkforceActor actor, Guid deviceId, int take = 200, CancellationToken ct = default)
    {
        AuthorizeGlobal(actor, WorkforcePermissions.AccessView);
        await using var db = dbFactory();
        return await db.WfDeviceLogs.AsNoTracking().Where(l => l.DeviceId == deviceId).OrderByDescending(l => l.Seq).Take(take).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<WfImportBatch>> ImportBatchesAsync(WorkforceActor actor, Guid deviceId, CancellationToken ct = default)
    {
        AuthorizeGlobal(actor, WorkforcePermissions.AccessView);
        await using var db = dbFactory();
        return (await db.WfImportBatches.AsNoTracking().Where(b => b.DeviceId == deviceId).ToListAsync(ct)).OrderByDescending(b => b.StartedAt).ToList();
    }

    /// <summary>Step-by-step diagnosis: ping, TCP connect, authentication, device info and clock drift.</summary>
    public async Task<IReadOnlyList<DiagnosticStep>> DiagnoseTerminalAsync(WorkforceActor actor, Guid deviceId, CancellationToken ct = default)
    {
        AuthorizeGlobal(actor, WorkforcePermissions.DevicesManage);
        WfDevice d;
        await using (var db = dbFactory()) d = await db.WfDevices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == deviceId, ct) ?? throw new InvalidOperationException("wf.err.unknownDevice");
        var steps = new List<DiagnosticStep>();
        if (d.Manufacturer != TerminalManufacturers.Simulated && d.Host is { Length: > 0 } host)
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(host, TimeSpan.FromSeconds(2), cancellationToken: ct);
                steps.Add(new DiagnosticStep("ping", reply.Status == IPStatus.Success, reply.Status == IPStatus.Success ? $"{reply.RoundtripTime} ms" : reply.Status.ToString()));
            }
            catch (Exception ex) when (ex is PingException or SocketException or InvalidOperationException or PlatformNotSupportedException)
            {
                steps.Add(new DiagnosticStep("ping", false, ex.Message));
            }
            var port = d.Port ?? (string.Equals(d.Protocol, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
            try
            {
                using var tcp = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                await tcp.ConnectAsync(host, port, cts.Token);
                steps.Add(new DiagnosticStep("tcp", true, $"{host}:{port}"));
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                steps.Add(new DiagnosticStep("tcp", false, $"{host}:{port} — {ex.Message}"));
                return steps;
            }
        }
        try
        {
            await using var adapter = TerminalFactory.Create(d, ConnectionFor(d));
            await adapter.ValidateConnectionAsync(ct);
            steps.Add(new DiagnosticStep("auth", true, "OK"));
            var info = await adapter.GetDeviceInfoAsync(ct);
            steps.Add(new DiagnosticStep("info", true, $"{info.Manufacturer} {info.Model} S/N {info.SerialNumber} FW {info.Firmware}"));
            if (info.DeviceClock is { } clock)
            {
                var drift = (int)Math.Round((clock - time.GetUtcNow()).TotalSeconds);
                steps.Add(new DiagnosticStep("clock", Math.Abs(drift) <= d.DriftToleranceSeconds, $"drift {drift} s (tolerance {d.DriftToleranceSeconds} s)"));
            }
        }
        catch (TerminalException ex) { steps.Add(new DiagnosticStep("auth", false, ex.Kind + ": " + ex.Message)); }
        await LogDeviceAsync(deviceId, steps.All(s => s.Ok) ? "Info" : "Warning", "Diagnose: " + string.Join("; ", steps.Select(s => $"{s.Step}={(s.Ok ? "ok" : "FAIL")}")), ct);
        return steps;
    }

    // ------------------------------------------------------------------ connector heartbeat

    public async Task WriteConnectorHeartbeatAsync(ConnectorHeartbeat hb, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        await db.SetValueAsync(ConnectorHeartbeatKey, JsonSerializer.Serialize(hb, Json), ct);
    }

    public async Task<ConnectorHeartbeat?> ConnectorHeartbeatAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var v = await db.GetValueAsync(ConnectorHeartbeatKey, ct);
        if (v is null) return null;
        try { return JsonSerializer.Deserialize<ConnectorHeartbeat>(v, Json); }
        catch (JsonException) { return null; }
    }
}
