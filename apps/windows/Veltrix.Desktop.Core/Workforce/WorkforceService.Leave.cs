using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Standalone;

namespace Veltrix.Desktop.Core.Workforce;

public sealed record LeaveTypeInput(string Code, string Name, bool IsPaid, decimal AccrualDaysPerMonth = 0, bool TracksBalance = false, bool AllowNegative = false,
    bool IsActive = true);

public sealed record LeaveRequestInput(Guid EmployeeId, Guid LeaveTypeId, DateOnly From, DateOnly To, string? Reason = null, string? AttachmentPath = null);

public sealed record LeaveBalanceRow(Guid EmployeeId, string EmployeeName, Guid LeaveTypeId, string LeaveType, decimal Accrued, decimal Taken, decimal Adjusted,
    decimal Balance);

public sealed partial class WorkforceService
{
    /// <summary>Default leave types (accrual 0: the user configures entitlements — none are assumed).</summary>
    public static readonly IReadOnlyList<LeaveTypeInput> DefaultLeaveTypes =
    [
        new("annual", "Annual leave", true, 0, TracksBalance: true),
        new("sick", "Sick leave", true),
        new("unpaid", "Unpaid leave", false),
        new("exceptional", "Exceptional leave", true),
    ];

    public async Task EnsureDefaultLeaveTypesAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var codes = (await db.WfLeaveTypes.Select(t => t.Code).ToListAsync(ct)).ToHashSet();
        foreach (var t in DefaultLeaveTypes.Where(t => !codes.Contains(t.Code)))
            db.WfLeaveTypes.Add(new WfLeaveType
            {
                Id = Guid.NewGuid(), Code = t.Code, Name = t.Name, IsPaid = t.IsPaid, AccrualDaysPerMonth = t.AccrualDaysPerMonth, TracksBalance = t.TracksBalance,
            });
        await db.SaveChangesAsync(ct);
    }

    public Task<WfLeaveType> UpsertLeaveTypeAsync(WorkforceActor actor, Guid? id, LeaveTypeInput input, CancellationToken ct = default) =>
        InTransactionAsync(async (db, _) =>
        {
            AuthorizeGlobal(actor, WorkforcePermissions.LeaveManage);
            Require(!string.IsNullOrWhiteSpace(input.Code) && !string.IsNullOrWhiteSpace(input.Name), "wf.err.nameRequired");
            Require(input.AccrualDaysPerMonth >= 0, "wf.err.accrual");
            var code = input.Code.Trim().ToLowerInvariant();
            var t = id is { } x ? await db.WfLeaveTypes.FindAsync([x], ct) ?? throw new InvalidOperationException("wf.err.unknown") : null;
            Require(!(await db.WfLeaveTypes.Where(z => z.Code == code).Select(z => z.Id).ToListAsync(ct)).Any(z => z != t?.Id), "wf.err.codeTaken");
            if (t is null) db.WfLeaveTypes.Add(t = new WfLeaveType { Id = Guid.NewGuid() });
            t.Code = code;
            t.Name = input.Name.Trim();
            t.IsPaid = input.IsPaid;
            t.AccrualDaysPerMonth = input.AccrualDaysPerMonth;
            t.TracksBalance = input.TracksBalance;
            t.AllowNegative = input.AllowNegative;
            t.IsActive = input.IsActive;
            return t;
        }, ct);

    public async Task<IReadOnlyList<WfLeaveType>> LeaveTypesAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory();
        return (await db.WfLeaveTypes.AsNoTracking().ToListAsync(ct)).OrderBy(t => t.Code).ToList();
    }

    /// <summary>Credits the monthly accrual of every accruing type to active employees hired by the month end. Idempotent per month.</summary>
    public Task<int> AccrueLeaveAsync(WorkforceActor actor, int year, int month, CancellationToken ct = default) => InTransactionAsync(async (db, _) =>
    {
        AuthorizeGlobal(actor, WorkforcePermissions.LeaveManage);
        var (_, end) = ShiftPlanner.Month(year, month);
        var period = end.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var types = await db.WfLeaveTypes.Where(t => t.IsActive).ToListAsync(ct);
        var employees = (await db.Employees.Where(e => e.IsActive).ToListAsync(ct)).Where(e => e.HireDate <= end).ToList();
        var existing = (await db.WfLeaveLedger.Where(l => l.Kind == "Accrual").Select(l => l.RefKey).ToListAsync(ct)).ToHashSet();
        var n = 0;
        foreach (var t in types.Where(t => t.AccrualDaysPerMonth > 0))
            foreach (var e in employees)
            {
                var key = $"accrual:{t.Id:N}:{e.Id:N}:{period}";
                if (existing.Contains(key)) continue;
                db.WfLeaveLedger.Add(new WfLeaveLedger { Id = Guid.NewGuid(), EmployeeId = e.Id, LeaveTypeId = t.Id, Date = end, Days = t.AccrualDaysPerMonth, Kind = "Accrual", RefKey = key });
                n++;
            }
        return n;
    }, ct);

    public Task<WfLeaveLedger> AdjustLeaveBalanceAsync(WorkforceActor actor, Guid employeeId, Guid leaveTypeId, decimal days, string note, CancellationToken ct = default) =>
        InTransactionAsync(async (db, now) =>
        {
            AuthorizeGlobal(actor, WorkforcePermissions.LeaveManage);
            Require(days != 0 && !string.IsNullOrWhiteSpace(note), "wf.err.adjustment");
            var l = new WfLeaveLedger
            {
                Id = Guid.NewGuid(), EmployeeId = employeeId, LeaveTypeId = leaveTypeId, Date = DateOnly.FromDateTime(now.UtcDateTime), Days = days, Kind = "Adjustment",
                RefKey = "adjust:" + Guid.NewGuid().ToString("N"), Note = note.Trim(),
            };
            db.WfLeaveLedger.Add(l);
            Audit(db, employeeId, actor, "leave.adjust", null, new { leaveTypeId, days, note }, now);
            return l;
        }, ct);

    /// <summary>Requests leave. Days = planned working days in the range (holidays and rest days excluded; calendar days minus holidays when no plan exists).</summary>
    public Task<WfLeaveRequest> RequestLeaveAsync(WorkforceActor actor, LeaveRequestInput input, CancellationToken ct = default) => InTransactionAsync(async (db, now) =>
    {
        await AuthorizeAsync(db, actor, WorkforcePermissions.LeaveRequest, input.EmployeeId, ct);
        Require(input.To >= input.From && input.To.DayNumber - input.From.DayNumber < 366, "wf.err.period");
        Require(await db.WfLeaveTypes.AnyAsync(t => t.Id == input.LeaveTypeId && t.IsActive, ct), "wf.err.unknownLeaveType");
        var overlapping = (await db.WfLeaveRequests.Where(l => l.EmployeeId == input.EmployeeId && (l.Status == RequestStatuses.Pending || l.Status == RequestStatuses.Approved))
            .ToListAsync(ct)).Any(l => l.From <= input.To && l.To >= input.From);
        Require(!overlapping, "wf.err.leaveOverlap");
        var planner = await PlannerAsync(db, ct);
        var hasPlan = await db.WfShiftAssignments.AnyAsync(a => a.EmployeeId == input.EmployeeId, ct);
        decimal days = 0;
        for (var d = input.From; d <= input.To; d = d.AddDays(1))
        {
            var p = planner.Plan(input.EmployeeId, d);
            if (hasPlan ? p.IsScheduledWorkday : !p.IsHoliday) days++;
        }
        Require(days > 0, "wf.err.noWorkingDays");
        var r = new WfLeaveRequest
        {
            Id = Guid.NewGuid(), EmployeeId = input.EmployeeId, LeaveTypeId = input.LeaveTypeId, From = input.From, To = input.To, Days = days, Reason = O(input.Reason),
            AttachmentPath = O(input.AttachmentPath), RequestedBy = actor.UserName, RequestedAt = now,
        };
        db.WfLeaveRequests.Add(r);
        return r;
    }, ct);

    /// <summary>Approves / rejects a leave request (manager in scope, never one's own). Approval debits the balance; balance-tracked types cannot go negative unless allowed.</summary>
    public Task<WfLeaveRequest> DecideLeaveAsync(WorkforceActor actor, Guid requestId, bool approve, CancellationToken ct = default) => InTransactionAsync(async (db, now) =>
    {
        var r = await db.WfLeaveRequests.FindAsync([requestId], ct) ?? throw new InvalidOperationException("wf.err.unknown");
        await AuthorizeAsync(db, actor, WorkforcePermissions.LeaveApprove, r.EmployeeId, ct);
        if (actor.EmployeeId == r.EmployeeId && actor.Role != LocalRoles.Owner) throw new WorkforceAccessDeniedException(WorkforcePermissions.LeaveApprove);
        Require(r.Status == RequestStatuses.Pending, "wf.err.alreadyDecided");
        r.Status = approve ? RequestStatuses.Approved : RequestStatuses.Rejected;
        r.DecidedBy = actor.UserName;
        r.DecidedAt = now;
        if (!approve) return r;
        var type = await db.WfLeaveTypes.FirstAsync(t => t.Id == r.LeaveTypeId, ct);
        if (type.TracksBalance && !type.AllowNegative)
        {
            var balance = (await db.WfLeaveLedger.Where(l => l.EmployeeId == r.EmployeeId && l.LeaveTypeId == r.LeaveTypeId).Select(l => l.Days).ToListAsync(ct)).Sum();
            Require(balance >= r.Days, "wf.err.insufficientBalance");
        }
        db.WfLeaveLedger.Add(new WfLeaveLedger
        {
            Id = Guid.NewGuid(), EmployeeId = r.EmployeeId, LeaveTypeId = r.LeaveTypeId, Date = r.From, Days = -r.Days, Kind = "Taken", RefKey = "leave:" + r.Id.ToString("N"),
        });
        return r;
    }, ct);

    /// <summary>Cancels a pending or approved request; an approved one is credited back (compensating ledger line).</summary>
    public Task<WfLeaveRequest> CancelLeaveAsync(WorkforceActor actor, Guid requestId, CancellationToken ct = default) => InTransactionAsync(async (db, _) =>
    {
        var r = await db.WfLeaveRequests.FindAsync([requestId], ct) ?? throw new InvalidOperationException("wf.err.unknown");
        await AuthorizeAsync(db, actor, r.Status == RequestStatuses.Approved ? WorkforcePermissions.LeaveApprove : WorkforcePermissions.LeaveRequest, r.EmployeeId, ct);
        Require(r.Status is RequestStatuses.Pending or RequestStatuses.Approved, "wf.err.alreadyDecided");
        if (r.Status == RequestStatuses.Approved)
            db.WfLeaveLedger.Add(new WfLeaveLedger
            {
                Id = Guid.NewGuid(), EmployeeId = r.EmployeeId, LeaveTypeId = r.LeaveTypeId, Date = r.From, Days = r.Days, Kind = "Adjustment",
                RefKey = "leave-cancel:" + r.Id.ToString("N"), Note = "cancelled",
            });
        r.Status = RequestStatuses.Cancelled;
        return r;
    }, ct);

    public async Task<IReadOnlyList<WfLeaveRequest>> LeaveRequestsAsync(WorkforceActor actor, string? status = null, CancellationToken ct = default)
    {
        Authorize(actor, WorkforcePermissions.AttendanceView);
        await using var db = dbFactory();
        var ids = (await ScopedEmployeesAsync(db, actor, ct)).Select(e => e.Id).ToHashSet();
        return (await db.WfLeaveRequests.AsNoTracking().ToListAsync(ct)).Where(r => ids.Contains(r.EmployeeId) && (status is null || r.Status == status))
            .OrderByDescending(r => r.From).ToList();
    }

    public async Task<IReadOnlyList<LeaveBalanceRow>> LeaveBalancesAsync(WorkforceActor actor, Guid? employeeId = null, CancellationToken ct = default)
    {
        Authorize(actor, WorkforcePermissions.AttendanceView);
        await using var db = dbFactory();
        var employees = (await ScopedEmployeesAsync(db, actor, ct)).Where(e => employeeId is null || e.Id == employeeId).ToList();
        var types = await db.WfLeaveTypes.AsNoTracking().Where(t => t.IsActive).ToListAsync(ct);
        var ledger = (await db.WfLeaveLedger.AsNoTracking().ToListAsync(ct)).ToLookup(l => (l.EmployeeId, l.LeaveTypeId));
        return (from e in employees
                from t in types.OrderBy(t => t.Code)
                let lines = ledger[(e.Id, t.Id)].ToList()
                select new LeaveBalanceRow(e.Id, e.Name, t.Id, t.Code, lines.Where(l => l.Kind == "Accrual").Sum(l => l.Days),
                    -lines.Where(l => l.Kind == "Taken").Sum(l => l.Days), lines.Where(l => l.Kind == "Adjustment").Sum(l => l.Days), lines.Sum(l => l.Days))).ToList();
    }
}
