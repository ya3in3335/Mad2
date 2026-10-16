using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Standalone;

namespace Veltrix.Desktop.Core.Workforce;

/// <param name="Type">Null = automatic In/Out toggle.</param>
public sealed record PunchInput(Guid EmployeeId, string? Type = null, string Source = PunchSources.Manual, DateTimeOffset? OccurredAtUtc = null,
    Guid? DeviceId = null, string? DeviceEventId = null, string? Reason = null, string? AuthorizedBy = null);

public static class PunchOutcomes
{
    public const string Recorded = "Recorded";
    public const string Duplicate = "Duplicate";
    public const string Rejected = "Rejected";
}

public sealed record PunchResult(string Outcome, WfAttendanceEvent? Event = null, string? Reason = null, Guid? EmployeeId = null)
{
    public bool IsRecorded => Outcome == PunchOutcomes.Recorded;
}

public sealed record CorrectionInput(Guid EmployeeId, string Kind, string Reason, string? PunchType = null, DateTimeOffset? OccurredAtUtc = null, Guid? TargetEventId = null);

public sealed record ChainVerification(bool IsValid, int Count, long? BrokenAtSeq);

public sealed partial class WorkforceService
{
    public const string GenesisHash = "VELTRIX-ATTENDANCE-GENESIS";

    /// <summary>SHA-256(prev hash | canonical event) in upper-case hex.</summary>
    public static string ComputeHash(string prevHash, WfAttendanceEvent e)
    {
        var canonical = string.Join('|', prevHash, e.Id.ToString("N"), e.EmployeeId.ToString("N"), e.Type, e.Source, e.DeviceId?.ToString("N") ?? "",
            e.DeviceEventId ?? "", e.OccurredTicks.ToString(CultureInfo.InvariantCulture), e.TimeZoneId, e.LocalOffsetMinutes.ToString(CultureInfo.InvariantCulture),
            e.RecordedAt.UtcTicks.ToString(CultureInfo.InvariantCulture), e.CreatedBy ?? "", e.Reason ?? "", e.AuthorizedBy ?? "", e.CorrectionId?.ToString("N") ?? "",
            e.CompensatesEventId?.ToString("N") ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>Appends a raw event to the hash chain and saves it (inside the caller's transaction). Raw events are never updated or deleted.</summary>
    private async Task<WfAttendanceEvent> AppendEventAsync(LocalDb db, WfAttendanceEvent e, WorkforcePolicy policy, DateTimeOffset now, CancellationToken ct)
    {
        var zone = Zone(policy);
        e.Id = e.Id == Guid.Empty ? Guid.NewGuid() : e.Id;
        e.OccurredAtUtc = e.OccurredAtUtc.ToUniversalTime();
        e.OccurredTicks = e.OccurredAtUtc.UtcTicks;
        e.TimeZoneId = zone.Id;
        e.LocalOffsetMinutes = (int)zone.GetUtcOffset(e.OccurredAtUtc).TotalMinutes;
        // Millisecond precision: the SQLite DateTimeOffset converter does not keep sub-millisecond ticks, and the hash must survive a round trip.
        e.RecordedAt = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        e.PrevHash = await db.WfAttendanceEvents.OrderByDescending(x => x.Seq).Select(x => x.Hash).FirstOrDefaultAsync(ct) ?? GenesisHash;
        e.Hash = ComputeHash(e.PrevHash, e);
        db.WfAttendanceEvents.Add(e);
        await db.SaveChangesAsync(ct);
        return e;
    }

    /// <summary>Events of an employee minus voided ones and the Void markers themselves.</summary>
    private static async Task<List<WfAttendanceEvent>> EffectiveEventsAsync(LocalDb db, Guid? employeeId, long fromTicks, long toTicks, CancellationToken ct)
    {
        var raw = await db.WfAttendanceEvents.AsNoTracking()
            .Where(e => (employeeId == null || e.EmployeeId == employeeId) && e.OccurredTicks >= fromTicks && e.OccurredTicks <= toTicks).ToListAsync(ct);
        var voidTargets = (await db.WfAttendanceEvents.AsNoTracking().Where(e => e.Type == PunchTypes.Void && (employeeId == null || e.EmployeeId == employeeId))
            .Select(e => e.CompensatesEventId).ToListAsync(ct)).OfType<Guid>().ToHashSet();
        return raw.Where(e => e.Type != PunchTypes.Void && !voidTargets.Contains(e.Id)).OrderBy(e => e.OccurredTicks).ThenBy(e => e.Seq).ToList();
    }

    /// <summary>
    /// Records a punch. Manual punches need <c>workforce.attendance.manual</c> (scoped), a reason and an authorizer; self punches need
    /// <c>workforce.attendance.self</c>. Duplicate scans within the policy window are rejected; Type null toggles In/Out automatically.
    /// </summary>
    public async Task<PunchResult> RecordPunchAsync(WorkforceActor actor, PunchInput input, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var self = actor.EmployeeId == input.EmployeeId;
        if (input.Source == PunchSources.Manual || !self)
        {
            await AuthorizeAsync(db, actor, WorkforcePermissions.AttendanceManual, input.EmployeeId, ct);
            if (input.Source == PunchSources.Manual)
            {
                Require(!string.IsNullOrWhiteSpace(input.Reason), "wf.err.reasonRequired");
                Require(!string.IsNullOrWhiteSpace(input.AuthorizedBy), "wf.err.authorizerRequired");
            }
        }
        else Authorize(actor, WorkforcePermissions.AttendanceSelf);
        Require(input.Source != PunchSources.Correction, "wf.err.useCorrections");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var result = await PunchCoreAsync(db, input, input.Source == PunchSources.Manual || !self ? actor.UserName : null, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>Kiosk / reader punch: the presented credential (card, QR, PIN "EMPNO:PIN", terminal user) identifies and authorizes the employee.</summary>
    public async Task<PunchResult> PunchByCredentialAsync(string credentialKind, string credentialValue, string? type = null, string source = PunchSources.Kiosk,
        Guid? deviceId = null, string? deviceEventId = null, DateTimeOffset? occurredAtUtc = null, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (employee, _) = await ResolveCredentialAsync(db, credentialKind, credentialValue, deviceId, ct);
        if (employee is null) return new PunchResult(PunchOutcomes.Rejected, Reason: AccessReasons.UnknownCredential);
        var r = await PunchCoreAsync(db, new PunchInput(employee.Id, type, source, occurredAtUtc, deviceId, deviceEventId), null, ct);
        await tx.CommitAsync(ct);
        return r;
    }

    private async Task<PunchResult> PunchCoreAsync(LocalDb db, PunchInput input, string? createdBy, CancellationToken ct)
    {
        var policy = WorkforcePolicy.FromJson(await db.GetValueAsync(WorkforcePolicy.Key, ct));
        var now = time.GetUtcNow();
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == input.EmployeeId, ct);
        if (employee is null) return new PunchResult(PunchOutcomes.Rejected, Reason: "erp.err.unknownEmployee");
        if (employee.Status is EmployeeStatuses.Terminated or EmployeeStatuses.Suspended)
            return new PunchResult(PunchOutcomes.Rejected, Reason: AccessReasons.EmployeeInactive, EmployeeId: employee.Id);
        if (input.Type is { } t0 && !PunchTypes.Punches.Contains(t0)) return new PunchResult(PunchOutcomes.Rejected, Reason: "wf.err.punchType");

        if (input.DeviceId is { } did && input.DeviceEventId is { } deid
            && await db.WfAttendanceEvents.AsNoTracking().FirstOrDefaultAsync(e => e.DeviceId == did && e.DeviceEventId == deid, ct) is { } existing)
            return new PunchResult(PunchOutcomes.Duplicate, existing, "wf.dup.deviceEvent", employee.Id);

        var at = (input.OccurredAtUtc ?? now).ToUniversalTime();
        var window = TimeSpan.FromSeconds(policy.Attendance.DuplicateWindowSeconds);
        var recent = await EffectiveEventsAsync(db, employee.Id, (at - TimeSpan.FromHours(24)).UtcTicks, (at + window).UtcTicks, ct);
        if (window > TimeSpan.Zero && recent.FirstOrDefault(e => Math.Abs(e.OccurredTicks - at.UtcTicks) < window.Ticks && (input.Type is null || e.Type == input.Type)) is { } dup)
            return new PunchResult(PunchOutcomes.Duplicate, dup, "wf.dup.window", employee.Id);
        var type = input.Type;
        if (type is null)
        {
            var last = recent.LastOrDefault(e => e.OccurredTicks <= at.UtcTicks);
            type = last is { Type: PunchTypes.In or PunchTypes.BreakEnd } && at.UtcTicks - last.OccurredTicks < TimeSpan.FromHours(20).Ticks ? PunchTypes.Out : PunchTypes.In;
        }
        var ev = await AppendEventAsync(db, new WfAttendanceEvent
        {
            EmployeeId = employee.Id, Type = type, Source = input.Source, DeviceId = input.DeviceId, DeviceEventId = input.DeviceEventId, OccurredAtUtc = at,
            CreatedBy = createdBy, Reason = O(input.Reason), AuthorizedBy = O(input.AuthorizedBy),
        }, policy, now, ct);
        return new PunchResult(PunchOutcomes.Recorded, ev, null, employee.Id);
    }

    public async Task<IReadOnlyList<WfAttendanceEvent>> EventsAsync(WorkforceActor actor, Guid employeeId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        bool includeVoided = true, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        await AuthorizeAsync(db, actor, WorkforcePermissions.AttendanceView, employeeId, ct);
        if (!includeVoided) return await EffectiveEventsAsync(db, employeeId, fromUtc.UtcTicks, toUtc.UtcTicks, ct);
        return await db.WfAttendanceEvents.AsNoTracking()
            .Where(e => e.EmployeeId == employeeId && e.OccurredTicks >= fromUtc.UtcTicks && e.OccurredTicks <= toUtc.UtcTicks).OrderBy(e => e.Seq).ToListAsync(ct);
    }

    /// <summary>Recomputes the whole hash chain; any edited, deleted or reordered raw event breaks it.</summary>
    public async Task<ChainVerification> VerifyChainAsync(WorkforceActor actor, CancellationToken ct = default)
    {
        Authorize(actor, WorkforcePermissions.AuditView);
        await using var db = dbFactory();
        var prev = GenesisHash;
        var n = 0;
        await foreach (var e in db.WfAttendanceEvents.AsNoTracking().OrderBy(e => e.Seq).AsAsyncEnumerable().WithCancellation(ct))
        {
            if (e.PrevHash != prev || e.Hash != ComputeHash(prev, e)) return new ChainVerification(false, n, e.Seq);
            prev = e.Hash;
            n++;
        }
        return new ChainVerification(true, n, null);
    }

    // ------------------------------------------------------------------ corrections (approved corrections append compensating events)

    public Task<WfCorrectionRequest> RequestCorrectionAsync(WorkforceActor actor, CorrectionInput input, CancellationToken ct = default) =>
        InTransactionAsync(async (db, now) =>
        {
            await AuthorizeAsync(db, actor, WorkforcePermissions.CorrectionsRequest, input.EmployeeId, ct);
            Require(!string.IsNullOrWhiteSpace(input.Reason), "wf.err.reasonRequired");
            if (input.Kind == CorrectionKinds.AddPunch)
                Require(input.PunchType is { } pt && PunchTypes.Punches.Contains(pt) && input.OccurredAtUtc is not null, "wf.err.correction");
            else if (input.Kind == CorrectionKinds.VoidPunch)
                Require(input.TargetEventId is { } target && await db.WfAttendanceEvents.AnyAsync(e => e.Id == target && e.EmployeeId == input.EmployeeId && e.Type != PunchTypes.Void, ct),
                    "wf.err.correction");
            else throw new InvalidOperationException("wf.err.correction");
            var r = new WfCorrectionRequest
            {
                Id = Guid.NewGuid(), EmployeeId = input.EmployeeId, Kind = input.Kind, PunchType = input.PunchType, OccurredAtUtc = input.OccurredAtUtc?.ToUniversalTime(),
                TargetEventId = input.TargetEventId, Reason = input.Reason.Trim(), RequestedBy = actor.UserName, RequestedAt = now,
            };
            db.WfCorrectionRequests.Add(r);
            return r;
        }, ct);

    /// <summary>Approves or rejects a correction (manager in scope; nobody but the owner approves their own). Approval appends events; nothing is edited.</summary>
    public Task<WfCorrectionRequest> DecideCorrectionAsync(WorkforceActor actor, Guid requestId, bool approve, string? note = null, CancellationToken ct = default) =>
        InTransactionAsync(async (db, now) =>
        {
            var r = await db.WfCorrectionRequests.FindAsync([requestId], ct) ?? throw new InvalidOperationException("wf.err.unknown");
            await AuthorizeAsync(db, actor, WorkforcePermissions.CorrectionsApprove, r.EmployeeId, ct);
            if (actor.EmployeeId == r.EmployeeId && actor.Role != LocalRoles.Owner) throw new WorkforceAccessDeniedException(WorkforcePermissions.CorrectionsApprove);
            Require(r.Status == RequestStatuses.Pending, "wf.err.alreadyDecided");
            r.Status = approve ? RequestStatuses.Approved : RequestStatuses.Rejected;
            r.DecidedBy = actor.UserName;
            r.DecidedAt = now;
            r.DecisionNote = O(note);
            if (!approve) return r;
            var policy = WorkforcePolicy.FromJson(await db.GetValueAsync(WorkforcePolicy.Key, ct));
            if (r.Kind == CorrectionKinds.AddPunch)
                await AppendEventAsync(db, new WfAttendanceEvent
                {
                    EmployeeId = r.EmployeeId, Type = r.PunchType!, Source = PunchSources.Correction, OccurredAtUtc = r.OccurredAtUtc!.Value, CreatedBy = r.RequestedBy,
                    Reason = r.Reason, AuthorizedBy = actor.UserName, CorrectionId = r.Id,
                }, policy, now, ct);
            else
            {
                var target = await db.WfAttendanceEvents.AsNoTracking().FirstAsync(e => e.Id == r.TargetEventId, ct);
                Require(!await db.WfAttendanceEvents.AnyAsync(e => e.Type == PunchTypes.Void && e.CompensatesEventId == target.Id, ct), "wf.err.alreadyVoided");
                await AppendEventAsync(db, new WfAttendanceEvent
                {
                    EmployeeId = r.EmployeeId, Type = PunchTypes.Void, Source = PunchSources.Correction, OccurredAtUtc = target.OccurredAtUtc, CreatedBy = r.RequestedBy,
                    Reason = r.Reason, AuthorizedBy = actor.UserName, CorrectionId = r.Id, CompensatesEventId = target.Id,
                }, policy, now, ct);
            }
            return r;
        }, ct);

    public async Task<IReadOnlyList<WfCorrectionRequest>> CorrectionsAsync(WorkforceActor actor, string? status = null, CancellationToken ct = default)
    {
        Authorize(actor, WorkforcePermissions.AttendanceView);
        await using var db = dbFactory();
        var ids = (await ScopedEmployeesAsync(db, actor, ct)).Select(e => e.Id).ToHashSet();
        return (await db.WfCorrectionRequests.AsNoTracking().ToListAsync(ct)).Where(r => ids.Contains(r.EmployeeId) && (status is null || r.Status == status))
            .OrderByDescending(r => r.RequestedAt).ToList();
    }

    // ------------------------------------------------------------------ daily computation

    /// <summary>Daily attendance per employee (scoped) for [from, to], against the planned shifts, holidays and approved leave.</summary>
    public async Task<IReadOnlyList<DayResult>> ComputeAttendanceAsync(WorkforceActor actor, DateOnly from, DateOnly to, Guid? employeeId = null,
        CancellationToken ct = default)
    {
        Authorize(actor, WorkforcePermissions.AttendanceView);
        Require(to >= from && to.DayNumber - from.DayNumber <= 400, "wf.err.period");
        await using var db = dbFactory();
        if (employeeId is { } eid) await AuthorizeAsync(db, actor, WorkforcePermissions.AttendanceView, eid, ct);
        var employees = (await ScopedEmployeesAsync(db, actor, ct)).Where(e => employeeId is null || e.Id == employeeId).ToList();
        return await ComputeCoreAsync(db, employees, from, to, ct);
    }

    internal async Task<ShiftPlanner> PlannerAsync(LocalDb db, CancellationToken ct) => new(
        await db.WfShiftTemplates.AsNoTracking().ToListAsync(ct), await db.WfRotations.AsNoTracking().ToListAsync(ct),
        await db.WfShiftAssignments.AsNoTracking().ToListAsync(ct), await db.WfShiftOverrides.AsNoTracking().ToListAsync(ct),
        await db.WfHolidays.AsNoTracking().ToListAsync(ct));

    internal async Task<List<DayResult>> ComputeCoreAsync(LocalDb db, IReadOnlyList<LocalEmployee> employees, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var policy = WorkforcePolicy.FromJson(await db.GetValueAsync(WorkforcePolicy.Key, ct));
        var planner = await PlannerAsync(db, ct);
        var types = (await db.WfLeaveTypes.AsNoTracking().ToListAsync(ct)).ToDictionary(t => t.Id);
        var leaves = (await db.WfLeaveRequests.AsNoTracking().Where(l => l.Status == RequestStatuses.Approved).ToListAsync(ct))
            .Where(l => l.From <= to && l.To >= from).ToLookup(l => l.EmployeeId);
        var fromTicks = from.AddDays(-2).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).Ticks;
        var toTicks = to.AddDays(3).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).Ticks;
        var events = (await EffectiveEventsAsync(db, null, fromTicks, toTicks, ct)).ToLookup(e => e.EmployeeId);
        var result = new List<DayResult>();
        foreach (var emp in employees)
        {
            var punches = events[emp.Id].Select(e => new Punch(e.Id, e.Type, new DateTime(e.OccurredTicks + TimeSpan.FromMinutes(e.LocalOffsetMinutes).Ticks), e.Source));
            var byDay = AttendanceCalculator.Attribute(punches, d => planner.Plan(emp.Id, d), policy.Attendance);
            for (var d = from; d <= to; d = d.AddDays(1))
            {
                if (d < emp.HireDate || emp.TerminationDate is { } td && d > td) continue;
                var lr = leaves[emp.Id].FirstOrDefault(l => l.From <= d && l.To >= d);
                var leave = lr is not null && types.TryGetValue(lr.LeaveTypeId, out var lt) ? new LeaveDay(lt.Code, lt.IsPaid) : null;
                result.Add(AttendanceCalculator.ComputeDay(planner.Plan(emp.Id, d), byDay.GetValueOrDefault(d) ?? [], policy, leave));
            }
        }
        return result;
    }
}
