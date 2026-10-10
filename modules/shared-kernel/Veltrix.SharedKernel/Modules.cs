using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Veltrix.SharedKernel;

/// <summary>Contract each module implements so the host can compose the modular monolith.</summary>
public interface IModule
{
    string Name { get; }
    void Register(IServiceCollection services);
    void MapEndpoints(IEndpointRouteBuilder app);
}

public static class EndpointExtensions
{
    public const string PermissionPolicyPrefix = "perm:";

    /// <summary>Require an authenticated user holding the given permission (enforced server-side).</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(PermissionPolicyPrefix + permission);

    public static RouteGroupBuilder MapModuleGroup(this IEndpointRouteBuilder app, string prefix, string tag) =>
        app.MapGroup("/api/v1/" + prefix).WithTags(tag).RequireAuthorization();
}

/// <summary>Data used to initialize a new company. Passwords are hashed by the identity module and never stored raw.</summary>
public sealed record OnboardingContext(
    Guid TenantId, Guid HeadOfficeBranchId, Guid DefaultWarehouseId, string CompanyName, string Currency, string Locale,
    string OwnerEmail, string OwnerFullName, string OwnerPassword, string? OwnerLanguage, bool ApplyAlgeriaTaxTemplate);

/// <summary>Modules implement this to seed tenant-specific data (roles, chart of accounts, units...) during onboarding.
/// All contributors run inside one database transaction.</summary>
public interface IOnboardingContributor
{
    int Order { get; }
    Task ContributeAsync(OnboardingContext context, CancellationToken ct);
}

public static class FeatureEndpointExtensions
{
    /// <summary>Require the tenant's plan to include a feature. Enforced server-side for every request.</summary>
    public static TBuilder RequireFeature<TBuilder>(this TBuilder builder, string feature) where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var tenant = ctx.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (tenant.TenantId is { } tid && !tenant.IsPlatformAdmin)
            {
                var checker = ctx.HttpContext.RequestServices.GetRequiredService<IEntitlementChecker>();
                if (!await checker.HasFeatureAsync(tid, feature, ctx.HttpContext.RequestAborted))
                    throw new PaymentRequiredException("entitlement.missing", $"Your plan does not include the '{feature}' module.");
            }
            return await next(ctx);
        });
        return builder;
    }
}
