using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Veltrix.Contracts.Tenants;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Tenants;

public sealed class TenantsModule : IModule
{
    public string Name => "tenants";

    public void Register(IServiceCollection services)
    {
        services.AddScoped<CompanyService>();
        services.AddScoped<BranchService>();
        services.AddScoped<IBranchDirectory, BranchDirectory>();
        services.AddScoped<SettingsService>();
        services.AddScoped<ISettingsService>(sp => sp.GetRequiredService<SettingsService>());
        services.AddScoped<ISequenceService, SequenceService>();
        services.AddScoped<OnboardingService>();
        services.AddScoped<TenantAdminService>();
        services.AddScoped<CompanyStatusReader>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/onboarding/complete", (CompleteOnboardingRequest req, OnboardingService svc, CancellationToken ct) =>
            svc.CompleteAsync(req, ct)).WithTags("Onboarding").AllowAnonymous().RequireRateLimiting("auth");

        var company = app.MapModuleGroup("company", "Company");
        company.MapGet("/", (CompanyService svc, CancellationToken ct) => svc.GetAsync(ct));
        company.MapPut("/", (UpdateCompanyRequest req, CompanyService svc, CancellationToken ct) => svc.UpdateAsync(req, ct))
            .RequirePermission(Permissions.CompanyManage);

        var branches = app.MapModuleGroup("branches", "Company");
        branches.MapGet("/", (BranchService svc, CancellationToken ct) => svc.ListAsync(ct));
        branches.MapPost("/", (UpsertBranchRequest req, BranchService svc, CancellationToken ct) => svc.UpsertAsync(null, req, ct))
            .RequirePermission(Permissions.BranchesManage);
        branches.MapPut("/{id:guid}", (Guid id, UpsertBranchRequest req, BranchService svc, CancellationToken ct) => svc.UpsertAsync(id, req, ct))
            .RequirePermission(Permissions.BranchesManage);

        var wh = app.MapModuleGroup("warehouses", "Company");
        wh.MapGet("/", (BranchService svc, CancellationToken ct) => svc.ListWarehousesAsync(ct));
        wh.MapPost("/", (UpsertWarehouseRequest req, BranchService svc, CancellationToken ct) => svc.UpsertWarehouseAsync(null, req, ct))
            .RequirePermission(Permissions.BranchesManage);
        wh.MapPut("/{id:guid}", (Guid id, UpsertWarehouseRequest req, BranchService svc, CancellationToken ct) => svc.UpsertWarehouseAsync(id, req, ct))
            .RequirePermission(Permissions.BranchesManage);

        var settings = app.MapModuleGroup("settings", "Company");
        settings.MapGet("/", (SettingsService svc, CancellationToken ct) => svc.ListAsync(ct));
        settings.MapPut("/{key}", async (string key, SettingDto body, SettingsService svc, CancellationToken ct) =>
        {
            await svc.SetRawAsync(key, body.Value, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.SettingsManage);

        var admin = app.MapGroup("/api/v1/platform/tenants").WithTags("Platform").RequirePermission(Permissions.PlatformTenants);
        admin.MapPost("/", (CreateTenantRequest req, TenantAdminService svc, CancellationToken ct) => svc.CreateAsync(req, ct));
        admin.MapPost("/{id:guid}/suspend", async (Guid id, SuspendTenantRequest req, TenantAdminService svc, CancellationToken ct) =>
        {
            await svc.SetSuspendedAsync(id, true, req.Reason, ct);
            return Results.NoContent();
        }).RequireStepUp();
        admin.MapPost("/{id:guid}/reactivate", async (Guid id, TenantAdminService svc, CancellationToken ct) =>
        {
            await svc.SetSuspendedAsync(id, false, null, ct);
            return Results.NoContent();
        }).RequireStepUp();
    }
}
