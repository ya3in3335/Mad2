using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Veltrix.Modules.Identity;
using Veltrix.SharedKernel;

namespace Veltrix.Api.Infrastructure;

/// <summary>Populates the request-scoped <see cref="TenantContext"/> from validated JWT claims and assigns a correlation id.</summary>
public sealed class TenantContextMiddleware(RequestDelegate next)
{
    public const string CorrelationHeader = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext http, TenantContext ctx)
    {
        var correlation = http.Request.Headers[CorrelationHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(correlation) || correlation.Length > 64) correlation = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        ctx.CorrelationId = correlation;
        http.Items[CorrelationHeader] = correlation;
        http.Response.Headers[CorrelationHeader] = correlation;

        var user = http.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            ctx.IsAuthenticated = true;
            ctx.UserId = Guid.TryParse(user.FindFirstValue("sub"), out var uid) ? uid : null;
            ctx.TenantId = Guid.TryParse(user.FindFirstValue(VeltrixClaims.Tenant), out var tid) ? tid : null;
            ctx.IsPlatformAdmin = user.FindFirstValue(VeltrixClaims.PlatformAdmin) == "1";
            ctx.DeviceId = Guid.TryParse(user.FindFirstValue(VeltrixClaims.Device), out var did) ? did : null;
            ctx.Permissions = user.FindAll(VeltrixClaims.Permission).Select(c => c.Value).ToHashSet();
            ctx.AllowedBranches = user.HasClaim(VeltrixClaims.AllBranches, "1")
                ? null
                : user.FindAll(VeltrixClaims.Branch).Select(c => Guid.Parse(c.Value)).ToHashSet();
        }
        using (http.RequestServices.GetRequiredService<ILogger<TenantContextMiddleware>>()
                   .BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = correlation, ["TenantId"] = ctx.TenantId, ["UserId"] = ctx.UserId }))
        {
            await next(http);
        }
    }
}

/// <summary>Rejects tenant requests when the company is suspended; expired subscriptions are read-only.</summary>
public sealed class TenantAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, ITenantContext ctx, ITenantStatusProvider status)
    {
        if (ctx.TenantId is { } tid && !ctx.IsPlatformAdmin)
        {
            var s = await status.GetStatusAsync(tid, http.RequestAborted);
            var isRead = HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method);
            // Telemetry (heartbeat, error reports) stays available to read-only (expired) tenants so the vendor keeps visibility.
            var isTelemetry = http.Request.Path.StartsWithSegments("/api/v1/devices/heartbeat") || http.Request.Path.StartsWithSegments("/api/v1/diagnostics/errors");
            if (!s.AllowsAccess() && !((isRead || isTelemetry) && s.AllowsReadOnly()) && !http.Request.Path.StartsWithSegments("/api/v1/auth"))
            {
                var code = s == TenantAccessStatus.Expired ? "subscription.expired" : "tenant.suspended";
                var msg = s == TenantAccessStatus.Expired
                    ? "Your subscription has expired. Data remains available read-only; renew to continue operating."
                    : "Your company account is suspended. Contact your vendor.";
                throw s == TenantAccessStatus.Expired ? new PaymentRequiredException(code, msg) : new ForbiddenException(code, msg);
            }
        }
        await next(http);
    }
}

/// <summary>Maps exceptions to RFC 7807 problem responses without leaking internals.</summary>
public sealed class ProblemExceptionHandler(ILogger<ProblemExceptionHandler> logger, IHostEnvironment env) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception ex, CancellationToken ct)
    {
        var correlation = http.Items[TenantContextMiddleware.CorrelationHeader] as string;
        ProblemDetails problem;
        switch (ex)
        {
            case DomainException d:
                problem = new ProblemDetails { Status = d.StatusCode, Title = d.Message, Type = $"https://docs.veltrix.app/errors/{d.Code}" };
                problem.Extensions["code"] = d.Code;
                if (d.Errors is not null) problem.Extensions["errors"] = d.Errors;
                if (d.StatusCode >= 500) logger.LogError(ex, "Domain error {Code}", d.Code);
                else logger.LogInformation("Request rejected {Code}: {Message}", d.Code, d.Message);
                break;
            case DbUpdateConcurrencyException:
                problem = new ProblemDetails { Status = 409, Title = "The record was modified by someone else. Reload and retry.", Type = "https://docs.veltrix.app/errors/concurrency" };
                problem.Extensions["code"] = "concurrency.conflict";
                break;
            case BadHttpRequestException bad:
                problem = new ProblemDetails { Status = bad.StatusCode, Title = "Malformed request.", Type = "https://docs.veltrix.app/errors/bad-request" };
                problem.Extensions["code"] = "request.malformed";
                break;
            default:
                logger.LogError(ex, "Unhandled exception");
                problem = new ProblemDetails { Status = 500, Title = "An unexpected error occurred.", Type = "https://docs.veltrix.app/errors/internal" };
                problem.Extensions["code"] = "internal.error";
                if (env.IsDevelopment() || env.IsEnvironment("Testing")) problem.Detail = ex.ToString();
                break;
        }
        problem.Extensions["correlationId"] = correlation;
        http.Response.StatusCode = problem.Status ?? 500;
        await http.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", ct);
        return true;
    }
}

/// <summary>Builds "perm:xyz" policies on demand.</summary>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(EndpointExtensions.PermissionPolicyPrefix, StringComparison.Ordinal))
            return await base.GetPolicyAsync(policyName);
        var permission = policyName[EndpointExtensions.PermissionPolicyPrefix.Length..];
        return new AuthorizationPolicyBuilder().RequireAuthenticatedUser()
            .RequireAssertion(c => c.User.HasClaim(VeltrixClaims.Permission, permission) || c.User.HasClaim(VeltrixClaims.Permission, Permissions.All)
                                   && !permission.StartsWith("platform.", StringComparison.Ordinal))
            .Build();
    }
}
