using Microsoft.EntityFrameworkCore;
using Veltrix.Contracts.Reporting;
using Veltrix.Modules.Accounting;
using Veltrix.Modules.Catalog;
using Veltrix.Modules.Crm;
using Veltrix.Modules.Hr;
using Veltrix.Modules.Identity;
using Veltrix.Modules.Inventory;
using Veltrix.Modules.Pos;
using Veltrix.Modules.Purchasing;
using Veltrix.Modules.Sales;
using Veltrix.Modules.Tenants;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Reporting;

public sealed record ReportFilter(DateOnly From, DateOnly To, Guid? BranchId, Guid? CashierId, Guid? CategoryId, Guid? EmployeeId, string? GroupBy);

/// <summary>Unified sales fact (POS sales/returns and B2B invoices/credit notes) built from persisted documents only.</summary>
public sealed record SaleFact(DateTimeOffset At, Guid BranchId, Guid? UserId, Guid? CustomerId, string Source, Guid ProductId, decimal Quantity, decimal Net, decimal Tax, decimal Cost);

public sealed class ReportingService(DbContext db, ITenantContext ctx, IClock clock, AccountingService accounting, InventoryService inventory,
    PurchasingService purchasing)
{
    public static readonly IReadOnlyDictionary<string, (string Title, string Permission)> Catalog = new Dictionary<string, (string, string)>
    {
        ["sales-summary"] = ("Sales summary", Permissions.ReportsSales),
        ["gross-profit"] = ("Gross profit", Permissions.ReportsFinancial),
        ["net-profit"] = ("Net profit (P&L)", Permissions.ReportsFinancial),
        ["expenses"] = ("Expenses", Permissions.ReportsFinancial),
        ["revenue-by-branch"] = ("Revenue by branch", Permissions.ReportsSales),
        ["revenue-by-cashier"] = ("Revenue by cashier", Permissions.ReportsSales),
        ["top-products"] = ("Top-selling products", Permissions.ReportsSales),
        ["slow-moving"] = ("Slow-moving products", Permissions.ReportsInventory),
        ["inventory-valuation"] = ("Inventory valuation", Permissions.ReportsInventory),
        ["supplier-performance"] = ("Supplier performance", Permissions.ReportsFinancial),
        ["customer-balances"] = ("Customer balances", Permissions.ReportsFinancial),
        ["outstanding-invoices"] = ("Outstanding invoices", Permissions.ReportsFinancial),
        ["employee-attendance"] = ("Employee attendance", Permissions.ReportsHr),
        ["cash-flow"] = ("Cash flow", Permissions.ReportsFinancial),
        ["tax-summary"] = ("Tax summary", Permissions.ReportsFinancial),
    };

    private static DateTimeOffset Start(DateOnly d) => new(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    private static DateTimeOffset End(DateOnly d) => new(d.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);

    public async Task<List<SaleFact>> FactsAsync(ReportFilter f, CancellationToken ct)
    {
        var fromTs = Start(f.From); var toTs = End(f.To);
        var pos = await (from s in db.Set<PosSale>().AsNoTracking()
                         join l in db.Set<PosSaleLine>() on s.Id equals l.SaleId
                         where s.CreatedAt >= fromTs && s.CreatedAt <= toTs
                         select new { At = s.ClientCreatedAt ?? s.CreatedAt, s.BranchId, s.CashierId, s.CustomerId, l.ProductId, l.Quantity, l.NetAmount, l.TaxAmount, l.UnitCost }).ToListAsync(ct);
        var docs = await (from d in db.Set<SalesDocument>().AsNoTracking()
                          join l in db.Set<SalesDocumentLine>() on d.Id equals l.DocumentId
                          where (d.Type == SalesDocumentType.Invoice || d.Type == SalesDocumentType.CreditNote) && d.Date >= f.From && d.Date <= f.To
                          select new { d.Type, d.Date, d.BranchId, d.SalespersonId, d.CustomerId, l.ProductId, l.Quantity, l.NetAmount, l.TaxAmount, l.UnitCost }).ToListAsync(ct);
        var facts = pos.Select(p => new SaleFact(p.At, p.BranchId, p.CashierId, p.CustomerId, "pos", p.ProductId, p.Quantity, p.NetAmount, p.TaxAmount, Money.Round(p.UnitCost * p.Quantity)))
            .Concat(docs.Select(d =>
            {
                var sign = d.Type == SalesDocumentType.CreditNote ? -1 : 1;
                return new SaleFact(Start(d.Date), d.BranchId, d.SalespersonId, d.CustomerId, "invoice", d.ProductId, sign * d.Quantity, sign * d.NetAmount, sign * d.TaxAmount,
                    sign * Money.Round(d.UnitCost * d.Quantity));
            })).ToList();
        if (f.BranchId is { } b) facts = facts.Where(x => x.BranchId == b).ToList();
        if (f.CashierId is { } c) facts = facts.Where(x => x.UserId == c).ToList();
        if (f.CategoryId is { } cat)
        {
            var ids = await db.Set<Product>().Where(p => p.CategoryId == cat).Select(p => p.Id).ToListAsync(ct);
            facts = facts.Where(x => ids.Contains(x.ProductId)).ToList();
        }
        if (ctx.AllowedBranches is { } allowed) facts = facts.Where(x => allowed.Contains(x.BranchId)).ToList();
        return facts;
    }

    public async Task<ReportTable> RunAsync(string key, ReportFilter f, CancellationToken ct)
    {
        if (!Catalog.TryGetValue(key, out var meta)) throw new NotFoundException("report.unknown", "Unknown report.");
        ctx.EnsurePermission(meta.Permission);
        if (f.To < f.From || f.To.DayNumber - f.From.DayNumber > 3660) throw ValidationException.For("to", "Invalid date range.");
        var p = new Dictionary<string, string?> { ["from"] = f.From.ToString("yyyy-MM-dd"), ["to"] = f.To.ToString("yyyy-MM-dd"), ["branchId"] = f.BranchId?.ToString(),
            ["cashierId"] = f.CashierId?.ToString(), ["categoryId"] = f.CategoryId?.ToString(), ["groupBy"] = f.GroupBy };
        ReportTable T(IReadOnlyList<ReportColumn> cols, IEnumerable<IReadOnlyList<object?>> rows, Dictionary<string, object?>? summary = null) =>
            new(key, meta.Title, cols, rows.ToList(), summary ?? new(), clock.UtcNow, p);
        ReportColumn C(string k, string t, string type = "text") => new(k, t, type);

        switch (key)
        {
            case "sales-summary":
            {
                var facts = await FactsAsync(f, ct);
                var group = f.GroupBy ?? "day";
                string Bucket(DateTimeOffset at)
                {
                    var d = DateOnly.FromDateTime(at.ToOffset(TimeSpan.FromHours(1)).DateTime);
                    return group switch
                    {
                        "week" => $"{System.Globalization.ISOWeek.GetYear(d.ToDateTime(TimeOnly.MinValue))}-W{System.Globalization.ISOWeek.GetWeekOfYear(d.ToDateTime(TimeOnly.MinValue)):00}",
                        "month" => d.ToString("yyyy-MM"),
                        "year" => d.ToString("yyyy"),
                        _ => d.ToString("yyyy-MM-dd"),
                    };
                }
                var rows = facts.GroupBy(x => Bucket(x.At)).OrderBy(g => g.Key)
                    .Select(g => (IReadOnlyList<object?>)[g.Key, g.Sum(x => x.Net), g.Sum(x => x.Tax), g.Sum(x => x.Net + x.Tax), g.Sum(x => x.Net - x.Cost)]).ToList();
                return T([C("period", "Period"), C("net", "Net sales", "money"), C("tax", "Tax", "money"), C("gross", "Sales incl. tax", "money"), C("margin", "Gross margin", "money")], rows,
                    new() { ["Net sales"] = facts.Sum(x => x.Net), ["Gross margin"] = facts.Sum(x => x.Net - x.Cost) });
            }
            case "gross-profit":
            {
                var facts = await FactsAsync(f, ct);
                var products = await ProductMapAsync(facts.Select(x => x.ProductId), ct);
                var rows = facts.GroupBy(x => x.ProductId).Select(g =>
                {
                    var net = g.Sum(x => x.Net); var cost = g.Sum(x => x.Cost);
                    return (IReadOnlyList<object?>)[products[g.Key].Sku, products[g.Key].Name, g.Sum(x => x.Quantity), net, cost, net - cost, net == 0 ? 0m : Math.Round((net - cost) / net * 100, 1)];
                }).OrderByDescending(r => (decimal)r[5]!).ToList();
                var totalNet = facts.Sum(x => x.Net); var totalCost = facts.Sum(x => x.Cost);
                return T([C("sku", "SKU"), C("name", "Product"), C("qty", "Qty", "number"), C("net", "Net sales", "money"), C("cost", "Cost", "money"), C("profit", "Gross profit", "money"), C("pct", "Margin %", "number")],
                    rows, new() { ["Net sales"] = totalNet, ["Cost of sales"] = totalCost, ["Gross profit"] = totalNet - totalCost });
            }
            case "net-profit":
            {
                var pl = await accounting.ProfitAndLossAsync(f.From, f.To, f.BranchId, ct);
                var rows = pl.Revenue.Select(r => (IReadOnlyList<object?>)["Revenue", r.Code, r.Name, r.Amount])
                    .Concat(pl.Expenses.Select(e => (IReadOnlyList<object?>)["Expense", e.Code, e.Name, e.Amount])).ToList();
                return T([C("section", "Section"), C("code", "Account"), C("name", "Name"), C("amount", "Amount", "money")], rows,
                    new() { ["Revenue"] = pl.TotalRevenue, ["Expenses"] = pl.TotalExpenses, ["Gross profit"] = pl.GrossProfit, ["Net profit"] = pl.NetProfit });
            }
            case "expenses":
            {
                var page = await accounting.ExpensesAsync(new PageQuery(1, 500), f.From, f.To, null, ct);
                var rows = page.Items.Select(e => (IReadOnlyList<object?>)[e.Date, e.Number, e.ExpenseAccountName, e.Payee, e.Description, e.Amount]).ToList();
                return T([C("date", "Date", "date"), C("number", "Number"), C("account", "Category"), C("payee", "Payee"), C("description", "Description"), C("amount", "Amount", "money")],
                    rows, new() { ["Total"] = page.Items.Sum(e => e.Amount) });
            }
            case "revenue-by-branch":
            {
                var facts = await FactsAsync(f, ct);
                var branches = await db.Set<Branch>().ToDictionaryAsync(b => b.Id, b => b.Name, ct);
                var rows = facts.GroupBy(x => x.BranchId).Select(g => (IReadOnlyList<object?>)[branches.GetValueOrDefault(g.Key, "?"), g.Sum(x => x.Net), g.Sum(x => x.Net - x.Cost)])
                    .OrderByDescending(r => (decimal)r[1]!).ToList();
                return T([C("branch", "Branch"), C("net", "Net sales", "money"), C("margin", "Gross margin", "money")], rows);
            }
            case "revenue-by-cashier":
            {
                var facts = await FactsAsync(f, ct);
                var users = await db.Set<User>().Where(u => u.TenantId == ctx.TenantId).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
                var rows = facts.Where(x => x.Source == "pos").GroupBy(x => x.UserId).Select(g => (IReadOnlyList<object?>)[g.Key is { } u ? users.GetValueOrDefault(u, "?") : "?", g.Sum(x => x.Net + x.Tax)])
                    .OrderByDescending(r => (decimal)r[1]!).ToList();
                return T([C("cashier", "Cashier"), C("sales", "Sales incl. tax", "money")], rows);
            }
            case "top-products":
            {
                var facts = await FactsAsync(f, ct);
                var products = await ProductMapAsync(facts.Select(x => x.ProductId), ct);
                var rows = facts.GroupBy(x => x.ProductId).Select(g => (IReadOnlyList<object?>)[products[g.Key].Sku, products[g.Key].Name, g.Sum(x => x.Quantity), g.Sum(x => x.Net)])
                    .OrderByDescending(r => (decimal)r[3]!).Take(50).ToList();
                return T([C("sku", "SKU"), C("name", "Product"), C("qty", "Qty sold", "number"), C("net", "Net sales", "money")], rows);
            }
            case "slow-moving":
            {
                var facts = await FactsAsync(f, ct);
                var sold = facts.GroupBy(x => x.ProductId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
                var stock = await (from b in db.Set<StockBalance>() join pr in db.Set<Product>() on b.ProductId equals pr.Id where b.Quantity > 0
                                   group b by new { pr.Id, pr.Sku, pr.Name } into g select new { g.Key.Id, g.Key.Sku, g.Key.Name, Qty = g.Sum(x => x.Quantity), Value = g.Sum(x => x.Quantity * x.AverageCost) }).ToListAsync(ct);
                var rows = stock.Select(s => (IReadOnlyList<object?>)[s.Sku, s.Name, s.Qty, Money.Round(s.Value), sold.GetValueOrDefault(s.Id)])
                    .OrderBy(r => (decimal)r[4]!).ThenByDescending(r => (decimal)r[3]!).Take(100).ToList();
                return T([C("sku", "SKU"), C("name", "Product"), C("onhand", "On hand", "number"), C("value", "Stock value", "money"), C("sold", "Sold in period", "number")], rows);
            }
            case "inventory-valuation":
            {
                var v = await inventory.ValuationAsync(ct);
                return T([C("warehouse", "Warehouse"), C("qty", "Quantity", "number"), C("value", "Value", "money")],
                    v.Rows.Select(r => (IReadOnlyList<object?>)[r.WarehouseName, r.Quantity, r.Value]), new() { ["Total value"] = v.TotalValue });
            }
            case "supplier-performance":
            {
                var rows = await purchasing.PerformanceAsync(f.From, f.To, ct);
                return T([C("supplier", "Supplier"), C("orders", "Orders", "number"), C("spend", "Spend", "money"), C("ontime", "On-time %", "number"), C("payable", "Payable", "money")],
                    rows.Select(r => (IReadOnlyList<object?>)[r.Name, r.Orders, r.Spend, Math.Round(r.OnTimeRate * 100, 1), r.OutstandingPayable]));
            }
            case "customer-balances":
            {
                var aging = await accounting.AgingAsync(PartyTypes.Customer, f.To, ct);
                return T([C("customer", "Customer"), C("current", "0-30", "money"), C("d30", "31-60", "money"), C("d60", "61-90", "money"), C("d90", "90+", "money"), C("total", "Total", "money")],
                    aging.Select(a => (IReadOnlyList<object?>)[a.PartyName, a.Current, a.Days30, a.Days60, a.Days90Plus, a.Total]), new() { ["Total receivable"] = aging.Sum(a => a.Total) });
            }
            case "outstanding-invoices":
            {
                var docs = await (from d in db.Set<SalesDocument>() join c in db.Set<Customer>() on d.CustomerId equals c.Id
                                  where d.Type == SalesDocumentType.Invoice && d.AmountPaid < d.Total && d.Date <= f.To
                                  orderby d.DueDate select new { d.Number, c.Name, d.Date, d.DueDate, d.Total, d.AmountPaid }).ToListAsync(ct);
                var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
                return T([C("number", "Invoice"), C("customer", "Customer"), C("date", "Date", "date"), C("due", "Due", "date"), C("total", "Total", "money"), C("outstanding", "Outstanding", "money"), C("overdue", "Days overdue", "number")],
                    docs.Select(d => (IReadOnlyList<object?>)[d.Number, d.Name, d.Date, d.DueDate, d.Total, d.Total - d.AmountPaid, d.DueDate is { } due && due < today ? today.DayNumber - due.DayNumber : 0]),
                    new() { ["Outstanding"] = docs.Sum(d => d.Total - d.AmountPaid) });
            }
            case "employee-attendance":
            {
                var rows = await (from a in db.Set<AttendanceRecord>() join e in db.Set<Employee>() on a.EmployeeId equals e.Id
                                  where a.Date >= f.From && a.Date <= f.To && (f.EmployeeId == null || e.Id == f.EmployeeId)
                                  group a by new { e.Code, e.FullName } into g
                                  select new { g.Key.Code, g.Key.FullName, Present = g.Count(x => x.Status == AttendanceStatus.Present || x.Status == AttendanceStatus.Late),
                                      Absent = g.Count(x => x.Status == AttendanceStatus.Absent), Late = g.Sum(x => x.LateMinutes), Overtime = g.Sum(x => x.OvertimeMinutes), Worked = g.Sum(x => x.WorkedMinutes) }).ToListAsync(ct);
                return T([C("code", "Code"), C("name", "Employee"), C("present", "Days present", "number"), C("absent", "Days absent", "number"), C("late", "Late (min)", "number"), C("ot", "Overtime (min)", "number"), C("hours", "Hours worked", "number")],
                    rows.Select(r => (IReadOnlyList<object?>)[r.Code, r.FullName, r.Present, r.Absent, r.Late, r.Overtime, Math.Round(r.Worked / 60m, 1)]));
            }
            case "cash-flow":
            {
                var cf = await accounting.CashFlowAsync(f.From, f.To, ct);
                return T([C("direction", "Direction"), C("source", "Source"), C("amount", "Amount", "money")],
                    cf.InflowsBySource.Select(i => (IReadOnlyList<object?>)["In", i.Name, i.Amount]).Concat(cf.OutflowsBySource.Select(o => (IReadOnlyList<object?>)["Out", o.Name, o.Amount])),
                    new() { ["Opening cash"] = cf.OpeningCash, ["Net change"] = cf.NetChange, ["Closing cash"] = cf.ClosingCash });
            }
            case "tax-summary":
            {
                var t = await accounting.TaxSummaryAsync(f.From, f.To, ct);
                return T([C("item", "Item"), C("amount", "Amount", "money")],
                    [["Tax collected", t.TaxCollected], ["Tax deductible", t.TaxDeductible], ["Net tax payable", t.NetTaxPayable]],
                    new() { ["Note"] = "Figures derive from configured tax rates; validate declarations with your accountant." });
            }
        }
        throw new NotFoundException("report.unknown", "Unknown report.");
    }

    public async Task<DashboardDto> DashboardAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.ToOffset(TimeSpan.FromHours(1)).DateTime);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var prevStart = monthStart.AddMonths(-1);
        var facts = await FactsAsync(new ReportFilter(today.AddDays(-30) < prevStart ? today.AddDays(-30) : prevStart, today, null, null, null, null, null), ct);
        DateOnly D(DateTimeOffset at) => DateOnly.FromDateTime(at.ToOffset(TimeSpan.FromHours(1)).DateTime);
        var todayFacts = facts.Where(x => D(x.At) == today).ToList();
        var month = facts.Where(x => D(x.At) >= monthStart).ToList();
        var prev = facts.Where(x => D(x.At) >= prevStart && D(x.At) < monthStart && D(x.At).Day <= today.Day).ToList();
        var todayStart = Start(today).AddHours(-1);
        var txToday = await db.Set<PosSale>().CountAsync(s => s.CreatedAt >= todayStart && !s.IsReturn && s.Status == SaleStatus.Completed, ct);
        var series = Enumerable.Range(0, 30).Select(i => today.AddDays(-29 + i)).Select(d => new SeriesPoint(d, facts.Where(x => D(x.At) == d).Sum(x => x.Net + x.Tax))).ToList();
        var products = await ProductMapAsync(month.Select(x => x.ProductId), ct);
        var branches = await db.Set<Branch>().ToDictionaryAsync(b => b.Id, b => b.Name, ct);
        var lowStock = ctx.HasPermission(Permissions.InventoryView) ? (await inventory.LowStockAsync(null, ct)).Count : 0;
        decimal receivables = 0, payables = 0;
        if (ctx.HasPermission(Permissions.ReportsFinancial))
        {
            receivables = (await accounting.AgingAsync(PartyTypes.Customer, today, ct)).Sum(a => a.Total);
            payables = (await accounting.AgingAsync(PartyTypes.Supplier, today, ct)).Sum(a => a.Total);
        }
        var pending = await db.Set<PurchaseOrder>().CountAsync(o => o.Status == PurchaseOrderStatus.PendingApproval, ct)
                      + await db.Set<SalesDocument>().CountAsync(d => d.Status == SalesStatus.PendingApproval, ct)
                      + await db.Set<LeaveRequest>().CountAsync(l => l.Status == ApprovalStatus.Pending, ct);
        var monthNet = month.Sum(x => x.Net);
        return new DashboardDto(todayFacts.Sum(x => x.Net + x.Tax), month.Sum(x => x.Net + x.Tax), prev.Sum(x => x.Net + x.Tax), txToday,
            txToday == 0 ? 0 : Money.Round(todayFacts.Where(x => x.Source == "pos").Sum(x => x.Net + x.Tax) / txToday),
            monthNet == 0 ? 0 : Math.Round((monthNet - month.Sum(x => x.Cost)) / monthNet * 100, 1), lowStock, receivables, payables, pending, series,
            month.GroupBy(x => x.ProductId).Select(g => new NamedValue(products[g.Key].Name, g.Sum(x => x.Net))).OrderByDescending(x => x.Value).Take(5).ToList(),
            month.GroupBy(x => x.BranchId).Select(g => new NamedValue(branches.GetValueOrDefault(g.Key, "?"), g.Sum(x => x.Net))).OrderByDescending(x => x.Value).ToList());
    }

    public async Task<Dictionary<Guid, (string Sku, string Name)>> ProductMapAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Set<Product>().Where(p => list.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => (p.Sku, p.Name), ct);
    }
}
