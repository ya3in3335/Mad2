using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Veltrix.Contracts.Hr;
using Veltrix.Modules.Accounting;
using Veltrix.Modules.Pos;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Hr;

public sealed class HrService(DbContext db, ITenantContext ctx, ISequenceService sequences, PostingService posting, IAuditWriter audit, IClock clock,
    IFileStorage storage, INotificationPublisher notifications)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private bool Sensitive => ctx.HasPermission(Permissions.HrSensitive);
    private DateOnly Today => DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

    // ---------- Org structure ----------
    public async Task<IReadOnlyList<DepartmentDto>> DepartmentsAsync(CancellationToken ct) =>
        await db.Set<Department>().OrderBy(d => d.Name).Select(d => new DepartmentDto(d.Id, d.Name, d.ManagerEmployeeId)).ToListAsync(ct);

    public async Task<DepartmentDto> UpsertDepartmentAsync(Guid? id, UpsertDepartmentRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Name, "name", 120).ThrowIfInvalid();
        var d = id is { } did ? await db.Set<Department>().SingleOrDefaultAsync(x => x.Id == did, ct) ?? throw new NotFoundException("department.not_found", "Not found.")
            : db.Add(new Department { TenantId = ctx.RequireTenant() }).Entity;
        d.Name = req.Name.Trim(); d.ManagerEmployeeId = req.ManagerEmployeeId;
        await db.SaveChangesAsync(ct);
        return new DepartmentDto(d.Id, d.Name, d.ManagerEmployeeId);
    }

    public async Task<IReadOnlyList<JobTitleDto>> JobTitlesAsync(CancellationToken ct) =>
        await db.Set<JobTitle>().OrderBy(d => d.Name).Select(d => new JobTitleDto(d.Id, d.Name)).ToListAsync(ct);

    public async Task<JobTitleDto> UpsertJobTitleAsync(Guid? id, UpsertJobTitleRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Name, "name", 120).ThrowIfInvalid();
        var j = id is { } jid ? await db.Set<JobTitle>().SingleOrDefaultAsync(x => x.Id == jid, ct) ?? throw new NotFoundException("job.not_found", "Not found.")
            : db.Add(new JobTitle { TenantId = ctx.RequireTenant() }).Entity;
        j.Name = req.Name.Trim();
        await db.SaveChangesAsync(ct);
        return new JobTitleDto(j.Id, j.Name);
    }

    // ---------- Employees ----------
    public async Task<PagedResult<EmployeeDto>> EmployeesAsync(PageQuery q, Guid? branchId, string? status, CancellationToken ct)
    {
        var query = db.Set<Employee>().AsNoTracking().AsQueryable();
        if (branchId is { } b) query = query.Where(e => e.BranchId == b);
        if (status is not null && Enum.TryParse<EmploymentStatus>(status, true, out var s)) query = query.Where(e => e.Status == s);
        if (!string.IsNullOrWhiteSpace(q.Search)) query = query.Where(e => e.FullName.ToUpper().Contains(q.Search.ToUpper()) || e.Code.Contains(q.Search));
        if (ctx.AllowedBranches is { } allowed) query = query.Where(e => allowed.Contains(e.BranchId));
        var page = await query.OrderBy(e => e.FullName).ToPagedAsync(q, ct);
        return new PagedResult<EmployeeDto>(page.Items.Select(ToDto).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<EmployeeDto> GetEmployeeAsync(Guid id, CancellationToken ct) => ToDto(await LoadEmployeeAsync(id, ct));

    public async Task<EmployeeDto> UpsertEmployeeAsync(Guid? id, UpsertEmployeeRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.FullName, "fullName").Require(Enum.TryParse<EmploymentStatus>(req.Status, true, out _), "status", "Unknown status.")
            .Require(Enum.TryParse<ContractType>(req.ContractType, true, out _), "contractType", "Unknown contract type.")
            .Require(req.BaseSalary is null or >= 0, "baseSalary", "Must not be negative.").MaxLength(req.NationalId, "nationalId", 40).MaxLength(req.BankAccount, "bankAccount", 60)
            .ThrowIfInvalid();
        ctx.EnsureBranch(req.BranchId);
        var touchesSensitive = req.NationalId is not null || req.BirthDate is not null || req.BaseSalary is not null || req.BankAccount is not null;
        if (touchesSensitive && !Sensitive) throw new ForbiddenException("hr.sensitive", "Sensitive HR fields require the hr.sensitive permission.");
        var e = id is { } eid ? await LoadEmployeeAsync(eid, ct)
            : db.Add(new Employee { TenantId = ctx.RequireTenant(), Code = await sequences.NextAsync(DocumentTypes.Employee, null, ct) }).Entity;
        e.FullName = req.FullName.Trim(); e.Phone = req.Phone; e.Email = req.Email; e.Address = req.Address; e.DepartmentId = req.DepartmentId; e.JobTitleId = req.JobTitleId;
        e.BranchId = req.BranchId; e.HireDate = req.HireDate; e.Status = Enum.Parse<EmploymentStatus>(req.Status, true); e.ContractType = Enum.Parse<ContractType>(req.ContractType, true);
        e.UserId = req.UserId; e.ManagerId = req.ManagerId; e.SalaryStructureId = req.SalaryStructureId; e.WorkScheduleId = req.WorkScheduleId; e.TerminationDate = req.TerminationDate;
        if (Sensitive) { e.NationalId = req.NationalId; e.BirthDate = req.BirthDate; e.BaseSalary = req.BaseSalary; e.BankAccount = req.BankAccount; }
        audit.Write(id is null ? "hr.employee_created" : "hr.employee_updated", nameof(Employee), e.Id.ToString(), new { e.Code, e.Status, sensitiveChanged = touchesSensitive });
        await db.SaveChangesAsync(ct);
        return ToDto(e);
    }

    private async Task<Employee> LoadEmployeeAsync(Guid id, CancellationToken ct)
    {
        var e = await db.Set<Employee>().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("employee.not_found", "Employee not found.");
        ctx.EnsureBranch(e.BranchId);
        return e;
    }

    private EmployeeDto ToDto(Employee e) => new(e.Id, e.Code, e.FullName, e.Phone, e.Email, e.Address, e.DepartmentId, e.JobTitleId, e.BranchId, e.HireDate,
        e.Status.ToString(), e.ContractType.ToString(), e.UserId, e.ManagerId, e.SalaryStructureId, e.WorkScheduleId,
        Sensitive ? e.NationalId : null, Sensitive ? e.BirthDate : null, Sensitive ? e.BaseSalary : null, Sensitive ? e.BankAccount : null, e.TerminationDate);

    // ---------- Schedules & attendance ----------
    public async Task<IReadOnlyList<WorkScheduleDto>> SchedulesAsync(CancellationToken ct) =>
        (await db.Set<WorkSchedule>().OrderBy(s => s.Name).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<WorkScheduleDto> UpsertScheduleAsync(Guid? id, UpsertWorkScheduleRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Name, "name", 100).Require(req.EndTime > req.StartTime, "endTime", "End must be after start.")
            .Require(req.WorkingDays.All(d => d is >= 1 and <= 7), "workingDays", "Days are 1 (Mon) to 7 (Sun).").Require(req.BreakMinutes is >= 0 and <= 240, "breakMinutes", "0-240.").ThrowIfInvalid();
        var s = id is { } sid ? await db.Set<WorkSchedule>().SingleOrDefaultAsync(x => x.Id == sid, ct) ?? throw new NotFoundException("schedule.not_found", "Not found.")
            : db.Add(new WorkSchedule { TenantId = ctx.RequireTenant() }).Entity;
        s.Name = req.Name.Trim(); s.StartTime = req.StartTime; s.EndTime = req.EndTime; s.WorkingDays = string.Join(',', req.WorkingDays.Distinct()); s.BreakMinutes = req.BreakMinutes;
        await db.SaveChangesAsync(ct);
        return ToDto(s);
    }

    private static WorkScheduleDto ToDto(WorkSchedule s) => new(s.Id, s.Name, s.StartTime, s.EndTime, s.WorkingDays.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList(), s.BreakMinutes);

    /// <summary>Check-in / check-out. Late and overtime minutes are computed against the employee's schedule (Africa/Algiers wall time).</summary>
    public async Task<AttendanceDto> CheckAsync(CheckRequest req, bool checkIn, CancellationToken ct)
    {
        var e = await LoadEmployeeAsync(req.EmployeeId, ct);
        if (!ctx.HasPermission(Permissions.HrManage) && e.UserId != ctx.UserId) throw new ForbiddenException("hr.self_only", "You can only record your own attendance.");
        var at = req.At ?? clock.UtcNow;
        var local = at.ToOffset(TimeSpan.FromHours(1));
        var date = DateOnly.FromDateTime(local.DateTime);
        var rec = await db.Set<AttendanceRecord>().SingleOrDefaultAsync(a => a.EmployeeId == e.Id && a.Date == date, ct)
                  ?? db.Add(new AttendanceRecord { TenantId = e.TenantId, EmployeeId = e.Id, Date = date, Status = AttendanceStatus.Present, Source = req.Source }).Entity;
        if (checkIn) rec.CheckIn ??= at; else rec.CheckOut = at;
        var schedule = e.WorkScheduleId is { } wsid ? await db.Set<WorkSchedule>().SingleOrDefaultAsync(s => s.Id == wsid, ct) : null;
        Compute(rec, schedule);
        await db.SaveChangesAsync(ct);
        return ToDto(rec);
    }

    public async Task<AttendanceDto> RecordAttendanceAsync(ManualAttendanceRequest req, CancellationToken ct)
    {
        if (!Enum.TryParse<AttendanceStatus>(req.Status, true, out var status)) throw ValidationException.For("status", "Unknown status.");
        var e = await LoadEmployeeAsync(req.EmployeeId, ct);
        var rec = await db.Set<AttendanceRecord>().SingleOrDefaultAsync(a => a.EmployeeId == e.Id && a.Date == req.Date, ct)
                  ?? db.Add(new AttendanceRecord { TenantId = e.TenantId, EmployeeId = e.Id, Date = req.Date, Source = "manual" }).Entity;
        rec.CheckIn = req.CheckIn; rec.CheckOut = req.CheckOut; rec.Status = status;
        var schedule = e.WorkScheduleId is { } wsid ? await db.Set<WorkSchedule>().SingleOrDefaultAsync(s => s.Id == wsid, ct) : null;
        Compute(rec, schedule);
        audit.Write("hr.attendance_recorded", nameof(AttendanceRecord), rec.Id.ToString());
        await db.SaveChangesAsync(ct);
        return ToDto(rec);
    }

    public static void Compute(AttendanceRecord rec, WorkSchedule? s)
    {
        rec.WorkedMinutes = rec.CheckIn is { } i && rec.CheckOut is { } o && o > i ? (int)(o - i).TotalMinutes - (s?.BreakMinutes ?? 0) : 0;
        if (rec.WorkedMinutes < 0) rec.WorkedMinutes = 0;
        if (s is null || rec.CheckIn is null) return;
        var inLocal = TimeOnly.FromDateTime(rec.CheckIn.Value.ToOffset(TimeSpan.FromHours(1)).DateTime);
        rec.LateMinutes = Math.Max(0, (int)(inLocal - s.StartTime).TotalMinutes is var late && inLocal > s.StartTime ? late : 0);
        if (rec.LateMinutes > 0 && rec.Status == AttendanceStatus.Present) rec.Status = AttendanceStatus.Late;
        var scheduled = (int)(s.EndTime - s.StartTime).TotalMinutes - s.BreakMinutes;
        rec.OvertimeMinutes = Math.Max(0, rec.WorkedMinutes - scheduled);
    }

    public async Task<AttendanceDto> ApproveOvertimeAsync(Guid id, CancellationToken ct)
    {
        var rec = await db.Set<AttendanceRecord>().SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException("attendance.not_found", "Not found.");
        rec.OvertimeApproved = true;
        audit.Write("hr.overtime_approved", nameof(AttendanceRecord), id.ToString(), new { rec.OvertimeMinutes });
        await db.SaveChangesAsync(ct);
        return ToDto(rec);
    }

    public async Task<IReadOnlyList<AttendanceDto>> AttendanceAsync(Guid? employeeId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var q = db.Set<AttendanceRecord>().Where(a => a.Date >= from && a.Date <= to);
        if (employeeId is { } e) q = q.Where(a => a.EmployeeId == e);
        else if (!ctx.HasPermission(Permissions.HrView)) throw new ForbiddenException("permission.denied", "Missing permission 'hr.view'.");
        return (await q.OrderBy(a => a.Date).Take(5000).ToListAsync(ct)).Select(ToDto).ToList();
    }

    private static AttendanceDto ToDto(AttendanceRecord a) => new(a.Id, a.EmployeeId, a.Date, a.CheckIn, a.CheckOut, a.WorkedMinutes, a.LateMinutes, a.OvertimeMinutes,
        a.OvertimeApproved, a.Status.ToString(), a.Source);

    // ---------- Leave ----------
    public async Task<IReadOnlyList<LeaveTypeDto>> LeaveTypesAsync(CancellationToken ct) =>
        await db.Set<LeaveType>().OrderBy(l => l.Name).Select(l => new LeaveTypeDto(l.Id, l.Code, l.Name, l.Paid, l.AnnualAllowanceDays)).ToListAsync(ct);

    public async Task<LeaveTypeDto> UpsertLeaveTypeAsync(Guid? id, UpsertLeaveTypeRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Code, "code", 20).NotEmpty(req.Name, "name", 100).Require(req.AnnualAllowanceDays is >= 0 and <= 366, "annualAllowanceDays", "0-366.").ThrowIfInvalid();
        var l = id is { } lid ? await db.Set<LeaveType>().SingleOrDefaultAsync(x => x.Id == lid, ct) ?? throw new NotFoundException("leave_type.not_found", "Not found.")
            : db.Add(new LeaveType { TenantId = ctx.RequireTenant() }).Entity;
        l.Code = req.Code.Trim().ToUpperInvariant(); l.Name = req.Name.Trim(); l.Paid = req.Paid; l.AnnualAllowanceDays = req.AnnualAllowanceDays;
        await db.SaveChangesAsync(ct);
        return new LeaveTypeDto(l.Id, l.Code, l.Name, l.Paid, l.AnnualAllowanceDays);
    }

    public async Task<LeaveRequestDto> RequestLeaveAsync(CreateLeaveRequest req, CancellationToken ct)
    {
        new Validator().Require(req.EndDate >= req.StartDate, "endDate", "End must be on or after start.").MaxLength(req.Reason, "reason", 500).ThrowIfInvalid();
        var e = await LoadEmployeeAsync(req.EmployeeId, ct);
        if (!ctx.HasPermission(Permissions.HrManage) && e.UserId != ctx.UserId) throw new ForbiddenException("hr.self_only", "You can only request leave for yourself.");
        var type = await db.Set<LeaveType>().SingleOrDefaultAsync(t => t.Id == req.LeaveTypeId, ct) ?? throw ValidationException.For("leaveTypeId", "Unknown leave type.");
        if (await db.Set<LeaveRequest>().AnyAsync(l => l.EmployeeId == e.Id && l.Status != ApprovalStatus.Rejected && l.Status != ApprovalStatus.Cancelled
                                                        && l.StartDate <= req.EndDate && l.EndDate >= req.StartDate, ct))
            throw new ConflictException("leave.overlap", "This period overlaps an existing leave request.");
        var schedule = e.WorkScheduleId is { } wsid ? await db.Set<WorkSchedule>().SingleOrDefaultAsync(s => s.Id == wsid, ct) : null;
        var days = WorkingDays(req.StartDate, req.EndDate, schedule);
        if (type.AnnualAllowanceDays > 0)
        {
            var bal = (await BalancesAsync(e.Id, req.StartDate.Year, ct)).Single(b => b.LeaveTypeId == type.Id);
            if (days > bal.Remaining) throw new DomainException("leave.insufficient_balance", $"Only {bal.Remaining} day(s) remaining.");
        }
        var l = db.Add(new LeaveRequest { TenantId = e.TenantId, EmployeeId = e.Id, LeaveTypeId = type.Id, StartDate = req.StartDate, EndDate = req.EndDate, Days = days,
            Status = ApprovalStatus.Pending, Reason = req.Reason }).Entity;
        notifications.Publish(e.TenantId, "hr.leave_request", $"Leave request: {e.FullName}", $"{type.Name} {req.StartDate:yyyy-MM-dd} → {req.EndDate:yyyy-MM-dd} ({days} d)",
            NotificationSeverity.Info, Permissions.LeaveApprove);
        await db.SaveChangesAsync(ct);
        return ToDto(l, e.FullName);
    }

    public async Task<LeaveRequestDto> DecideLeaveAsync(Guid id, DecideRequest req, CancellationToken ct)
    {
        var l = await db.Set<LeaveRequest>().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("leave.not_found", "Not found.");
        var e = await LoadEmployeeAsync(l.EmployeeId, ct);
        if (e.UserId == ctx.UserId) throw new ForbiddenException("leave.self_approval", "You cannot approve your own leave.");
        if (l.Status != ApprovalStatus.Pending) throw new ConflictException("leave.decided", "Already decided.");
        l.Status = req.Approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        l.DecidedBy = ctx.UserId; l.DecisionNote = req.Note;
        if (e.UserId is { } uid)
            notifications.Publish(e.TenantId, "hr.leave_decided", req.Approve ? "Leave approved" : "Leave rejected", $"{l.StartDate:yyyy-MM-dd} → {l.EndDate:yyyy-MM-dd}", userId: uid);
        audit.Write(req.Approve ? "hr.leave_approved" : "hr.leave_rejected", nameof(LeaveRequest), l.Id.ToString());
        await db.SaveChangesAsync(ct);
        return ToDto(l, e.FullName);
    }

    public async Task<IReadOnlyList<LeaveRequestDto>> LeaveRequestsAsync(Guid? employeeId, string? status, CancellationToken ct)
    {
        var q = from l in db.Set<LeaveRequest>() join e in db.Set<Employee>() on l.EmployeeId equals e.Id select new { l, e.FullName, e.BranchId, e.UserId };
        if (employeeId is { } eid) q = q.Where(x => x.l.EmployeeId == eid);
        if (status is not null && Enum.TryParse<ApprovalStatus>(status, true, out var s)) q = q.Where(x => x.l.Status == s);
        if (!ctx.HasPermission(Permissions.HrView) && !ctx.HasPermission(Permissions.LeaveApprove)) q = q.Where(x => x.UserId == ctx.UserId);
        var rows = await q.OrderByDescending(x => x.l.StartDate).Take(1000).ToListAsync(ct);
        return rows.Where(r => ctx.CanAccessBranch(r.BranchId)).Select(r => ToDto(r.l, r.FullName)).ToList();
    }

    public async Task<IReadOnlyList<LeaveBalanceDto>> BalancesAsync(Guid employeeId, int year, CancellationToken ct)
    {
        var types = await db.Set<LeaveType>().ToListAsync(ct);
        var taken = await db.Set<LeaveRequest>().Where(l => l.EmployeeId == employeeId && l.Status == ApprovalStatus.Approved || l.EmployeeId == employeeId && l.Status == ApprovalStatus.Pending)
            .Where(l => l.StartDate.Year == year).GroupBy(l => l.LeaveTypeId).Select(g => new { g.Key, Days = g.Sum(x => x.Days) }).ToDictionaryAsync(x => x.Key, x => x.Days, ct);
        return types.Select(t => new LeaveBalanceDto(t.Id, t.Name, t.AnnualAllowanceDays, taken.GetValueOrDefault(t.Id), t.AnnualAllowanceDays - taken.GetValueOrDefault(t.Id))).ToList();
    }

    private static decimal WorkingDays(DateOnly from, DateOnly to, WorkSchedule? s)
    {
        var days = s?.WorkingDays.Split(',').Select(int.Parse).ToHashSet() ?? [7, 1, 2, 3, 4];
        decimal n = 0;
        for (var d = from; d <= to; d = d.AddDays(1)) if (days.Contains(d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek)) n++;
        return n;
    }

    private static LeaveRequestDto ToDto(LeaveRequest l, string name) => new(l.Id, l.EmployeeId, name, l.LeaveTypeId, l.StartDate, l.EndDate, l.Days, l.Status.ToString(),
        l.Reason, l.DecidedBy, l.DecisionNote);

    // ---------- Expense claims ----------
    public async Task<ExpenseClaimDto> SubmitClaimAsync(CreateExpenseClaimRequest req, CancellationToken ct)
    {
        new Validator().Positive(req.Amount, "amount").NotEmpty(req.Category, "category", 60).MaxLength(req.Description, "description", 500).ThrowIfInvalid();
        var e = await LoadEmployeeAsync(req.EmployeeId, ct);
        if (!ctx.HasPermission(Permissions.HrManage) && e.UserId != ctx.UserId) throw new ForbiddenException("hr.self_only", "You can only submit your own claims.");
        var c = db.Add(new ExpenseClaim { TenantId = e.TenantId, EmployeeId = e.Id, Date = req.Date, Category = req.Category, Amount = Money.Round(req.Amount),
            Description = req.Description, Status = ClaimStatus.Submitted }).Entity;
        await db.SaveChangesAsync(ct);
        return ToDto(c);
    }

    public async Task<ExpenseClaimDto> DecideClaimAsync(Guid id, DecideRequest req, CancellationToken ct)
    {
        var c = await db.Set<ExpenseClaim>().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("claim.not_found", "Not found.");
        if (c.Status != ClaimStatus.Submitted) throw new ConflictException("claim.state", "Already decided.");
        c.Status = req.Approve ? ClaimStatus.Approved : ClaimStatus.Rejected;
        audit.Write(req.Approve ? "hr.claim_approved" : "hr.claim_rejected", nameof(ExpenseClaim), c.Id.ToString(), new { c.Amount });
        await db.SaveChangesAsync(ct);
        return ToDto(c);
    }

    public async Task<ExpenseClaimDto> ReimburseClaimAsync(Guid id, ReimburseRequest req, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var c = await db.Set<ExpenseClaim>().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("claim.not_found", "Not found.");
        if (c.Status != ClaimStatus.Approved) throw new ConflictException("claim.state", "Only approved claims can be reimbursed.");
        var e = await LoadEmployeeAsync(c.EmployeeId, ct);
        var b = new PostingBuilder();
        if (req.ExpenseAccountId is { } ea) b.DebitAccount(ea, c.Amount, c.Category, PartyTypes.Employee, e.Id); else b.Debit(AccountRoles.GeneralExpense, c.Amount, c.Category, PartyTypes.Employee, e.Id);
        b.CreditAccount(req.PaymentAccountId, c.Amount, e.FullName);
        c.JournalEntryId = (await posting.PostAsync(Today, $"Expense claim {e.FullName} — {c.Category}", JournalSource.Expense, c.Id, e.BranchId, b, ct)).Id;
        c.Status = ClaimStatus.Reimbursed;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(c);
    }

    public async Task<IReadOnlyList<ExpenseClaimDto>> ClaimsAsync(Guid? employeeId, CancellationToken ct) =>
        (await db.Set<ExpenseClaim>().Where(c => employeeId == null || c.EmployeeId == employeeId).OrderByDescending(c => c.Date).Take(1000).ToListAsync(ct)).Select(ToDto).ToList();

    private static ExpenseClaimDto ToDto(ExpenseClaim c) => new(c.Id, c.EmployeeId, c.Date, c.Category, c.Amount, c.Description, c.Status.ToString(), c.JournalEntryId);

    // ---------- Payroll ----------
    public async Task<IReadOnlyList<SalaryStructureDto>> StructuresAsync(CancellationToken ct) =>
        (await db.Set<SalaryStructure>().OrderBy(s => s.Name).ToListAsync(ct)).Select(s => new SalaryStructureDto(s.Id, s.Name, s.Jurisdiction,
            JsonSerializer.Deserialize<List<SalaryComponentDto>>(s.ComponentsJson, Json) ?? [])).ToList();

    public async Task<SalaryStructureDto> UpsertStructureAsync(Guid? id, UpsertSalaryStructureRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Name, "name", 100)
            .Require(req.Components.All(c => c.Kind is "Earning" or "Deduction" or "EmployerContribution"), "components", "Kind must be Earning, Deduction or EmployerContribution.")
            .Require(req.Components.All(c => c.Calculation is "Fixed" or "PercentOfBase" or "PercentOfGross"), "components", "Calculation must be Fixed, PercentOfBase or PercentOfGross.")
            .Require(req.Components.All(c => c.Value >= 0 && c.Code.Length is > 0 and <= 30), "components", "Invalid component.")
            .Require(req.Components.Select(c => c.Code).Distinct().Count() == req.Components.Count, "components", "Component codes must be unique.").ThrowIfInvalid();
        var s = id is { } sid ? await db.Set<SalaryStructure>().SingleOrDefaultAsync(x => x.Id == sid, ct) ?? throw new NotFoundException("structure.not_found", "Not found.")
            : db.Add(new SalaryStructure { TenantId = ctx.RequireTenant() }).Entity;
        s.Name = req.Name.Trim(); s.Jurisdiction = req.Jurisdiction; s.ComponentsJson = JsonSerializer.Serialize(req.Components, Json);
        audit.Write("hr.salary_structure", nameof(SalaryStructure), s.Id.ToString());
        await db.SaveChangesAsync(ct);
        return new SalaryStructureDto(s.Id, s.Name, s.Jurisdiction, req.Components);
    }

    public async Task<PayrollInputDto> AddInputAsync(CreatePayrollInputRequest req, CancellationToken ct)
    {
        new Validator().Require(req.Month is >= 1 and <= 12, "month", "1-12.").Require(req.Kind is "Earning" or "Deduction", "kind", "Earning or Deduction.")
            .NotEmpty(req.Code, "code", 30).Positive(req.Amount, "amount").ThrowIfInvalid();
        var e = await LoadEmployeeAsync(req.EmployeeId, ct);
        var i = db.Add(new PayrollInput { TenantId = e.TenantId, EmployeeId = e.Id, Year = req.Year, Month = req.Month, Code = req.Code, Kind = req.Kind, Amount = req.Amount, Note = req.Note }).Entity;
        await db.SaveChangesAsync(ct);
        return new PayrollInputDto(i.Id, i.EmployeeId, i.Year, i.Month, i.Code, i.Kind, i.Amount, i.Note);
    }

    /// <summary>Calculate (or recalculate) a monthly payroll from salary structures and inputs. Rates come only from configuration.</summary>
    public async Task<PayrollRunDto> CalculateRunAsync(int year, int month, CancellationToken ct)
    {
        if (month is < 1 or > 12) throw ValidationException.For("month", "1-12.");
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var run = await db.Set<PayrollRun>().SingleOrDefaultAsync(r => r.Year == year && r.Month == month, ct);
        if (run is { Status: PayrollStatus.Approved or PayrollStatus.Posted }) throw new ConflictException("payroll.locked", "This payroll is approved and cannot be recalculated.");
        if (run is null) run = db.Add(new PayrollRun { TenantId = ctx.RequireTenant(), Year = year, Month = month, Number = await sequences.NextAsync(DocumentTypes.PayrollRun, null, ct) }).Entity;
        else await db.Set<Payslip>().Where(p => p.RunId == run.Id).ExecuteDeleteAsync(ct);

        var periodEnd = new DateOnly(year, month, 1).AddMonths(1).AddDays(-1);
        var employees = await db.Set<Employee>().Where(e => e.Status != EmploymentStatus.Terminated && e.HireDate <= periodEnd && e.BaseSalary != null).ToListAsync(ct);
        var structures = (await db.Set<SalaryStructure>().ToListAsync(ct)).ToDictionary(s => s.Id, s => JsonSerializer.Deserialize<List<SalaryComponentDto>>(s.ComponentsJson, Json) ?? []);
        var inputs = await db.Set<PayrollInput>().Where(i => i.Year == year && i.Month == month).ToListAsync(ct);
        decimal tg = 0, td = 0, tn = 0;
        foreach (var e in employees)
        {
            var lines = new List<PayslipLineDto> { new("BASE", "Base salary", "Earning", e.BaseSalary!.Value) };
            var comps = e.SalaryStructureId is { } sid && structures.TryGetValue(sid, out var c) ? c : [];
            foreach (var comp in comps.Where(x => x.Kind == "Earning"))
                lines.Add(new(comp.Code, comp.Name, "Earning", comp.Calculation == "Fixed" ? comp.Value : Money.Round(e.BaseSalary!.Value * comp.Value / 100m)));
            lines.AddRange(inputs.Where(i => i.EmployeeId == e.Id && i.Kind == "Earning").Select(i => new PayslipLineDto(i.Code, i.Note ?? i.Code, "Earning", i.Amount)));
            var gross = lines.Sum(l => l.Amount);
            foreach (var comp in comps.Where(x => x.Kind is "Deduction" or "EmployerContribution"))
            {
                var amount = comp.Calculation switch
                {
                    "Fixed" => comp.Value,
                    "PercentOfBase" => Money.Round(e.BaseSalary!.Value * comp.Value / 100m),
                    _ => Money.Round(gross * comp.Value / 100m),
                };
                lines.Add(new(comp.Code, comp.Name, comp.Kind, amount));
            }
            lines.AddRange(inputs.Where(i => i.EmployeeId == e.Id && i.Kind == "Deduction").Select(i => new PayslipLineDto(i.Code, i.Note ?? i.Code, "Deduction", i.Amount)));
            var deductions = lines.Where(l => l.Kind == "Deduction").Sum(l => l.Amount);
            db.Add(new Payslip { TenantId = e.TenantId, RunId = run.Id, EmployeeId = e.Id, BaseSalary = e.BaseSalary!.Value, Gross = gross, Deductions = deductions,
                Net = gross - deductions, LinesJson = JsonSerializer.Serialize(lines, Json) });
            tg += gross; td += deductions; tn += gross - deductions;
        }
        run.Status = PayrollStatus.Calculated; run.TotalGross = tg; run.TotalDeductions = td; run.TotalNet = tn;
        audit.Write("hr.payroll_calculated", nameof(PayrollRun), run.Id.ToString(), new { year, month, employees = employees.Count });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await RunAsync(run.Id, ct);
    }

    /// <summary>Approve and post: Dr salaries expense (gross) / Cr salaries payable (net) / Cr deductions payable.</summary>
    public async Task<PayrollRunDto> PostRunAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var run = await db.Set<PayrollRun>().SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("payroll.not_found", "Not found.");
        if (run.Status != PayrollStatus.Calculated) throw new ConflictException("payroll.state", "Calculate the payroll first.");
        if (run.TotalGross > 0)
        {
            var b = new PostingBuilder().Debit(AccountRoles.Salaries, run.TotalGross).Credit(AccountRoles.SalariesPayable, run.TotalNet).Credit(AccountRoles.PayrollDeductionsPayable, run.TotalDeductions);
            var end = new DateOnly(run.Year, run.Month, 1).AddMonths(1).AddDays(-1);
            run.JournalEntryId = (await posting.PostAsync(end, $"Payroll {run.Year}-{run.Month:00}", JournalSource.Payroll, run.Id, null, b, ct)).Id;
        }
        run.Status = PayrollStatus.Posted;
        audit.Write("hr.payroll_posted", nameof(PayrollRun), run.Id.ToString(), new { run.TotalGross, run.TotalNet });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await RunAsync(id, ct);
    }

    public async Task<PayrollRunDto> RunAsync(Guid id, CancellationToken ct)
    {
        var r = await db.Set<PayrollRun>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("payroll.not_found", "Not found.");
        var count = await db.Set<Payslip>().CountAsync(p => p.RunId == id, ct);
        return new PayrollRunDto(r.Id, r.Number, r.Year, r.Month, r.Status.ToString(), r.TotalGross, r.TotalDeductions, r.TotalNet, count, r.JournalEntryId);
    }

    public async Task<IReadOnlyList<PayrollRunDto>> RunsAsync(CancellationToken ct)
    {
        var ids = await db.Set<PayrollRun>().OrderByDescending(r => r.Year).ThenByDescending(r => r.Month).Select(r => r.Id).Take(60).ToListAsync(ct);
        var list = new List<PayrollRunDto>();
        foreach (var id in ids) list.Add(await RunAsync(id, ct));
        return list;
    }

    public async Task<IReadOnlyList<PayslipDto>> PayslipsAsync(Guid runId, Guid? employeeId, CancellationToken ct)
    {
        var run = await db.Set<PayrollRun>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, ct) ?? throw new NotFoundException("payroll.not_found", "Not found.");
        var q = from p in db.Set<Payslip>() join e in db.Set<Employee>() on p.EmployeeId equals e.Id where p.RunId == runId select new { p, e.FullName, e.UserId };
        if (employeeId is { } eid) q = q.Where(x => x.p.EmployeeId == eid);
        if (!ctx.HasPermission(Permissions.HrPayroll)) q = q.Where(x => x.UserId == ctx.UserId);
        return (await q.ToListAsync(ct)).Select(x => new PayslipDto(x.p.Id, runId, x.p.EmployeeId, x.FullName, run.Year, run.Month, x.p.BaseSalary, x.p.Gross, x.p.Deductions, x.p.Net,
            JsonSerializer.Deserialize<List<PayslipLineDto>>(x.p.LinesJson, Json) ?? [])).ToList();
    }

    public async Task<string> PayslipHtmlAsync(Guid payslipId, CancellationToken ct)
    {
        var p = await db.Set<Payslip>().SingleOrDefaultAsync(x => x.Id == payslipId, ct) ?? throw new NotFoundException("payslip.not_found", "Not found.");
        var slip = (await PayslipsAsync(p.RunId, p.EmployeeId, ct)).SingleOrDefault() ?? throw new ForbiddenException("permission.denied", "Not allowed.");
        string E(string s) => System.Net.WebUtility.HtmlEncode(s);
        var rows = string.Join("", slip.Lines.Select(l => $"<tr><td>{E(l.Code)}</td><td>{E(l.Name)}</td><td>{E(l.Kind)}</td><td style='text-align:right'>{l.Amount:0.00}</td></tr>"));
        return $"<!doctype html><html><head><meta charset='utf-8'><title>Payslip</title></head><body style='font-family:Arial'><h2>Bulletin de paie {slip.Year}-{slip.Month:00}</h2>" +
               $"<p>{E(slip.EmployeeName)}</p><table border='1' cellpadding='4' style='border-collapse:collapse'>{rows}</table>" +
               $"<p>Brut: {slip.Gross:0.00} — Retenues: {slip.Deductions:0.00} — <b>Net: {slip.Net:0.00}</b></p>" +
               "<p style='font-size:10px'>Calculated from the company's configured salary structure. Statutory compliance must be verified by a qualified professional.</p></body></html>";
    }

    // ---------- Documents & performance ----------
    public async Task<EmployeeDocumentDto> UploadDocumentAsync(Guid employeeId, string type, DateOnly? expiresOn, Stream content, string fileName, string? contentType, CancellationToken ct)
    {
        var e = await LoadEmployeeAsync(employeeId, ct);
        var (ms, ext) = await UploadValidation.ReadValidatedAsync(content, contentType, 5 * 1024 * 1024, UploadValidation.DocumentTypes, ct);
        var path = await storage.SaveAsync(e.TenantId, "hr-documents", ext, ms, ct);
        var d = db.Add(new EmployeeDocument { TenantId = e.TenantId, EmployeeId = e.Id, Type = type.Length > 60 ? type[..60] : type, ExpiresOn = expiresOn,
            FileName = Path.GetFileName(fileName).Length > 200 ? Path.GetFileName(fileName)[..200] : Path.GetFileName(fileName), StoragePath = path, ContentType = contentType! }).Entity;
        audit.Write("hr.document_uploaded", nameof(EmployeeDocument), d.Id.ToString(), new { type });
        await db.SaveChangesAsync(ct);
        return new EmployeeDocumentDto(d.Id, d.EmployeeId, d.Type, d.FileName, d.ExpiresOn, d.CreatedAt);
    }

    public async Task<IReadOnlyList<EmployeeDocumentDto>> DocumentsAsync(Guid employeeId, CancellationToken ct) =>
        await db.Set<EmployeeDocument>().Where(d => d.EmployeeId == employeeId).OrderByDescending(d => d.CreatedAt)
            .Select(d => new EmployeeDocumentDto(d.Id, d.EmployeeId, d.Type, d.FileName, d.ExpiresOn, d.CreatedAt)).ToListAsync(ct);

    public async Task<(Stream Stream, string ContentType, string Name)?> OpenDocumentAsync(Guid id, CancellationToken ct)
    {
        var d = await db.Set<EmployeeDocument>().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return null;
        await LoadEmployeeAsync(d.EmployeeId, ct);
        var s = await storage.OpenAsync(d.StoragePath, ct);
        audit.Write("hr.document_downloaded", nameof(EmployeeDocument), d.Id.ToString());
        await db.SaveChangesAsync(ct);
        return s is null ? null : (s, d.ContentType, d.FileName);
    }

    public async Task<IReadOnlyList<EmployeePerformanceDto>> PerformanceAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var employees = await db.Set<Employee>().Where(e => e.Status != EmploymentStatus.Terminated).ToListAsync(ct);
        var att = await db.Set<AttendanceRecord>().Where(a => a.Date >= from && a.Date <= to).ToListAsync(ct);
        var fromTs = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var toTs = new DateTimeOffset(to.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);
        var sales = await db.Set<PosSale>().Where(s => s.CreatedAt >= fromTs && s.CreatedAt <= toTs && s.Status == SaleStatus.Completed && !s.IsVoidReversal)
            .GroupBy(s => s.CashierId).Select(g => new { g.Key, Total = g.Sum(x => x.Total), Count = g.Count(x => !x.IsReturn) }).ToListAsync(ct);
        return employees.Where(e => ctx.CanAccessBranch(e.BranchId)).Select(e =>
        {
            var a = att.Where(x => x.EmployeeId == e.Id).ToList();
            var s = e.UserId is { } uid ? sales.SingleOrDefault(x => x.Key == uid) : null;
            return new EmployeePerformanceDto(e.Id, e.FullName, a.Count(x => x.Status is AttendanceStatus.Present or AttendanceStatus.Late), a.Count(x => x.Status == AttendanceStatus.Absent),
                a.Sum(x => x.LateMinutes), a.Sum(x => x.OvertimeMinutes), s?.Total ?? 0, s?.Count ?? 0);
        }).OrderByDescending(x => x.SalesTotal).ToList();
    }
}

public sealed class HrOnboardingContributor(DbContext db) : IOnboardingContributor
{
    public int Order => 40;

    public async Task ContributeAsync(OnboardingContext c, CancellationToken ct)
    {
        // Example leave types; allowances are company policy and must be configured.
        db.Add(new LeaveType { TenantId = c.TenantId, Code = "ANNUAL", Name = c.Locale.StartsWith("fr") ? "Congé annuel" : "Annual leave", Paid = true, AnnualAllowanceDays = 0 });
        db.Add(new LeaveType { TenantId = c.TenantId, Code = "SICK", Name = c.Locale.StartsWith("fr") ? "Maladie" : "Sick leave", Paid = true });
        db.Add(new LeaveType { TenantId = c.TenantId, Code = "UNPAID", Name = c.Locale.StartsWith("fr") ? "Sans solde" : "Unpaid leave", Paid = false });
        db.Add(new WorkSchedule { TenantId = c.TenantId, Name = "Standard 08:00–16:30", StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(16, 30), WorkingDays = "7,1,2,3,4", BreakMinutes = 30 });
        await db.SaveChangesAsync(ct);
    }
}
