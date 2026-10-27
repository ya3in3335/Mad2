using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Veltrix.Desktop.Core.Data;

public sealed class LocalProduct
{
    public Guid Id { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NameAr { get; set; }
    /// <summary>Newline-separated barcodes, matched exactly by the scanner lookup.</summary>
    public string Barcodes { get; set; } = "";
    public string Type { get; set; } = "Standard";
    public string Status { get; set; } = "Active";
    public Guid? CategoryId { get; set; }
    public decimal SalePrice { get; set; }
    public decimal TaxRatePercent { get; set; }
    public bool TrackStock { get; set; }
    public decimal LowStockThreshold { get; set; }
    /// <summary>Purchase cost (schema v2; maintained locally in standalone mode for margin reporting).</summary>
    public decimal PurchasePrice { get; set; }
    /// <summary>Moving-average unit cost (schema v3), updated by purchase receipts; 0 until the first receipt.</summary>
    public decimal AverageCost { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class LocalCustomer
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Type { get; set; } = "Individual";
    public string Name { get; set; } = "";
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public decimal Balance { get; set; }
    public decimal CreditLimit { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Server UpdatedAt the local copy is based on (sent as BaseVersion for conflict detection).</summary>
    public DateTimeOffset? ServerVersion { get; set; }
    public bool PendingSync { get; set; }
}

public sealed class LocalStock
{
    public Guid WarehouseId { get; set; }
    public Guid ProductId { get; set; }
    public string WarehouseName { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal Available { get; set; }
    public decimal LowStockThreshold { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class LocalRegister
{
    public Guid Id { get; set; }
    public Guid BranchId { get; set; }
    public Guid WarehouseId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsActive { get; set; }
    public Guid? OpenShiftId { get; set; }
}

public sealed class LocalShift
{
    public Guid Id { get; set; }
    public Guid RegisterId { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public decimal OpeningFloat { get; set; }
    public string Status { get; set; } = "Open";
    public DateTimeOffset? ClosedAt { get; set; }
    public decimal? CountedCash { get; set; }
}

public sealed class LocalSale
{
    public Guid Id { get; set; }
    /// <summary>Device-side number printed on receipts before the server assigns its own.</summary>
    public string ClientNumber { get; set; } = "";
    public string? ServerNumber { get; set; }
    public Guid ShiftId { get; set; }
    public Guid RegisterId { get; set; }
    public Guid? CustomerId { get; set; }
    public string OpId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Discount { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public decimal CashReceived { get; set; }
    public decimal Change { get; set; }
    /// <summary>JSON <c>ReceiptModel</c> used to (re)print the receipt offline.</summary>
    public string ReceiptJson { get; set; } = "";
    public string SyncStatus { get; set; } = OutboxStatus.Pending;
}

/// <summary>One line of a locally recorded sale (schema v2): feeds standalone reports, top products and margins.</summary>
public sealed class LocalSaleLine
{
    public Guid Id { get; set; }
    public Guid SaleId { get; set; }
    public Guid ProductId { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Discount { get; set; }
    public decimal Net { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public decimal UnitCost { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class OutboxStatus
{
    public const string Pending = "pending";
    public const string Done = "done";
    public const string Conflict = "conflict";
    public const string Rejected = "rejected";
    /// <summary>Standalone (offline-licensed) installations: recorded locally, never synchronized.</summary>
    public const string Local = "local";
}

/// <summary>Durable queue of offline operations, replayed to <c>/api/v1/sync/push</c> strictly in <see cref="Seq"/> order.</summary>
public sealed class OutboxEntry
{
    public long Seq { get; set; }
    public string OpId { get; set; } = "";
    public string Type { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public DateTimeOffset ClientTime { get; set; }
    public DateTimeOffset? BaseVersion { get; set; }
    public string Status { get; set; } = OutboxStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public string? EntityId { get; set; }
    public string? ResultJson { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class ParkedCart
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>CCTV camera (schema v4). Username, password and custom URL are stored encrypted (<c>dp1:</c> + base64 of DPAPI output).</summary>
public sealed class LocalCamera
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Brand { get; set; } = "Hikvision";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 554;
    public int HttpPort { get; set; } = 80;
    public string? UsernameProtected { get; set; }
    public string? PasswordProtected { get; set; }
    public string? CustomUrlProtected { get; set; }
    public int Channel { get; set; } = 1;
    public string Stream { get; set; } = "sub";
    public string Role { get; set; } = "general";
    public bool Enabled { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Cashier-camera photo taken when a POS sale completed (schema v4). <see cref="RelativePath"/> is relative to the app data root.</summary>
public sealed class LocalSaleSnapshot
{
    public Guid SaleId { get; set; }
    public Guid CameraId { get; set; }
    public string RelativePath { get; set; } = "";
    public DateTimeOffset TakenAt { get; set; }
}

public sealed class KeyValue
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class LocalDb(DbContextOptions<LocalDb> options) : DbContext(options)
{
    public const int SchemaVersion = 7;

    /// <summary>VELTRIX Link change capture for this unit of work (null = not captured: standalone PC, or applying replicated changes on a node).</summary>
    public Replication.LinkCapture? Link { get; init; }

    public DbSet<Replication.LinkChange> LinkChanges => Set<Replication.LinkChange>();
    public DbSet<Replication.LinkNode> LinkNodes => Set<Replication.LinkNode>();

    public DbSet<LocalProduct> Products => Set<LocalProduct>();
    public DbSet<LocalCustomer> Customers => Set<LocalCustomer>();
    public DbSet<LocalStock> Stock => Set<LocalStock>();
    public DbSet<LocalRegister> Registers => Set<LocalRegister>();
    public DbSet<LocalShift> Shifts => Set<LocalShift>();
    public DbSet<LocalSale> Sales => Set<LocalSale>();
    public DbSet<LocalSaleLine> SaleLines => Set<LocalSaleLine>();
    public DbSet<OutboxEntry> Outbox => Set<OutboxEntry>();
    public DbSet<ParkedCart> ParkedCarts => Set<ParkedCart>();
    public DbSet<KeyValue> Values => Set<KeyValue>();
    // schema v3: local ERP
    public DbSet<LocalSupplier> Suppliers => Set<LocalSupplier>();
    public DbSet<LocalPurchase> Purchases => Set<LocalPurchase>();
    public DbSet<LocalPurchaseLine> PurchaseLines => Set<LocalPurchaseLine>();
    public DbSet<LocalPayment> Payments => Set<LocalPayment>();
    public DbSet<LocalExpenseCategory> ExpenseCategories => Set<LocalExpenseCategory>();
    public DbSet<LocalExpense> Expenses => Set<LocalExpense>();
    public DbSet<LocalAccount> Accounts => Set<LocalAccount>();
    public DbSet<LocalJournalEntry> JournalEntries => Set<LocalJournalEntry>();
    public DbSet<LocalJournalLine> JournalLines => Set<LocalJournalLine>();
    public DbSet<LocalClosedPeriod> ClosedPeriods => Set<LocalClosedPeriod>();
    public DbSet<LocalEmployee> Employees => Set<LocalEmployee>();
    public DbSet<LocalSalaryAdvance> SalaryAdvances => Set<LocalSalaryAdvance>();
    public DbSet<LocalPayrollRun> PayrollRuns => Set<LocalPayrollRun>();
    public DbSet<LocalPayslip> Payslips => Set<LocalPayslip>();
    public DbSet<LocalSaleReturn> SaleReturns => Set<LocalSaleReturn>();
    public DbSet<LocalSaleReturnLine> SaleReturnLines => Set<LocalSaleReturnLine>();
    public DbSet<LocalCashMovement> CashMovements => Set<LocalCashMovement>();
    // schema v4: CCTV
    public DbSet<LocalCamera> Cameras => Set<LocalCamera>();
    public DbSet<LocalSaleSnapshot> SaleSnapshots => Set<LocalSaleSnapshot>();
    // schema v5: workforce & attendance
    public DbSet<WfDepartment> WfDepartments => Set<WfDepartment>();
    public DbSet<WfPosition> WfPositions => Set<WfPosition>();
    public DbSet<WfEmployeeDocument> WfEmployeeDocuments => Set<WfEmployeeDocument>();
    public DbSet<WfEmployeeAudit> WfEmployeeAudits => Set<WfEmployeeAudit>();
    public DbSet<WfCredential> WfCredentials => Set<WfCredential>();
    public DbSet<WfAttendanceEvent> WfAttendanceEvents => Set<WfAttendanceEvent>();
    public DbSet<WfCorrectionRequest> WfCorrectionRequests => Set<WfCorrectionRequest>();
    public DbSet<WfShiftTemplate> WfShiftTemplates => Set<WfShiftTemplate>();
    public DbSet<WfRotation> WfRotations => Set<WfRotation>();
    public DbSet<WfShiftAssignment> WfShiftAssignments => Set<WfShiftAssignment>();
    public DbSet<WfShiftOverride> WfShiftOverrides => Set<WfShiftOverride>();
    public DbSet<WfHoliday> WfHolidays => Set<WfHoliday>();
    public DbSet<WfSwapRequest> WfSwapRequests => Set<WfSwapRequest>();
    public DbSet<WfLeaveType> WfLeaveTypes => Set<WfLeaveType>();
    public DbSet<WfLeaveRequest> WfLeaveRequests => Set<WfLeaveRequest>();
    public DbSet<WfLeaveLedger> WfLeaveLedger => Set<WfLeaveLedger>();
    public DbSet<WfPayItem> WfPayItems => Set<WfPayItem>();
    public DbSet<WfStatutoryRule> WfStatutoryRules => Set<WfStatutoryRule>();
    public DbSet<WfPayrollPrep> WfPayrollPreps => Set<WfPayrollPrep>();
    public DbSet<WfPayrollPrepLine> WfPayrollPrepLines => Set<WfPayrollPrepLine>();
    public DbSet<WfDevice> WfDevices => Set<WfDevice>();
    public DbSet<WfDeviceInbox> WfDeviceInbox => Set<WfDeviceInbox>();
    public DbSet<WfZone> WfZones => Set<WfZone>();
    public DbSet<WfDoor> WfDoors => Set<WfDoor>();
    public DbSet<WfAccessGroup> WfAccessGroups => Set<WfAccessGroup>();
    public DbSet<WfAccessGroupMember> WfAccessGroupMembers => Set<WfAccessGroupMember>();
    public DbSet<WfZonePresence> WfZonePresence => Set<WfZonePresence>();
    public DbSet<WfAccessLog> WfAccessLogs => Set<WfAccessLog>();
    // schema v6: physical terminals
    public DbSet<WfImportBatch> WfImportBatches => Set<WfImportBatch>();
    public DbSet<WfDeviceLog> WfDeviceLogs => Set<WfDeviceLog>();

    /// <summary>Tables added by schema v3 (created in place when upgrading a v2 database).</summary>
    public static readonly IReadOnlyList<string> V3Tables =
    [
        "Suppliers", "Purchases", "PurchaseLines", "Payments", "ExpenseCategories", "Expenses", "Accounts", "JournalEntries", "JournalLines",
        "ClosedPeriods", "Employees", "SalaryAdvances", "PayrollRuns", "Payslips", "SaleReturns", "SaleReturnLines", "CashMovements",
    ];

    /// <summary>Tables added by schema v4 (cameras and per-sale snapshots).</summary>
    public static readonly IReadOnlyList<string> V4Tables = ["Cameras", "SaleSnapshots"];

    /// <summary>Tables added by schema v5 (workforce &amp; attendance).</summary>
    public static readonly IReadOnlyList<string> V5Tables =
    [
        "WfDepartments", "WfPositions", "WfEmployeeDocuments", "WfEmployeeAudits", "WfCredentials", "WfAttendanceEvents", "WfCorrectionRequests",
        "WfShiftTemplates", "WfRotations", "WfShiftAssignments", "WfShiftOverrides", "WfHolidays", "WfSwapRequests", "WfLeaveTypes", "WfLeaveRequests",
        "WfLeaveLedger", "WfPayItems", "WfStatutoryRules", "WfPayrollPreps", "WfPayrollPrepLines", "WfDevices", "WfDeviceInbox", "WfZones", "WfDoors",
        "WfAccessGroups", "WfAccessGroupMembers", "WfZonePresence", "WfAccessLogs",
    ];

    /// <summary>Columns added to existing tables by schema v5: (table, column, SQLite DDL type with default).</summary>
    public static readonly IReadOnlyList<(string Table, string Column, string Ddl)> V5Columns =
    [
        ("Employees", "PhotoPath", "TEXT NULL"), ("Employees", "Email", "TEXT NULL"), ("Employees", "DepartmentId", "TEXT NULL"),
        ("Employees", "PositionId", "TEXT NULL"), ("Employees", "Workplace", "TEXT NULL"), ("Employees", "ManagerId", "TEXT NULL"),
        ("Employees", "ContractType", "TEXT NULL"), ("Employees", "ContractStart", "TEXT NULL"), ("Employees", "ContractEnd", "TEXT NULL"),
        ("Employees", "TerminationDate", "TEXT NULL"), ("Employees", "Status", "TEXT NOT NULL DEFAULT 'Active'"),
        ("Employees", "EmergencyContactName", "TEXT NULL"), ("Employees", "EmergencyContactPhone", "TEXT NULL"), ("Employees", "Notes", "TEXT NULL"),
        ("Employees", "PayBasis", "TEXT NOT NULL DEFAULT 'Monthly'"), ("Employees", "HourlyRate", "TEXT NOT NULL DEFAULT '0.0'"),
        ("Payslips", "Deductions", "TEXT NOT NULL DEFAULT '0.0'"), ("PayrollRuns", "TotalDeductions", "TEXT NOT NULL DEFAULT '0.0'"),
    ];

    /// <summary>Tables added by schema v7 (VELTRIX Link: replication log and paired PCs).</summary>
    public static readonly IReadOnlyList<string> V7Tables = ["LinkChanges", "LinkNodes"];

    /// <summary>Tables added by schema v6 (terminal import batches and device logs).</summary>
    public static readonly IReadOnlyList<string> V6Tables = ["WfImportBatches", "WfDeviceLogs"];

    /// <summary>Columns added by schema v6 to the device registry and the persisted device queue.</summary>
    public static readonly IReadOnlyList<(string Table, string Column, string Ddl)> V6Columns =
    [
        ("WfDevices", "Manufacturer", "TEXT NULL"), ("WfDevices", "Model", "TEXT NULL"), ("WfDevices", "SerialNumber", "TEXT NULL"), ("WfDevices", "Firmware", "TEXT NULL"),
        ("WfDevices", "Host", "TEXT NULL"), ("WfDevices", "Port", "INTEGER NULL"), ("WfDevices", "Protocol", "TEXT NULL"), ("WfDevices", "Branch", "TEXT NULL"),
        ("WfDevices", "Location", "TEXT NULL"), ("WfDevices", "UserName", "TEXT NULL"), ("WfDevices", "SecretProtected", "TEXT NULL"),
        ("WfDevices", "TimeZoneOffsetMinutes", "INTEGER NULL"), ("WfDevices", "ApplyDriftCorrection", "INTEGER NOT NULL DEFAULT 1"),
        ("WfDevices", "DriftToleranceSeconds", "INTEGER NOT NULL DEFAULT 300"), ("WfDevices", "ClockDriftSeconds", "INTEGER NULL"),
        ("WfDevices", "ClockDriftMeasuredAt", "INTEGER NULL"), ("WfDevices", "PollIntervalSeconds", "INTEGER NOT NULL DEFAULT 60"),
        ("WfDevices", "SyncCursor", "TEXT NULL"), ("WfDevices", "LastSyncAt", "INTEGER NULL"), ("WfDevices", "LastSyncStatus", "TEXT NULL"),
        ("WfDevices", "LastConnectionAt", "INTEGER NULL"), ("WfDevices", "Capabilities", "TEXT NULL"), ("WfDevices", "IsRemoved", "INTEGER NOT NULL DEFAULT 0"),
        ("WfDevices", "RemovedAt", "INTEGER NULL"),
        ("WfDeviceInbox", "BatchId", "TEXT NULL"), ("WfDeviceInbox", "VendorEventId", "TEXT NULL"), ("WfDeviceInbox", "DeviceUserId", "TEXT NULL"),
        ("WfDeviceInbox", "CardNo", "TEXT NULL"), ("WfDeviceInbox", "DeviceLocalTime", "TEXT NULL"), ("WfDeviceInbox", "DeviceOffsetMinutes", "INTEGER NULL"),
        ("WfDeviceInbox", "Method", "TEXT NULL"), ("WfDeviceInbox", "VerifyResult", "TEXT NULL"), ("WfDeviceInbox", "RawJson", "TEXT NULL"),
        ("WfDeviceInbox", "ContentHash", "TEXT NULL"), ("WfDeviceInbox", "DriftAppliedSeconds", "INTEGER NULL"), ("WfDeviceInbox", "EmployeeId", "TEXT NULL"),
        ("WfDeviceInbox", "AttendanceEventId", "TEXT NULL"), ("WfDeviceInbox", "ReviewReason", "TEXT NULL"), ("WfDeviceInbox", "SuggestedUtc", "INTEGER NULL"),
        ("WfDeviceInbox", "ResolvedBy", "TEXT NULL"), ("WfDeviceInbox", "ResolvedAt", "INTEGER NULL"), ("WfDeviceInbox", "ResolutionNote", "TEXT NULL"),
    ];

    public static DbContextOptions<LocalDb> OptionsFor(string connectionString) =>
        new DbContextOptionsBuilder<LocalDb>().UseSqlite(connectionString).Options;

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<LocalProduct>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.Sku); e.HasIndex(x => x.Name); });
        b.Entity<LocalCustomer>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.Name); });
        b.Entity<LocalStock>(e => e.HasKey(x => new { x.WarehouseId, x.ProductId }));
        b.Entity<LocalRegister>().HasKey(x => x.Id);
        b.Entity<LocalShift>().HasKey(x => x.Id);
        b.Entity<LocalSale>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.CreatedAt); e.HasIndex(x => x.OpId).IsUnique(); });
        b.Entity<LocalSaleLine>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.SaleId); e.HasIndex(x => x.CreatedAt); });
        b.Entity<OutboxEntry>(e =>
        {
            e.HasKey(x => x.Seq);
            e.Property(x => x.Seq).ValueGeneratedOnAdd();
            e.HasIndex(x => x.OpId).IsUnique();
            e.HasIndex(x => new { x.Status, x.Seq });
        });
        b.Entity<ParkedCart>().HasKey(x => x.Id);
        b.Entity<KeyValue>().HasKey(x => x.Key);
        b.Entity<LocalSupplier>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.Name); });
        b.Entity<LocalPurchase>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.SupplierId); e.HasIndex(x => x.Date); e.HasIndex(x => x.Number).IsUnique(); });
        b.Entity<LocalPurchaseLine>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.PurchaseId); });
        b.Entity<LocalPayment>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.PartyType, x.PartyId }); });
        b.Entity<LocalExpenseCategory>().HasKey(x => x.Id);
        b.Entity<LocalExpense>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.Date); });
        b.Entity<LocalAccount>().HasKey(x => x.Code);
        b.Entity<LocalJournalEntry>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.Date); e.HasIndex(x => x.Number).IsUnique(); });
        b.Entity<LocalJournalLine>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.EntryId); e.HasIndex(x => new { x.AccountCode, x.Date }); });
        b.Entity<LocalClosedPeriod>().HasKey(x => x.Period);
        b.Entity<LocalEmployee>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.Name); });
        b.Entity<LocalSalaryAdvance>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.EmployeeId); });
        b.Entity<LocalPayrollRun>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.Year, x.Month }).IsUnique(); });
        b.Entity<LocalPayslip>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.RunId); });
        b.Entity<LocalSaleReturn>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.SaleId); });
        b.Entity<LocalSaleReturnLine>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.ReturnId); });
        b.Entity<LocalCashMovement>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.Date); e.HasIndex(x => x.ShiftId); });
        b.Entity<LocalCamera>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.SortOrder); });
        b.Entity<LocalSaleSnapshot>(e => { e.HasKey(x => x.SaleId); e.HasIndex(x => x.TakenAt); });
        b.Entity<WfDepartment>().HasKey(x => x.Id);
        b.Entity<WfPosition>().HasKey(x => x.Id);
        b.Entity<WfEmployeeDocument>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.EmployeeId); });
        b.Entity<WfEmployeeAudit>(e => { e.HasKey(x => x.Seq); e.Property(x => x.Seq).ValueGeneratedOnAdd(); e.HasIndex(x => x.EmployeeId); });
        b.Entity<WfCredential>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.EmployeeId); e.HasIndex(x => new { x.Kind, x.Value }); });
        b.Entity<WfAttendanceEvent>(e =>
        {
            e.HasKey(x => x.Seq);
            e.Property(x => x.Seq).ValueGeneratedOnAdd();
            e.HasIndex(x => x.Id).IsUnique();
            e.HasIndex(x => new { x.EmployeeId, x.OccurredTicks });
            e.HasIndex(x => new { x.DeviceId, x.DeviceEventId });
        });
        b.Entity<WfCorrectionRequest>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.EmployeeId); });
        b.Entity<WfShiftTemplate>().HasKey(x => x.Id);
        b.Entity<WfRotation>().HasKey(x => x.Id);
        b.Entity<WfShiftAssignment>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.EmployeeId); });
        b.Entity<WfShiftOverride>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.EmployeeId, x.Date }); });
        b.Entity<WfHoliday>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.Date); });
        b.Entity<WfSwapRequest>().HasKey(x => x.Id);
        b.Entity<WfLeaveType>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.Code).IsUnique(); });
        b.Entity<WfLeaveRequest>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.EmployeeId); });
        b.Entity<WfLeaveLedger>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.EmployeeId, x.LeaveTypeId }); e.HasIndex(x => x.RefKey).IsUnique(); });
        b.Entity<WfPayItem>().HasKey(x => x.Id);
        b.Entity<WfStatutoryRule>().HasKey(x => x.Id);
        b.Entity<WfPayrollPrep>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.Year, x.Month }); });
        b.Entity<WfPayrollPrepLine>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.PrepId); });
        b.Entity<WfDevice>().HasKey(x => x.Id);
        b.Entity<WfDeviceInbox>(e =>
        {
            e.HasKey(x => x.Seq);
            e.Property(x => x.Seq).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.DeviceId, x.DeviceEventId }).IsUnique();
            e.HasIndex(x => x.Status);
        });
        b.Entity<WfImportBatch>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.DeviceId); });
        b.Entity<WfDeviceLog>(e => { e.HasKey(x => x.Seq); e.Property(x => x.Seq).ValueGeneratedOnAdd(); e.HasIndex(x => x.DeviceId); });
        b.Entity<Replication.LinkChange>(e =>
        {
            e.HasKey(x => x.Seq);
            e.Property(x => x.Seq).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.Origin, x.OriginSeq });
            e.HasIndex(x => new { x.Status, x.Seq });
            e.HasIndex(x => new { x.Table, x.Key });
        });
        b.Entity<Replication.LinkNode>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.Code).IsUnique(); });
        b.Entity<WfZone>().HasKey(x => x.Id);
        b.Entity<WfDoor>().HasKey(x => x.Id);
        b.Entity<WfAccessGroup>().HasKey(x => x.Id);
        b.Entity<WfAccessGroupMember>().HasKey(x => new { x.GroupId, x.EmployeeId });
        b.Entity<WfZonePresence>().HasKey(x => new { x.EmployeeId, x.ZoneId });
        b.Entity<WfAccessLog>(e => { e.HasKey(x => x.Seq); e.Property(x => x.Seq).ValueGeneratedOnAdd(); e.HasIndex(x => x.DoorId); });

        // SQLite has no native decimal/DateTimeOffset ordering; store as TEXT/ticks via converters where queried.
        foreach (var p in b.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
        {
            if (p.ClrType == typeof(DateTimeOffset)) p.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
            if (p.ClrType == typeof(DateTimeOffset?)) p.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
        }
    }

    /// <summary>Creates the schema on first run, upgrades older schemas in place (never dropping data) and records the version.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var created = await Database.EnsureCreatedAsync(ct);
        await Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        var stored = await Values.FindAsync([Keys.SchemaVersion], ct);
        var version = created ? SchemaVersion : int.TryParse(stored?.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 1;
        if (version < 2) await MigrateToV2Async(ct);
        if (version < 3) await MigrateToV3Async(ct);
        if (version < 4) await CreateModelTablesAsync(V4Tables, ct);
        if (version < 5) await MigrateToV5Async(ct);
        if (version < 6) await MigrateColumnsAndTablesAsync(V6Columns, V6Tables, ct);
        if (version < 7) await CreateModelTablesAsync(V7Tables, ct);
        await Erp.DefaultChart.EnsureSeededAsync(this, ct);
        if (stored is null) Values.Add(new KeyValue { Key = Keys.SchemaVersion, Value = SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        else stored.Value = SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await SaveChangesAsync(ct);
    }

    /// <summary>v1 → v2: product purchase price and the sale-lines table (additive only).</summary>
    private async Task MigrateToV2Async(CancellationToken ct)
    {
        var conn = Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        var hasColumn = false;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Products') WHERE name = 'PurchasePrice'";
            hasColumn = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) > 0;
        }
        if (!hasColumn) await Database.ExecuteSqlRawAsync("ALTER TABLE \"Products\" ADD COLUMN \"PurchasePrice\" TEXT NOT NULL DEFAULT '0.0';", ct);
        await Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "SaleLines" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_SaleLines" PRIMARY KEY, "SaleId" TEXT NOT NULL, "ProductId" TEXT NOT NULL, "Sku" TEXT NOT NULL,
                "Name" TEXT NOT NULL, "Quantity" TEXT NOT NULL, "UnitPrice" TEXT NOT NULL, "Discount" TEXT NOT NULL, "Net" TEXT NOT NULL,
                "Tax" TEXT NOT NULL, "Total" TEXT NOT NULL, "UnitCost" TEXT NOT NULL, "CreatedAt" INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS "IX_SaleLines_SaleId" ON "SaleLines" ("SaleId");
            CREATE INDEX IF NOT EXISTS "IX_SaleLines_CreatedAt" ON "SaleLines" ("CreatedAt");
            """, ct);
    }

    /// <summary>v2 → v3: product moving-average cost and the ERP tables (additive only; existing rows are kept).</summary>
    private async Task MigrateToV3Async(CancellationToken ct)
    {
        var conn = Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        long count;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Products') WHERE name = 'AverageCost'";
            count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        }
        await using var tx = await Database.BeginTransactionAsync(ct);
        if (count == 0)
        {
            await Database.ExecuteSqlRawAsync("ALTER TABLE \"Products\" ADD COLUMN \"AverageCost\" TEXT NOT NULL DEFAULT '0.0';", ct);
            // Existing stock keeps its known purchase cost as the starting average.
            await Database.ExecuteSqlRawAsync("UPDATE \"Products\" SET \"AverageCost\" = \"PurchasePrice\";", ct);
        }
        await CreateModelTablesAsync(V3Tables, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>v4 → v5: workforce profile columns on Employees, statutory deduction columns on payroll, and the workforce tables (additive only).</summary>
    private async Task MigrateToV5Async(CancellationToken ct)
    {
        await using var tx = await Database.BeginTransactionAsync(ct);
        await AddMissingColumnsAsync(V5Columns, tx, ct);
        // Existing employees keep their active flag as status.
        await Database.ExecuteSqlRawAsync("UPDATE \"Employees\" SET \"Status\" = 'Terminated' WHERE \"IsActive\" = 0 AND \"Status\" = 'Active';", ct);
        await CreateModelTablesAsync(V5Tables, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Additive migration: missing columns, then missing tables (with their indexes), in one transaction.</summary>
    private async Task MigrateColumnsAndTablesAsync(IReadOnlyList<(string Table, string Column, string Ddl)> columns, IReadOnlyList<string> tables, CancellationToken ct)
    {
        await using var tx = await Database.BeginTransactionAsync(ct);
        await AddMissingColumnsAsync(columns, tx, ct);
        await CreateModelTablesAsync(tables, ct);
        await tx.CommitAsync(ct);
    }

    private async Task AddMissingColumnsAsync(IReadOnlyList<(string Table, string Column, string Ddl)> columns, IDbContextTransaction tx, CancellationToken ct)
    {
        var conn = Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        foreach (var (table, column, ddl) in columns)
        {
            long count;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx.GetDbTransaction();
                cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
                count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
            }
#pragma warning disable EF1002 // constant identifiers from V5Columns, no user input
            if (count == 0) await Database.ExecuteSqlRawAsync($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {ddl};", ct);
#pragma warning restore EF1002
        }
    }

    /// <summary>Creates the given tables (and their indexes) from the model's own DDL, so column types always match the mapping; idempotent.</summary>
    private async Task CreateModelTablesAsync(IReadOnlyList<string> tables, CancellationToken ct)
    {
        foreach (var stmt in Database.GenerateCreateScript().Split(";", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!tables.Any(t => stmt.Contains($"\"{t}\" (", StringComparison.Ordinal))) continue;
            var sql = stmt.Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ").Replace("CREATE UNIQUE INDEX ", "CREATE UNIQUE INDEX IF NOT EXISTS ")
                .Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ");
#pragma warning disable EF1002 // DDL generated by EF from the model, no user input
            await Database.ExecuteSqlRawAsync(sql, ct);
#pragma warning restore EF1002
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Link?.Capture(this);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Link?.Capture(this);
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public async Task<string?> GetValueAsync(string key, CancellationToken ct = default) => (await Values.FindAsync([key], ct))?.Value;

    public async Task SetValueAsync(string key, string value, CancellationToken ct = default)
    {
        var kv = await Values.FindAsync([key], ct);
        if (kv is null) Values.Add(new KeyValue { Key = key, Value = value });
        else kv.Value = value;
        await SaveChangesAsync(ct);
    }

    public static class Keys
    {
        /// <summary>VELTRIX Link: this PC's branch code (absent on the company PC).</summary>
        public const string LinkNodeCode = "link.nodeCode";
        public const string SchemaVersion = "schema.version";
        public const string SyncCursor = "sync.cursor";
        public const string LastSyncAt = "sync.lastAt";
        public const string ServerSettingPrefix = "server.";
        public const string ReceiptCounter = "pos.receiptCounter";
        public const string StandaloneRegister = "standalone.registerId";
        public const string InStoreBarcodeCounter = "barcode.inStoreCounter";
    }
}
