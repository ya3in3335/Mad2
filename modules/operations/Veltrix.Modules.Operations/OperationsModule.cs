using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Veltrix.Contracts.Operations;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Operations;

public sealed class OperationsModule : IModule
{
    /// <summary>Rate limiter policy (defined by the host) applied to client telemetry/error ingestion.</summary>
    public const string TelemetryRateLimitPolicy = "telemetry";

    public string Name => "operations";

    public void Register(IServiceCollection services)
    {
        services.AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection(OperationsOptions.Section).Get<OperationsOptions>() ?? new OperationsOptions());
        services.AddScoped<DiagnosticsService>();
        services.AddScoped<DeviceOpsService>();
        services.AddScoped<OwnerDashboardService>();
        services.AddSingleton<IDiagnosticsRecorder, DiagnosticsRecorder>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        // ---- Client (company devices/users) ----
        var dev = app.MapModuleGroup("devices", "Devices");
        dev.MapPost("/heartbeat", (DeviceHeartbeatRequest r, DeviceOpsService s, CancellationToken ct) => s.HeartbeatAsync(r, ct))
            .RequireRateLimiting(TelemetryRateLimitPolicy);
        dev.MapPost("/diagnostic-requests/{id:guid}/report", async (Guid id, DiagnosticReportUploadRequest r, DeviceOpsService s, CancellationToken ct) =>
        {
            await s.UploadReportAsync(id, r, ct);
            return Results.NoContent();
        }).RequireRateLimiting(TelemetryRateLimitPolicy);
        app.MapModuleGroup("diagnostics", "Diagnostics").MapPost("/errors", (ErrorReportBatchRequest r, DiagnosticsService s, CancellationToken ct) => s.IngestAsync(r, ct))
            .RequireRateLimiting(TelemetryRateLimitPolicy);

        // ---- Vendor (platform) ----
        var p = app.MapGroup("/api/v1/platform").WithTags("Platform").RequireAuthorization();
        p.MapGet("/dashboard/admin", (int? onlineWithinMinutes, OwnerDashboardService s, CancellationToken ct) => s.GetAsync(onlineWithinMinutes, ct))
            .RequirePermission(Permissions.PlatformTenants);
        p.MapGet("/tenants/{id:guid}/overview", (Guid id, OwnerDashboardService s, CancellationToken ct) => s.TenantAsync(id, ct))
            .RequirePermission(Permissions.PlatformTenants);

        var fleet = p.MapGroup("/fleet/devices").RequirePermission(Permissions.PlatformLicenses);
        fleet.MapGet("/", ([AsParameters] PageQuery q, Guid? tenantId, string? platform, string? status, bool? online, int? onlineWithinMinutes, string? appVersion,
            DeviceOpsService s, CancellationToken ct) => s.ListAsync(q, tenantId, platform, status, online, onlineWithinMinutes, appVersion, ct));
        fleet.MapGet("/{id:guid}", (Guid id, DeviceOpsService s, CancellationToken ct) => s.GetAsync(id, ct));
        fleet.MapPost("/{id:guid}/deactivate", (Guid id, DeviceActionRequest r, DeviceOpsService s, CancellationToken ct) => s.DeactivateAsync(id, r.Reason, false, ct))
            .RequireStepUp();
        fleet.MapPost("/{id:guid}/replace", (Guid id, DeviceActionRequest r, DeviceOpsService s, CancellationToken ct) => s.DeactivateAsync(id, r.Reason, true, ct))
            .RequireStepUp();
        fleet.MapPost("/{id:guid}/reassign", (Guid id, ReassignDeviceRequest r, DeviceOpsService s, CancellationToken ct) => s.ReassignAsync(id, r, ct))
            .RequireStepUp();
        fleet.MapPost("/{id:guid}/diagnostic-requests", (Guid id, CreateDiagnosticRequestRequest r, DeviceOpsService s, CancellationToken ct) => s.CreateRequestAsync(id, r, ct));

        var diag = p.MapGroup("/diagnostic-requests").RequirePermission(Permissions.PlatformLicenses);
        diag.MapGet("/{id:guid}", (Guid id, DeviceOpsService s, CancellationToken ct) => s.ReportAsync(id, ct));
        diag.MapPost("/{id:guid}/cancel", (Guid id, DeviceOpsService s, CancellationToken ct) => s.CancelRequestAsync(id, ct));

        var inc = p.MapGroup("/incidents").RequirePermission(Permissions.PlatformSupport);
        inc.MapGet("/", ([AsParameters] PageQuery q, string? status, string? category, string? severity, Guid? tenantId, string? platform, string? app, string? source,
            DateTimeOffset? since, DiagnosticsService s, CancellationToken ct) => s.ListAsync(q, status, category, severity, tenantId, platform, app, source, since, ct));
        inc.MapGet("/groups", (string? status, string? category, int? take, DiagnosticsService s, CancellationToken ct) => s.GroupsAsync(status, category, take ?? 100, ct));
        inc.MapGet("/{id:guid}", (Guid id, DiagnosticsService s, CancellationToken ct) => s.GetAsync(id, ct));
        inc.MapPost("/{id:guid}/status", (Guid id, UpdateIncidentStatusRequest r, DiagnosticsService s, CancellationToken ct) => s.UpdateStatusAsync(id, r, ct));
    }
}
