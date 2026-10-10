using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Veltrix.Contracts.Reporting;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Reporting;

public sealed class ScheduledReport : TenantEntity
{
    public string ReportKey { get; set; } = "";
    /// <summary>daily, weekly (Sunday), monthly (1st).</summary>
    public string Frequency { get; set; } = "daily";
    public string Format { get; set; } = "xlsx";
    public string Recipients { get; set; } = "";
    public string? ParametersJson { get; set; }
    public Guid OwnerUserId { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public string? LastStatus { get; set; }
    public bool IsActive { get; set; } = true;
}

internal sealed class ScheduledReportConfig : IEntityTypeConfiguration<ScheduledReport>
{
    public void Configure(EntityTypeBuilder<ScheduledReport> b)
    {
        b.ToTable("scheduled_reports", "reporting");
        b.Property(x => x.ReportKey).HasMaxLength(60);
        b.Property(x => x.Frequency).HasMaxLength(20);
        b.Property(x => x.Format).HasMaxLength(10);
        b.Property(x => x.Recipients).HasMaxLength(2000);
        b.Property(x => x.ParametersJson).HasMaxLength(2000);
        b.Property(x => x.LastStatus).HasMaxLength(300);
    }
}

public sealed class ScheduledReportService(DbContext db, ITenantContext ctx)
{
    public async Task<IReadOnlyList<ScheduledReportDto>> ListAsync(CancellationToken ct) =>
        (await db.Set<ScheduledReport>().OrderBy(r => r.ReportKey).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<ScheduledReportDto> UpsertAsync(Guid? id, UpsertScheduledReportRequest req, CancellationToken ct)
    {
        new Validator().Require(ReportingService.Catalog.ContainsKey(req.ReportKey), "reportKey", "Unknown report.")
            .Require(req.Frequency is "daily" or "weekly" or "monthly", "frequency", "daily, weekly or monthly.")
            .Require(req.Format is "csv" or "xlsx" or "pdf" or "html", "format", "csv, xlsx, pdf or html.")
            .Require(req.Recipients is { Count: > 0 and <= 20 } && req.Recipients.All(r => r.Contains('@') && r.Length <= 256), "recipients", "1-20 valid emails.").ThrowIfInvalid();
        ctx.EnsurePermission(ReportingService.Catalog[req.ReportKey].Permission);
        var r = id is { } rid ? await db.Set<ScheduledReport>().SingleOrDefaultAsync(x => x.Id == rid, ct) ?? throw new NotFoundException("schedule.not_found", "Not found.")
            : db.Add(new ScheduledReport { TenantId = ctx.RequireTenant(), OwnerUserId = ctx.RequireUser() }).Entity;
        r.ReportKey = req.ReportKey; r.Frequency = req.Frequency; r.Format = req.Format; r.Recipients = string.Join(';', req.Recipients); r.ParametersJson = req.ParametersJson; r.IsActive = req.IsActive;
        await db.SaveChangesAsync(ct);
        return ToDto(r);
    }

    public static ScheduledReportDto ToDto(ScheduledReport r) => new(r.Id, r.ReportKey, r.Frequency, r.Format, r.Recipients.Split(';', StringSplitOptions.RemoveEmptyEntries),
        r.ParametersJson, r.LastRunAt, r.LastStatus, r.IsActive);
}

public sealed class ReportingModule : IModule
{
    public string Name => "reporting";

    public void Register(IServiceCollection services)
    {
        services.AddScoped<ReportingService>();
        services.AddScoped<IntelligenceService>();
        services.AddScoped<ScheduledReportService>();
        services.AddSingleton<IAiInsightProvider, NullAiInsightProvider>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var g = app.MapModuleGroup("reports", "Reports").RequireFeature(Features.Reports);
        g.MapGet("/", (ITenantContext ctx) => ReportingService.Catalog.Where(c => ctx.HasPermission(c.Value.Permission)).Select(c => new { key = c.Key, title = c.Value.Title }));
        g.MapGet("/dashboard", (ReportingService s, CancellationToken ct) => s.DashboardAsync(ct));
        g.MapGet("/run/{key}", async (string key, DateOnly from, DateOnly to, Guid? branchId, Guid? cashierId, Guid? categoryId, Guid? employeeId, string? groupBy,
            string? format, ReportingService s, CancellationToken ct) =>
        {
            var table = await s.RunAsync(key, new ReportFilter(from, to, branchId, cashierId, categoryId, employeeId, groupBy), ct);
            if (format is null or "json") return Results.Ok(table);
            var (content, type, ext) = ReportExporters.Export(table, format);
            return Results.File(content, type, $"{key}-{from:yyyyMMdd}-{to:yyyyMMdd}.{ext}");
        });
        g.MapGet("/schedules", (ScheduledReportService s, CancellationToken ct) => s.ListAsync(ct));
        g.MapPost("/schedules", (UpsertScheduledReportRequest r, ScheduledReportService s, CancellationToken ct) => s.UpsertAsync(null, r, ct));
        g.MapPut("/schedules/{id:guid}", (Guid id, UpsertScheduledReportRequest r, ScheduledReportService s, CancellationToken ct) => s.UpsertAsync(id, r, ct));

        var ai = app.MapModuleGroup("intelligence", "Intelligence").RequireFeature(Features.Intelligence).RequirePermission(Permissions.ReportsSales);
        ai.MapGet("/sales-trend", (int? days, Guid? branchId, IntelligenceService s, CancellationToken ct) => s.SalesTrendAsync(days ?? 30, branchId, ct));
        ai.MapGet("/stock-forecast", (int? lookbackDays, int? leadTimeDays, int? safetyDays, IntelligenceService s, CancellationToken ct) =>
            s.StockForecastAsync(lookbackDays ?? 28, leadTimeDays ?? 7, safetyDays ?? 7, ct)).RequirePermission(Permissions.InventoryView);
        ai.MapGet("/product-scores", (int? days, IntelligenceService s, CancellationToken ct) => s.ProductScoresAsync(days ?? 90, ct));
        ai.MapGet("/customer-patterns", (IntelligenceService s, CancellationToken ct) => s.CustomerPatternsAsync(ct)).RequirePermission(Permissions.CustomersView);
        ai.MapGet("/anomalies", (IntelligenceService s, CancellationToken ct) => s.AnomaliesAsync(ct)).RequirePermission(Permissions.SalesApprove);
        ai.MapGet("/summary", (string? lang, IntelligenceService s, CancellationToken ct) => s.SummaryAsync(lang ?? "en", ct));
    }
}
