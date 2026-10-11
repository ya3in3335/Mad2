using Microsoft.EntityFrameworkCore;

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
    public const int SchemaVersion = 4;

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

    /// <summary>Tables added by schema v3 (created in place when upgrading a v2 database).</summary>
    public static readonly IReadOnlyList<string> V3Tables =
    [
        "Suppliers", "Purchases", "PurchaseLines", "Payments", "ExpenseCategories", "Expenses", "Accounts", "JournalEntries", "JournalLines",
        "ClosedPeriods", "Employees", "SalaryAdvances", "PayrollRuns", "Payslips", "SaleReturns", "SaleReturnLines", "CashMovements",
    ];

    /// <summary>Tables added by schema v4 (cameras and per-sale snapshots).</summary>
    public static readonly IReadOnlyList<string> V4Tables = ["Cameras", "SaleSnapshots"];

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
        public const string SchemaVersion = "schema.version";
        public const string SyncCursor = "sync.cursor";
        public const string LastSyncAt = "sync.lastAt";
        public const string ServerSettingPrefix = "server.";
        public const string ReceiptCounter = "pos.receiptCounter";
        public const string StandaloneRegister = "standalone.registerId";
        public const string InStoreBarcodeCounter = "barcode.inStoreCounter";
    }
}
