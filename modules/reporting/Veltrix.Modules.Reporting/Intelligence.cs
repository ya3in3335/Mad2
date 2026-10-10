using Microsoft.EntityFrameworkCore;
using Veltrix.Contracts.Reporting;
using Veltrix.Modules.Catalog;
using Veltrix.Modules.Crm;
using Veltrix.Modules.Inventory;
using Veltrix.Modules.Pos;
using Veltrix.Modules.Tenants;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Reporting;

/// <summary>Optional AI enrichment. Core analytics never depend on it; no provider is bundled.</summary>
public interface IAiInsightProvider
{
    bool IsConfigured { get; }
    Task<string?> SummarizeAsync(string language, IReadOnlyList<string> facts, CancellationToken ct);
}

public sealed class NullAiInsightProvider : IAiInsightProvider
{
    public bool IsConfigured => false;
    public Task<string?> SummarizeAsync(string language, IReadOnlyList<string> facts, CancellationToken ct) => Task.FromResult<string?>(null);
}

/// <summary>VELTRIX Intelligence: transparent, deterministic statistics over real business records.
/// Forecasts are estimates from historical averages, never guarantees.</summary>
public sealed class IntelligenceService(DbContext db, ITenantContext ctx, IClock clock, ReportingService reports, IAiInsightProvider ai)
{
    private DateOnly Today => DateOnly.FromDateTime(clock.UtcNow.ToOffset(TimeSpan.FromHours(1)).DateTime);
    private static DateOnly D(DateTimeOffset at) => DateOnly.FromDateTime(at.ToOffset(TimeSpan.FromHours(1)).DateTime);

    public async Task<TrendDto> SalesTrendAsync(int days, Guid? branchId, CancellationToken ct)
    {
        days = Math.Clamp(days, 7, 365);
        var from = Today.AddDays(-(2 * days) + 1);
        var facts = await reports.FactsAsync(new ReportFilter(from, Today, branchId, null, null, null, null), ct);
        var byDay = facts.GroupBy(x => D(x.At)).ToDictionary(g => g.Key, g => g.Sum(x => x.Net));
        var daily = Enumerable.Range(0, days).Select(i => Today.AddDays(-days + 1 + i)).Select(d => new SeriesPoint(d, byDay.GetValueOrDefault(d))).ToList();
        var ma = daily.Select((p, i) => new SeriesPoint(p.Date, Math.Round(daily.Skip(Math.Max(0, i - 6)).Take(Math.Min(7, i + 1)).Average(x => x.Value), 2))).ToList();
        // Least-squares slope over the window.
        var n = daily.Count;
        var xMean = (n - 1) / 2m;
        var yMean = daily.Average(p => p.Value);
        var num = daily.Select((p, i) => (i - xMean) * (p.Value - yMean)).Sum();
        var den = daily.Select((_, i) => (i - xMean) * (i - xMean)).Sum();
        var slope = den == 0 ? 0 : Math.Round(num / den, 2);
        var current = daily.Sum(p => p.Value);
        var previous = Enumerable.Range(0, days).Select(i => Today.AddDays(-2 * days + 1 + i)).Sum(d => byDay.GetValueOrDefault(d));
        var change = previous == 0 ? (current > 0 ? 100 : 0) : Math.Round((current - previous) / previous * 100, 1);
        var direction = Math.Abs(change) < 5 ? "stable" : change > 0 ? "up" : "down";
        return new TrendDto(daily, ma, slope, current, previous, change, direction, "7-day moving average + ordinary least squares slope; period-over-period comparison.");
    }

    public async Task<IReadOnlyList<StockForecastDto>> StockForecastAsync(int lookbackDays, int leadTimeDays, int safetyDays, CancellationToken ct)
    {
        lookbackDays = Math.Clamp(lookbackDays, 7, 180);
        var since = clock.UtcNow.AddDays(-lookbackDays);
        var raw = await db.Set<StockMovement>().Where(m => m.OccurredAt >= since && (m.Type == MovementType.Sale || m.Type == MovementType.Delivery || m.Type == MovementType.BundleComponent))
            .Select(m => new { m.ProductId, m.WarehouseId, m.Quantity, m.OccurredAt }).ToListAsync(ct);
        var outflow = raw.GroupBy(m => new { m.ProductId, m.WarehouseId })
            .Select(g => new { g.Key.ProductId, g.Key.WarehouseId, Qty = -g.Sum(x => x.Quantity), Days = g.Select(x => D(x.OccurredAt)).Distinct().Count() }).ToList();
        var balances = await (from b in db.Set<StockBalance>() join p in db.Set<Product>() on b.ProductId equals p.Id join w in db.Set<Warehouse>() on b.WarehouseId equals w.Id
                              where p.TrackStock && p.Status == ProductStatus.Active
                              select new { b.ProductId, b.WarehouseId, w.BranchId, p.Sku, p.Name, b.Quantity, b.ReservedQuantity }).ToListAsync(ct);
        var result = new List<StockForecastDto>();
        foreach (var b in balances.Where(x => ctx.CanAccessBranch(x.BranchId)))
        {
            var o = outflow.SingleOrDefault(x => x.ProductId == b.ProductId && x.WarehouseId == b.WarehouseId);
            var avg = o is null ? 0 : Math.Round(o.Qty / lookbackDays, 3);
            var available = b.Quantity - b.ReservedQuantity;
            decimal? cover = avg > 0 ? Math.Round(available / avg, 1) : null;
            var target = avg * (leadTimeDays + safetyDays);
            var reorder = Math.Max(0, Math.Ceiling(target - available));
            var confidence = o is null ? "none" : o.Days >= lookbackDays / 2 ? "medium" : o.Days >= 5 ? "low" : "very_low";
            if (avg == 0 && available > 0) continue;
            result.Add(new StockForecastDto(b.ProductId, b.Sku, b.Name, b.WarehouseId, available, avg, cover,
                cover is { } c ? Today.AddDays((int)Math.Floor(c)) : null, reorder, confidence));
        }
        return result.OrderBy(r => r.DaysOfCover ?? 0).ThenByDescending(r => r.RecommendedOrderQuantity).ToList();
    }

    public async Task<IReadOnlyList<ProductScoreDto>> ProductScoresAsync(int days, CancellationToken ct)
    {
        var facts = await reports.FactsAsync(new ReportFilter(Today.AddDays(-Math.Clamp(days, 7, 365)), Today, null, null, null, null, null), ct);
        var groups = facts.GroupBy(x => x.ProductId).Select(g => new { Id = g.Key, Revenue = g.Sum(x => x.Net), Margin = g.Sum(x => x.Net - x.Cost), Units = g.Sum(x => x.Quantity) }).ToList();
        if (groups.Count == 0) return [];
        var maxRev = Math.Max(1, groups.Max(g => g.Revenue)); var maxMargin = Math.Max(1, groups.Max(g => g.Margin)); var maxUnits = Math.Max(1, groups.Max(g => g.Units));
        var names = await reports.ProductMapAsync(groups.Select(g => g.Id), ct);
        return groups.Select(g =>
        {
            // Weighted normalized score: revenue 40%, margin 40%, velocity 20%.
            var score = (int)Math.Round(40 * Math.Max(0, g.Revenue) / maxRev + 40 * Math.Max(0, g.Margin) / maxMargin + 20 * Math.Max(0, g.Units) / maxUnits);
            var cls = score >= 70 ? "A" : score >= 35 ? "B" : "C";
            return new ProductScoreDto(g.Id, names[g.Id].Sku, names[g.Id].Name, g.Revenue, g.Margin, g.Revenue == 0 ? 0 : Math.Round(g.Margin / g.Revenue * 100, 1), g.Units, score, cls);
        }).OrderByDescending(s => s.Score).ToList();
    }

    public async Task<IReadOnlyList<CustomerPatternDto>> CustomerPatternsAsync(CancellationToken ct)
    {
        var since = clock.UtcNow.AddDays(-365);
        var sales = await db.Set<PosSale>().Where(s => s.CustomerId != null && !s.IsReturn && s.Status == SaleStatus.Completed && s.CreatedAt >= since)
            .Select(s => new { CustomerId = s.CustomerId!.Value, s.CreatedAt, s.Total }).ToListAsync(ct);
        var names = await db.Set<Customer>().Where(c => sales.Select(s => s.CustomerId).Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var now = clock.UtcNow;
        return sales.GroupBy(s => s.CustomerId).Select(g =>
        {
            var dates = g.Select(x => x.CreatedAt).OrderBy(x => x).ToList();
            double? avgGap = dates.Count > 1 ? dates.Zip(dates.Skip(1), (a, b) => (b - a).TotalDays).Average() : null;
            var last = dates.Last();
            DateTimeOffset? next = avgGap is { } gap ? last.AddDays(gap) : null;
            return new CustomerPatternDto(g.Key, names.GetValueOrDefault(g.Key, "?"), g.Count(), g.Sum(x => x.Total), Money.Round(g.Average(x => x.Total)),
                avgGap is { } a ? Math.Round(a, 1) : null, last, next, next is { } nx && nx < now.AddDays(-7));
        }).OrderByDescending(c => c.TotalSpent).Take(500).ToList();
    }

    /// <summary>Flags sales whose amount deviates strongly (z-score ≥ 3) from the 30-day distribution, heavy discounts, and cashiers with unusual void rates.</summary>
    public async Task<IReadOnlyList<AnomalyDto>> AnomaliesAsync(CancellationToken ct)
    {
        var since = clock.UtcNow.AddDays(-30);
        var sales = await db.Set<PosSale>().AsNoTracking().Where(s => s.CreatedAt >= since && !s.IsReturn).ToListAsync(ct);
        var result = new List<AnomalyDto>();
        if (sales.Count >= 10)
        {
            var mean = sales.Average(s => (double)s.Total);
            var sd = Math.Sqrt(sales.Average(s => Math.Pow((double)s.Total - mean, 2)));
            if (sd > 0)
                foreach (var s in sales)
                {
                    var z = ((double)s.Total - mean) / sd;
                    if (z >= 3) result.Add(new AnomalyDto("large_sale", s.Number, s.CreatedAt, s.Total, (decimal)Math.Round(mean, 2), Math.Round(z, 2), $"Sale is {z:0.0} standard deviations above the 30-day mean."));
                }
        }
        foreach (var s in sales.Where(s => s.Subtotal > 0 && s.DiscountTotal / s.Subtotal >= 0.3m))
            result.Add(new AnomalyDto("heavy_discount", s.Number, s.CreatedAt, s.DiscountTotal, 0, (double)(s.DiscountTotal / s.Subtotal), $"Discount of {s.DiscountTotal / s.Subtotal:P0}."));
        var byCashier = sales.GroupBy(s => s.CashierId).Select(g => new { g.Key, Total = g.Count(), Voided = g.Count(x => x.Status == SaleStatus.Voided) }).Where(x => x.Total >= 10).ToList();
        var avgVoid = byCashier.Count == 0 ? 0 : byCashier.Average(x => (double)x.Voided / x.Total);
        foreach (var c in byCashier.Where(x => (double)x.Voided / x.Total > Math.Max(0.05, avgVoid * 3)))
            result.Add(new AnomalyDto("void_rate", c.Key.ToString(), clock.UtcNow, c.Voided, (decimal)Math.Round(avgVoid * c.Total, 1), (double)c.Voided / c.Total, $"Cashier voided {c.Voided} of {c.Total} sales."));
        return result.OrderByDescending(a => a.Score).ToList();
    }

    public async Task<BusinessSummaryDto> SummaryAsync(string language, CancellationToken ct)
    {
        var lang = language is "ar" or "fr" ? language : "en";
        var trend = await SalesTrendAsync(30, null, ct);
        var forecast = ctx.HasPermission(Permissions.InventoryView) ? await StockForecastAsync(28, 7, 7, ct) : [];
        var anomalies = await AnomaliesAsync(ct);
        var top = (await ProductScoresAsync(30, ct)).Take(3).ToList();
        var stockouts = forecast.Count(f => f.DaysOfCover is < 7);
        string T(string en, string fr, string ar) => lang switch { "fr" => fr, "ar" => ar, _ => en };
        var highlights = new List<string>
        {
            T($"Net sales over the last 30 days: {trend.CurrentPeriod:N2} ({trend.ChangePercent:+0.#;-0.#;0}% vs previous 30 days).",
              $"Ventes nettes sur 30 jours : {trend.CurrentPeriod:N2} ({trend.ChangePercent:+0.#;-0.#;0} % par rapport aux 30 jours précédents).",
              $"صافي المبيعات خلال آخر 30 يوماً: {trend.CurrentPeriod:N2} ({trend.ChangePercent:+0.#;-0.#;0}% مقارنة بالفترة السابقة)."),
        };
        if (top.Count > 0) highlights.Add(T($"Best performing products: {string.Join(", ", top.Select(t => t.Name))}.",
            $"Produits les plus performants : {string.Join(", ", top.Select(t => t.Name))}.", $"أفضل المنتجات أداءً: {string.Join("، ", top.Select(t => t.Name))}."));
        var warnings = new List<string>();
        if (stockouts > 0) warnings.Add(T($"{stockouts} product(s) may run out within 7 days at the current sales pace.",
            $"{stockouts} produit(s) risquent la rupture sous 7 jours au rythme actuel.", $"{stockouts} منتج(ات) قد تنفد خلال 7 أيام بوتيرة البيع الحالية."));
        if (anomalies.Count > 0) warnings.Add(T($"{anomalies.Count} unusual transaction(s) need review.", $"{anomalies.Count} transaction(s) inhabituelle(s) à vérifier.", $"{anomalies.Count} معاملة غير اعتيادية تحتاج للمراجعة."));
        if (ai.IsConfigured && await ai.SummarizeAsync(lang, highlights.Concat(warnings).ToList(), ct) is { } extra) highlights.Add(extra);
        return new BusinessSummaryDto(lang, highlights, warnings, T("Statistical estimates from historical data; not guarantees.",
            "Estimations statistiques issues de l'historique ; aucune garantie.", "تقديرات إحصائية مبنية على البيانات السابقة وليست ضمانات."));
    }
}
