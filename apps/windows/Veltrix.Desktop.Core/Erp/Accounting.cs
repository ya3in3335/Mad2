using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;

namespace Veltrix.Desktop.Core.Erp;

/// <summary>System roles used by automatic postings (ported from the server accounting module).</summary>
public static class AccountRoles
{
    public const string Cash = "cash";
    public const string Bank = "bank";
    public const string CardClearing = "card_clearing";
    public const string AccountsReceivable = "accounts_receivable";
    public const string AccountsPayable = "accounts_payable";
    public const string Inventory = "inventory";
    public const string VatCollected = "vat_collected";
    public const string VatDeductible = "vat_deductible";
    public const string SalesRevenue = "sales_revenue";
    public const string ServiceRevenue = "service_revenue";
    public const string SalesReturns = "sales_returns";
    public const string CostOfGoodsSold = "cogs";
    public const string NonStockPurchases = "non_stock_purchases";
    public const string InventoryAdjustment = "inventory_adjustment";
    public const string PurchasePriceVariance = "purchase_price_variance";
    public const string GeneralExpense = "general_expense";
    public const string Salaries = "salaries_expense";
    public const string SalariesPayable = "salaries_payable";
    public const string EmployeeAdvances = "employee_advances";
    public const string PayrollDeductionsPayable = "payroll_deductions_payable";
    public const string Equity = "equity";
    public const string RetainedEarnings = "retained_earnings";
    public const string CashOverShort = "cash_over_short";
    public const string Rounding = "rounding";
    public const string CustomerAdvances = "customer_advances";
    public const string SupplierAdvances = "supplier_advances";

    public static string ForMethod(string method) => method switch
    {
        PayMethods.Cash => Cash,
        PayMethods.Bank => Bank,
        _ => throw new InvalidOperationException("erp.err.method"),
    };
}

public static class AccountTypes
{
    public const string Asset = "Asset";
    public const string Liability = "Liability";
    public const string Equity = "Equity";
    public const string Revenue = "Revenue";
    public const string Expense = "Expense";
    public static bool IsDebitNormal(string type) => type is Asset or Expense;
}

/// <summary>
/// Default chart of accounts inspired by the Algerian SCF numbering (classes 1–7), ported from the server's DefaultChart.
/// A starting template the company's accountant must review; it is not a claim of regulatory compliance.
/// </summary>
public static class DefaultChart
{
    public sealed record Def(string Code, string Fr, string Ar, string En, string Type, string? Role, string? Parent, bool Postable = true);

    public static readonly IReadOnlyList<Def> Accounts =
    [
        new("1", "Capitaux propres", "رؤوس الأموال", "Equity", AccountTypes.Equity, null, null, false),
        new("101", "Capital social", "رأس المال", "Share capital", AccountTypes.Equity, AccountRoles.Equity, "1"),
        new("106", "Réserves", "الاحتياطات", "Reserves", AccountTypes.Equity, null, "1"),
        new("110", "Report à nouveau", "الترحيل من جديد", "Retained earnings", AccountTypes.Equity, AccountRoles.RetainedEarnings, "1"),
        new("2", "Immobilisations", "التثبيتات", "Fixed assets", AccountTypes.Asset, null, null, false),
        new("215", "Installations techniques, matériel", "المنشآت التقنية والمعدات", "Equipment", AccountTypes.Asset, null, "2"),
        new("218", "Autres immobilisations corporelles", "تثبيتات عينية أخرى", "Other fixed assets", AccountTypes.Asset, null, "2"),
        new("3", "Stocks", "المخزونات", "Inventories", AccountTypes.Asset, null, null, false),
        new("30", "Stocks de marchandises", "مخزون البضائع", "Merchandise inventory", AccountTypes.Asset, AccountRoles.Inventory, "3"),
        new("31", "Matières premières et fournitures", "المواد الأولية واللوازم", "Raw materials & supplies", AccountTypes.Asset, null, "3"),
        new("37", "Stocks à l'extérieur", "مخزونات في الخارج", "Stock held elsewhere", AccountTypes.Asset, null, "3"),
        new("4", "Comptes de tiers", "حسابات الغير", "Third parties", AccountTypes.Asset, null, null, false),
        new("401", "Fournisseurs", "الموردون", "Accounts payable", AccountTypes.Liability, AccountRoles.AccountsPayable, "4"),
        new("409", "Fournisseurs débiteurs (avances)", "موردون مدينون", "Supplier advances", AccountTypes.Asset, AccountRoles.SupplierAdvances, "4"),
        new("411", "Clients", "الزبائن", "Accounts receivable", AccountTypes.Asset, AccountRoles.AccountsReceivable, "4"),
        new("419", "Clients créditeurs (avances)", "زبائن دائنون", "Customer advances", AccountTypes.Liability, AccountRoles.CustomerAdvances, "4"),
        new("421", "Personnel - rémunérations dues", "المستخدمون - أجور مستحقة", "Salaries payable", AccountTypes.Liability, AccountRoles.SalariesPayable, "4"),
        new("425", "Personnel - avances et acomptes", "المستخدمون - تسبيقات", "Salary advances", AccountTypes.Asset, AccountRoles.EmployeeAdvances, "4"),
        new("431", "Organismes sociaux", "الهيئات الاجتماعية", "Payroll deductions payable", AccountTypes.Liability, AccountRoles.PayrollDeductionsPayable, "4"),
        new("4456", "TVA déductible", "الرسم على القيمة المضافة القابل للخصم", "Input VAT", AccountTypes.Asset, AccountRoles.VatDeductible, "4"),
        new("4457", "TVA collectée", "الرسم على القيمة المضافة المحصل", "Output VAT", AccountTypes.Liability, AccountRoles.VatCollected, "4"),
        new("5", "Comptes financiers", "الحسابات المالية", "Financial accounts", AccountTypes.Asset, null, null, false),
        new("512", "Banque", "البنك", "Bank", AccountTypes.Asset, AccountRoles.Bank, "5"),
        new("53", "Caisse", "الصندوق", "Cash", AccountTypes.Asset, null, "5", false),
        new("530", "Caisse principale", "الصندوق الرئيسي", "Main cash", AccountTypes.Asset, AccountRoles.Cash, "53"),
        new("581", "Paiements par carte en cours", "مدفوعات البطاقات قيد التحصيل", "Card payments clearing", AccountTypes.Asset, AccountRoles.CardClearing, "5"),
        new("6", "Charges", "الأعباء", "Expenses", AccountTypes.Expense, null, null, false),
        new("60", "Achats consommés", "المشتريات المستهلكة", "Purchases consumed", AccountTypes.Expense, null, "6", false),
        new("600", "Achats de marchandises vendues", "مشتريات البضائع المبيعة", "Cost of goods sold", AccountTypes.Expense, AccountRoles.CostOfGoodsSold, "60"),
        new("607", "Achats non stockés (eau, électricité, gaz)", "مشتريات غير مخزنة (ماء، كهرباء، غاز)", "Non-stocked purchases (utilities)", AccountTypes.Expense, AccountRoles.NonStockPurchases, "60"),
        new("61", "Services extérieurs", "الخدمات الخارجية", "External services", AccountTypes.Expense, null, "6", false),
        new("613", "Locations", "الإيجارات", "Rent", AccountTypes.Expense, null, "61"),
        new("615", "Entretien et réparations", "الصيانة والإصلاحات", "Maintenance", AccountTypes.Expense, null, "61"),
        new("616", "Primes d'assurances", "أقساط التأمين", "Insurance", AccountTypes.Expense, null, "61"),
        new("62", "Autres services extérieurs", "خدمات خارجية أخرى", "Other external services", AccountTypes.Expense, null, "6", false),
        new("624", "Transports", "النقل", "Transport", AccountTypes.Expense, null, "62"),
        new("626", "Frais postaux et télécommunications", "البريد والاتصالات", "Telecom", AccountTypes.Expense, null, "62"),
        new("627", "Services bancaires", "الخدمات المصرفية", "Bank charges", AccountTypes.Expense, null, "62"),
        new("628", "Charges diverses de gestion", "أعباء تسيير متنوعة", "General expenses", AccountTypes.Expense, AccountRoles.GeneralExpense, "62"),
        new("63", "Charges de personnel", "أعباء المستخدمين", "Staff costs", AccountTypes.Expense, null, "6", false),
        new("631", "Rémunérations du personnel", "أجور المستخدمين", "Salaries", AccountTypes.Expense, AccountRoles.Salaries, "63"),
        new("64", "Impôts, taxes et versements assimilés", "الضرائب والرسوم", "Taxes and duties", AccountTypes.Expense, null, "6"),
        new("65", "Autres charges opérationnelles", "أعباء عملياتية أخرى", "Other operating expenses", AccountTypes.Expense, null, "6", false),
        new("6581", "Écarts d'inventaire", "فروقات الجرد", "Inventory adjustments", AccountTypes.Expense, AccountRoles.InventoryAdjustment, "65"),
        new("6582", "Écarts sur prix d'achat", "فروقات أسعار الشراء", "Purchase price variance", AccountTypes.Expense, AccountRoles.PurchasePriceVariance, "65"),
        new("6583", "Écarts de caisse", "فروقات الصندوق", "Cash over/short", AccountTypes.Expense, AccountRoles.CashOverShort, "65"),
        new("6584", "Écarts d'arrondi", "فروقات التقريب", "Rounding differences", AccountTypes.Expense, AccountRoles.Rounding, "65"),
        new("7", "Produits", "المنتوجات", "Revenue", AccountTypes.Revenue, null, null, false),
        new("70", "Ventes et produits annexes", "المبيعات والمنتوجات الملحقة", "Sales", AccountTypes.Revenue, null, "7", false),
        new("700", "Ventes de marchandises", "مبيعات البضائع", "Sales of goods", AccountTypes.Revenue, AccountRoles.SalesRevenue, "70"),
        new("706", "Prestations de services", "تقديم الخدمات", "Services revenue", AccountTypes.Revenue, AccountRoles.ServiceRevenue, "70"),
        new("709", "Retours et rabais sur ventes", "مردودات وتخفيضات المبيعات", "Sales returns & allowances", AccountTypes.Revenue, AccountRoles.SalesReturns, "70"),
        new("75", "Autres produits opérationnels", "منتوجات عملياتية أخرى", "Other operating income", AccountTypes.Revenue, null, "7", false),
        new("758", "Autres produits de gestion", "منتوجات تسيير أخرى", "Other income", AccountTypes.Revenue, null, "75"),
    ];

    /// <summary>Seeded expense categories: key, French name, account.</summary>
    public static readonly IReadOnlyList<(string Key, string Name, string Account)> ExpenseCategories =
    [
        ("rent", "Loyer", "613"),
        ("electricity", "Électricité / eau / gaz", "607"),
        ("salaries", "Salaires (hors paie)", "631"),
        ("telecom", "Téléphone / Internet", "626"),
        ("transport", "Transport", "624"),
        ("maintenance", "Entretien et réparations", "615"),
        ("insurance", "Assurances", "616"),
        ("bank", "Frais bancaires", "627"),
        ("taxes", "Impôts et taxes", "64"),
        ("other", "Divers", "628"),
    ];

    /// <summary>Adds the chart and the expense categories when missing (first run, upgrade, restore of an old backup).</summary>
    public static async Task EnsureSeededAsync(LocalDb db, CancellationToken ct = default)
    {
        var existing = (await db.Accounts.Select(a => a.Code).ToListAsync(ct)).ToHashSet();
        foreach (var d in Accounts.Where(d => !existing.Contains(d.Code)))
            db.Accounts.Add(new LocalAccount { Code = d.Code, Name = d.Fr, NameEn = d.En, NameAr = d.Ar, Type = d.Type, Role = d.Role, ParentCode = d.Parent, IsPostable = d.Postable });
        if (!await db.ExpenseCategories.AnyAsync(ct))
            foreach (var c in ExpenseCategories)
                db.ExpenseCategories.Add(new LocalExpenseCategory { Id = Guid.NewGuid(), Key = c.Key, Name = c.Name, AccountCode = c.Account });
        await db.SaveChangesAsync(ct);
    }
}

public sealed record PostingLine(string? Role, string? AccountCode, decimal Debit, decimal Credit, string? Description = null, string? PartyType = null, Guid? PartyId = null);

/// <summary>Accumulates journal lines for a business document, then posts them as one balanced entry (port of the server builder).</summary>
public sealed class PostingBuilder
{
    private readonly List<PostingLine> _lines = [];
    public IReadOnlyList<PostingLine> Lines => _lines;

    public PostingBuilder Debit(string role, decimal amount, string? description = null, string? partyType = null, Guid? partyId = null) =>
        Add(new PostingLine(role, null, amount, 0, description, partyType, partyId));
    public PostingBuilder Credit(string role, decimal amount, string? description = null, string? partyType = null, Guid? partyId = null) =>
        Add(new PostingLine(role, null, 0, amount, description, partyType, partyId));
    public PostingBuilder DebitAccount(string code, decimal amount, string? description = null, string? partyType = null, Guid? partyId = null) =>
        Add(new PostingLine(null, code, amount, 0, description, partyType, partyId));
    public PostingBuilder CreditAccount(string code, decimal amount, string? description = null, string? partyType = null, Guid? partyId = null) =>
        Add(new PostingLine(null, code, 0, amount, description, partyType, partyId));

    private PostingBuilder Add(PostingLine line)
    {
        // A negative debit is a credit (and vice versa); zero lines are dropped.
        var debit = Money.Round(line.Debit);
        var credit = Money.Round(line.Credit);
        if (debit < 0) { credit += -debit; debit = 0; }
        if (credit < 0) { debit += -credit; credit = 0; }
        var net = debit - credit;
        if (net == 0) return this;
        _lines.Add(line with { Debit = net > 0 ? net : 0, Credit = net < 0 ? -net : 0 });
        return this;
    }

    public decimal TotalDebit => _lines.Sum(l => l.Debit);
    public decimal TotalCredit => _lines.Sum(l => l.Credit);
    public bool IsBalanced => TotalDebit == TotalCredit;
}

public static class Money
{
    public static decimal Round(decimal amount, int decimals = 2) => Math.Round(amount, decimals, MidpointRounding.AwayFromZero);
}

public static class JournalSources
{
    public const string Manual = "Manual";
    public const string Pos = "Pos";
    public const string SalesReturn = "SalesReturn";
    public const string Purchase = "Purchase";
    public const string PurchaseReturn = "PurchaseReturn";
    public const string SupplierPayment = "SupplierPayment";
    public const string CustomerPayment = "CustomerPayment";
    public const string Expense = "Expense";
    public const string Payroll = "Payroll";
    public const string PayrollPayment = "PayrollPayment";
    public const string SalaryAdvance = "SalaryAdvance";
    public const string Cash = "Cash";
    public const string Inventory = "Inventory";
}

/// <summary>The local posting engine. Every entry is validated (≥ 2 lines, debit = credit, open period, postable accounts)
/// and added to the caller's unit of work, so the document and its entry commit in the same transaction.</summary>
public static class Ledger
{
    public static string PeriodKey(DateOnly date) => date.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    public static async Task EnsurePeriodOpenAsync(LocalDb db, DateOnly date, CancellationToken ct)
    {
        var key = PeriodKey(date);
        if (await db.ClosedPeriods.AnyAsync(p => p.Period == key, ct)) throw new InvalidOperationException("erp.err.periodClosed");
    }

    /// <summary>Posts a balanced entry. Returns null when the builder holds no line at all (nothing to record, e.g. a zero-cost movement).</summary>
    public static async Task<LocalJournalEntry?> PostAsync(LocalDb db, DateOnly date, string description, string source, Guid? sourceId, PostingBuilder b,
        DateTimeOffset now, CancellationToken ct)
    {
        if (b.Lines.Count == 0) return null;
        if (b.Lines.Count < 2 || !b.IsBalanced) throw new InvalidOperationException("erp.err.unbalanced");
        await EnsurePeriodOpenAsync(db, date, ct);

        var accounts = await db.Accounts.AsNoTracking().ToListAsync(ct);
        var byCode = accounts.ToDictionary(a => a.Code);
        var entry = new LocalJournalEntry
        {
            Id = Guid.NewGuid(), Date = date, Description = Truncate(description, 300), Source = source, SourceId = sourceId, TotalDebit = b.TotalDebit, CreatedAt = now,
            Number = await NextNumberAsync(db, "JE", ct),
        };
        var n = 0;
        foreach (var l in b.Lines)
        {
            var code = l.AccountCode ?? accounts.FirstOrDefault(a => a.Role == l.Role)?.Code ?? throw new InvalidOperationException("erp.err.roleUnmapped");
            if (!byCode.TryGetValue(code, out var acc) || !acc.IsPostable || !acc.IsActive) throw new InvalidOperationException("erp.err.accountInvalid");
            db.JournalLines.Add(new LocalJournalLine
            {
                Id = Guid.NewGuid(), EntryId = entry.Id, LineNo = ++n, Date = date, AccountCode = code, Debit = l.Debit, Credit = l.Credit,
                Description = l.Description is null ? null : Truncate(l.Description, 200), PartyType = l.PartyType, PartyId = l.PartyId,
            });
        }
        db.JournalEntries.Add(entry);
        return entry;
    }

    /// <summary>Next document number <c>{prefix}-000001</c>, kept in the key/value table (saved with the caller's unit of work).</summary>
    public static async Task<string> NextNumberAsync(LocalDb db, string prefix, CancellationToken ct)
    {
        var key = "erp.seq." + prefix;
        var kv = await db.Values.FindAsync([key], ct);
        if (kv is null) db.Values.Add(kv = new KeyValue { Key = key, Value = "0" });
        var next = (long.TryParse(kv.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0) + 1;
        kv.Value = next.ToString(CultureInfo.InvariantCulture);
        // Branch PCs (VELTRIX Link) number with their own code so documents from several PCs never collide: JE-B1-000001.
        var node = await NodeCodeAsync(db, ct);
        return node is null ? $"{prefix}-{next.ToString("000000", CultureInfo.InvariantCulture)}" : $"{prefix}-{node}-{next.ToString("000000", CultureInfo.InvariantCulture)}";
    }

    /// <summary>Code of this PC when it is a VELTRIX Link branch PC (null on the company PC or a standalone PC).</summary>
    public static async Task<string?> NodeCodeAsync(LocalDb db, CancellationToken ct) =>
        (await db.Values.FindAsync([LocalDb.Keys.LinkNodeCode], ct))?.Value is { Length: > 0 } c ? c : null;

    /// <summary>Adds a cash-book line (cash account movements outside POS sales), linked to the open shift if any.</summary>
    public static async Task<LocalCashMovement> RecordCashAsync(LocalDb db, DateOnly date, DateTimeOffset now, decimal amount, string reason, string source,
        Guid? sourceId, Guid? entryId, string? counterAccount, CancellationToken ct)
    {
        var shift = (await db.Shifts.Where(s => s.Status == "Open").ToListAsync(ct)).OrderByDescending(s => s.OpenedAt).FirstOrDefault();
        var m = new LocalCashMovement
        {
            Id = Guid.NewGuid(), Number = await NextNumberAsync(db, "CM", ct), Date = date, CreatedAt = now, ShiftId = shift?.Id, Amount = amount,
            Reason = Truncate(reason, 200), Source = source, SourceId = sourceId, JournalEntryId = entryId, CounterAccountCode = counterAccount,
        };
        db.CashMovements.Add(m);
        return m;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>Moving-average costing (port of the server StockEngine rule) for the single local warehouse.</summary>
public static class Costing
{
    /// <summary>Current unit cost of a product: its moving average, or the purchase price before any receipt.</summary>
    public static decimal UnitCost(LocalProduct p) => p.AverageCost > 0 ? p.AverageCost : p.PurchasePrice;

    /// <summary>Inbound movement: only positive on-hand quantity carries cost weight; average kept at 4 decimals.</summary>
    public static decimal NewAverage(decimal onHand, decimal currentAverage, decimal quantityIn, decimal unitCost)
    {
        var weight = Math.Max(onHand, 0);
        var newQty = weight + quantityIn;
        return newQty > 0 ? Math.Round((weight * currentAverage + quantityIn * unitCost) / newQty, 4, MidpointRounding.AwayFromZero) : unitCost;
    }

    /// <summary>Receives stock into the local warehouse at <paramref name="unitCost"/>, updating the moving average.</summary>
    public static async Task ReceiveAsync(LocalDb db, Guid warehouseId, LocalProduct p, decimal quantity, decimal unitCost, DateTimeOffset now, CancellationToken ct)
    {
        var row = await db.Stock.FindAsync([warehouseId, p.Id], ct);
        if (p.TrackStock)
        {
            p.AverageCost = NewAverage(row?.Quantity ?? 0, UnitCost(p), quantity, unitCost);
            Standalone.StandaloneStore.ApplyStock(db, row, warehouseId, p.Id, quantity, now);
        }
        p.UpdatedAt = now;
    }

    /// <summary>Issues stock (outbound): valued at the moving average, which is unchanged. Rejects going below zero.</summary>
    public static async Task<decimal> IssueAsync(LocalDb db, Guid warehouseId, LocalProduct p, decimal quantity, DateTimeOffset now, CancellationToken ct)
    {
        var cost = UnitCost(p);
        if (!p.TrackStock) return cost;
        var row = await db.Stock.FindAsync([warehouseId, p.Id], ct);
        if ((row?.Quantity ?? 0) < quantity) throw new InvalidOperationException("erp.err.insufficientStock");
        Standalone.StandaloneStore.ApplyStock(db, row, warehouseId, p.Id, -quantity, now);
        return cost;
    }
}
