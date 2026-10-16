using Veltrix.Desktop.Core.Standalone;

namespace Veltrix.Desktop.Core.Workforce;

/// <summary>Fine-grained workforce permissions (enforced by <see cref="WorkforceService"/>, not only by the UI).</summary>
public static class WorkforcePermissions
{
    public const string EmployeesView = "workforce.employees.view";
    public const string EmployeesManage = "workforce.employees.manage";
    public const string CredentialsManage = "workforce.credentials.manage";
    public const string AuditView = "workforce.audit.view";
    public const string AttendanceView = "workforce.attendance.view";
    /// <summary>Record a punch for oneself (kiosk / self-service).</summary>
    public const string AttendanceSelf = "workforce.attendance.self";
    /// <summary>Record a manual punch for someone else (reason + authorizer mandatory).</summary>
    public const string AttendanceManual = "workforce.attendance.manual";
    public const string CorrectionsRequest = "workforce.corrections.request";
    public const string CorrectionsApprove = "workforce.corrections.approve";
    public const string ShiftsView = "workforce.shifts.view";
    public const string ShiftsManage = "workforce.shifts.manage";
    public const string SwapsRequest = "workforce.swaps.request";
    public const string SwapsApprove = "workforce.swaps.approve";
    public const string LeaveRequest = "workforce.leave.request";
    public const string LeaveApprove = "workforce.leave.approve";
    public const string LeaveManage = "workforce.leave.manage";
    public const string PayrollView = "workforce.payroll.view";
    public const string PayrollPrepare = "workforce.payroll.prepare";
    public const string PayrollPost = "workforce.payroll.post";
    public const string PayrollConfigure = "workforce.payroll.configure";
    public const string DevicesManage = "workforce.devices.manage";
    public const string AccessView = "workforce.access.view";
    public const string AccessManage = "workforce.access.manage";
    public const string EmergencyUnlock = "workforce.access.emergency";
    public const string ReportsView = "workforce.reports.view";

    public static readonly IReadOnlyList<string> All =
    [
        EmployeesView, EmployeesManage, CredentialsManage, AuditView, AttendanceView, AttendanceSelf, AttendanceManual, CorrectionsRequest, CorrectionsApprove,
        ShiftsView, ShiftsManage, SwapsRequest, SwapsApprove, LeaveRequest, LeaveApprove, LeaveManage, PayrollView, PayrollPrepare, PayrollPost, PayrollConfigure,
        DevicesManage, AccessView, AccessManage, EmergencyUnlock, ReportsView,
    ];

    /// <summary>Read-only permissions (the Auditor role gets exactly these).</summary>
    public static readonly IReadOnlyList<string> ReadOnly = [EmployeesView, AuditView, AttendanceView, ShiftsView, PayrollView, AccessView, ReportsView];

    public static IReadOnlyList<string> ForRole(string role) => role switch
    {
        LocalRoles.Owner => All,
        LocalRoles.HrAdmin => All,
        LocalRoles.DeptManager =>
        [
            EmployeesView, AttendanceView, AttendanceSelf, AttendanceManual, CorrectionsRequest, CorrectionsApprove, ShiftsView, ShiftsManage, SwapsRequest,
            SwapsApprove, LeaveRequest, LeaveApprove, ReportsView, AccessView, EmergencyUnlock,
        ],
        LocalRoles.AttendanceSupervisor =>
        [
            EmployeesView, AttendanceView, AttendanceSelf, AttendanceManual, CorrectionsRequest, ShiftsView, SwapsRequest, LeaveRequest, ReportsView, AccessView,
            EmergencyUnlock,
        ],
        LocalRoles.Employee => [AttendanceSelf, AttendanceView, CorrectionsRequest, ShiftsView, SwapsRequest, LeaveRequest],
        LocalRoles.Auditor => ReadOnly,
        _ => [],
    };

    /// <summary>True for roles whose access is limited to their department (managers) or to themselves (employees).</summary>
    public static bool IsScoped(string role) => role is LocalRoles.DeptManager or LocalRoles.Employee;
}

/// <summary>Who performs a workforce operation. Department managers are scoped to <see cref="DepartmentId"/>, employees to themselves.</summary>
public sealed record WorkforceActor(string UserName, string Role, Guid? EmployeeId = null, Guid? DepartmentId = null)
{
    public IReadOnlyList<string> Permissions => WorkforcePermissions.ForRole(Role);
    public bool Has(string permission) => Permissions.Contains(permission);

    /// <summary>Built-in actor for device/kiosk processing (records punches identified by a credential).</summary>
    public static WorkforceActor System { get; } = new("system", LocalRoles.Owner);

    public static WorkforceActor From(LocalUser user, Guid? departmentId = null) => new(user.Name, user.Role, user.EmployeeId, departmentId);
}

public sealed class WorkforceAccessDeniedException(string permission) : UnauthorizedAccessException("wf.err.forbidden:" + permission)
{
    public string Permission { get; } = permission;
}
