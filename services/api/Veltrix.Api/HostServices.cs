using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Veltrix.Modules.Identity;
using Veltrix.Modules.Tenants;
using Veltrix.SharedKernel;

namespace Veltrix.Api;

/// <summary>Cross-cutting host services not owned by a single module.</summary>
public static class HostServices
{
    public static void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<IPasswordResetNotifier, LoggingPasswordResetNotifier>();
    }

    public static void MapEndpoints(IEndpointRouteBuilder app) { }
}

public sealed class BasicTenantStatusProvider(CompanyStatusReader reader, IMemoryCache cache) : ITenantStatusProvider
{
    public async Task<TenantAccessStatus> GetStatusAsync(Guid tenantId, CancellationToken ct = default) =>
        await cache.GetOrCreateAsync("tenant-status:" + tenantId, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            return await reader.GetAsync(tenantId, ct) switch
            {
                null => TenantAccessStatus.NotFound,
                CompanyStatus.Suspended or CompanyStatus.Closed => TenantAccessStatus.Suspended,
                _ => TenantAccessStatus.Active,
            };
        });

    public void Invalidate(Guid tenantId) => cache.Remove("tenant-status:" + tenantId);
}

/// <summary>Fallback when email is not configured: records that a reset was requested without logging the token.</summary>
public sealed class LoggingPasswordResetNotifier(ILogger<LoggingPasswordResetNotifier> logger) : IPasswordResetNotifier
{
    public Task SendResetAsync(User user, string token, CancellationToken ct)
    {
        logger.LogWarning("Password reset requested for user {UserId} but no email channel is configured; an administrator must reset the password.", user.Id);
        return Task.CompletedTask;
    }
}
