using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Veltrix.Contracts.Catalog;
using Veltrix.Contracts.Crm;
using Veltrix.Desktop.Core.Api;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Erp;
using Veltrix.Desktop.Core.Pos;
using Veltrix.Desktop.Core.Standalone;

namespace Veltrix.Desktop.Tests;

/// <summary>Local ERP (standalone mode): balanced postings for every document, moving-average cost, returns, period lock, payroll, reports, v2→v3 migration.</summary>
public sealed class ErpTests : IAsyncLifetime
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero));
    private static readonly DateOnly D = new(2026, 10, 10);

    private LocalDb Db() => new(new DbContextOptionsBuilder<LocalDb>().UseSqlite(_conn).Options);
    private ErpService Erp() => new(Db, _time);
    private StandaloneStore Store() => new(Db, _time);

    public async Task InitializeAsync()
    {
        await _conn.OpenAsync();
        await using var db = Db();
        await db.InitializeAsync();
    }

    public async Task DisposeAsync() => await _conn.DisposeAsync();

    private async Task<ProductDto> ProductAsync(string sku, decimal price, decimal cost, bool track = true, int tax = 2) =>
        await Store().UpsertProductAsync(new UpsertProductRequest(sku, sku + " name", null, null, "Standard", "Active", null, null, null,
            StandaloneStore.TaxCategories[tax].Id, price, null, cost, track, false, false, false, 0, 0, null, null, [], null), null);

    /// <summary>Every journal entry balances and the whole ledger sums to zero.</summary>
    private async Task AssertLedgerBalancedAsync()
    {
        await using var db = Db();
        var lines = await db.JournalLines.AsNoTracking().ToListAsync();
        foreach (var g in lines.GroupBy(l => l.EntryId))
            Assert.Equal(g.Sum(l => l.Debit), g.Sum(l => l.Credit));
        Assert.Equal(0m, lines.Sum(l => l.Debit - l.Credit));
    }

    private async Task<decimal> BalanceAsync(string code)
    {
        await using var db = Db();
        return (await db.JournalLines.AsNoTracking().Where(l => l.AccountCode == code).ToListAsync()).Sum(l => l.Debit - l.Credit);
    }

    private async Task<List<LocalJournalLine>> EntryLinesAsync(string source)
    {
        await using var db = Db();
        var ids = await db.JournalEntries.Where(e => e.Source == source).Select(e => e.Id).ToListAsync();
        return await db.JournalLines.AsNoTracking().Where(l => ids.Contains(l.EntryId)).ToListAsync();
    }

    private async Task<decimal> StockAsync(Guid productId)
    {
        await using var db = Db();
        return (await db.Stock.AsNoTracking().SingleOrDefaultAsync(s => s.ProductId == productId))?.Quantity ?? 0;
    }

    private async Task<LocalPurchase> BuyAsync(Guid supplierId, Guid productId, decimal qty, decimal cost, decimal vat = 19)
    {
        var erp = Erp();
        var draft = await erp.SavePurchaseDraftAsync(null, new PurchaseInput(supplierId, D, [new DocLineInput(productId, qty, cost, vat)], "FAC-1"));
        return await erp.ReceivePurchaseAsync(draft.Id);
    }

    // ------------------------------------------------------------------ chart & engine

    [Fact]
    public async Task Chart_is_seeded_with_scf_classes_and_key_accounts()
    {
        var accounts = await Erp().AccountsAsync();
        foreach (var code in new[] { "101", "30", "31", "37", "401", "411", "421", "425", "4456", "4457", "512", "53", "530", "600", "607", "613", "628", "63", "631", "700", "709" })
            Assert.Contains(accounts, a => a.Code == code);
        Assert.Equal(["1", "2", "3", "4", "5", "6", "7"], accounts.Select(a => a.Code[..1]).Distinct().Order().ToList());
        Assert.NotEmpty(await Erp().ExpenseCategoriesAsync());
    }

    [Fact]
    public async Task Posting_rejects_unbalanced_and_single_line_entries()
    {
        await using var db = Db();
        var unbalanced = new PostingBuilder().Debit(AccountRoles.Cash, 100).Credit(AccountRoles.SalesRevenue, 90);
        Assert.Equal("erp.err.unbalanced", (await Assert.ThrowsAsync<InvalidOperationException>(() => Ledger.PostAsync(db, D, "x", "Manual", null, unbalanced, _time.GetUtcNow(), default))).Message);
        var single = new PostingBuilder().Debit(AccountRoles.Cash, 100);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Ledger.PostAsync(db, D, "x", "Manual", null, single, _time.GetUtcNow(), default));
        var nonPostable = new PostingBuilder().DebitAccount("53", 10).Credit(AccountRoles.SalesRevenue, 10);
        Assert.Equal("erp.err.accountInvalid", (await Assert.ThrowsAsync<InvalidOperationException>(() => Ledger.PostAsync(db, D, "x", "Manual", null, nonPostable, _time.GetUtcNow(), default))).Message);
    }

    [Fact]
    public async Task Manual_entry_must_balance_and_lines_take_one_side()
    {
        var erp = Erp();
        await Assert.ThrowsAsync<InvalidOperationException>(() => erp.CreateManualEntryAsync(new ManualEntryInput(D, "Capital", [new("512", 1000, 0), new("101", 0, 900)])));
        await Assert.ThrowsAsync<InvalidOperationException>(() => erp.CreateManualEntryAsync(new ManualEntryInput(D, "Capital", [new("512", 1000, 1000), new("101", 0, 0)])));
        var e = await erp.CreateManualEntryAsync(new ManualEntryInput(D, "Capital", [new("512", 1000, 0), new("101", 0, 1000)]));
        Assert.Equal(1000m, e.TotalDebit);
        Assert.Equal(1000m, await BalanceAsync("512"));
        Assert.Single(await erp.JournalAsync(D, D));
        await AssertLedgerBalancedAsync();
    }

    // ------------------------------------------------------------------ purchases

    [Fact]
    public async Task Moving_average_cost_formula_matches_the_server_rule()
    {
        Assert.Equal(12m, Costing.NewAverage(10, 10, 10, 14));
        Assert.Equal(14m, Costing.NewAverage(-5, 10, 10, 14)); // negative on-hand carries no weight
        Assert.Equal(10.6667m, Costing.NewAverage(20, 10, 10, 12));
    }

    [Fact]
    public async Task Purchase_receipt_increases_stock_updates_average_cost_and_posts_balanced_entry()
    {
        var erp = Erp();
        var p = await ProductAsync("RICE", 200, 100);
        var supplier = await erp.UpsertSupplierAsync(null, new SupplierInput("Grossiste Alger", "0550"));
        var draft = await erp.SavePurchaseDraftAsync(null, new PurchaseInput(supplier.Id, D, [new DocLineInput(p.Id, 10, 100, 19)]));
        Assert.Equal(0m, await StockAsync(p.Id)); // a draft has no effect
        Assert.Empty(await EntryLinesAsync(JournalSources.Purchase));

        await erp.ReceivePurchaseAsync(draft.Id);
        Assert.Equal(10m, await StockAsync(p.Id));
        await BuyAsync(supplier.Id, p.Id, 10, 140);
        Assert.Equal(20m, await StockAsync(p.Id));
        await using (var db = Db()) Assert.Equal(120m, (await db.Products.FindAsync(p.Id))!.AverageCost);

        // Dr 30 1000 + 1400, Dr 4456 190 + 266, Cr 401 1190 + 1666
        Assert.Equal(2400m, await BalanceAsync("30"));
        Assert.Equal(456m, await BalanceAsync("4456"));
        Assert.Equal(-2856m, await BalanceAsync("401"));
        Assert.Equal(2856m, (await erp.SuppliersAsync()).Single().Balance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => erp.ReceivePurchaseAsync(draft.Id)); // received once only
        await AssertLedgerBalancedAsync();
    }

    [Fact]
    public async Task Purchase_return_reverses_stock_at_average_cost_with_price_variance()
    {
        var erp = Erp();
        var p = await ProductAsync("OIL", 300, 0);
        var s = await erp.UpsertSupplierAsync(null, new SupplierInput("Huilerie"));
        var inv1 = await BuyAsync(s.Id, p.Id, 10, 100);
        await BuyAsync(s.Id, p.Id, 10, 140); // average 120

        var ret = await erp.CreatePurchaseReturnAsync(new PurchaseReturnInput(s.Id, D, [new DocLineInput(p.Id, 5, 100, 19)], inv1.Id, "Damaged"));
        Assert.Equal(595m, ret.Total);
        Assert.Equal(15m, await StockAsync(p.Id));
        var lines = await EntryLinesAsync(JournalSources.PurchaseReturn);
        Assert.Equal(595m, lines.Single(l => l.AccountCode == "401").Debit);
        Assert.Equal(600m, lines.Single(l => l.AccountCode == "30").Credit); // 5 × average 120
        Assert.Equal(100m, lines.Single(l => l.AccountCode == "6582").Debit); // invoiced 500 − cost 600
        Assert.Equal(95m, lines.Single(l => l.AccountCode == "4456").Credit);
        Assert.Equal(1190m + 1666m - 595m, (await erp.SuppliersAsync()).Single().Balance);
        await using (var db = Db()) Assert.Equal(120m, (await db.Products.FindAsync(p.Id))!.AverageCost); // outbound keeps the average

        await Assert.ThrowsAsync<InvalidOperationException>(() => erp.CreatePurchaseReturnAsync(new PurchaseReturnInput(s.Id, D, [new DocLineInput(p.Id, 6, 100)], inv1.Id)));
        await AssertLedgerBalancedAsync();
    }

    [Fact]
    public async Task Supplier_payment_reduces_balance_and_posts_401_against_cash_or_bank()
    {
        var erp = Erp();
        var p = await ProductAsync("SUG", 100, 50);
        var s = await erp.UpsertSupplierAsync(null, new SupplierInput("Sucrerie"));
        await BuyAsync(s.Id, p.Id, 10, 50, 0);
        await erp.PaySupplierAsync(s.Id, 300, PayMethods.Cash, D);
        await erp.PaySupplierAsync(s.Id, 200, PayMethods.Bank, D);
        Assert.Equal(0m, (await erp.SuppliersAsync()).Single().Balance);
        Assert.Equal(-300m, await BalanceAsync("530"));
        Assert.Equal(-200m, await BalanceAsync("512"));
        Assert.Equal(0m, await BalanceAsync("401"));
        Assert.Equal(-300m, (await erp.CashMovementsAsync(D, D)).Single().Amount);
        var statement = await erp.SupplierStatementAsync(s.Id);
        Assert.Equal(0m, statement[^1].Balance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => erp.PaySupplierAsync(s.Id, 0, PayMethods.Cash, D));
        await AssertLedgerBalancedAsync();
    }

    // ------------------------------------------------------------------ sales

    private async Task<(PosService Pos, ShiftInfo Shift, Contracts.Pos.RegisterDto Reg)> OpenShiftAsync(decimal openingFloat = 0)
    {
        var pos = new PosService(new ApiClient(new HttpClient(new FailingHandler()), new NoTokens(), new NoServer()), Db, _time, () => true);
        var reg = (await pos.RegistersAsync(default)).Registers[0];
        return (pos, await pos.OpenShiftAsync(reg.Id, openingFloat, default), reg);
    }

    [Fact]
    public async Task Pos_sale_posts_revenue_vat_and_cost_of_goods_at_moving_average()
    {
        var p = await ProductAsync("COF", 119, 60);
        var s = await Erp().UpsertSupplierAsync(null, new SupplierInput("Torréfacteur"));
        await BuyAsync(s.Id, p.Id, 10, 60, 0);
        var (pos, shift, reg) = await OpenShiftAsync();
        var cart = new Cart();
        cart.Add(SellableProduct.From(p), 2);
        await pos.CheckoutAsync(cart, PosConfig.Default, reg.Id, reg.Code, shift, null, null, [new PaymentLine("Cash", 300m)], "Shop", "A", "fr", default);

        var lines = await EntryLinesAsync(JournalSources.Pos);
        Assert.Equal(238m, lines.Single(l => l.AccountCode == "530").Debit);
        Assert.Equal(200m, lines.Single(l => l.AccountCode == "700").Credit);
        Assert.Equal(38m, lines.Single(l => l.AccountCode == "4457").Credit);
        Assert.Equal(120m, lines.Single(l => l.AccountCode == "600").Debit);
        Assert.Equal(120m, lines.Single(l => l.AccountCode == "30").Credit);
        Assert.Equal(8m, await StockAsync(p.Id));
        await AssertLedgerBalancedAsync();
    }

    [Fact]
    public async Task Credit_sale_and_customer_payment_track_the_receivable()
    {
        var p = await ProductAsync("TEA", 100, 40, tax: 0);
        await Store().AdjustStockAsync(p.Id, 5);
        var customer = await Store().UpsertCustomerAsync(new UpsertCustomerRequest("Individual", "Client A", null, null, null, null, null, null, null, null, null, 0, false, null), null);
        var (pos, shift, reg) = await OpenShiftAsync();
        var cart = new Cart();
        cart.Add(SellableProduct.From(p), 3);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pos.CheckoutAsync(cart, PosConfig.Default, reg.Id, reg.Code, shift, null, null, [new PaymentLine("OnAccount", 300m)], "Shop", "A", "fr", default));
        await pos.CheckoutAsync(cart, PosConfig.Default, reg.Id, reg.Code, shift, customer.Id, customer.Name, [new PaymentLine("OnAccount", 300m)], "Shop", "A", "fr", default);
        Assert.Equal(300m, await BalanceAsync("411"));
        await Erp().ReceiveCustomerPaymentAsync(customer.Id, 120, PayMethods.Cash, D);
        Assert.Equal(180m, await BalanceAsync("411"));
        await using (var db = Db()) Assert.Equal(180m, (await db.Customers.FindAsync(customer.Id))!.Balance);
        Assert.Equal(120m, (await Erp().CashRegisterAsync()).CashIn);
        await AssertLedgerBalancedAsync();
    }

    [Fact]
    public async Task Sales_return_reverses_revenue_vat_cost_and_restocks()
    {
        var p = await ProductAsync("CAK", 119, 50);
        await Store().AdjustStockAsync(p.Id, 4);
        var (pos, shift, reg) = await OpenShiftAsync(100);
        var cart = new Cart();
        cart.Add(SellableProduct.From(p), 2);
        var sale = await pos.CheckoutAsync(cart, PosConfig.Default, reg.Id, reg.Code, shift, null, null, [new PaymentLine("Cash", 238m)], "Shop", "A", "fr", default);

        var erp = Erp();
        var found = await erp.FindSaleForReturnAsync(sale.Number);
        var line = found.Lines.Single();
        await Assert.ThrowsAsync<InvalidOperationException>(() => erp.CreateSaleReturnAsync(new SaleReturnInput(found.Sale.Id, [new(line.Line.Id, 3)], PayMethods.Cash, D)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => erp.CreateSaleReturnAsync(new SaleReturnInput(found.Sale.Id, [new(line.Line.Id, 1)], RefundMethods.OnAccount, D)));
        var ret = await erp.CreateSaleReturnAsync(new SaleReturnInput(found.Sale.Id, [new(line.Line.Id, 1)], PayMethods.Cash, D, "Broken"));
        Assert.Equal(119m, ret.Total);
        Assert.Equal(3m, await StockAsync(p.Id));

        var lines = await EntryLinesAsync(JournalSources.SalesReturn);
        Assert.Equal(100m, lines.Single(l => l.AccountCode == "709").Debit);
        Assert.Equal(19m, lines.Single(l => l.AccountCode == "4457").Debit);
        Assert.Equal(119m, lines.Single(l => l.AccountCode == "530").Credit);
        Assert.Equal(50m, lines.Single(l => l.AccountCode == "30").Debit);
        Assert.Equal(50m, lines.Single(l => l.AccountCode == "600").Credit);
        Assert.Equal(1m, (await erp.FindSaleForReturnAsync(sale.Number)).Lines.Single().Returnable);

        // The drawer expects the float + the sale − the cash refund; closing posts the variance to 6583.
        Assert.Equal(100m + 238m - 119m, (await erp.CashRegisterAsync()).Expected);
        var closed = await pos.CloseShiftAsync(shift.Id, 210m, null, default);
        Assert.Equal(-9m, closed!.Variance);
        Assert.Equal(9m, await BalanceAsync("6583"));
        await AssertLedgerBalancedAsync();
    }

    // ------------------------------------------------------------------ expenses, payroll, cash

    [Fact]
    public async Task Expense_posts_category_account_vat_and_payment()
    {
        var erp = Erp();
        var rent = (await erp.ExpenseCategoriesAsync()).Single(c => c.Key == "rent");
        var electricity = (await erp.ExpenseCategoriesAsync()).Single(c => c.Key == "electricity");
        await erp.CreateExpenseAsync(new ExpenseInput(D, rent.Id, 50000, 0, PayMethods.Bank, "Bailleur"));
        await erp.CreateExpenseAsync(new ExpenseInput(D, electricity.Id, 1190, 190, PayMethods.Cash, "Sonelgaz"));
        Assert.Equal(50000m, await BalanceAsync("613"));
        Assert.Equal(1000m, await BalanceAsync("607"));
        Assert.Equal(190m, await BalanceAsync("4456"));
        Assert.Equal(-50000m, await BalanceAsync("512"));
        Assert.Equal(-1190m, await BalanceAsync("530"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => erp.CreateExpenseAsync(new ExpenseInput(D, rent.Id, 100, 100, PayMethods.Cash)));
        Assert.Equal(2, (await erp.ExpensesAsync(D, D)).Count);
        await AssertLedgerBalancedAsync();
    }

    [Fact]
    public async Task Payroll_deducts_advances_and_posts_63_421_then_payment()
    {
        var erp = Erp();
        var a = await erp.UpsertEmployeeAsync(null, new EmployeeInput("Amina", "Caissière", null, 40000, new DateOnly(2025, 1, 1)));
        await erp.UpsertEmployeeAsync(null, new EmployeeInput("Karim", "Magasinier", null, 35000, new DateOnly(2026, 11, 1))); // hired after the month
        await erp.GiveAdvanceAsync(a.Id, 5000, PayMethods.Cash, D);
        Assert.Equal(5000m, await BalanceAsync("425"));

        var run = await erp.RunPayrollAsync(2026, 10);
        Assert.Equal((40000m, 5000m, 35000m), (run.TotalGross, run.TotalAdvances, run.TotalNet));
        Assert.Equal(40000m, await BalanceAsync("631"));
        Assert.Equal(-35000m, await BalanceAsync("421"));
        Assert.Equal(0m, await BalanceAsync("425"));
        Assert.Empty(await erp.AdvancesAsync(outstandingOnly: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => erp.RunPayrollAsync(2026, 10));

        await erp.PayPayrollAsync(run.Id, PayMethods.Bank, D);
        Assert.Equal(0m, await BalanceAsync("421"));
        Assert.Equal(-35000m, await BalanceAsync("512"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => erp.PayPayrollAsync(run.Id, PayMethods.Bank, D));
        await AssertLedgerBalancedAsync();
    }

    [Fact]
    public async Task Manual_cash_movement_posts_against_counter_account()
    {
        var erp = Erp();
        await erp.RecordCashMovementAsync(5000, "Apport", "101", D);
        await erp.RecordCashMovementAsync(-2000, "Dépôt banque", "512", D);
        Assert.Equal(3000m, await BalanceAsync("530"));
        Assert.Equal(2000m, await BalanceAsync("512"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => erp.RecordCashMovementAsync(10, "x", "530", D));
        await AssertLedgerBalancedAsync();
    }

    // ------------------------------------------------------------------ period lock & reports

    [Fact]
    public async Task Closed_period_rejects_postings_and_rolls_back_the_document()
    {
        var erp = Erp();
        var rent = (await erp.ExpenseCategoriesAsync()).Single(c => c.Key == "rent");
        await erp.ClosePeriodAsync(2026, 9);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => erp.CreateExpenseAsync(new ExpenseInput(new DateOnly(2026, 9, 30), rent.Id, 100, 0, PayMethods.Cash)));
        Assert.Equal("erp.err.periodClosed", e.Message);
        Assert.Empty(await erp.ExpensesAsync()); // the document was not saved either
        Assert.Empty(await erp.CashMovementsAsync(new DateOnly(2026, 1, 1), D));
        await erp.CreateExpenseAsync(new ExpenseInput(D, rent.Id, 100, 0, PayMethods.Cash)); // October still open
        await erp.ReopenPeriodAsync(2026, 9);
        await erp.CreateExpenseAsync(new ExpenseInput(new DateOnly(2026, 9, 30), rent.Id, 100, 0, PayMethods.Cash));
        Assert.Equal(2, (await erp.ExpensesAsync()).Count);
    }

    [Fact]
    public async Task Trial_balance_income_statement_and_balance_sheet_are_consistent()
    {
        var erp = Erp();
        await erp.CreateManualEntryAsync(new ManualEntryInput(D, "Capital", [new("512", 100000, 0), new("101", 0, 100000)]));
        var p = await ProductAsync("BIS", 119, 0);
        var s = await erp.UpsertSupplierAsync(null, new SupplierInput("Biscuiterie"));
        await BuyAsync(s.Id, p.Id, 100, 50);
        var (pos, shift, reg) = await OpenShiftAsync();
        var cart = new Cart();
        cart.Add(SellableProduct.From(p), 10);
        await pos.CheckoutAsync(cart, PosConfig.Default, reg.Id, reg.Code, shift, null, null, [new PaymentLine("Cash", 1190m)], "Shop", "A", "fr", default);
        var rent = (await erp.ExpenseCategoriesAsync()).Single(c => c.Key == "rent");
        await erp.CreateExpenseAsync(new ExpenseInput(D, rent.Id, 200, 0, PayMethods.Cash));

        var tb = await erp.TrialBalanceAsync(new DateOnly(2026, 1, 1), D);
        Assert.True(tb.IsBalanced);
        Assert.Equal(0m, tb.Rows.Sum(r => r.Debit - r.Credit));

        var pl = await erp.IncomeStatementAsync(new DateOnly(2026, 1, 1), D);
        Assert.Equal(1000m, pl.TotalRevenue);
        Assert.Equal(500m, pl.CostOfGoodsSold);
        Assert.Equal(500m, pl.GrossMargin);
        Assert.Equal(300m, pl.NetResult); // 1000 − 500 − 200

        var bs = await erp.BalanceSheetAsync(D);
        Assert.Equal(300m, bs.CurrentResult);
        Assert.True(bs.IsBalanced);

        var ledger = await erp.LedgerAsync("530", new DateOnly(2026, 1, 1), D);
        Assert.Equal(990m, ledger.Closing);
        Assert.Equal(ledger.Opening + ledger.TotalDebit - ledger.TotalCredit, ledger.Closing);
    }

    [Fact]
    public async Task Stock_adjustment_posts_inventory_against_adjustments()
    {
        var p = await ProductAsync("ADJ", 10, 4);
        await Store().AdjustStockAsync(p.Id, 5);
        await Store().AdjustStockAsync(p.Id, -1);
        Assert.Equal(16m, await BalanceAsync("30"));
        Assert.Equal(-16m, await BalanceAsync("6581"));
        await AssertLedgerBalancedAsync();
    }

    // ------------------------------------------------------------------ migration & backup

    [Fact]
    public async Task Schema_v2_database_is_upgraded_to_v3_keeping_its_data()
    {
        var productId = Guid.NewGuid();
        await using (var db = Db())
        {
            db.Products.Add(new LocalProduct { Id = productId, Sku = "V2", Name = "Old product", SalePrice = 50, PurchasePrice = 30, TrackStock = true });
            db.Customers.Add(new LocalCustomer { Id = Guid.NewGuid(), Code = "C00001", Name = "Old customer", Balance = 75 });
            db.Sales.Add(new LocalSale { Id = Guid.NewGuid(), ClientNumber = "POS1-1", OpId = "op1", Total = 50, CreatedAt = _time.GetUtcNow() });
            await db.SaveChangesAsync();
            // Reshape to v2: no ERP tables, no AverageCost column.
            foreach (var t in LocalDb.V3Tables) await db.Database.ExecuteSqlRawAsync($"DROP TABLE \"{t}\";");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Products\" DROP COLUMN \"AverageCost\";");
            await db.SetValueAsync(LocalDb.Keys.SchemaVersion, "2");
        }
        await using (var db = Db()) await db.InitializeAsync();
        await using (var db = Db())
        {
            var p = await db.Products.SingleAsync();
            Assert.Equal(("Old product", 30m), (p.Name, p.AverageCost)); // average starts at the known cost
            Assert.Equal(75m, (await db.Customers.SingleAsync()).Balance);
            Assert.Equal(1, await db.Sales.CountAsync());
            Assert.True(await db.Accounts.CountAsync() > 40);
            Assert.Equal("3", await db.GetValueAsync(LocalDb.Keys.SchemaVersion));
        }
        // The upgraded database is fully usable.
        var s = await Erp().UpsertSupplierAsync(null, new SupplierInput("New supplier"));
        await BuyAsync(s.Id, productId, 2, 30);
        await AssertLedgerBalancedAsync();
    }

    [Fact]
    public async Task Backup_round_trip_keeps_erp_data()
    {
        var erp = Erp();
        await erp.UpsertSupplierAsync(null, new SupplierInput("Kept supplier"));
        await erp.CreateManualEntryAsync(new ManualEntryInput(D, "Capital", [new("512", 10, 0), new("101", 0, 10)]));
        BackupPayload payload;
        await using (var db = Db()) payload = await LocalBackup.CaptureAsync(db, "[]", null, "Shop", _time.GetUtcNow());
        var bytes = LocalBackup.Encrypt(payload, "pw-123456", PasswordHasher.MinimumIterations);
        var restored = LocalBackup.Decrypt(bytes, "pw-123456");
        await using (var db = Db()) await LocalBackup.RestoreAsync(db, restored);
        Assert.Equal("Kept supplier", (await erp.SuppliersAsync()).Single().Name);
        Assert.Equal(10m, await BalanceAsync("512"));
        Assert.NotEmpty(await erp.AccountsAsync());
    }

    private sealed class NoServer : IServerAddress { public string? ServerUrl => null; }

    private sealed class NoTokens : ITokenStore
    {
        public string? AccessToken => null;
        public string? RefreshToken => null;
        public void Save(Contracts.Identity.TokenResponse tokens) { }
        public void Clear() { }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("Standalone mode must not call the server: " + request.RequestUri);
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => start;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
