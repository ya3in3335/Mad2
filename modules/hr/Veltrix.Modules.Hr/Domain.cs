using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Hr;

public enum EmploymentStatus { Active, OnLeave, Suspended, Terminated }
public enum ContractType { Permanent, FixedTerm, PartTime, Intern, Contractor }
public enum AttendanceStatus { Present, Absent, Late, Leave, Holiday }
public enum ApprovalStatus { Pending, Approved, Rejected, Cancelled }
public enum ClaimStatus { Submitted, Approved, Rejected, Reimbursed }
public enum PayrollStatus { Draft, Calculated, Approved, Posted }

public sealed class Department : TenantEntity { public string Name { get; set; } = ""; public Guid? ManagerEmployeeId { get; set; } }
public sealed class JobTitle : TenantEntity { public string Name { get; set; } = ""; }

public sealed class Employee : TenantEntity, IBranchScoped
{
    public string Code { get; set; } = "";
    public string FullName { get; set; } = "";
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? JobTitleId { get; set; }
    public Guid BranchId { get; set; }
    public DateOnly HireDate { get; set; }
    public DateOnly? TerminationDate { get; set; }
    public EmploymentStatus Status { get; set; }
    public ContractType ContractType { get; set; }
    public Guid? UserId { get; set; }
    public Guid? ManagerId { get; set; }
    public Guid? SalaryStructureId { get; set; }
    public Guid? WorkScheduleId { get; set; }
    // Sensitive (restricted to hr.sensitive)
    public string? NationalId { get; set; }
    public DateOnly? BirthDate { get; set; }
    public decimal? BaseSalary { get; set; }
    public string? BankAccount { get; set; }
}

public sealed class WorkSchedule : TenantEntity
{
    public string Name { get; set; } = "";
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    /// <summary>Comma separated ISO day numbers (1 = Monday ... 7 = Sunday).</summary>
    public string WorkingDays { get; set; } = "7,1,2,3,4";
    public int BreakMinutes { get; set; }
}

public sealed class AttendanceRecord : TenantEntity
{
    public Guid EmployeeId { get; set; }
    public DateOnly Date { get; set; }
    public DateTimeOffset? CheckIn { get; set; }
    public DateTimeOffset? CheckOut { get; set; }
    public int WorkedMinutes { get; set; }
    public int LateMinutes { get; set; }
    public int OvertimeMinutes { get; set; }
    public bool OvertimeApproved { get; set; }
    public AttendanceStatus Status { get; set; }
    public string Source { get; set; } = "manual";
}

public sealed class LeaveType : TenantEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Paid { get; set; } = true;
    public decimal AnnualAllowanceDays { get; set; }
}

public sealed class LeaveRequest : TenantEntity
{
    public Guid EmployeeId { get; set; }
    public Guid LeaveTypeId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public decimal Days { get; set; }
    public ApprovalStatus Status { get; set; }
    public string? Reason { get; set; }
    public Guid? DecidedBy { get; set; }
    public string? DecisionNote { get; set; }
}

public sealed class ExpenseClaim : TenantEntity
{
    public Guid EmployeeId { get; set; }
    public DateOnly Date { get; set; }
    public string Category { get; set; } = "";
    public decimal Amount { get; set; }
    public string? Description { get; set; }
    public ClaimStatus Status { get; set; }
    public Guid? JournalEntryId { get; set; }
}

public sealed class SalaryStructure : TenantEntity
{
    public string Name { get; set; } = "";
    public string? Jurisdiction { get; set; }
    /// <summary>JSON array of salary components (configurable per jurisdiction; no statutory rates are hardcoded).</summary>
    public string ComponentsJson { get; set; } = "[]";
}

public sealed class PayrollInput : TenantEntity
{
    public Guid EmployeeId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public string Code { get; set; } = "";
    public string Kind { get; set; } = "Earning";
    public decimal Amount { get; set; }
    public string? Note { get; set; }
}

public sealed class PayrollRun : TenantEntity
{
    public string Number { get; set; } = "";
    public int Year { get; set; }
    public int Month { get; set; }
    public PayrollStatus Status { get; set; }
    public decimal TotalGross { get; set; }
    public decimal TotalDeductions { get; set; }
    public decimal TotalNet { get; set; }
    public Guid? JournalEntryId { get; set; }
}

public sealed class Payslip : TenantEntity
{
    public Guid RunId { get; set; }
    public Guid EmployeeId { get; set; }
    public decimal BaseSalary { get; set; }
    public decimal Gross { get; set; }
    public decimal Deductions { get; set; }
    public decimal Net { get; set; }
    public string LinesJson { get; set; } = "[]";
}

public sealed class EmployeeDocument : TenantEntity
{
    public Guid EmployeeId { get; set; }
    public string Type { get; set; } = "";
    public string FileName { get; set; } = "";
    public string StoragePath { get; set; } = "";
    public string ContentType { get; set; } = "";
    public DateOnly? ExpiresOn { get; set; }
}

internal sealed class HrConfigs :
    IEntityTypeConfiguration<Department>, IEntityTypeConfiguration<JobTitle>, IEntityTypeConfiguration<Employee>, IEntityTypeConfiguration<WorkSchedule>,
    IEntityTypeConfiguration<AttendanceRecord>, IEntityTypeConfiguration<LeaveType>, IEntityTypeConfiguration<LeaveRequest>, IEntityTypeConfiguration<ExpenseClaim>,
    IEntityTypeConfiguration<SalaryStructure>, IEntityTypeConfiguration<PayrollInput>, IEntityTypeConfiguration<PayrollRun>, IEntityTypeConfiguration<Payslip>,
    IEntityTypeConfiguration<EmployeeDocument>
{
    public void Configure(EntityTypeBuilder<Department> b) { b.ToTable("departments", "hr"); b.Property(x => x.Name).HasMaxLength(120); b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique(); }
    public void Configure(EntityTypeBuilder<JobTitle> b) { b.ToTable("job_titles", "hr"); b.Property(x => x.Name).HasMaxLength(120); b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique(); }

    public void Configure(EntityTypeBuilder<Employee> b)
    {
        b.ToTable("employees", "hr");
        b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.UserId });
        b.Property(x => x.Code).HasMaxLength(30);
        b.Property(x => x.FullName).HasMaxLength(200);
        b.Property(x => x.Phone).HasMaxLength(40);
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.Address).HasMaxLength(300);
        b.Property(x => x.NationalId).HasMaxLength(40);
        b.Property(x => x.BankAccount).HasMaxLength(60);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ContractType).HasConversion<string>().HasMaxLength(20);
        b.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<JobTitle>().WithMany().HasForeignKey(x => x.JobTitleId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<WorkSchedule>().WithMany().HasForeignKey(x => x.WorkScheduleId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<SalaryStructure>().WithMany().HasForeignKey(x => x.SalaryStructureId).OnDelete(DeleteBehavior.SetNull);
    }

    public void Configure(EntityTypeBuilder<WorkSchedule> b) { b.ToTable("work_schedules", "hr"); b.Property(x => x.Name).HasMaxLength(100); b.Property(x => x.WorkingDays).HasMaxLength(20); }

    public void Configure(EntityTypeBuilder<AttendanceRecord> b)
    {
        b.ToTable("attendance", "hr");
        b.HasIndex(x => new { x.EmployeeId, x.Date }).IsUnique();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Source).HasMaxLength(20);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<LeaveType> b) { b.ToTable("leave_types", "hr"); b.Property(x => x.Code).HasMaxLength(20); b.Property(x => x.Name).HasMaxLength(100); b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique(); }

    public void Configure(EntityTypeBuilder<LeaveRequest> b)
    {
        b.ToTable("leave_requests", "hr");
        b.HasIndex(x => new { x.EmployeeId, x.StartDate });
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.DecisionNote).HasMaxLength(500);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LeaveType>().WithMany().HasForeignKey(x => x.LeaveTypeId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<ExpenseClaim> b)
    {
        b.ToTable("expense_claims", "hr");
        b.Property(x => x.Category).HasMaxLength(60);
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<SalaryStructure> b) { b.ToTable("salary_structures", "hr"); b.Property(x => x.Name).HasMaxLength(100); b.Property(x => x.Jurisdiction).HasMaxLength(40); b.Property(x => x.ComponentsJson).HasColumnType("jsonb"); }

    public void Configure(EntityTypeBuilder<PayrollInput> b)
    {
        b.ToTable("payroll_inputs", "hr");
        b.HasIndex(x => new { x.EmployeeId, x.Year, x.Month });
        b.Property(x => x.Code).HasMaxLength(30);
        b.Property(x => x.Kind).HasMaxLength(20);
        b.Property(x => x.Note).HasMaxLength(300);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<PayrollRun> b)
    {
        b.ToTable("payroll_runs", "hr");
        b.HasIndex(x => new { x.TenantId, x.Year, x.Month }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
        b.Property(x => x.Number).HasMaxLength(40);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
    }

    public void Configure(EntityTypeBuilder<Payslip> b)
    {
        b.ToTable("payslips", "hr");
        b.HasIndex(x => new { x.RunId, x.EmployeeId }).IsUnique();
        b.Property(x => x.LinesJson).HasColumnType("jsonb");
        b.HasOne<PayrollRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<EmployeeDocument> b)
    {
        b.ToTable("employee_documents", "hr");
        b.Property(x => x.Type).HasMaxLength(60);
        b.Property(x => x.FileName).HasMaxLength(200);
        b.Property(x => x.StoragePath).HasMaxLength(300);
        b.Property(x => x.ContentType).HasMaxLength(60);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}
