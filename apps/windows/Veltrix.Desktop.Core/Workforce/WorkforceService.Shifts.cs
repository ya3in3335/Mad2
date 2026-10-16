using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Standalone;

namespace Veltrix.Desktop.Core.Workforce;

public sealed record ShiftTemplateInput(string Code, string Name, IReadOnlyList<ShiftSegment> Segments, int BreakMinutes = 0, int GraceInMinutes = 0,
    int GraceOutMinutes = 0, bool IsFlexible = false, TimeOnly? CoreStart = null, TimeOnly? CoreEnd = null, int RequiredMinutes = 0);

/// <param name="DaysMask">Fixed template only: day-of-week bit mask (bit 0 = Sunday), 0 = every day.</param>
public sealed record AssignmentInput(Guid EmployeeId, DateOnly From, DateOnly? To = null, Guid? TemplateId = null, Guid? RotationId = null,
    DateOnly? RotationAnchor = null, int DaysMask = 0);

public sealed record AssignmentResult(WfShiftAssignment Assignment, IReadOnlyList<ShiftConflict> Conflicts);

public sealed partial class WorkforceService
{
    public Task<WfShiftTemplate> UpsertShiftTemplateAsync(WorkforceActor actor, Guid? id, ShiftTemplateInput input, CancellationToken ct = default) =>
        InTransactionAsync(async (db, _) =>
        {
            AuthorizeGlobal(actor, WorkforcePermissions.ShiftsManage);
            Require(!string.IsNullOrWhiteSpace(input.Code) && !string.IsNullOrWhiteSpace(input.Name), "wf.err.nameRequired");
            Require(input.Segments.Count > 0 || input.IsFlexible, "wf.err.segments");
            Require(input.Segments.All(s => s.Start != s.End), "wf.err.segments");
            Require(input.BreakMinutes >= 0 && input.GraceInMinutes >= 0 && input.GraceOutMinutes >= 0 && input.RequiredMinutes >= 0, "wf.err.minutes");
            Require(!input.IsFlexible || input.RequiredMinutes > 0, "wf.err.requiredMinutes");
            var t = id is { } x ? await db.WfShiftTemplates.FindAsync([x], ct) ?? throw new InvalidOperationException("wf.err.unknown") : null;
            if (t is null) db.WfShiftTemplates.Add(t = new WfShiftTemplate { Id = Guid.NewGuid() });
            t.Code = input.Code.Trim();
            t.Name = input.Name.Trim();
            t.SegmentsJson = ShiftDefinition.SerializeSegments(input.Segments);
            t.BreakMinutes = input.BreakMinutes;
            t.GraceInMinutes = input.GraceInMinutes;
            t.GraceOutMinutes = input.GraceOutMinutes;
            t.IsFlexible = input.IsFlexible;
            t.CoreStart = input.CoreStart;
            t.CoreEnd = input.CoreEnd;
            t.RequiredMinutes = input.RequiredMinutes;
            return t;
        }, ct);

    public async Task<IReadOnlyList<WfShiftTemplate>> ShiftTemplatesAsync(WorkforceActor actor, CancellationToken ct = default)
    {
        Authorize(actor, WorkforcePermissions.ShiftsView);
        await using var db = dbFactory();
        return (await db.WfShiftTemplates.AsNoTracking().ToListAsync(ct)).OrderBy(t => t.Code).ToList();
    }

    /// <summary>Rotation: one template id (or null = rest) per day of the cycle.</summary>
    public Task<WfRotation> UpsertRotationAsync(WorkforceActor actor, Guid? id, string name, IReadOnlyList<Guid?> pattern, CancellationToken ct = default) =>
        InTransactionAsync(async (db, _) =>
        {
            AuthorizeGlobal(actor, WorkforcePermissions.ShiftsManage);
            Require(!string.IsNullOrWhiteSpace(name) && pattern.Count is > 0 and <= 366, "wf.err.rotation");
            var known = (await db.WfShiftTemplates.Select(t => t.Id).ToListAsync(ct)).ToHashSet();
            Require(pattern.All(p => p is null || known.Contains(p.Value)), "wf.err.unknownTemplate");
            var r = id is { } x ? await db.WfRotations.FindAsync([x], ct) ?? throw new InvalidOperationException("wf.err.unknown") : null;
            if (r is null) db.WfRotations.Add(r = new WfRotation { Id = Guid.NewGuid() });
            r.Name = name.Trim();
            r.PatternJson = JsonSerializer.Serialize(pattern);
            return r;
        }, ct);

    /// <summary>
    /// Assigns a template or rotation for a date range. Overlapping assignments of the same employee are refused unless
    /// <paramref name="replaceOverlaps"/> (older ones are then cut). Rest-period and overlap conflicts of the result are returned as warnings.
    /// </summary>
    public async Task<AssignmentResult> AssignShiftAsync(WorkforceActor actor, AssignmentInput input, bool replaceOverlaps = false, CancellationToken ct = default)
    {
        var a = await InTransactionAsync(async (db, now) =>
        {
            await AuthorizeAsync(db, actor, WorkforcePermissions.ShiftsManage, input.EmployeeId, ct);
            Require(input.To is null || input.To >= input.From, "wf.err.period");
            Require(input.TemplateId is not null ^ input.RotationId is not null, "wf.err.assignment");
            if (input.TemplateId is { } t) Require(await db.WfShiftTemplates.AnyAsync(x => x.Id == t, ct), "wf.err.unknownTemplate");
            if (input.RotationId is { } r) Require(await db.WfRotations.AnyAsync(x => x.Id == r, ct), "wf.err.unknownRotation");
            var overlapping = (await db.WfShiftAssignments.Where(x => x.EmployeeId == input.EmployeeId).ToListAsync(ct))
                .Where(x => ShiftPlanner.RangesOverlap(x.From, x.To, input.From, input.To)).ToList();
            if (overlapping.Count > 0)
            {
                Require(replaceOverlaps, "wf.err.assignmentOverlap");
                foreach (var o in overlapping)
                {
                    if (o.From >= input.From && (input.To is null || (o.To is { } ot && ot <= input.To))) db.WfShiftAssignments.Remove(o);
                    else if (o.From < input.From) o.To = input.From.AddDays(-1);
                    else o.From = input.To!.Value.AddDays(1);
                }
            }
            var a = new WfShiftAssignment
            {
                Id = Guid.NewGuid(), EmployeeId = input.EmployeeId, From = input.From, To = input.To, TemplateId = input.TemplateId, RotationId = input.RotationId,
                RotationAnchor = input.RotationAnchor, DaysMask = input.DaysMask,
            };
            db.WfShiftAssignments.Add(a);
            await db.SaveChangesAsync(ct);
            return a;
        }, ct);
        await using var db2 = dbFactory();
        var policy = WorkforcePolicy.FromJson(await db2.GetValueAsync(WorkforcePolicy.Key, ct));
        var planner = await PlannerAsync(db2, ct);
        var to = input.To ?? input.From.AddDays(60);
        return new AssignmentResult(a, planner.DetectConflicts(input.EmployeeId, input.From, to, policy.Rest.MinRestHours));
    }

    /// <summary>Single-day override (TemplateId null = rest day).</summary>
    public Task<WfShiftOverride> SetShiftOverrideAsync(WorkforceActor actor, Guid employeeId, DateOnly date, Guid? templateId, CancellationToken ct = default) =>
        InTransactionAsync(async (db, _) =>
        {
            await AuthorizeAsync(db, actor, WorkforcePermissions.ShiftsManage, employeeId, ct);
            return await SetOverrideCoreAsync(db, employeeId, date, templateId, "Manual", null, ct);
        }, ct);

    private static async Task<WfShiftOverride> SetOverrideCoreAsync(LocalDb db, Guid employeeId, DateOnly date, Guid? templateId, string source, Guid? sourceId, CancellationToken ct)
    {
        var o = await db.WfShiftOverrides.FirstOrDefaultAsync(x => x.EmployeeId == employeeId && x.Date == date, ct);
        if (o is null) db.WfShiftOverrides.Add(o = new WfShiftOverride { Id = Guid.NewGuid(), EmployeeId = employeeId, Date = date });
        o.TemplateId = templateId;
        o.Source = source;
        o.SourceId = sourceId;
        return o;
    }

    public Task<WfHoliday> AddHolidayAsync(WorkforceActor actor, DateOnly date, string name, bool isPaid = true, CancellationToken ct = default) =>
        InTransactionAsync(async (db, _) =>
        {
            AuthorizeGlobal(actor, WorkforcePermissions.ShiftsManage);
            Require(!string.IsNullOrWhiteSpace(name), "wf.err.nameRequired");
            Require(!await db.WfHolidays.AnyAsync(h => h.Date == date, ct), "wf.err.holidayExists");
            var h = new WfHoliday { Id = Guid.NewGuid(), Date = date, Name = name.Trim(), IsPaid = isPaid };
            db.WfHolidays.Add(h);
            return h;
        }, ct);

    public Task<bool> RemoveHolidayAsync(WorkforceActor actor, Guid id, CancellationToken ct = default) => InTransactionAsync(async (db, _) =>
    {
        AuthorizeGlobal(actor, WorkforcePermissions.ShiftsManage);
        var h = await db.WfHolidays.FindAsync([id], ct);
        if (h is null) return false;
        db.WfHolidays.Remove(h);
        return true;
    }, ct);

    public async Task<IReadOnlyList<WfHoliday>> HolidaysAsync(WorkforceActor actor, int? year = null, CancellationToken ct = default)
    {
        Authorize(actor, WorkforcePermissions.ShiftsView);
        await using var db = dbFactory();
        return (await db.WfHolidays.AsNoTracking().ToListAsync(ct)).Where(h => year is null || h.Date.Year == year).OrderBy(h => h.Date).ToList();
    }

    /// <summary>Planned calendar (scoped) — use <see cref="ShiftPlanner.Week"/> / <see cref="ShiftPlanner.Month"/> for weekly / monthly views.</summary>
    public async Task<IReadOnlyList<PlannedDay>> CalendarAsync(WorkforceActor actor, DateOnly from, DateOnly to, Guid? employeeId = null, CancellationToken ct = default)
    {
        Authorize(actor, WorkforcePermissions.ShiftsView);
        Require(to >= from && to.DayNumber - from.DayNumber <= 400, "wf.err.period");
        await using var db = dbFactory();
        var ids = (await ScopedEmployeesAsync(db, actor, ct)).Where(e => e.IsActive && (employeeId is null || e.Id == employeeId)).Select(e => e.Id);
        return (await PlannerAsync(db, ct)).Calendar(ids, from, to);
    }

    public async Task<IReadOnlyList<ShiftConflict>> DetectConflictsAsync(WorkforceActor actor, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        Authorize(actor, WorkforcePermissions.ShiftsView);
        await using var db = dbFactory();
        var policy = WorkforcePolicy.FromJson(await db.GetValueAsync(WorkforcePolicy.Key, ct));
        var planner = await PlannerAsync(db, ct);
        return (await ScopedEmployeesAsync(db, actor, ct)).Where(e => e.IsActive)
            .SelectMany(e => planner.DetectConflicts(e.Id, from, to, policy.Rest.MinRestHours)).ToList();
    }

    // ------------------------------------------------------------------ shift swaps

    public Task<WfSwapRequest> RequestSwapAsync(WorkforceActor actor, Guid requesterId, DateOnly requesterDate, Guid counterpartId, DateOnly counterpartDate,
        string? reason = null, CancellationToken ct = default) => InTransactionAsync(async (db, now) =>
    {
        await AuthorizeAsync(db, actor, WorkforcePermissions.SwapsRequest, requesterId, ct);
        Require(requesterId != counterpartId, "wf.err.swapSelf");
        Require(await db.Employees.AnyAsync(e => e.Id == counterpartId && e.IsActive, ct), "erp.err.unknownEmployee");
        var s = new WfSwapRequest
        {
            Id = Guid.NewGuid(), RequesterId = requesterId, RequesterDate = requesterDate, CounterpartId = counterpartId, CounterpartDate = counterpartDate,
            Reason = O(reason), RequestedBy = actor.UserName, RequestedAt = now,
        };
        db.WfSwapRequests.Add(s);
        return s;
    }, ct);

    /// <summary>
    /// Approves (or rejects) a swap: on each of the two dates the two employees exchange their planned shifts (via day overrides).
    /// Refused when the result would break the minimum rest period or overlap.
    /// </summary>
    public Task<WfSwapRequest> DecideSwapAsync(WorkforceActor actor, Guid swapId, bool approve, CancellationToken ct = default) => InTransactionAsync(async (db, now) =>
    {
        var s = await db.WfSwapRequests.FindAsync([swapId], ct) ?? throw new InvalidOperationException("wf.err.unknown");
        await AuthorizeAsync(db, actor, WorkforcePermissions.SwapsApprove, s.RequesterId, ct);
        await AuthorizeAsync(db, actor, WorkforcePermissions.SwapsApprove, s.CounterpartId, ct);
        if ((actor.EmployeeId == s.RequesterId || actor.EmployeeId == s.CounterpartId) && actor.Role != LocalRoles.Owner)
            throw new WorkforceAccessDeniedException(WorkforcePermissions.SwapsApprove);
        Require(s.Status == RequestStatuses.Pending, "wf.err.alreadyDecided");
        s.Status = approve ? RequestStatuses.Approved : RequestStatuses.Rejected;
        s.DecidedBy = actor.UserName;
        s.DecidedAt = now;
        if (!approve) return s;
        var planner = await PlannerAsync(db, ct);
        foreach (var d in new[] { s.RequesterDate, s.CounterpartDate }.Distinct())
        {
            var a = planner.Plan(s.RequesterId, d).Shift?.Id;
            var b = planner.Plan(s.CounterpartId, d).Shift?.Id;
            await SetOverrideCoreAsync(db, s.RequesterId, d, b, "Swap", s.Id, ct);
            await SetOverrideCoreAsync(db, s.CounterpartId, d, a, "Swap", s.Id, ct);
        }
        await db.SaveChangesAsync(ct);
        var policy = WorkforcePolicy.FromJson(await db.GetValueAsync(WorkforcePolicy.Key, ct));
        var after = await PlannerAsync(db, ct);
        var from = (s.RequesterDate < s.CounterpartDate ? s.RequesterDate : s.CounterpartDate).AddDays(-1);
        var to = (s.RequesterDate > s.CounterpartDate ? s.RequesterDate : s.CounterpartDate).AddDays(1);
        var conflicts = after.DetectConflicts(s.RequesterId, from, to, policy.Rest.MinRestHours)
            .Concat(after.DetectConflicts(s.CounterpartId, from, to, policy.Rest.MinRestHours)).Where(c => c.Kind != ConflictKinds.AssignmentOverlap).ToList();
        if (conflicts.Count > 0) throw new InvalidOperationException("wf.err.swapConflict:" + conflicts[0].Kind);
        return s;
    }, ct);

    public async Task<IReadOnlyList<WfSwapRequest>> SwapsAsync(WorkforceActor actor, string? status = null, CancellationToken ct = default)
    {
        Authorize(actor, WorkforcePermissions.ShiftsView);
        await using var db = dbFactory();
        var ids = (await ScopedEmployeesAsync(db, actor, ct)).Select(e => e.Id).ToHashSet();
        return (await db.WfSwapRequests.AsNoTracking().ToListAsync(ct))
            .Where(s => (ids.Contains(s.RequesterId) || ids.Contains(s.CounterpartId)) && (status is null || s.Status == status)).ToList();
    }
}
