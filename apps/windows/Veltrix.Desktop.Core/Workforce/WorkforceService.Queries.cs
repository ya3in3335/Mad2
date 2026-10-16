using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;

namespace Veltrix.Desktop.Core.Workforce;

/// <summary>A punch with its employee (live feed, dashboard).</summary>
public sealed record PunchFeedRow(Guid EventId, Guid EmployeeId, string EmployeeNo, string Employee, string Type, string Source, DateTimeOffset OccurredAtUtc);

/// <summary>Minimal employee reference (pickers, names in lists) — no salary or personal data.</summary>
public sealed record EmployeeRef(Guid Id, string Code, string Name, Guid? DepartmentId, string Status);

/// <summary>Reviewed overtime of one employee on one day (the review itself; payroll keeps computing overtime from punches and policy).</summary>
public sealed record OvertimeDecision(Guid EmployeeId, DateOnly Date, int Minutes, string Status, string DecidedBy, DateTimeOffset DecidedAt, string? Note = null);

/// <summary>Read-side queries used by the workforce screens (lists of configuration records, live punch feed, overtime review).</summary>
public sealed partial class WorkforceService
{
    public const string OvertimeDecisionsKey = "workforce.overtime.decisions";

    /// <summary>Employees in the actor's scope as references (needs attendance or employee view).</summary>
    public async Task<IReadOnlyList<EmployeeRef>> DirectoryAsync(WorkforceActor actor, CancellationToken ct = default)
    {
        if (!actor.Has(WorkforcePermissions.AttendanceView) && !actor.Has(WorkforcePermissions.EmployeesView))
            throw new WorkforceAccessDeniedException(WorkforcePermissions.AttendanceView);
        await using var db = dbFactory();
        return (await ScopedEmployeesAsync(db, actor, ct)).Select(e => new EmployeeRef(e.Id, e.Code, e.Name, e.DepartmentId, e.Status)).ToList();
    }

    public async Task<IReadOnlyList<WfZone>> ZonesAsync(WorkforceActor actor, CancellationToken ct = default)
    {
        AuthorizeGlobal(actor, WorkforcePermissions.AccessView);
        await using var db = dbFactory();
        return (await db.WfZones.AsNoTracking().ToListAsync(ct)).OrderBy(z => z.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<WfDoor>> DoorsAsync(WorkforceActor actor, CancellationToken ct = default)
    {
        AuthorizeGlobal(actor, WorkforcePermissions.AccessView);
        await using var db = dbFactory();
        return (await db.WfDoors.AsNoTracking().ToListAsync(ct)).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<WfAccessGroup>> AccessGroupsAsync(WorkforceActor actor, CancellationToken ct = default)
    {
        AuthorizeGlobal(actor, WorkforcePermissions.AccessView);
        await using var db = dbFactory();
        return (await db.WfAccessGroups.AsNoTracking().ToListAsync(ct)).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<Guid>> AccessGroupMembersAsync(WorkforceActor actor, Guid groupId, CancellationToken ct = default)
    {
        AuthorizeGlobal(actor, WorkforcePermissions.AccessView);
        await using var db = dbFactory();
        return await db.WfAccessGroupMembers.AsNoTracking().Where(m => m.GroupId == groupId).Select(m => m.EmployeeId).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<WfRotation>> RotationsAsync(WorkforceActor actor, CancellationToken ct = default)
    {
        Authorize(actor, WorkforcePermissions.ShiftsView);
        await using var db = dbFactory();
        return (await db.WfRotations.AsNoTracking().ToListAsync(ct)).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Shift assignments of the employees in the actor's scope that overlap [from, to].</summary>
    public async Task<IReadOnlyList<WfShiftAssignment>> AssignmentsAsync(WorkforceActor actor, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        Authorize(actor, WorkforcePermissions.ShiftsView);
        await using var db = dbFactory();
        var ids = (await ScopedEmployeesAsync(db, actor, ct)).Select(e => e.Id).ToHashSet();
        return (await db.WfShiftAssignments.AsNoTracking().ToListAsync(ct))
            .Where(a => ids.Contains(a.EmployeeId) && ShiftPlanner.RangesOverlap(a.From, a.To, from, to)).OrderBy(a => a.From).ToList();
    }

    /// <summary>Effective punches (voided ones excluded) of the employees in scope, newest first.</summary>
    public async Task<IReadOnlyList<PunchFeedRow>> RecentPunchesAsync(WorkforceActor actor, DateTimeOffset fromUtc, DateTimeOffset toUtc, int take = 50,
        CancellationToken ct = default)
    {
        Authorize(actor, WorkforcePermissions.AttendanceView);
        await using var db = dbFactory();
        var emps = (await ScopedEmployeesAsync(db, actor, ct)).ToDictionary(e => e.Id);
        var events = await EffectiveEventsAsync(db, null, fromUtc.UtcTicks, toUtc.UtcTicks, ct);
        return events.Where(e => emps.ContainsKey(e.EmployeeId)).OrderByDescending(e => e.OccurredTicks).ThenByDescending(e => e.Seq).Take(Math.Max(1, take))
            .Select(e => new PunchFeedRow(e.Id, e.EmployeeId, emps[e.EmployeeId].Code, emps[e.EmployeeId].Name, e.Type, e.Source, e.OccurredAtUtc)).ToList();
    }

    public async Task<IReadOnlyList<OvertimeDecision>> OvertimeDecisionsAsync(WorkforceActor actor, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        Authorize(actor, WorkforcePermissions.AttendanceView);
        await using var db = dbFactory();
        var ids = (await ScopedEmployeesAsync(db, actor, ct)).Select(e => e.Id).ToHashSet();
        return (await LoadOvertimeDecisionsAsync(db, ct)).Where(d => ids.Contains(d.EmployeeId) && d.Date >= from && d.Date <= to).ToList();
    }

    /// <summary>Approves or rejects the overtime of one employee on one day (needs <c>workforce.corrections.approve</c>, scoped).</summary>
    public Task<OvertimeDecision> DecideOvertimeAsync(WorkforceActor actor, Guid employeeId, DateOnly date, int minutes, bool approve, string? note = null,
        CancellationToken ct = default) => InTransactionAsync(async (db, now) =>
    {
        await AuthorizeAsync(db, actor, WorkforcePermissions.CorrectionsApprove, employeeId, ct);
        Require(minutes >= 0, "wf.err.minutes");
        var all = await LoadOvertimeDecisionsAsync(db, ct);
        all.RemoveAll(d => d.EmployeeId == employeeId && d.Date == date);
        var decision = new OvertimeDecision(employeeId, date, minutes, approve ? RequestStatuses.Approved : RequestStatuses.Rejected, actor.UserName, now, O(note));
        all.Add(decision);
        await db.SetValueAsync(OvertimeDecisionsKey, JsonSerializer.Serialize(all, Json), ct);
        return decision;
    }, ct);

    private static async Task<List<OvertimeDecision>> LoadOvertimeDecisionsAsync(LocalDb db, CancellationToken ct)
    {
        var json = await db.GetValueAsync(OvertimeDecisionsKey, ct);
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<OvertimeDecision>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }
}
