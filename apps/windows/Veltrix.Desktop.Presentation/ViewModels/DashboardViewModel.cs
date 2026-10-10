using CommunityToolkit.Mvvm.ComponentModel;
using Veltrix.Contracts.Reporting;

namespace Veltrix.Desktop.Presentation.ViewModels;

public sealed record Kpi(string TitleKey, string Title, decimal Value, string Display, string? Hint = null, bool Warning = false);

/// <summary>One bar of the 30-day chart; <see cref="Ratio"/> is the height relative to the best day (0..1).</summary>
public sealed record ChartBar(DateOnly Date, decimal Value, double Ratio, string Label);

public sealed record RankedItem(string Name, decimal Value, double Ratio);

public sealed partial class DashboardViewModel(DesktopServices s) : PageViewModel(s)
{
    public override string TitleKey => "nav.dashboard";

    [ObservableProperty] private IReadOnlyList<Kpi> _kpis = [];
    [ObservableProperty] private IReadOnlyList<ChartBar> _last30Days = [];
    [ObservableProperty] private IReadOnlyList<RankedItem> _topProducts = [];
    [ObservableProperty] private IReadOnlyList<RankedItem> _salesByBranch = [];
    [ObservableProperty] private decimal _monthChangePercent;
    [ObservableProperty] private string? _greeting;

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        var d = S.IsStandalone ? await S.Standalone.Store.DashboardAsync(ct) : await S.Api.GetAsync<DashboardDto>("/api/v1/reports/dashboard", ct);
        Apply(d);
        return true;
    }

    internal void Apply(DashboardDto d)
    {
        MonthChangePercent = d.SalesPreviousMonth == 0 ? 0 : Math.Round((d.SalesMonth - d.SalesPreviousMonth) / d.SalesPreviousMonth * 100m, 1);
        Kpis =
        [
            new("rep.salesToday", Loc.Get("rep.salesToday"), d.SalesToday, (d.SalesToday).ToString("N2", Loc.Culture)),
            new("rep.transactions", Loc.Get("rep.transactions"), d.TransactionsToday, (d.TransactionsToday).ToString("N0", Loc.Culture), Loc.Get("dk.dash.avgBasket") + " " + d.AverageBasket.ToString("N2", Loc.Culture)),
            new("rep.salesMonth", Loc.Get("rep.salesMonth"), d.SalesMonth, (d.SalesMonth).ToString("N2", Loc.Culture), (MonthChangePercent >= 0 ? "+" : "") + MonthChangePercent.ToString("N1", Loc.Culture) + " %"),
            new("rep.margin", Loc.Get("rep.margin"), d.GrossMarginMonth, (d.GrossMarginMonth).ToString("N2", Loc.Culture)),
            new("rep.receivables", Loc.Get("rep.receivables"), d.ReceivablesOutstanding, (d.ReceivablesOutstanding).ToString("N2", Loc.Culture)),
            new("dk.dash.payables", Loc.Get("dk.dash.payables"), d.PayablesOutstanding, (d.PayablesOutstanding).ToString("N2", Loc.Culture)),
            new("inv.lowStock", Loc.Get("inv.lowStock"), d.LowStockItems, (d.LowStockItems).ToString("N0", Loc.Culture), null, d.LowStockItems > 0),
            new("dk.dash.approvals", Loc.Get("dk.dash.approvals"), d.PendingApprovals, (d.PendingApprovals).ToString("N0", Loc.Culture), null, d.PendingApprovals > 0),
        ];
        Last30Days = Bars(d.Last30Days, Loc.Culture);
        TopProducts = Rank(d.TopProducts);
        SalesByBranch = Rank(d.SalesByBranch);
        var name = S.Session.User?.FullName;
        Greeting = string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public static IReadOnlyList<ChartBar> Bars(IReadOnlyList<SeriesPoint> points, IFormatProvider culture)
    {
        var max = points.Count == 0 ? 0 : points.Max(p => p.Value);
        return points.OrderBy(p => p.Date)
            .Select(p => new ChartBar(p.Date, p.Value, max <= 0 ? 0 : (double)(Math.Max(0, p.Value) / max), p.Date.ToString("dd/MM", culture) + "  " + p.Value.ToString("N2", culture)))
            .ToList();
    }

    public static IReadOnlyList<RankedItem> Rank(IReadOnlyList<NamedValue> values)
    {
        var max = values.Count == 0 ? 0 : values.Max(v => v.Value);
        return values.OrderByDescending(v => v.Value).Select(v => new RankedItem(v.Name, v.Value, max <= 0 ? 0 : (double)(v.Value / max))).ToList();
    }
}
