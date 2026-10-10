namespace Veltrix.Contracts.Reporting;

/// <summary>Generic tabular report used for on-screen display and export (CSV, Excel, PDF, print).</summary>
public sealed record ReportTable(string Key, string Title, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows,
    IReadOnlyDictionary<string, object?> Summary, DateTimeOffset GeneratedAt, IReadOnlyDictionary<string, string?> Parameters);
public sealed record ReportColumn(string Key, string Title, string Type);

public sealed record DashboardDto(decimal SalesToday, decimal SalesMonth, decimal SalesPreviousMonth, int TransactionsToday, decimal AverageBasket,
    decimal GrossMarginMonth, int LowStockItems, decimal ReceivablesOutstanding, decimal PayablesOutstanding, int PendingApprovals,
    IReadOnlyList<SeriesPoint> Last30Days, IReadOnlyList<NamedValue> TopProducts, IReadOnlyList<NamedValue> SalesByBranch);
public sealed record SeriesPoint(DateOnly Date, decimal Value);
public sealed record NamedValue(string Name, decimal Value);

public sealed record ScheduledReportDto(Guid Id, string ReportKey, string Frequency, string Format, IReadOnlyList<string> Recipients, string? ParametersJson,
    DateTimeOffset? LastRunAt, string? LastStatus, bool IsActive);
public sealed record UpsertScheduledReportRequest(string ReportKey, string Frequency, string Format, IReadOnlyList<string> Recipients, string? ParametersJson, bool IsActive);

// ---- Intelligence ----
public sealed record TrendDto(IReadOnlyList<SeriesPoint> Daily, IReadOnlyList<SeriesPoint> MovingAverage7, decimal SlopePerDay, decimal CurrentPeriod,
    decimal PreviousPeriod, decimal ChangePercent, string Direction, string Method);
public sealed record StockForecastDto(Guid ProductId, string Sku, string Name, Guid WarehouseId, decimal OnHand, decimal AverageDailySales, decimal? DaysOfCover,
    DateOnly? ProjectedStockout, decimal RecommendedOrderQuantity, string Confidence);
public sealed record ProductScoreDto(Guid ProductId, string Sku, string Name, decimal Revenue, decimal Margin, decimal MarginPercent, decimal UnitsSold, int Score, string Class);
public sealed record CustomerPatternDto(Guid CustomerId, string Name, int Purchases, decimal TotalSpent, decimal AverageBasket, double? AverageDaysBetween,
    DateTimeOffset? LastPurchase, DateTimeOffset? ExpectedNextPurchase, bool Overdue);
public sealed record AnomalyDto(string Kind, string Reference, DateTimeOffset At, decimal Value, decimal Expected, double Score, string Explanation);
public sealed record BusinessSummaryDto(string Language, IReadOnlyList<string> Highlights, IReadOnlyList<string> Warnings, string Disclaimer);
