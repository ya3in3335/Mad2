using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Veltrix.Contracts.Identity;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Identity;

public sealed class IdentityModule : IModule
{
    public string Name => "identity";

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<JwtTokenService>();
        services.AddSingleton(sp => new PasswordHasher(sp.GetRequiredService<IOptions<AuthOptions>>().Value.PasswordIterations));
        services.AddScoped<AuthService>();
        services.AddScoped<UserService>();
        services.AddScoped<IOnboardingContributor, IdentityOnboardingContributor>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/v1/auth").WithTags("Authentication");
        auth.MapPost("/login", async (LoginRequest req, AuthService svc, HttpContext http, CancellationToken ct) =>
            Results.Ok(await svc.LoginAsync(req, Client(http), ct))).AllowAnonymous().RequireRateLimiting("auth");
        auth.MapPost("/refresh", async (RefreshRequest req, AuthService svc, HttpContext http, CancellationToken ct) =>
            Results.Ok(await svc.RefreshAsync(req.RefreshToken, Client(http), ct))).AllowAnonymous().RequireRateLimiting("auth");
        auth.MapPost("/logout", async (LogoutRequest req, AuthService svc, CancellationToken ct) =>
        {
            await svc.LogoutAsync(req.RefreshToken, ct);
            return Results.NoContent();
        }).AllowAnonymous();
        auth.MapPost("/password-reset/request", async (PasswordResetRequest req, AuthService svc, CancellationToken ct) =>
        {
            await svc.RequestPasswordResetAsync(req.Email, ct);
            return Results.Accepted();
        }).AllowAnonymous().RequireRateLimiting("auth");
        auth.MapPost("/password-reset/confirm", async (PasswordResetConfirm req, AuthService svc, CancellationToken ct) =>
        {
            await svc.ConfirmPasswordResetAsync(req, ct);
            return Results.NoContent();
        }).AllowAnonymous().RequireRateLimiting("auth");

        var me = app.MapGroup("/api/v1/me").WithTags("Authentication").RequireAuthorization();
        me.MapGet("/", (AuthService svc, ITenantContext ctx, CancellationToken ct) => svc.GetProfileAsync(ctx.RequireUser(), ct));
        me.MapPost("/password", async (ChangePasswordRequest req, AuthService svc, ITenantContext ctx, CancellationToken ct) =>
        {
            await svc.ChangePasswordAsync(ctx.RequireUser(), req, ct);
            return Results.NoContent();
        });
        me.MapGet("/sessions", (AuthService svc, ITenantContext ctx, HttpContext http, CancellationToken ct) =>
            svc.ListSessionsAsync(ctx.RequireUser(), http.Request.Headers["X-Refresh-Token"].FirstOrDefault(), ct));
        me.MapDelete("/sessions/{familyId:guid}", async (Guid familyId, AuthService svc, ITenantContext ctx, CancellationToken ct) =>
        {
            await svc.RevokeSessionAsync(ctx.RequireUser(), familyId, ct);
            return Results.NoContent();
        });
        me.MapPost("/mfa/setup", (AuthService svc, ITenantContext ctx, CancellationToken ct) => svc.BeginMfaSetupAsync(ctx.RequireUser(), ct));
        me.MapPost("/mfa/enable", async (MfaVerifyRequest req, AuthService svc, ITenantContext ctx, CancellationToken ct) =>
        {
            await svc.EnableMfaAsync(ctx.RequireUser(), req.Code, ct);
            return Results.NoContent();
        });
        me.MapPost("/mfa/disable", async (MfaVerifyRequest req, AuthService svc, ITenantContext ctx, CancellationToken ct) =>
        {
            await svc.DisableMfaAsync(ctx.RequireUser(), req.Code, ct);
            return Results.NoContent();
        });
        me.MapPost("/pin", async (PinRequest req, UserService svc, ITenantContext ctx, CancellationToken ct) =>
        {
            await svc.SetPinAsync(ctx.RequireUser(), req.Pin, ct);
            return Results.NoContent();
        });

        var users = app.MapModuleGroup("users", "Users").RequirePermission(Permissions.UsersManage);
        users.MapGet("/", ([AsParameters] PageQuery q, UserService svc, CancellationToken ct) => svc.ListAsync(q, ct));
        users.MapGet("/{id:guid}", (Guid id, UserService svc, CancellationToken ct) => svc.GetAsync(id, ct));
        users.MapPost("/", async (CreateUserRequest req, UserService svc, CancellationToken ct) =>
        {
            var u = await svc.CreateAsync(req, ct);
            return Results.Created($"/api/v1/users/{u.Id}", u);
        });
        users.MapPut("/{id:guid}", (Guid id, UpdateUserRequest req, UserService svc, CancellationToken ct) => svc.UpdateAsync(id, req, ct));
        users.MapPost("/{id:guid}/reset-password", async (Guid id, AdminResetPasswordRequest req, UserService svc, CancellationToken ct) =>
        {
            await svc.AdminResetPasswordAsync(id, req.NewPassword, ct);
            return Results.NoContent();
        });

        var roles = app.MapModuleGroup("roles", "Users");
        roles.MapGet("/", (UserService svc, CancellationToken ct) => svc.ListRolesAsync(ct)).RequirePermission(Permissions.UsersManage);
        roles.MapGet("/permissions", () => Permissions.CompanyPermissions).RequirePermission(Permissions.RolesManage);
        roles.MapPost("/", (UpsertRoleRequest req, UserService svc, CancellationToken ct) => svc.UpsertRoleAsync(null, req, ct))
            .RequirePermission(Permissions.RolesManage);
        roles.MapPut("/{id:guid}", (Guid id, UpsertRoleRequest req, UserService svc, CancellationToken ct) => svc.UpsertRoleAsync(id, req, ct))
            .RequirePermission(Permissions.RolesManage);
    }

    private static ClientInfo Client(HttpContext http) =>
        new(http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString());
}

public sealed record PinRequest(string Pin);
