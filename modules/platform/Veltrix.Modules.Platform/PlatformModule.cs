using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Veltrix.Contracts.Platform;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Platform;

public sealed class PlatformModule : IModule
{
    public string Name => "platform";

    public void Register(IServiceCollection services) => services.AddScoped<PlatformService>();

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var p = app.MapGroup("/api/v1/platform").WithTags("Platform").RequireAuthorization();
        p.MapGet("/dashboard", (PlatformService s, CancellationToken ct) => s.DashboardAsync(ct)).RequirePermission(Permissions.PlatformTenants);
        p.MapGet("/tenants", ([AsParameters] PageQuery q, string? status, PlatformService s, CancellationToken ct) => s.TenantsAsync(q, status, ct)).RequirePermission(Permissions.PlatformTenants);
        p.MapGet("/tenants/{id:guid}", (Guid id, PlatformService s, CancellationToken ct) => s.TenantAsync(id, ct)).RequirePermission(Permissions.PlatformTenants);
        p.MapGet("/admins", (PlatformService s, CancellationToken ct) => s.AdminsAsync(ct)).RequirePermission(Permissions.PlatformAdmins);
        p.MapPost("/admins", (CreatePlatformAdminRequest r, PlatformService s, CancellationToken ct) => s.CreateAdminAsync(r, ct)).RequirePermission(Permissions.PlatformAdmins).RequireStepUp();
        p.MapPost("/admins/{id:guid}/disable", async (Guid id, PlatformService s, CancellationToken ct) => { await s.SetAdminActiveAsync(id, false, ct); return Results.NoContent(); })
            .RequirePermission(Permissions.PlatformAdmins).RequireStepUp();
        p.MapPost("/admins/{id:guid}/enable", async (Guid id, PlatformService s, CancellationToken ct) => { await s.SetAdminActiveAsync(id, true, ct); return Results.NoContent(); })
            .RequirePermission(Permissions.PlatformAdmins).RequireStepUp();
        p.MapGet("/support", (string? status, PlatformService s, CancellationToken ct) => s.CasesAsync(status, ct)).RequirePermission(Permissions.PlatformSupport);
        p.MapPost("/support/{id:guid}/reply", (Guid id, ReplySupportCaseRequest r, PlatformService s, CancellationToken ct) => s.ReplyAsync(id, r, ct)).RequirePermission(Permissions.PlatformSupport);
        p.MapGet("/audit", ([AsParameters] PageQuery q, Guid? tenantId, string? action, DateTimeOffset? from, DateTimeOffset? to, PlatformService s, CancellationToken ct) =>
            s.AuditAsync(q, tenantId, action, from, to, ct)).RequirePermission(Permissions.PlatformAudit);
        p.MapGet("/security-alerts", (int? days, PlatformService s, CancellationToken ct) => s.SecurityAlertsAsync(days ?? 7, ct)).RequirePermission(Permissions.PlatformAudit);
        p.MapGet("/health", (PlatformService s, CancellationToken ct) =>
            s.HealthAsync(typeof(PlatformModule).Assembly.GetName().Version?.ToString() ?? "0", ct)).RequirePermission(Permissions.PlatformConfig);
        p.MapGet("/backups", (PlatformService s, CancellationToken ct) => s.BackupsAsync(ct)).RequirePermission(Permissions.PlatformConfig);
        p.MapGet("/releases", (PlatformService s, CancellationToken ct) => s.ReleasesAsync(ct)).RequirePermission(Permissions.PlatformReleases);
        p.MapPost("/releases", (UpsertReleaseRequest r, PlatformService s, CancellationToken ct) => s.UpsertReleaseAsync(null, r, ct)).RequirePermission(Permissions.PlatformReleases);
        p.MapPut("/releases/{id:guid}", (Guid id, UpsertReleaseRequest r, PlatformService s, CancellationToken ct) => s.UpsertReleaseAsync(id, r, ct)).RequirePermission(Permissions.PlatformReleases);
        p.MapGet("/announcements", (PlatformService s, CancellationToken ct) => s.AnnouncementsAsync(false, ct)).RequirePermission(Permissions.PlatformConfig);
        p.MapPost("/announcements", (UpsertAnnouncementRequest r, PlatformService s, CancellationToken ct) => s.UpsertAnnouncementAsync(null, r, ct)).RequirePermission(Permissions.PlatformConfig);
        p.MapPut("/announcements/{id:guid}", (Guid id, UpsertAnnouncementRequest r, PlatformService s, CancellationToken ct) => s.UpsertAnnouncementAsync(id, r, ct)).RequirePermission(Permissions.PlatformConfig);
        p.MapGet("/settings", (PlatformService s, CancellationToken ct) => s.SettingsAsync(ct)).RequirePermission(Permissions.PlatformConfig);
        p.MapPut("/settings/{key}", async (string key, PlatformSettingDto body, PlatformService s, CancellationToken ct) => { await s.SetSettingAsync(key, body.Value, ct); return Results.NoContent(); })
            .RequirePermission(Permissions.PlatformConfig);

        // Company-facing endpoints.
        app.MapGet("/api/v1/releases/check", (string platform, string currentVersion, string? channel, PlatformService s, CancellationToken ct) =>
            s.CheckUpdateAsync(platform, currentVersion, channel ?? "stable", ct)).WithTags("System").AllowAnonymous();
        var c = app.MapModuleGroup("support", "Support");
        c.MapGet("/", (PlatformService s, CancellationToken ct) => s.CasesAsync(null, ct)).RequirePermission(Permissions.CompanyManage);
        c.MapPost("/", (CreateSupportCaseRequest r, PlatformService s, CancellationToken ct) => s.OpenCaseAsync(r, ct)).RequirePermission(Permissions.CompanyManage);
        c.MapPost("/{id:guid}/reply", (Guid id, ReplySupportCaseRequest r, PlatformService s, CancellationToken ct) => s.ReplyAsync(id, r, ct)).RequirePermission(Permissions.CompanyManage);
        app.MapModuleGroup("announcements", "System").MapGet("/", (PlatformService s, CancellationToken ct) => s.AnnouncementsAsync(true, ct));
        app.MapModuleGroup("audit", "Company").MapGet("/", ([AsParameters] PageQuery q, string? action, DateTimeOffset? from, DateTimeOffset? to, PlatformService s, CancellationToken ct) =>
            s.AuditAsync(q, null, action, from, to, ct)).RequirePermission(Permissions.AuditView);
        app.MapModuleGroup("company", "Company").MapGet("/export", async (PlatformService s, ITenantContext ctx, CancellationToken ct) =>
            Results.File(await s.ExportTenantAsync(ctx.RequireTenant(), ct), "application/zip", $"veltrix-export-{DateTime.UtcNow:yyyyMMdd}.zip")).RequirePermission(Permissions.CompanyManage);
    }
}
