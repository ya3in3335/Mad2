using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Veltrix.Contracts.Hr;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Hr;

public sealed class HrModule : IModule
{
    public string Name => "hr";

    public void Register(IServiceCollection services)
    {
        services.AddScoped<HrService>();
        services.AddScoped<IOnboardingContributor, HrOnboardingContributor>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var g = app.MapModuleGroup("hr", "HR").RequireFeature(Features.Hr);
        g.MapGet("/departments", (HrService s, CancellationToken ct) => s.DepartmentsAsync(ct)).RequirePermission(Permissions.HrView);
        g.MapPost("/departments", (UpsertDepartmentRequest r, HrService s, CancellationToken ct) => s.UpsertDepartmentAsync(null, r, ct)).RequirePermission(Permissions.HrManage);
        g.MapPut("/departments/{id:guid}", (Guid id, UpsertDepartmentRequest r, HrService s, CancellationToken ct) => s.UpsertDepartmentAsync(id, r, ct)).RequirePermission(Permissions.HrManage);
        g.MapGet("/job-titles", (HrService s, CancellationToken ct) => s.JobTitlesAsync(ct)).RequirePermission(Permissions.HrView);
        g.MapPost("/job-titles", (UpsertJobTitleRequest r, HrService s, CancellationToken ct) => s.UpsertJobTitleAsync(null, r, ct)).RequirePermission(Permissions.HrManage);

        g.MapGet("/employees", ([AsParameters] PageQuery q, Guid? branchId, string? status, HrService s, CancellationToken ct) => s.EmployeesAsync(q, branchId, status, ct)).RequirePermission(Permissions.HrView);
        g.MapGet("/employees/{id:guid}", (Guid id, HrService s, CancellationToken ct) => s.GetEmployeeAsync(id, ct)).RequirePermission(Permissions.HrView);
        g.MapPost("/employees", (UpsertEmployeeRequest r, HrService s, CancellationToken ct) => s.UpsertEmployeeAsync(null, r, ct)).RequirePermission(Permissions.HrManage);
        g.MapPut("/employees/{id:guid}", (Guid id, UpsertEmployeeRequest r, HrService s, CancellationToken ct) => s.UpsertEmployeeAsync(id, r, ct)).RequirePermission(Permissions.HrManage);
        g.MapGet("/employees/{id:guid}/documents", (Guid id, HrService s, CancellationToken ct) => s.DocumentsAsync(id, ct)).RequirePermission(Permissions.HrSensitive);
        g.MapPost("/employees/{id:guid}/documents", async (Guid id, HttpRequest req, HrService s, CancellationToken ct) =>
        {
            if (!req.HasFormContentType) throw ValidationException.For("file", "Use multipart/form-data.");
            var form = await req.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? throw ValidationException.For("file", "Missing file.");
            DateOnly? expires = DateOnly.TryParse(form["expiresOn"], out var d) ? d : null;
            await using var stream = file.OpenReadStream();
            return await s.UploadDocumentAsync(id, form["type"].ToString() is { Length: > 0 } t ? t : "other", expires, stream, file.FileName, file.ContentType, ct);
        }).RequirePermission(Permissions.HrSensitive).DisableAntiforgery();
        g.MapGet("/documents/{id:guid}", async (Guid id, HrService s, CancellationToken ct) =>
            await s.OpenDocumentAsync(id, ct) is { } d ? Results.File(d.Stream, d.ContentType, d.Name) : Results.NotFound()).RequirePermission(Permissions.HrSensitive);

        g.MapGet("/schedules", (HrService s, CancellationToken ct) => s.SchedulesAsync(ct)).RequirePermission(Permissions.HrView);
        g.MapPost("/schedules", (UpsertWorkScheduleRequest r, HrService s, CancellationToken ct) => s.UpsertScheduleAsync(null, r, ct)).RequirePermission(Permissions.HrManage);
        g.MapPut("/schedules/{id:guid}", (Guid id, UpsertWorkScheduleRequest r, HrService s, CancellationToken ct) => s.UpsertScheduleAsync(id, r, ct)).RequirePermission(Permissions.HrManage);

        // Attendance & leave: employees may act on their own records (checked in the service).
        var self = app.MapModuleGroup("hr", "HR").RequireFeature(Features.Hr);
        self.MapPost("/attendance/check-in", (CheckRequest r, HrService s, CancellationToken ct) => s.CheckAsync(r, true, ct));
        self.MapPost("/attendance/check-out", (CheckRequest r, HrService s, CancellationToken ct) => s.CheckAsync(r, false, ct));
        self.MapGet("/attendance", (Guid? employeeId, DateOnly from, DateOnly to, HrService s, CancellationToken ct) => s.AttendanceAsync(employeeId, from, to, ct));
        g.MapPost("/attendance", (ManualAttendanceRequest r, HrService s, CancellationToken ct) => s.RecordAttendanceAsync(r, ct)).RequirePermission(Permissions.HrManage);
        g.MapPost("/attendance/{id:guid}/approve-overtime", (Guid id, HrService s, CancellationToken ct) => s.ApproveOvertimeAsync(id, ct)).RequirePermission(Permissions.LeaveApprove);

        self.MapGet("/leave-types", (HrService s, CancellationToken ct) => s.LeaveTypesAsync(ct));
        g.MapPost("/leave-types", (UpsertLeaveTypeRequest r, HrService s, CancellationToken ct) => s.UpsertLeaveTypeAsync(null, r, ct)).RequirePermission(Permissions.HrManage);
        g.MapPut("/leave-types/{id:guid}", (Guid id, UpsertLeaveTypeRequest r, HrService s, CancellationToken ct) => s.UpsertLeaveTypeAsync(id, r, ct)).RequirePermission(Permissions.HrManage);
        self.MapGet("/leave", (Guid? employeeId, string? status, HrService s, CancellationToken ct) => s.LeaveRequestsAsync(employeeId, status, ct)).RequirePermission(Permissions.LeaveRequest);
        self.MapPost("/leave", (CreateLeaveRequest r, HrService s, CancellationToken ct) => s.RequestLeaveAsync(r, ct)).RequirePermission(Permissions.LeaveRequest);
        g.MapPost("/leave/{id:guid}/decide", (Guid id, DecideRequest r, HrService s, CancellationToken ct) => s.DecideLeaveAsync(id, r, ct)).RequirePermission(Permissions.LeaveApprove);
        self.MapGet("/leave/balances/{employeeId:guid}", (Guid employeeId, int? year, HrService s, IClock clock, CancellationToken ct) =>
            s.BalancesAsync(employeeId, year ?? clock.UtcNow.Year, ct)).RequirePermission(Permissions.LeaveRequest);

        self.MapPost("/claims", (CreateExpenseClaimRequest r, HrService s, CancellationToken ct) => s.SubmitClaimAsync(r, ct)).RequirePermission(Permissions.LeaveRequest);
        g.MapGet("/claims", (Guid? employeeId, HrService s, CancellationToken ct) => s.ClaimsAsync(employeeId, ct)).RequirePermission(Permissions.HrView);
        g.MapPost("/claims/{id:guid}/decide", (Guid id, DecideRequest r, HrService s, CancellationToken ct) => s.DecideClaimAsync(id, r, ct)).RequirePermission(Permissions.LeaveApprove);
        g.MapPost("/claims/{id:guid}/reimburse", (Guid id, ReimburseRequest r, HrService s, CancellationToken ct) => s.ReimburseClaimAsync(id, r, ct)).RequirePermission(Permissions.AccountingPost);

        g.MapGet("/salary-structures", (HrService s, CancellationToken ct) => s.StructuresAsync(ct)).RequirePermission(Permissions.HrPayroll);
        g.MapPost("/salary-structures", (UpsertSalaryStructureRequest r, HrService s, CancellationToken ct) => s.UpsertStructureAsync(null, r, ct)).RequirePermission(Permissions.HrPayroll);
        g.MapPut("/salary-structures/{id:guid}", (Guid id, UpsertSalaryStructureRequest r, HrService s, CancellationToken ct) => s.UpsertStructureAsync(id, r, ct)).RequirePermission(Permissions.HrPayroll);
        g.MapPost("/payroll/inputs", (CreatePayrollInputRequest r, HrService s, CancellationToken ct) => s.AddInputAsync(r, ct)).RequirePermission(Permissions.HrPayroll);
        g.MapGet("/payroll/runs", (HrService s, CancellationToken ct) => s.RunsAsync(ct)).RequirePermission(Permissions.HrPayroll);
        g.MapPost("/payroll/runs/{year:int}/{month:int}/calculate", (int year, int month, HrService s, CancellationToken ct) => s.CalculateRunAsync(year, month, ct)).RequirePermission(Permissions.HrPayroll);
        g.MapPost("/payroll/runs/{id:guid}/post", (Guid id, HrService s, CancellationToken ct) => s.PostRunAsync(id, ct)).RequirePermission(Permissions.HrPayroll);
        self.MapGet("/payroll/runs/{id:guid}/payslips", (Guid id, Guid? employeeId, HrService s, CancellationToken ct) => s.PayslipsAsync(id, employeeId, ct)).RequirePermission(Permissions.LeaveRequest);
        self.MapGet("/payslips/{id:guid}/html", async (Guid id, HrService s, CancellationToken ct) => Results.Content(await s.PayslipHtmlAsync(id, ct), "text/html; charset=utf-8")).RequirePermission(Permissions.LeaveRequest);
        g.MapGet("/performance", (DateOnly from, DateOnly to, HrService s, CancellationToken ct) => s.PerformanceAsync(from, to, ct)).RequirePermission(Permissions.ReportsHr);
    }
}
