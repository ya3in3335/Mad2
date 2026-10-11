using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Standalone;

namespace Veltrix.Desktop.Core.Erp;

public sealed record SupplierInput(string Name, string? Phone = null, string? Email = null, string? Address = null, string? TaxId = null, string? Notes = null,
    bool IsActive = true);
public sealed record DocLineInput(Guid ProductId, decimal Quantity, decimal UnitCost, decimal TaxRatePercent = 0);
public sealed record PurchaseInput(Guid SupplierId, DateOnly Date, IReadOnlyList<DocLineInput> Lines, string? SupplierReference = null, string? Notes = null);
public sealed record PurchaseReturnInput(Guid SupplierId, DateOnly Date, IReadOnlyList<DocLineInput> Lines, Guid? OriginalPurchaseId = null, string? Reason = null);
public sealed record PurchaseRow(LocalPurchase Doc, string SupplierName);
public sealed record PurchaseDetail(LocalPurchase Doc, string SupplierName, IReadOnlyList<LocalPurchaseLine> Lines);
public sealed record PartyStatementRow(DateOnly Date, string Number, string Kind, decimal Increase, decimal Decrease, decimal Balance);
public sealed record ExpenseInput(DateOnly Date, Guid CategoryId, decimal Amount, decimal TaxAmount, string Method, string? Payee = null, string? Description = null);
public sealed record ExpenseRow(LocalExpense Expense, string CategoryName, string AccountCode);

/// <summary>
/// Local ERP (standalone mode): suppliers, purchases, payments, expenses, accounting, HR/payroll and the cash book.
/// Every multi-step write runs in one SQLite transaction together with its balanced journal entry.
/// </summary>
public sealed partial class ErpService(Func<LocalDb> dbFactory, TimeProvider time)
{
    public DateOnly Today => Ledger.Today(time);

    private async Task<T> InTransactionAsync<T>(Func<LocalDb, DateTimeOffset, Task<T>> work, CancellationToken ct)
    {
        await using var db = dbFactory();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var result = await work(db, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private Task<Guid> WarehouseIdAsync(CancellationToken ct) => new StandaloneStore(dbFactory, time).WarehouseIdAsync(ct);

    private static void Require(bool condition, string key)
    {
        if (!condition) throw new InvalidOperationException(key);
    }

    private static string? O(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static bool Matches(string? value, string search) => value is not null && value.Contains(search, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ suppliers

    public async Task<IReadOnlyList<LocalSupplier>> SuppliersAsync(string? search = null, bool activeOnly = false, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var list = await db.Suppliers.AsNoTracking().ToListAsync(ct);
        var s = search?.Trim() ?? "";
        return list.Where(x => (!activeOnly || x.IsActive) && (s.Length == 0 || Matches(x.Name, s) || Matches(x.Code, s) || Matches(x.Phone, s) || Matches(x.Email, s)))
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public Task<LocalSupplier> UpsertSupplierAsync(Guid? id, SupplierInput input, CancellationToken ct = default) => InTransactionAsync(async (db, now) =>
    {
        Require(!string.IsNullOrWhiteSpace(input.Name), "erp.err.nameRequired");
        Require(input.Email is null || string.IsNullOrWhiteSpace(input.Email) || input.Email.Contains('@'), "erp.err.email");
        var s = id is { } existing ? await db.Suppliers.FindAsync([existing], ct) ?? throw new InvalidOperationException("erp.err.unknownSupplier") : null;
        if (s is null)
            db.Suppliers.Add(s = new LocalSupplier { Id = Guid.NewGuid(), CreatedAt = now, Code = "F" + (await db.Suppliers.CountAsync(ct) + 1).ToString("00000", CultureInfo.InvariantCulture) });
        s.Name = input.Name.Trim();
        s.Phone = O(input.Phone);
        s.Email = O(input.Email);
        s.Address = O(input.Address);
        s.TaxId = O(input.TaxId);
        s.Notes = O(input.Notes);
        s.IsActive = input.IsActive;
        return s;
    }, ct);

    /// <summary>Supplier account: received invoices increase what we owe; returns and payments decrease it.</summary>
    public async Task<IReadOnlyList<PartyStatementRow>> SupplierStatementAsync(Guid supplierId, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var docs = await db.Purchases.AsNoTracking().Where(p => p.SupplierId == supplierId && p.Status == PurchaseStatuses.Received).ToListAsync(ct);
        var pays = await db.Payments.AsNoTracking().Where(p => p.PartyType == PartyKinds.Supplier && p.PartyId == supplierId).ToListAsync(ct);
        var rows = docs.Select(d => (d.Date, d.CreatedAt, d.Number, d.Kind, Inc: d.Kind == PurchaseKinds.Invoice ? d.Total : 0m, Dec: d.Kind == PurchaseKinds.Return ? d.Total : 0m))
            .Concat(pays.Select(p => (p.Date, p.CreatedAt, p.Number, Kind: "Payment", Inc: 0m, Dec: p.Amount)))
            .OrderBy(r => r.Date).ThenBy(r => r.CreatedAt).ToList();
        decimal bal = 0;
        return rows.Select(r => new PartyStatementRow(r.Date, r.Number, r.Kind, r.Inc, r.Dec, bal += r.Inc - r.Dec)).ToList();
    }

    // ------------------------------------------------------------------ purchases

    public async Task<IReadOnlyList<PurchaseRow>> PurchasesAsync(string? kind = null, Guid? supplierId = null, string? search = null, DateOnly? from = null,
        DateOnly? to = null, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var q = db.Purchases.AsNoTracking();
        if (kind is not null) q = q.Where(p => p.Kind == kind);
        if (supplierId is { } sid) q = q.Where(p => p.SupplierId == sid);
        if (from is { } f) q = q.Where(p => p.Date >= f);
        if (to is { } t) q = q.Where(p => p.Date <= t);
        var docs = await q.ToListAsync(ct);
        var names = (await db.Suppliers.AsNoTracking().ToListAsync(ct)).ToDictionary(s => s.Id, s => s.Name);
        var s = search?.Trim() ?? "";
        return docs.Select(d => new PurchaseRow(d, names.GetValueOrDefault(d.SupplierId, "?")))
            .Where(r => s.Length == 0 || Matches(r.Doc.Number, s) || Matches(r.SupplierName, s) || Matches(r.Doc.SupplierReference, s))
            .OrderByDescending(r => r.Doc.Date).ThenByDescending(r => r.Doc.CreatedAt).ToList();
    }

    public async Task<PurchaseDetail> PurchaseAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var doc = await db.Purchases.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw new InvalidOperationException("erp.err.unknownDocument");
        var supplier = await db.Suppliers.FindAsync([doc.SupplierId], ct);
        var lines = await db.PurchaseLines.AsNoTracking().Where(l => l.PurchaseId == id).OrderBy(l => l.LineNo).ToListAsync(ct);
        return new PurchaseDetail(doc, supplier?.Name ?? "?", lines);
    }

    private static void ValidateLines(IReadOnlyList<DocLineInput>? lines)
    {
        Require(lines is { Count: > 0 }, "erp.err.linesRequired");
        Require(lines!.All(l => l.Quantity > 0 && l.UnitCost >= 0 && l.TaxRatePercent is >= 0 and <= 100), "erp.err.lineInvalid");
    }

    private static async Task<(decimal Net, decimal Tax)> FillLinesAsync(LocalDb db, LocalPurchase doc, IReadOnlyList<DocLineInput> lines, CancellationToken ct)
    {
        decimal net = 0, tax = 0;
        var n = 0;
        foreach (var l in lines)
        {
            var p = await db.Products.FindAsync([l.ProductId], ct) ?? throw new InvalidOperationException("erp.err.unknownProduct");
            var lineNet = Money.Round(l.Quantity * l.UnitCost);
            var lineTax = Money.Round(lineNet * l.TaxRatePercent / 100m);
            db.PurchaseLines.Add(new LocalPurchaseLine
            {
                Id = Guid.NewGuid(), PurchaseId = doc.Id, LineNo = ++n, ProductId = p.Id, Sku = p.Sku, Name = p.Name, Quantity = l.Quantity, UnitCost = l.UnitCost,
                TaxRatePercent = l.TaxRatePercent, Net = lineNet, Tax = lineTax, Total = lineNet + lineTax,
            });
            net += lineNet;
            tax += lineTax;
        }
        doc.Subtotal = net;
        doc.Tax = tax;
        doc.Total = net + tax;
        return (net, tax);
    }

    /// <summary>Creates or edits a draft purchase invoice (no stock or accounting effect until it is received).</summary>
    public Task<LocalPurchase> SavePurchaseDraftAsync(Guid? id, PurchaseInput input, CancellationToken ct = default) => InTransactionAsync(async (db, now) =>
    {
        ValidateLines(input.Lines);
        Require(await db.Suppliers.AnyAsync(s => s.Id == input.SupplierId, ct), "erp.err.unknownSupplier");
        LocalPurchase doc;
        if (id is { } existing)
        {
            doc = await db.Purchases.FindAsync([existing], ct) ?? throw new InvalidOperationException("erp.err.unknownDocument");
            Require(doc.Status == PurchaseStatuses.Draft && doc.Kind == PurchaseKinds.Invoice, "erp.err.notDraft");
            db.PurchaseLines.RemoveRange(await db.PurchaseLines.Where(l => l.PurchaseId == doc.Id).ToListAsync(ct));
        }
        else
        {
            db.Purchases.Add(doc = new LocalPurchase { Id = Guid.NewGuid(), Kind = PurchaseKinds.Invoice, Status = PurchaseStatuses.Draft, CreatedAt = now,
                Number = await Ledger.NextNumberAsync(db, "PI", ct) });
        }
        doc.SupplierId = input.SupplierId;
        doc.Date = input.Date;
        doc.SupplierReference = O(input.SupplierReference);
        doc.Notes = O(input.Notes);
        await FillLinesAsync(db, doc, input.Lines, ct);
        return doc;
    }, ct);

    public Task<bool> DeletePurchaseDraftAsync(Guid id, CancellationToken ct = default) => InTransactionAsync(async (db, _) =>
    {
        var doc = await db.Purchases.FindAsync([id], ct) ?? throw new InvalidOperationException("erp.err.unknownDocument");
        Require(doc.Status == PurchaseStatuses.Draft, "erp.err.notDraft");
        db.PurchaseLines.RemoveRange(await db.PurchaseLines.Where(l => l.PurchaseId == id).ToListAsync(ct));
        db.Purchases.Remove(doc);
        return true;
    }, ct);

    /// <summary>Receives a draft invoice: stock in at the invoiced cost (moving average updated), supplier balance up, and
    /// Dr 30 stock (607 for non-stocked items) + Dr 4456 input VAT / Cr 401 supplier.</summary>
    public async Task<LocalPurchase> ReceivePurchaseAsync(Guid id, CancellationToken ct = default)
    {
        var wid = await WarehouseIdAsync(ct);
        return await InTransactionAsync(async (db, now) =>
        {
            var doc = await db.Purchases.FindAsync([id], ct) ?? throw new InvalidOperationException("erp.err.unknownDocument");
            Require(doc.Status == PurchaseStatuses.Draft && doc.Kind == PurchaseKinds.Invoice, "erp.err.notDraft");
            var supplier = await db.Suppliers.FindAsync([doc.SupplierId], ct) ?? throw new InvalidOperationException("erp.err.unknownSupplier");
            var lines = await db.PurchaseLines.Where(l => l.PurchaseId == id).OrderBy(l => l.LineNo).ToListAsync(ct);
            Require(lines.Count > 0, "erp.err.linesRequired");
            var b = new PostingBuilder();
            foreach (var l in lines)
            {
                var p = await db.Products.FindAsync([l.ProductId], ct) ?? throw new InvalidOperationException("erp.err.unknownProduct");
                await Costing.ReceiveAsync(db, wid, p, l.Quantity, l.UnitCost, now, ct);
                p.PurchasePrice = l.UnitCost;
                if (p.TrackStock) b.Debit(AccountRoles.Inventory, l.Net, l.Sku); else b.Debit(AccountRoles.NonStockPurchases, l.Net, l.Sku);
            }
            b.Debit(AccountRoles.VatDeductible, doc.Tax)
             .Credit(AccountRoles.AccountsPayable, doc.Total, $"{doc.Number} {doc.SupplierReference}".Trim(), PartyKinds.Supplier, supplier.Id);
            var je = await Ledger.PostAsync(db, doc.Date, $"Purchase {doc.Number} — {supplier.Name}", JournalSources.Purchase, doc.Id, b, now, ct);
            doc.JournalEntryId = je?.Id;
            doc.Status = PurchaseStatuses.Received;
            supplier.Balance += doc.Total;
            return doc;
        }, ct);
    }

    /// <summary>Returns goods to a supplier (port of the server rule): stock leaves at moving-average cost; Dr 401 / Cr 30 at cost,
    /// Cr 6582 price variance (invoiced − cost), Cr 4456 VAT. The supplier balance decreases.</summary>
    public async Task<LocalPurchase> CreatePurchaseReturnAsync(PurchaseReturnInput input, CancellationToken ct = default)
    {
        ValidateLines(input.Lines);
        var wid = await WarehouseIdAsync(ct);
        return await InTransactionAsync(async (db, now) =>
        {
            var supplier = await db.Suppliers.FindAsync([input.SupplierId], ct) ?? throw new InvalidOperationException("erp.err.unknownSupplier");
            if (input.OriginalPurchaseId is { } oid)
            {
                var original = await db.Purchases.FindAsync([oid], ct) ?? throw new InvalidOperationException("erp.err.unknownDocument");
                Require(original.SupplierId == supplier.Id && original.Kind == PurchaseKinds.Invoice && original.Status == PurchaseStatuses.Received, "erp.err.unknownDocument");
                var bought = (await db.PurchaseLines.AsNoTracking().Where(l => l.PurchaseId == oid).ToListAsync(ct))
                    .GroupBy(l => l.ProductId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
                var returnIds = await db.Purchases.Where(p => p.OriginalPurchaseId == oid && p.Kind == PurchaseKinds.Return).Select(p => p.Id).ToListAsync(ct);
                var returned = (await db.PurchaseLines.AsNoTracking().Where(l => returnIds.Contains(l.PurchaseId)).ToListAsync(ct))
                    .GroupBy(l => l.ProductId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
                foreach (var g in input.Lines.GroupBy(l => l.ProductId))
                    Require(g.Sum(l => l.Quantity) <= bought.GetValueOrDefault(g.Key) - returned.GetValueOrDefault(g.Key), "erp.err.returnExceeds");
            }
            var doc = new LocalPurchase
            {
                Id = Guid.NewGuid(), Kind = PurchaseKinds.Return, Status = PurchaseStatuses.Received, CreatedAt = now, SupplierId = supplier.Id, Date = input.Date,
                OriginalPurchaseId = input.OriginalPurchaseId, Notes = O(input.Reason), Number = await Ledger.NextNumberAsync(db, "PR", ct),
            };
            db.Purchases.Add(doc);
            await FillLinesAsync(db, doc, input.Lines, ct);
            var b = new PostingBuilder();
            foreach (var l in db.PurchaseLines.Local.Where(l => l.PurchaseId == doc.Id).OrderBy(l => l.LineNo))
            {
                var p = await db.Products.FindAsync([l.ProductId], ct) ?? throw new InvalidOperationException("erp.err.unknownProduct");
                if (p.TrackStock)
                {
                    l.StockUnitCost = await Costing.IssueAsync(db, wid, p, l.Quantity, now, ct);
                    var cost = Money.Round(l.Quantity * l.StockUnitCost);
                    b.Credit(AccountRoles.Inventory, cost, l.Sku).Credit(AccountRoles.PurchasePriceVariance, l.Net - cost, l.Sku);
                }
                else b.Credit(AccountRoles.NonStockPurchases, l.Net, l.Sku);
            }
            b.Credit(AccountRoles.VatDeductible, doc.Tax).Debit(AccountRoles.AccountsPayable, doc.Total, doc.Number, PartyKinds.Supplier, supplier.Id);
            var je = await Ledger.PostAsync(db, doc.Date, $"Purchase return {doc.Number} — {supplier.Name}", JournalSources.PurchaseReturn, doc.Id, b, now, ct);
            doc.JournalEntryId = je?.Id;
            supplier.Balance -= doc.Total;
            return doc;
        }, ct);
    }

    // ------------------------------------------------------------------ payments

    /// <summary>Pays a supplier: Dr 401 / Cr 530 cash or 512 bank. The supplier balance decreases.</summary>
    public Task<LocalPayment> PaySupplierAsync(Guid supplierId, decimal amount, string method, DateOnly date, string? notes = null, CancellationToken ct = default) =>
        InTransactionAsync(async (db, now) =>
        {
            Require(amount > 0, "erp.err.amountPositive");
            var supplier = await db.Suppliers.FindAsync([supplierId], ct) ?? throw new InvalidOperationException("erp.err.unknownSupplier");
            var pay = NewPayment(PartyKinds.Supplier, supplierId, amount, method, date, notes, now, await Ledger.NextNumberAsync(db, "PAY", ct));
            var b = new PostingBuilder().Debit(AccountRoles.AccountsPayable, amount, pay.Number, PartyKinds.Supplier, supplierId).Credit(AccountRoles.ForMethod(method), amount, supplier.Name);
            pay.JournalEntryId = (await Ledger.PostAsync(db, date, $"Supplier payment {pay.Number} — {supplier.Name}", JournalSources.SupplierPayment, pay.Id, b, now, ct))?.Id;
            if (method == PayMethods.Cash)
                await Ledger.RecordCashAsync(db, date, now, -amount, $"{pay.Number} {supplier.Name}", "Payment", pay.Id, pay.JournalEntryId, "401", ct);
            supplier.Balance -= amount;
            db.Payments.Add(pay);
            return pay;
        }, ct);

    /// <summary>Receives a customer payment: Dr 530 cash or 512 bank / Cr 411. The customer balance decreases.</summary>
    public Task<LocalPayment> ReceiveCustomerPaymentAsync(Guid customerId, decimal amount, string method, DateOnly date, string? notes = null, CancellationToken ct = default) =>
        InTransactionAsync(async (db, now) =>
        {
            Require(amount > 0, "erp.err.amountPositive");
            var customer = await db.Customers.FindAsync([customerId], ct) ?? throw new InvalidOperationException("erp.err.unknownCustomer");
            var pay = NewPayment(PartyKinds.Customer, customerId, amount, method, date, notes, now, await Ledger.NextNumberAsync(db, "RCV", ct));
            var b = new PostingBuilder().Debit(AccountRoles.ForMethod(method), amount, customer.Name).Credit(AccountRoles.AccountsReceivable, amount, pay.Number, PartyKinds.Customer, customerId);
            pay.JournalEntryId = (await Ledger.PostAsync(db, date, $"Customer payment {pay.Number} — {customer.Name}", JournalSources.CustomerPayment, pay.Id, b, now, ct))?.Id;
            if (method == PayMethods.Cash)
                await Ledger.RecordCashAsync(db, date, now, amount, $"{pay.Number} {customer.Name}", "Payment", pay.Id, pay.JournalEntryId, "411", ct);
            customer.Balance -= amount;
            db.Payments.Add(pay);
            return pay;
        }, ct);

    private static LocalPayment NewPayment(string partyType, Guid partyId, decimal amount, string method, DateOnly date, string? notes, DateTimeOffset now, string number)
    {
        Require(PayMethods.All.Contains(method), "erp.err.method");
        return new LocalPayment { Id = Guid.NewGuid(), Number = number, PartyType = partyType, PartyId = partyId, Amount = amount, Method = method, Date = date, Notes = O(notes), CreatedAt = now };
    }

    public async Task<IReadOnlyList<LocalPayment>> PaymentsAsync(string partyType, Guid? partyId = null, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var q = db.Payments.AsNoTracking().Where(p => p.PartyType == partyType);
        if (partyId is { } id) q = q.Where(p => p.PartyId == id);
        return (await q.ToListAsync(ct)).OrderByDescending(p => p.Date).ThenByDescending(p => p.CreatedAt).ToList();
    }

    // ------------------------------------------------------------------ expenses

    public async Task<IReadOnlyList<LocalExpenseCategory>> ExpenseCategoriesAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory();
        return (await db.ExpenseCategories.AsNoTracking().ToListAsync(ct)).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public Task<LocalExpenseCategory> UpsertExpenseCategoryAsync(Guid? id, string name, string accountCode, bool isActive = true, CancellationToken ct = default) =>
        InTransactionAsync(async (db, _) =>
        {
            Require(!string.IsNullOrWhiteSpace(name), "erp.err.nameRequired");
            var acc = await db.Accounts.FindAsync([accountCode.Trim()], ct);
            Require(acc is { Type: AccountTypes.Expense, IsPostable: true, IsActive: true }, "erp.err.accountInvalid");
            var c = id is { } existing ? await db.ExpenseCategories.FindAsync([existing], ct) ?? throw new InvalidOperationException("erp.err.unknownCategory") : null;
            if (c is null) db.ExpenseCategories.Add(c = new LocalExpenseCategory { Id = Guid.NewGuid() });
            c.Name = name.Trim();
            c.AccountCode = acc!.Code;
            c.IsActive = isActive;
            return c;
        }, ct);

    public async Task<IReadOnlyList<ExpenseRow>> ExpensesAsync(DateOnly? from = null, DateOnly? to = null, Guid? categoryId = null, string? search = null, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var q = db.Expenses.AsNoTracking();
        if (from is { } f) q = q.Where(e => e.Date >= f);
        if (to is { } t) q = q.Where(e => e.Date <= t);
        if (categoryId is { } c) q = q.Where(e => e.CategoryId == c);
        var cats = (await db.ExpenseCategories.AsNoTracking().ToListAsync(ct)).ToDictionary(x => x.Id);
        var s = search?.Trim() ?? "";
        return (await q.ToListAsync(ct))
            .Select(e => new ExpenseRow(e, cats.TryGetValue(e.CategoryId, out var cat) ? cat.Name : "?", cat?.AccountCode ?? ""))
            .Where(r => s.Length == 0 || Matches(r.Expense.Number, s) || Matches(r.Expense.Payee, s) || Matches(r.Expense.Description, s) || Matches(r.CategoryName, s))
            .OrderByDescending(r => r.Expense.Date).ThenByDescending(r => r.Expense.CreatedAt).ToList();
    }

    /// <summary>Records a paid expense (port of the server rule): Dr category account (net) + Dr 4456 VAT / Cr 530 or 512.</summary>
    public Task<LocalExpense> CreateExpenseAsync(ExpenseInput input, CancellationToken ct = default) => InTransactionAsync(async (db, now) =>
    {
        Require(input.Amount > 0, "erp.err.amountPositive");
        Require(input.TaxAmount >= 0 && input.TaxAmount < input.Amount, "erp.err.taxInvalid");
        Require(PayMethods.All.Contains(input.Method), "erp.err.method");
        var cat = await db.ExpenseCategories.FindAsync([input.CategoryId], ct) ?? throw new InvalidOperationException("erp.err.unknownCategory");
        var e = new LocalExpense
        {
            Id = Guid.NewGuid(), Number = await Ledger.NextNumberAsync(db, "EXP", ct), Date = input.Date, CategoryId = cat.Id, Amount = Money.Round(input.Amount),
            TaxAmount = Money.Round(input.TaxAmount), Method = input.Method, Payee = O(input.Payee), Description = O(input.Description), CreatedAt = now,
        };
        var b = new PostingBuilder().DebitAccount(cat.AccountCode, e.Amount - e.TaxAmount, e.Description ?? cat.Name).Debit(AccountRoles.VatDeductible, e.TaxAmount)
            .Credit(AccountRoles.ForMethod(e.Method), e.Amount, e.Payee);
        e.JournalEntryId = (await Ledger.PostAsync(db, e.Date, $"Expense {e.Number} {cat.Name} {e.Payee}".Trim(), JournalSources.Expense, e.Id, b, now, ct))?.Id;
        if (e.Method == PayMethods.Cash)
            await Ledger.RecordCashAsync(db, e.Date, now, -e.Amount, $"{e.Number} {cat.Name}", "Expense", e.Id, e.JournalEntryId, cat.AccountCode, ct);
        db.Expenses.Add(e);
        return e;
    }, ct);
}
