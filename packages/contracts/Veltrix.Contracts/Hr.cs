namespace Veltrix.Contracts.Hr;

public sealed record DepartmentDto(Guid Id, string Name, Guid? ManagerEmployeeId);
public sealed record UpsertDepartmentRequest(string Name, Guid? ManagerEmployeeId);
public sealed record JobTitleDto(Guid Id, string Name);
public sealed record UpsertJobTitleRequest(string Name);

/// <summary>Sensitive fields (national id, birth date, salary, bank account) are null unless the caller holds hr.sensitive.</summary>
public sealed record EmployeeDto(Guid Id, string Code, string FullName, string? Phone, string? Email, string? Address, Guid? DepartmentId, Guid? JobTitleId,
    Guid BranchId, DateOnly HireDate, string Status, string ContractType, Guid? UserId, Guid? ManagerId, Guid? SalaryStructureId, Guid? WorkScheduleId,
    string? NationalId, DateOnly? BirthDate, decimal? BaseSalary, string? BankAccount, DateOnly? TerminationDate);
public sealed record UpsertEmployeeRequest(string FullName, string? Phone, string? Email, string? Address, Guid? DepartmentId, Guid? JobTitleId, Guid BranchId,
    DateOnly HireDate, string Status, string ContractType, Guid? UserId, Guid? ManagerId, Guid? SalaryStructureId, Guid? WorkScheduleId,
    string? NationalId, DateOnly? BirthDate, decimal? BaseSalary, string? BankAccount, DateOnly? TerminationDate);

public sealed record WorkScheduleDto(Guid Id, string Name, TimeOnly StartTime, TimeOnly EndTime, IReadOnlyList<int> WorkingDays, int BreakMinutes);
public sealed record UpsertWorkScheduleRequest(string Name, TimeOnly StartTime, TimeOnly EndTime, IReadOnlyList<int> WorkingDays, int BreakMinutes);

public sealed record AttendanceDto(Guid Id, Guid EmployeeId, DateOnly Date, DateTimeOffset? CheckIn, DateTimeOffset? CheckOut, int WorkedMinutes, int LateMinutes,
    int OvertimeMinutes, bool OvertimeApproved, string Status, string Source);
public sealed record CheckRequest(Guid EmployeeId, string Source, DateTimeOffset? At, Guid? Id = null);
public sealed record ManualAttendanceRequest(Guid EmployeeId, DateOnly Date, DateTimeOffset? CheckIn, DateTimeOffset? CheckOut, string Status);

public sealed record LeaveTypeDto(Guid Id, string Code, string Name, bool Paid, decimal AnnualAllowanceDays);
public sealed record UpsertLeaveTypeRequest(string Code, string Name, bool Paid, decimal AnnualAllowanceDays);
public sealed record LeaveRequestDto(Guid Id, Guid EmployeeId, string EmployeeName, Guid LeaveTypeId, DateOnly StartDate, DateOnly EndDate, decimal Days,
    string Status, string? Reason, Guid? DecidedBy, string? DecisionNote);
public sealed record CreateLeaveRequest(Guid EmployeeId, Guid LeaveTypeId, DateOnly StartDate, DateOnly EndDate, string? Reason);
public sealed record DecideRequest(bool Approve, string? Note);
public sealed record LeaveBalanceDto(Guid LeaveTypeId, string Name, decimal Allowance, decimal Taken, decimal Remaining);

public sealed record ExpenseClaimDto(Guid Id, Guid EmployeeId, DateOnly Date, string Category, decimal Amount, string? Description, string Status, Guid? JournalEntryId);
public sealed record CreateExpenseClaimRequest(Guid EmployeeId, DateOnly Date, string Category, decimal Amount, string? Description);
public sealed record ReimburseRequest(Guid PaymentAccountId, Guid? ExpenseAccountId);

/// <summary>Component calculation: Fixed amount, PercentOfBase, or PercentOfGross. Kind: Earning or Deduction. Employer contributions are informational.</summary>
public sealed record SalaryComponentDto(string Code, string Name, string Kind, string Calculation, decimal Value, bool Taxable);
public sealed record SalaryStructureDto(Guid Id, string Name, string? Jurisdiction, IReadOnlyList<SalaryComponentDto> Components);
public sealed record UpsertSalaryStructureRequest(string Name, string? Jurisdiction, IReadOnlyList<SalaryComponentDto> Components);

public sealed record PayrollInputDto(Guid Id, Guid EmployeeId, int Year, int Month, string Code, string Kind, decimal Amount, string? Note);
public sealed record CreatePayrollInputRequest(Guid EmployeeId, int Year, int Month, string Code, string Kind, decimal Amount, string? Note);

public sealed record PayrollRunDto(Guid Id, string Number, int Year, int Month, string Status, decimal TotalGross, decimal TotalDeductions, decimal TotalNet,
    int Employees, Guid? JournalEntryId);
public sealed record PayslipDto(Guid Id, Guid RunId, Guid EmployeeId, string EmployeeName, int Year, int Month, decimal BaseSalary, decimal Gross, decimal Deductions,
    decimal Net, IReadOnlyList<PayslipLineDto> Lines);
public sealed record PayslipLineDto(string Code, string Name, string Kind, decimal Amount);

public sealed record EmployeeDocumentDto(Guid Id, Guid EmployeeId, string Type, string FileName, DateOnly? ExpiresOn, DateTimeOffset CreatedAt);
public sealed record EmployeePerformanceDto(Guid EmployeeId, string Name, int DaysPresent, int DaysAbsent, int LateMinutes, int OvertimeMinutes, decimal SalesTotal, int SalesCount);
