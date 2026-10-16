using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;

namespace Veltrix.Desktop.Core.Standalone;

/// <summary>Everything a standalone installation needs to continue on another device.</summary>
public sealed record BackupPayload(int Version, DateTimeOffset CreatedAt, string? TenantId, string? TenantName, string UsersJson,
    List<LocalProduct> Products, List<LocalCustomer> Customers, List<LocalStock> Stock, List<LocalRegister> Registers, List<LocalShift> Shifts,
    List<LocalSale> Sales, List<LocalSaleLine> SaleLines, List<KeyValue> Values, ErpBackup? Erp = null,
    Dictionary<string, JsonElement>? Workforce = null);

/// <summary>Local ERP tables (format version 2). Absent in version-1 backups.</summary>
public sealed record ErpBackup(List<LocalSupplier> Suppliers, List<LocalPurchase> Purchases, List<LocalPurchaseLine> PurchaseLines, List<LocalPayment> Payments,
    List<LocalExpenseCategory> ExpenseCategories, List<LocalExpense> Expenses, List<LocalAccount> Accounts, List<LocalJournalEntry> JournalEntries,
    List<LocalJournalLine> JournalLines, List<LocalClosedPeriod> ClosedPeriods, List<LocalEmployee> Employees, List<LocalSalaryAdvance> SalaryAdvances,
    List<LocalPayrollRun> PayrollRuns, List<LocalPayslip> Payslips, List<LocalSaleReturn> SaleReturns, List<LocalSaleReturnLine> SaleReturnLines,
    List<LocalCashMovement> CashMovements);

/// <summary>
/// Encrypted local backup. File layout: <c>"VLXBAK01" | iterations (int32 LE) | salt (16) | nonce (12) | tag (16) | ciphertext</c>.
/// The AES-256-GCM key is derived from the owner's password with PBKDF2-SHA256; the header is authenticated as associated data,
/// and the plaintext is gzip-compressed JSON. A wrong password or any tampering fails authentication and nothing is restored.
/// </summary>
public static class LocalBackup
{
    public const int FormatVersion = 3;
    public const string FileExtension = ".vlxbak";
    private static readonly byte[] Magic = "VLXBAK01"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int HeaderLength = 8 + 4 + 16;

    public static byte[] Encrypt(BackupPayload payload, string password, int iterations = PasswordHasher.DefaultIterations)
    {
        using var raw = new MemoryStream();
        using (var gz = new GZipStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(gz, payload, Json);
        var plain = raw.ToArray();

        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var header = new byte[HeaderLength];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), iterations);
        salt.CopyTo(header, 12);
        var key = DeriveKey(password, salt, iterations);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(nonce, plain, cipher, tag, header);
        CryptographicOperations.ZeroMemory(key);
        return [.. header, .. nonce, .. tag, .. cipher];
    }

    /// <summary>Decrypts a backup. Throws <see cref="InvalidDataException"/> for a foreign file and
    /// <see cref="AuthenticationTagMismatchException"/> for a wrong password or a modified file.</summary>
    public static BackupPayload Decrypt(byte[] file, string password)
    {
        if (file.Length < HeaderLength + 28 || !file.AsSpan(0, 8).SequenceEqual(Magic)) throw new InvalidDataException("Not a VELTRIX backup file.");
        var iterations = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(8));
        if (iterations is < PasswordHasher.MinimumIterations or > 10_000_000) throw new InvalidDataException("Unsupported backup parameters.");
        var salt = file.AsSpan(12, 16).ToArray();
        var nonce = file.AsSpan(HeaderLength, 12);
        var tag = file.AsSpan(HeaderLength + 12, 16);
        var cipher = file.AsSpan(HeaderLength + 28);
        var plain = new byte[cipher.Length];
        var key = DeriveKey(password, salt, iterations);
        try
        {
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(nonce, cipher, tag, plain, file.AsSpan(0, HeaderLength));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        using var gz = new GZipStream(new MemoryStream(plain), CompressionMode.Decompress);
        var payload = JsonSerializer.Deserialize<BackupPayload>(gz, Json) ?? throw new InvalidDataException("Empty backup.");
        if (payload.Version > FormatVersion) throw new InvalidDataException("This backup was made by a newer version.");
        return payload;
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>Snapshot of the local database (excluding the sync outbox/cursor, which belong to online mode).</summary>
    public static async Task<BackupPayload> CaptureAsync(LocalDb db, string usersJson, string? tenantId, string? tenantName, DateTimeOffset now, CancellationToken ct = default) =>
        new(FormatVersion, now, tenantId, tenantName, usersJson,
            await db.Products.AsNoTracking().ToListAsync(ct), await db.Customers.AsNoTracking().ToListAsync(ct), await db.Stock.AsNoTracking().ToListAsync(ct),
            await db.Registers.AsNoTracking().ToListAsync(ct), await db.Shifts.AsNoTracking().ToListAsync(ct), await db.Sales.AsNoTracking().ToListAsync(ct),
            await db.SaleLines.AsNoTracking().ToListAsync(ct),
            await db.Values.AsNoTracking().Where(v => v.Key != LocalDb.Keys.SchemaVersion && !v.Key.StartsWith("sync.")).ToListAsync(ct),
            new ErpBackup(await db.Suppliers.AsNoTracking().ToListAsync(ct), await db.Purchases.AsNoTracking().ToListAsync(ct),
                await db.PurchaseLines.AsNoTracking().ToListAsync(ct), await db.Payments.AsNoTracking().ToListAsync(ct),
                await db.ExpenseCategories.AsNoTracking().ToListAsync(ct), await db.Expenses.AsNoTracking().ToListAsync(ct), await db.Accounts.AsNoTracking().ToListAsync(ct),
                await db.JournalEntries.AsNoTracking().ToListAsync(ct), await db.JournalLines.AsNoTracking().ToListAsync(ct),
                await db.ClosedPeriods.AsNoTracking().ToListAsync(ct), await db.Employees.AsNoTracking().ToListAsync(ct),
                await db.SalaryAdvances.AsNoTracking().ToListAsync(ct), await db.PayrollRuns.AsNoTracking().ToListAsync(ct), await db.Payslips.AsNoTracking().ToListAsync(ct),
                await db.SaleReturns.AsNoTracking().ToListAsync(ct), await db.SaleReturnLines.AsNoTracking().ToListAsync(ct),
                await db.CashMovements.AsNoTracking().ToListAsync(ct)),
            await CaptureWorkforceAsync(db, ct));

    /// <summary>Workforce tables (format version 3), keyed by table name. Every v5 and v6 table must be listed here.</summary>
    private static readonly IReadOnlyList<WorkforceTable> WorkforceTables =
    [
        T(d => d.WfDepartments, "WfDepartments"), T(d => d.WfPositions, "WfPositions"), T(d => d.WfEmployeeDocuments, "WfEmployeeDocuments"),
        T(d => d.WfEmployeeAudits, "WfEmployeeAudits"), T(d => d.WfCredentials, "WfCredentials"), T(d => d.WfAttendanceEvents, "WfAttendanceEvents"),
        T(d => d.WfCorrectionRequests, "WfCorrectionRequests"), T(d => d.WfShiftTemplates, "WfShiftTemplates"), T(d => d.WfRotations, "WfRotations"),
        T(d => d.WfShiftAssignments, "WfShiftAssignments"), T(d => d.WfShiftOverrides, "WfShiftOverrides"), T(d => d.WfHolidays, "WfHolidays"),
        T(d => d.WfSwapRequests, "WfSwapRequests"), T(d => d.WfLeaveTypes, "WfLeaveTypes"), T(d => d.WfLeaveRequests, "WfLeaveRequests"),
        T(d => d.WfLeaveLedger, "WfLeaveLedger"), T(d => d.WfPayItems, "WfPayItems"), T(d => d.WfStatutoryRules, "WfStatutoryRules"),
        T(d => d.WfPayrollPreps, "WfPayrollPreps"), T(d => d.WfPayrollPrepLines, "WfPayrollPrepLines"), T(d => d.WfDevices, "WfDevices"),
        T(d => d.WfDeviceInbox, "WfDeviceInbox"), T(d => d.WfZones, "WfZones"), T(d => d.WfDoors, "WfDoors"), T(d => d.WfAccessGroups, "WfAccessGroups"),
        T(d => d.WfAccessGroupMembers, "WfAccessGroupMembers"), T(d => d.WfZonePresence, "WfZonePresence"), T(d => d.WfAccessLogs, "WfAccessLogs"),
        T(d => d.WfImportBatches, "WfImportBatches"), T(d => d.WfDeviceLogs, "WfDeviceLogs"),
    ];

    /// <summary>Names of the workforce tables carried by a backup (tests assert this covers every schema v5 table).</summary>
    public static IReadOnlyList<string> WorkforceTableNames => WorkforceTables.Select(t => t.Name).ToList();

    private sealed record WorkforceTable(string Name, Func<LocalDb, CancellationToken, Task<JsonElement>> Capture, Action<LocalDb> Clear, Action<LocalDb, JsonElement> Restore);

    private static WorkforceTable T<TEntity>(Func<LocalDb, DbSet<TEntity>> set, string name) where TEntity : class => new(name,
        async (db, ct) => JsonSerializer.SerializeToElement(await set(db).AsNoTracking().ToListAsync(ct), Json),
        db => set(db).RemoveRange(set(db)),
        (db, json) => set(db).AddRange(json.Deserialize<List<TEntity>>(Json) ?? []));

    private static async Task<Dictionary<string, JsonElement>> CaptureWorkforceAsync(LocalDb db, CancellationToken ct)
    {
        var result = new Dictionary<string, JsonElement>();
        foreach (var t in WorkforceTables) result[t.Name] = await t.Capture(db, ct);
        return result;
    }

    /// <summary>Replaces all local business data with the backup, atomically.</summary>
    public static async Task RestoreAsync(LocalDb db, BackupPayload p, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.SaleLines.RemoveRange(db.SaleLines);
        db.Suppliers.RemoveRange(db.Suppliers);
        db.Purchases.RemoveRange(db.Purchases);
        db.PurchaseLines.RemoveRange(db.PurchaseLines);
        db.Payments.RemoveRange(db.Payments);
        db.ExpenseCategories.RemoveRange(db.ExpenseCategories);
        db.Expenses.RemoveRange(db.Expenses);
        db.Accounts.RemoveRange(db.Accounts);
        db.JournalEntries.RemoveRange(db.JournalEntries);
        db.JournalLines.RemoveRange(db.JournalLines);
        db.ClosedPeriods.RemoveRange(db.ClosedPeriods);
        db.Employees.RemoveRange(db.Employees);
        foreach (var t in WorkforceTables) t.Clear(db);
        db.SalaryAdvances.RemoveRange(db.SalaryAdvances);
        db.PayrollRuns.RemoveRange(db.PayrollRuns);
        db.Payslips.RemoveRange(db.Payslips);
        db.SaleReturns.RemoveRange(db.SaleReturns);
        db.SaleReturnLines.RemoveRange(db.SaleReturnLines);
        db.CashMovements.RemoveRange(db.CashMovements);
        db.Sales.RemoveRange(db.Sales);
        db.Shifts.RemoveRange(db.Shifts);
        db.Stock.RemoveRange(db.Stock);
        db.Registers.RemoveRange(db.Registers);
        db.Customers.RemoveRange(db.Customers);
        db.Products.RemoveRange(db.Products);
        db.ParkedCarts.RemoveRange(db.ParkedCarts);
        db.Outbox.RemoveRange(db.Outbox);
        db.Values.RemoveRange(db.Values.Where(v => v.Key != LocalDb.Keys.SchemaVersion));
        await db.SaveChangesAsync(ct);
        db.Products.AddRange(p.Products);
        db.Customers.AddRange(p.Customers);
        db.Stock.AddRange(p.Stock);
        db.Registers.AddRange(p.Registers);
        db.Shifts.AddRange(p.Shifts);
        db.Sales.AddRange(p.Sales);
        db.SaleLines.AddRange(p.SaleLines);
        db.Values.AddRange(p.Values.Where(v => v.Key != LocalDb.Keys.SchemaVersion));
        if (p.Erp is { } e)
        {
            db.Suppliers.AddRange(e.Suppliers); db.Purchases.AddRange(e.Purchases); db.PurchaseLines.AddRange(e.PurchaseLines); db.Payments.AddRange(e.Payments);
            db.ExpenseCategories.AddRange(e.ExpenseCategories); db.Expenses.AddRange(e.Expenses); db.Accounts.AddRange(e.Accounts);
            db.JournalEntries.AddRange(e.JournalEntries); db.JournalLines.AddRange(e.JournalLines); db.ClosedPeriods.AddRange(e.ClosedPeriods);
            db.Employees.AddRange(e.Employees); db.SalaryAdvances.AddRange(e.SalaryAdvances); db.PayrollRuns.AddRange(e.PayrollRuns); db.Payslips.AddRange(e.Payslips);
            db.SaleReturns.AddRange(e.SaleReturns); db.SaleReturnLines.AddRange(e.SaleReturnLines); db.CashMovements.AddRange(e.CashMovements);
        }
        if (p.Workforce is { } w)
            foreach (var t in WorkforceTables)
                if (w.TryGetValue(t.Name, out var json)) t.Restore(db, json);
        await db.SaveChangesAsync(ct);
        // A version-1 backup has no chart of accounts: seed the default one so postings keep working.
        await Erp.DefaultChart.EnsureSeededAsync(db, ct);
        await tx.CommitAsync(ct);
    }
}
