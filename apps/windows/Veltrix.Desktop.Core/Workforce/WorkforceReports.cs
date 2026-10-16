using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Veltrix.Contracts.Reporting;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Standalone;

namespace Veltrix.Desktop.Core.Workforce;

public sealed record DailyAttendanceRow(DateOnly Date, string EmployeeNo, string Employee, string? Department, string? Shift, string Status, DateTime? FirstIn,
    DateTime? LastOut, int WorkedMinutes, int BreakMinutes, int LateMinutes, int EarlyLeaveMinutes, int OvertimeMinutes, bool MissedPunch, string Anomalies);

public sealed record MonthlyAttendanceRow(string EmployeeNo, string Employee, string? Department, int ScheduledDays, int PresentDays, int AbsentDays, int LeaveDays,
    int IncompleteDays, decimal WorkedHours, int LateMinutes, int EarlyLeaveMinutes, int OvertimeMinutes);

public sealed record DepartmentAttendanceRow(string Department, int Employees, int PresentDays, int AbsentDays, int LeaveDays, decimal WorkedHours, int LateMinutes,
    int OvertimeMinutes);

public sealed record LeaveReportRow(string EmployeeNo, string Employee, string LeaveType, DateOnly From, DateOnly To, decimal Days, string Status, string? Attachment);

public sealed record ShiftCoverageRow(DateOnly Date, string Shift, int Planned, int Present, int Absent, int OnLeave);

public sealed record DeviceActivityRow(string Device, string Kind, int Received, int Processed, int Rejected, DateTimeOffset? LastSeenAt, int ErrorCount, string? LastError);

public sealed record AccessDenialRow(DateTimeOffset At, string? Door, string? EmployeeNo, string? Employee, string Credential, string Reason);

/// <summary>Workforce report data providers (row models) with CSV export. Excel / PDF rendering belongs to the UI layer.</summary>
public sealed class WorkforceReports(WorkforceService service, Func<LocalDb> dbFactory)
{
    private async Task<(List<DayResult> Days, Dictionary<Guid, LocalEmployee> Employees, Dictionary<Guid, string> Departments)> DaysAsync(WorkforceActor actor,
        DateOnly from, DateOnly to, Guid? employeeId, CancellationToken ct)
    {
        Guard(actor, WorkforcePermissions.ReportsView);
        var days = (await service.ComputeAttendanceAsync(actor, from, to, employeeId, ct)).ToList();
        await using var db = dbFactory();
        return (days, (await db.Employees.AsNoTracking().ToListAsync(ct)).ToDictionary(e => e.Id),
            (await db.WfDepartments.AsNoTracking().ToListAsync(ct)).ToDictionary(d => d.Id, d => d.Name));
    }

    private static void Guard(WorkforceActor actor, string permission, bool global = false)
    {
        if (!actor.Has(permission) || global && WorkforcePermissions.IsScoped(actor.Role)) throw new WorkforceAccessDeniedException(permission);
    }

    public async Task<IReadOnlyList<DailyAttendanceRow>> DailyAsync(WorkforceActor actor, DateOnly from, DateOnly to, Guid? employeeId = null, CancellationToken ct = default)
    {
        var (days, emps, depts) = await DaysAsync(actor, from, to, employeeId, ct);
        return days.OrderBy(d => d.Date).ThenBy(d => emps[d.EmployeeId].Name).Select(d =>
        {
            var e = emps[d.EmployeeId];
            return new DailyAttendanceRow(d.Date, e.Code, e.Name, e.DepartmentId is { } dep ? depts.GetValueOrDefault(dep) : null, d.ShiftCode, d.Status, d.FirstIn, d.LastOut,
                d.WorkedMinutes, d.BreakMinutes, d.LateMinutes, d.EarlyLeaveMinutes, d.OvertimeMinutes, d.MissedPunch, string.Join(' ', d.Anomalies));
        }).ToList();
    }

    public async Task<IReadOnlyList<DailyAttendanceRow>> LateAsync(WorkforceActor actor, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        (await DailyAsync(actor, from, to, null, ct)).Where(r => r.LateMinutes > 0).ToList();

    public async Task<IReadOnlyList<DailyAttendanceRow>> AbsenceAsync(WorkforceActor actor, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        (await DailyAsync(actor, from, to, null, ct)).Where(r => r.Status == DayStatuses.Absent || r.Anomalies.Contains("segment_absent")).ToList();

    public async Task<IReadOnlyList<DailyAttendanceRow>> OvertimeAsync(WorkforceActor actor, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        (await DailyAsync(actor, from, to, null, ct)).Where(r => r.OvertimeMinutes > 0).ToList();

    public async Task<IReadOnlyList<MonthlyAttendanceRow>> MonthlyAsync(WorkforceActor actor, int year, int month, Guid? employeeId = null, CancellationToken ct = default)
    {
        var (from, to) = ShiftPlanner.Month(year, month);
        var (days, emps, depts) = await DaysAsync(actor, from, to, employeeId, ct);
        return days.GroupBy(d => d.EmployeeId).Select(g =>
        {
            var e = emps[g.Key];
            return new MonthlyAttendanceRow(e.Code, e.Name, e.DepartmentId is { } dep ? depts.GetValueOrDefault(dep) : null, g.Count(d => d.IsScheduledWorkday),
                g.Count(d => d.Status == DayStatuses.Present), g.Count(d => d.Status == DayStatuses.Absent), g.Count(d => d.Status == DayStatuses.Leave),
                g.Count(d => d.Status == DayStatuses.Incomplete), Math.Round(g.Sum(d => d.WorkedMinutes) / 60m, 2), g.Sum(d => d.LateMinutes), g.Sum(d => d.EarlyLeaveMinutes),
                g.Sum(d => d.OvertimeMinutes));
        }).OrderBy(r => r.Employee).ToList();
    }

    public async Task<IReadOnlyList<DepartmentAttendanceRow>> DepartmentAsync(WorkforceActor actor, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var (days, emps, depts) = await DaysAsync(actor, from, to, null, ct);
        return days.GroupBy(d => emps[d.EmployeeId].DepartmentId is { } dep ? depts.GetValueOrDefault(dep, "?") : "—").Select(g => new DepartmentAttendanceRow(g.Key,
            g.Select(d => d.EmployeeId).Distinct().Count(), g.Count(d => d.Status == DayStatuses.Present), g.Count(d => d.Status == DayStatuses.Absent),
            g.Count(d => d.Status == DayStatuses.Leave), Math.Round(g.Sum(d => d.WorkedMinutes) / 60m, 2), g.Sum(d => d.LateMinutes), g.Sum(d => d.OvertimeMinutes)))
            .OrderBy(r => r.Department).ToList();
    }

    public async Task<IReadOnlyList<LeaveReportRow>> LeaveAsync(WorkforceActor actor, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        Guard(actor, WorkforcePermissions.ReportsView);
        var requests = await service.LeaveRequestsAsync(actor, null, ct);
        await using var db = dbFactory();
        var emps = (await db.Employees.AsNoTracking().ToListAsync(ct)).ToDictionary(e => e.Id);
        var types = (await db.WfLeaveTypes.AsNoTracking().ToListAsync(ct)).ToDictionary(t => t.Id, t => t.Code);
        return requests.Where(r => r.From <= to && r.To >= from).OrderBy(r => r.From)
            .Select(r => new LeaveReportRow(emps[r.EmployeeId].Code, emps[r.EmployeeId].Name, types.GetValueOrDefault(r.LeaveTypeId, "?"), r.From, r.To, r.Days, r.Status, r.AttachmentPath))
            .ToList();
    }

    public async Task<IReadOnlyList<LeaveBalanceRow>> LeaveBalancesAsync(WorkforceActor actor, CancellationToken ct = default)
    {
        Guard(actor, WorkforcePermissions.ReportsView);
        return await service.LeaveBalancesAsync(actor, null, ct);
    }

    /// <summary>Per day and shift: planned headcount vs present / absent / on leave.</summary>
    public async Task<IReadOnlyList<ShiftCoverageRow>> ShiftCoverageAsync(WorkforceActor actor, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var (days, _, _) = await DaysAsync(actor, from, to, null, ct);
        return days.Where(d => d.ShiftCode is not null && !d.IsHoliday).GroupBy(d => (d.Date, d.ShiftCode!)).Select(g => new ShiftCoverageRow(g.Key.Date, g.Key.Item2,
            g.Count(), g.Count(d => d.Status is DayStatuses.Present or DayStatuses.Incomplete), g.Count(d => d.Status == DayStatuses.Absent),
            g.Count(d => d.Status == DayStatuses.Leave))).OrderBy(r => r.Date).ThenBy(r => r.Shift).ToList();
    }

    public async Task<IReadOnlyList<WfPayrollPrepLine>> PayrollPrepAsync(WorkforceActor actor, int year, int month, CancellationToken ct = default)
    {
        Guard(actor, WorkforcePermissions.PayrollView, global: true);
        return (await service.PayrollPrepAsync(actor, year, month, ct))?.Lines ?? [];
    }

    public async Task<IReadOnlyList<DeviceActivityRow>> DeviceActivityAsync(WorkforceActor actor, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        Guard(actor, WorkforcePermissions.ReportsView, global: true);
        Guard(actor, WorkforcePermissions.AccessView, global: true);
        await using var db = dbFactory();
        var inbox = (await db.WfDeviceInbox.AsNoTracking().ToListAsync(ct)).Where(i => i.ReceivedAt >= fromUtc && i.ReceivedAt <= toUtc).ToLookup(i => i.DeviceId);
        return (await db.WfDevices.AsNoTracking().ToListAsync(ct)).OrderBy(d => d.Name).Select(d => new DeviceActivityRow(d.Name, d.Kind, inbox[d.Id].Count(),
            inbox[d.Id].Count(i => i.Status == "Processed"), inbox[d.Id].Count(i => i.Status == "Rejected"), d.LastSeenAt, d.ErrorCount, d.LastError)).ToList();
    }

    public async Task<IReadOnlyList<AccessDenialRow>> AccessDenialsAsync(WorkforceActor actor, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        Guard(actor, WorkforcePermissions.ReportsView, global: true);
        var logs = await service.AccessLogsAsync(actor, fromUtc, toUtc, deniedOnly: true, ct);
        await using var db = dbFactory();
        var emps = (await db.Employees.AsNoTracking().ToListAsync(ct)).ToDictionary(e => e.Id);
        var doors = (await db.WfDoors.AsNoTracking().ToListAsync(ct)).ToDictionary(d => d.Id, d => d.Name);
        return logs.Select(l => new AccessDenialRow(l.At, l.DoorId is { } d ? doors.GetValueOrDefault(d) : null, l.EmployeeId is { } e ? emps[e].Code : null,
            l.EmployeeId is { } e2 ? emps[e2].Name : null, l.CredentialKind + " " + l.CredentialHint, l.Reason)).ToList();
    }

    /// <summary>CSV of any row model (public properties in declaration order), with spreadsheet-formula protection.</summary>
    public static string ToCsv<T>(IEnumerable<T> rows, string key = "workforce", string? title = null)
    {
        var props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0).ToArray();
        var table = new ReportTable(key, title ?? typeof(T).Name, props.Select(p => new ReportColumn(p.Name, p.Name, p.PropertyType.Name)).ToList(),
            rows.Select(r => (IReadOnlyList<object?>)props.Select(p => p.GetValue(r) switch
            {
                DateTime dt => dt.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture),
                DateOnly d => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                var v => v,
            }).ToList()).ToList(),
            new Dictionary<string, object?>(), DateTimeOffset.UtcNow, new Dictionary<string, string?>());
        return StandaloneStore.ToCsv(table);
    }
}
