using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Veltrix.Contracts;
using Veltrix.Contracts.Catalog;
using Veltrix.Contracts.Crm;
using Veltrix.Contracts.Inventory;
using Veltrix.Contracts.Pos;
using Veltrix.Contracts.Reporting;
using Veltrix.Contracts.Tenants;
using Veltrix.Desktop.Core.Data;

namespace Veltrix.Desktop.Core.Standalone;

/// <summary>
/// Local repository used when the installation runs standalone (offline license, no server). It reads and writes the same local
/// tables the online client uses as its cache and returns the API contract DTOs, so screens render identically in both modes.
/// </summary>
public sealed class StandaloneStore(Func<LocalDb> dbFactory, TimeProvider time)
{
    public const string WarehouseName = "Main";

    // ------------------------------------------------------------------ setup

    /// <summary>Creates the single local register/warehouse on first use and returns it.</summary>
    public async Task<LocalRegister> EnsureRegisterAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory();
        if (await db.GetValueAsync(LocalDb.Keys.StandaloneRegister, ct) is { } idText && Guid.TryParse(idText, out var id)
            && await db.Registers.FindAsync([id], ct) is { } existing)
            return existing;
        var reg = new LocalRegister { Id = Guid.NewGuid(), BranchId = Guid.NewGuid(), WarehouseId = Guid.NewGuid(), Code = "POS1", Name = "POS 1", IsActive = true };
        db.Registers.Add(reg);
        await db.SaveChangesAsync(ct);
        await db.SetValueAsync(LocalDb.Keys.StandaloneRegister, reg.Id.ToString(), ct);
        return reg;
    }

    /// <summary>Case-insensitive (ASCII) "contains" pattern for SQLite LIKE with '\' as escape character.</summary>
    public static string LikePattern(string text) => "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    public async Task<Guid> WarehouseIdAsync(CancellationToken ct = default) => (await EnsureRegisterAsync(ct)).WarehouseId;

    // ------------------------------------------------------------------ catalog

    /// <summary>Fixed local tax categories (stable ids so products keep their rate across backups).</summary>
    public static IReadOnlyList<TaxCategoryDto> TaxCategories { get; } =
    [
        new(new Guid("5a1e0000-0000-0000-0000-000000000000"), "TVA0", "0 %", 0, true),
        new(new Guid("5a1e0000-0000-0000-0000-000000000009"), "TVA9", "9 %", 9, true),
        new(new Guid("5a1e0000-0000-0000-0000-000000000019"), "TVA19", "19 %", 19, true),
    ];

    public async Task<PagedResponse<ProductDto>> ProductsAsync(int page, int pageSize, string? search, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var q = db.Products.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = LikePattern(search);
            q = q.Where(p => EF.Functions.Like(p.Name, s, "\\") || EF.Functions.Like(p.Sku, s, "\\") || (p.NameAr != null && EF.Functions.Like(p.NameAr, s, "\\"))
                || EF.Functions.Like(p.Barcodes, s, "\\"));
        }
        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(p => p.Name).Skip(Math.Max(0, page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResponse<ProductDto>(items.Select(ToDto).ToList(), page, pageSize, total);
    }

    public async Task<ProductDto> UpsertProductAsync(UpsertProductRequest req, Guid? id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Sku) || string.IsNullOrWhiteSpace(req.Name)) throw new InvalidOperationException("SKU / name");
        if (req.SalePrice < 0) throw new InvalidOperationException("Price");
        await using var db = dbFactory();
        var sku = req.Sku.Trim();
        if (await db.Products.AnyAsync(p => p.Sku == sku && (id == null || p.Id != id), ct)) throw new InvalidOperationException("SKU already exists: " + sku);
        var p = id is { } existing ? await db.Products.FindAsync([existing], ct) ?? throw new InvalidOperationException("Unknown product.") : null;
        if (p is null) db.Products.Add(p = new LocalProduct { Id = req.Id ?? Guid.NewGuid() });
        p.Sku = sku;
        p.Name = req.Name.Trim();
        p.NameAr = req.NameAr;
        p.Type = req.Type;
        p.Status = req.Status;
        p.CategoryId = req.CategoryId;
        p.TaxRatePercent = TaxCategories.FirstOrDefault(t => t.Id == req.TaxCategoryId)?.RatePercent ?? p.TaxRatePercent;
        p.SalePrice = req.SalePrice;
        p.PurchasePrice = req.PurchasePrice;
        p.TrackStock = req.TrackStock;
        p.LowStockThreshold = req.LowStockThreshold;
        p.Barcodes = string.Join('\n', (req.Barcodes ?? []).Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim()).Distinct());
        p.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return ToDto(p);
    }

    public static ProductDto ToDto(LocalProduct p) => new(p.Id, p.Sku, p.Name, p.NameAr, null, p.Type, p.Status, p.CategoryId, null, null,
        TaxCategories.FirstOrDefault(t => t.RatePercent == p.TaxRatePercent)?.Id, p.TaxRatePercent, p.SalePrice, null, p.PurchasePrice, Erp.Costing.UnitCost(p), p.TrackStock, false, false, false, p.LowStockThreshold, 0, null, null, false,
        p.Barcodes.Split('\n', StringSplitOptions.RemoveEmptyEntries), [], p.UpdatedAt);

    // ------------------------------------------------------------------ customers

    public async Task<PagedResponse<CustomerDto>> CustomersAsync(int page, int pageSize, string? search, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var q = db.Customers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = LikePattern(search);
            q = q.Where(c => EF.Functions.Like(c.Name, s, "\\") || EF.Functions.Like(c.Code, s, "\\") || (c.Phone != null && EF.Functions.Like(c.Phone, s, "\\"))
                || (c.Email != null && EF.Functions.Like(c.Email, s, "\\")));
        }
        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(c => c.Name).Skip(Math.Max(0, page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResponse<CustomerDto>(items.Select(ToDto).ToList(), page, pageSize, total);
    }

    public async Task<CustomerDto> UpsertCustomerAsync(UpsertCustomerRequest req, Guid? id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) throw new InvalidOperationException("Name");
        await using var db = dbFactory();
        var c = id is { } existing ? await db.Customers.FindAsync([existing], ct) : null;
        if (c is null)
        {
            var n = await db.Customers.CountAsync(ct) + 1;
            db.Customers.Add(c = new LocalCustomer { Id = id ?? Guid.NewGuid(), Code = "C" + n.ToString("00000", CultureInfo.InvariantCulture) });
        }
        c.Type = req.Type;
        c.Name = req.Name.Trim();
        c.Phone = req.Phone;
        c.Email = req.Email;
        c.Address = req.Address;
        c.City = req.City;
        c.CreditLimit = req.CreditLimit;
        c.ServerVersion = time.GetUtcNow();
        c.PendingSync = false;
        await db.SaveChangesAsync(ct);
        return ToDto(c);
    }

    public static CustomerDto ToDto(LocalCustomer c) => new(c.Id, c.Code, c.Type, c.Name, c.Phone, c.Email, c.Address, c.City, null, null, null, null, null,
        c.CreditLimit, 0, c.IsActive, false, null, null, c.Balance, c.ServerVersion ?? DateTimeOffset.MinValue);

    public async Task<IReadOnlyList<CustomerPurchaseDto>> CustomerPurchasesAsync(Guid customerId, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var sales = await db.Sales.AsNoTracking().Where(s => s.CustomerId == customerId).ToListAsync(ct);
        return sales.OrderByDescending(s => s.CreatedAt).Take(50).Select(s => new CustomerPurchaseDto("pos", s.Id, s.ClientNumber, s.CreatedAt, s.Total)).ToList();
    }

    // ------------------------------------------------------------------ inventory

    public async Task<IReadOnlyList<WarehouseDto>> WarehousesAsync(CancellationToken ct = default)
    {
        var reg = await EnsureRegisterAsync(ct);
        return [new WarehouseDto(reg.WarehouseId, reg.BranchId, "MAIN", WarehouseName, true, true)];
    }

    /// <summary>Every stock-tracked product with its quantity in the local warehouse (0 when never received).</summary>
    public async Task<PagedResponse<StockBalanceDto>> StockAsync(int page, int pageSize, string? search, bool inStockOnly, CancellationToken ct = default)
    {
        var wid = await WarehouseIdAsync(ct);
        await using var db = dbFactory();
        var products = db.Products.AsNoTracking().Where(p => p.TrackStock);
        if (!string.IsNullOrWhiteSpace(search)) { var s = LikePattern(search); products = products.Where(p => EF.Functions.Like(p.Name, s, "\\") || EF.Functions.Like(p.Sku, s, "\\")); }
        var list = await products.OrderBy(p => p.Name).ToListAsync(ct);
        var stock = (await db.Stock.AsNoTracking().Where(x => x.WarehouseId == wid).ToListAsync(ct)).ToDictionary(x => x.ProductId);
        var rows = list.Select(p =>
        {
            var q = stock.TryGetValue(p.Id, out var st) ? st.Quantity : 0;
            var cost = Erp.Costing.UnitCost(p);
            return new StockBalanceDto(wid, WarehouseName, p.Id, p.Sku, p.Name, q, 0, q, cost, Erp.Money.Round(q * cost), p.LowStockThreshold, null,
                st?.UpdatedAt ?? p.UpdatedAt);
        }).Where(r => !inStockOnly || r.Quantity > 0).ToList();
        return new PagedResponse<StockBalanceDto>(rows.Skip(Math.Max(0, page - 1) * pageSize).Take(pageSize).ToList(), page, pageSize, rows.Count);
    }

    public async Task<IReadOnlyList<LowStockDto>> LowStockAsync(CancellationToken ct = default)
    {
        var all = await StockAsync(1, int.MaxValue, null, false, ct);
        return all.Items.Where(s => s.LowStockThreshold > 0 && s.Quantity <= s.LowStockThreshold)
            .Select(s => new LowStockDto(s.ProductId, s.Sku, s.ProductName, s.WarehouseId, s.WarehouseName, s.Quantity, s.LowStockThreshold,
                Math.Max(0, s.LowStockThreshold * 2 - s.Quantity))).ToList();
    }

    /// <summary>Adds (positive) or removes (negative) stock, e.g. a count correction. The value moves between stock (30) and
    /// inventory adjustments (6581) at the current unit cost, in the same transaction as the quantity.</summary>
    public async Task AdjustStockAsync(Guid productId, decimal quantityChange, CancellationToken ct = default)
    {
        if (quantityChange == 0) return;
        var wid = await WarehouseIdAsync(ct);
        await using var db = dbFactory();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var p = await db.Products.FindAsync([productId], ct) ?? throw new InvalidOperationException("Unknown product.");
        var now = time.GetUtcNow();
        ApplyStock(db, await db.Stock.FindAsync([wid, productId], ct), wid, productId, quantityChange, now);
        if (p.TrackStock)
        {
            var value = Erp.Money.Round(quantityChange * Erp.Costing.UnitCost(p));
            var b = new Erp.PostingBuilder().Debit(Erp.AccountRoles.Inventory, value).Credit(Erp.AccountRoles.InventoryAdjustment, value, p.Sku);
            await Erp.Ledger.PostAsync(db, Erp.Ledger.Today(time), $"Stock adjustment {p.Sku} {quantityChange:0.###}", Erp.JournalSources.Inventory, p.Id, b, now, ct);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    internal static void ApplyStock(LocalDb db, LocalStock? row, Guid warehouseId, Guid productId, decimal change, DateTimeOffset now)
    {
        if (row is null)
            db.Stock.Add(row = new LocalStock { WarehouseId = warehouseId, ProductId = productId, WarehouseName = WarehouseName });
        row.Quantity += change;
        row.Available += change;
        row.UpdatedAt = now;
    }

    // ------------------------------------------------------------------ sales

    public async Task<IReadOnlyList<SaleDto>> SalesAsync(string? search, DateTimeOffset from, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var fromKey = from;
        var sales = (await db.Sales.AsNoTracking().ToListAsync(ct)).Where(s => s.CreatedAt >= fromKey)
            .Where(s => string.IsNullOrWhiteSpace(search) || s.ClientNumber.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.CreatedAt).Take(200).ToList();
        var ids = sales.Select(s => s.Id).ToList();
        var lines = (await db.SaleLines.AsNoTracking().Where(l => ids.Contains(l.SaleId)).ToListAsync(ct)).ToLookup(l => l.SaleId);
        return sales.Select(s => new SaleDto(s.Id, s.ClientNumber, "Completed", false, null, Guid.Empty, s.RegisterId, s.ShiftId, s.CustomerId, Guid.Empty,
            s.Subtotal, s.Discount, s.Tax, s.Total, s.CashReceived, s.Change, 0, 0,
            lines[s.Id].Select(l => new SaleLineDto(l.Id, l.ProductId, l.Sku, l.Name, l.Quantity, l.UnitPrice, l.Discount, 0, l.Tax, l.Net, l.Total, 0)).ToList(),
            [], s.CreatedAt, s.CreatedAt, true, null)).ToList();
    }

    // ------------------------------------------------------------------ dashboard & reports

    public async Task<DashboardDto> DashboardAsync(CancellationToken ct = default)
    {
        var now = time.GetLocalNow();
        var today = new DateTimeOffset(now.Date, now.Offset);
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
        var prevMonthStart = monthStart.AddMonths(-1);
        var since = today.AddDays(-29) < prevMonthStart ? today.AddDays(-29) : prevMonthStart;
        await using var db = dbFactory();
        var sales = (await db.Sales.AsNoTracking().ToListAsync(ct)).Where(s => s.CreatedAt >= since).ToList();
        var lines = (await db.SaleLines.AsNoTracking().ToListAsync(ct)).Where(l => l.CreatedAt >= monthStart).ToList();
        var todays = sales.Where(s => s.CreatedAt >= today).ToList();
        var month = sales.Where(s => s.CreatedAt >= monthStart).Sum(s => s.Total);
        var prev = sales.Where(s => s.CreatedAt >= prevMonthStart && s.CreatedAt < monthStart).Sum(s => s.Total);
        var net = lines.Sum(l => l.Net);
        var cost = lines.Sum(l => l.UnitCost * l.Quantity);
        var margin = net == 0 ? 0 : Math.Round((net - cost) / net * 100m, 2);
        var series = Enumerable.Range(0, 30).Select(i => DateOnly.FromDateTime(today.AddDays(-29 + i).Date))
            .Select(d => new SeriesPoint(d, sales.Where(s => DateOnly.FromDateTime(s.CreatedAt.ToOffset(now.Offset).Date) == d).Sum(s => s.Total))).ToList();
        var top = lines.GroupBy(l => l.Name).Select(g => new NamedValue(g.Key, g.Sum(l => l.Total))).OrderByDescending(v => v.Value).Take(5).ToList();
        var low = (await LowStockAsync(ct)).Count;
        var receivables = await db.Customers.AsNoTracking().Select(c => c.Balance).ToListAsync(ct);
        return new DashboardDto(todays.Sum(s => s.Total), month, prev, todays.Count, todays.Count == 0 ? 0 : Math.Round(todays.Sum(s => s.Total) / todays.Count, 2),
            margin, low, receivables.Where(b => b > 0).Sum(), 0, 0, series, top, []);
    }

    public static IReadOnlyList<(string Key, string Title)> Reports { get; } =
        [("sales-by-day", "Sales by day"), ("sales-by-product", "Sales by product"), ("stock", "Stock on hand")];

    public async Task<ReportTable> RunReportAsync(string key, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var offset = time.GetLocalNow().Offset;
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), offset);
        var end = new DateTimeOffset(to.ToDateTime(TimeOnly.MinValue), offset).AddDays(1);
        var parameters = new Dictionary<string, string?> { ["from"] = from.ToString("O"), ["to"] = to.ToString("O") };
        await using var db = dbFactory();
        switch (key)
        {
            case "sales-by-day":
            {
                var sales = (await db.Sales.AsNoTracking().ToListAsync(ct)).Where(s => s.CreatedAt >= start && s.CreatedAt < end).ToList();
                var rows = sales.GroupBy(s => DateOnly.FromDateTime(s.CreatedAt.ToOffset(offset).Date)).OrderBy(g => g.Key)
                    .Select(g => (IReadOnlyList<object?>)[g.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), g.Count(), g.Sum(s => s.Subtotal), g.Sum(s => s.Discount), g.Sum(s => s.Tax), g.Sum(s => s.Total)])
                    .ToList();
                return new ReportTable(key, "Sales by day",
                    [new("date", "Date", "date"), new("count", "Receipts", "number"), new("gross", "Gross", "money"), new("discount", "Discount", "money"), new("tax", "Tax", "money"), new("total", "Total", "money")],
                    rows, new Dictionary<string, object?> { ["Receipts"] = sales.Count, ["Total"] = sales.Sum(s => s.Total) }, time.GetUtcNow(), parameters);
            }
            case "sales-by-product":
            {
                var lines = (await db.SaleLines.AsNoTracking().ToListAsync(ct)).Where(l => l.CreatedAt >= start && l.CreatedAt < end).ToList();
                var rows = lines.GroupBy(l => (l.Sku, l.Name)).Select(g => (Key: g.Key, Qty: g.Sum(l => l.Quantity), Total: g.Sum(l => l.Total), Margin: g.Sum(l => l.Net - l.UnitCost * l.Quantity)))
                    .OrderByDescending(r => r.Total).Select(r => (IReadOnlyList<object?>)[r.Key.Sku, r.Key.Name, r.Qty, r.Total, r.Margin]).ToList();
                return new ReportTable(key, "Sales by product",
                    [new("sku", "SKU", "text"), new("name", "Product", "text"), new("qty", "Quantity", "number"), new("total", "Total", "money"), new("margin", "Margin", "money")],
                    rows, new Dictionary<string, object?> { ["Total"] = lines.Sum(l => l.Total) }, time.GetUtcNow(), parameters);
            }
            case "stock":
            {
                var stock = await StockAsync(1, int.MaxValue, null, false, ct);
                var rows = stock.Items.Select(s => (IReadOnlyList<object?>)[s.Sku, s.ProductName, s.Quantity, s.Value]).ToList();
                return new ReportTable(key, "Stock on hand", [new("sku", "SKU", "text"), new("name", "Product", "text"), new("qty", "Quantity", "number"), new("value", "Value", "money")],
                    rows, new Dictionary<string, object?> { ["Value"] = stock.Items.Sum(s => s.Value) }, time.GetUtcNow(), parameters);
            }
            default:
                throw new InvalidOperationException("Unknown report: " + key);
        }
    }

    /// <summary>CSV export of a local report (the server-side Excel/PDF renderers are not available standalone).</summary>
    public static string ToCsv(ReportTable t)
    {
        static string Cell(object? v)
        {
            var s = v switch { null => "", IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), _ => v.ToString() ?? "" };
            if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' && !decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) s = "'" + s;
            return s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join(',', t.Columns.Select(c => Cell(c.Title))));
        foreach (var r in t.Rows) sb.AppendLine(string.Join(',', r.Select(Cell)));
        return sb.ToString();
    }
}
