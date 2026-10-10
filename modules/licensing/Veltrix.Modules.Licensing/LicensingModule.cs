using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Veltrix.Contracts.Licensing;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Licensing;

public sealed class LicensingModule : IModule
{
    public string Name => "licensing";

    public void Register(IServiceCollection services)
    {
        services.AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection(LicensingOptions.Section).Get<LicensingOptions>() ?? new LicensingOptions());
        services.AddSingleton(sp => new LicenseSigner(sp.GetRequiredService<LicensingOptions>(), sp.GetRequiredService<IHostEnvironment>(), sp.GetRequiredService<IConfiguration>()));
        services.AddScoped<ActivationService>();
        services.AddScoped<LicenseAdminService>();
        services.AddScoped<LicenseTenantStatusProvider>();
        services.AddScoped<ITenantStatusProvider>(sp => sp.GetRequiredService<LicenseTenantStatusProvider>());
        services.AddScoped<IEntitlementChecker>(sp => sp.GetRequiredService<LicenseTenantStatusProvider>());
        services.AddScoped<IDeviceAuthorization>(sp => sp.GetRequiredService<LicenseTenantStatusProvider>());
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var dev = app.MapGroup("/api/v1/licensing").WithTags("Licensing");
        dev.MapGet("/public-key", (LicenseSigner s) => new { algorithm = "ES256", spki = s.PublicKeySpki, pem = s.PublicKeyPem }).AllowAnonymous();
        dev.MapPost("/challenge", (ChallengeRequest req, ActivationService svc, CancellationToken ct) => svc.CreateChallengeAsync(req, ct))
            .AllowAnonymous().RequireRateLimiting("auth");
        dev.MapPost("/activate", (ActivateRequest req, ActivationService svc, CancellationToken ct) => svc.ActivateAsync(req, ct))
            .AllowAnonymous().RequireRateLimiting("auth");
        dev.MapPost("/refresh", (RefreshCertificateRequest req, ActivationService svc, CancellationToken ct) => svc.RefreshAsync(req, ct))
            .AllowAnonymous().RequireRateLimiting("auth");
        dev.MapPost("/release", async (ReleaseDeviceRequest req, ActivationService svc, CancellationToken ct) =>
        {
            await svc.ReleaseAsync(req, ct);
            return Results.NoContent();
        }).AllowAnonymous().RequireRateLimiting("auth");

        var my = app.MapModuleGroup("licensing/my", "Licensing");
        my.MapGet("/", (LicenseAdminService svc, CancellationToken ct) => svc.MyLicensesAsync(ct));
        my.MapGet("/devices", ([AsParameters] PageQuery q, LicenseAdminService svc, CancellationToken ct) => svc.ListDevicesAsync(q, null, null, ct))
            .RequirePermission(Permissions.DevicesManage);
        my.MapPost("/devices/{id:guid}/revoke", async (Guid id, RevokeDeviceRequest req, LicenseAdminService svc, CancellationToken ct) =>
        {
            await svc.RevokeDeviceAsync(id, req.Reason, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.DevicesManage);
        my.MapGet("/{id:guid}/events", (Guid id, LicenseAdminService svc, CancellationToken ct) => svc.EventsAsync(id, ct))
            .RequirePermission(Permissions.DevicesManage);

        var admin = app.MapGroup("/api/v1/platform/licenses").WithTags("Platform").RequirePermission(Permissions.PlatformLicenses);
        admin.MapGet("/", ([AsParameters] PageQuery q, Guid? tenantId, LicenseAdminService svc, CancellationToken ct) => svc.ListAsync(q, tenantId, ct));
        admin.MapGet("/{id:guid}", (Guid id, LicenseAdminService svc, CancellationToken ct) => svc.GetAsync(id, ct));
        admin.MapPost("/", (CreateLicenseRequest req, LicenseAdminService svc, CancellationToken ct) => svc.CreateAsync(req, ct));
        admin.MapPut("/{id:guid}", (Guid id, UpdateLicenseRequest req, LicenseAdminService svc, CancellationToken ct) => svc.UpdateAsync(id, req, ct));
        admin.MapPost("/{id:guid}/extend", (Guid id, ExtendLicenseRequest req, LicenseAdminService svc, CancellationToken ct) => svc.ExtendAsync(id, req.Days, req.Reason, ct));
        admin.MapPost("/{id:guid}/suspend", (Guid id, LicenseStatusRequest req, LicenseAdminService svc, CancellationToken ct) => svc.SetStatusAsync(id, LicenseStatus.Suspended, req.Reason, ct)).RequireStepUp();
        admin.MapPost("/{id:guid}/reactivate", (Guid id, LicenseStatusRequest req, LicenseAdminService svc, CancellationToken ct) => svc.SetStatusAsync(id, LicenseStatus.Active, req.Reason, ct));
        admin.MapPost("/{id:guid}/revoke", (Guid id, LicenseStatusRequest req, LicenseAdminService svc, CancellationToken ct) => svc.SetStatusAsync(id, LicenseStatus.Revoked, req.Reason, ct)).RequireStepUp();
        admin.MapPost("/{id:guid}/transfer", (Guid id, TransferLicenseRequest req, LicenseAdminService svc, CancellationToken ct) => svc.TransferAsync(id, req, ct)).RequireStepUp();
        admin.MapGet("/{id:guid}/events", (Guid id, LicenseAdminService svc, CancellationToken ct) => svc.EventsAsync(id, ct));

        var devices = app.MapGroup("/api/v1/platform/devices").WithTags("Platform").RequirePermission(Permissions.PlatformLicenses);
        devices.MapGet("/", ([AsParameters] PageQuery q, Guid? tenantId, Guid? licenseId, LicenseAdminService svc, CancellationToken ct) =>
            svc.ListDevicesAsync(q, tenantId, licenseId, ct));
        devices.MapPost("/{id:guid}/revoke", async (Guid id, RevokeDeviceRequest req, LicenseAdminService svc, CancellationToken ct) =>
        {
            await svc.RevokeDeviceAsync(id, req.Reason, ct);
            return Results.NoContent();
        });
    }
}
