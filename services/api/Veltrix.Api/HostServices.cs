using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Veltrix.Modules.Identity;
using Veltrix.Modules.Tenants;
using Veltrix.SharedKernel;
using Veltrix.Workers;

namespace Veltrix.Api;

/// <summary>Cross-cutting host services not owned by a single module.</summary>
public static class HostServices
{
    public static void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddVeltrixWorkers(config);
        services.AddSingleton<IFileStorage>(sp => new LocalFileStorage(config["Storage:Root"] ?? Path.Combine(config["Veltrix:DataDirectory"] ?? Path.Combine(AppContext.BaseDirectory, ".veltrix-data"), "files")));
    }

    public static void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/platform/backups/run", async (BackupService backups, CancellationToken ct) =>
            Veltrix.Modules.Platform.PlatformService.ToDto(await backups.RunAsync(ct)))
            .WithTags("Platform").RequirePermission(Permissions.PlatformConfig).RequireStepUp();
        app.MapPost("/api/v1/platform/jobs/{name}/run", async (string name, JobScheduler scheduler, IEnumerable<IJob> jobs, CancellationToken ct) =>
        {
            var job = jobs.SingleOrDefault(j => j.Name == name) ?? throw new NotFoundException("job.not_found", "Unknown job.");
            await scheduler.RunJobAsync(job, ct);
            return Results.NoContent();
        }).WithTags("Platform").RequirePermission(Permissions.PlatformConfig);
    }
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

/// <summary>Local filesystem storage. Paths are generated server-side (never from client file names) and confined to the root.</summary>
public sealed class LocalFileStorage(string root) : IFileStorage
{
    private readonly string _root = Path.GetFullPath(root);

    public async Task<string> SaveAsync(Guid tenantId, string category, string extension, Stream content, CancellationToken ct = default)
    {
        if (category.Any(c => !char.IsLetterOrDigit(c) && c != '-')) throw new ArgumentException("Invalid category.");
        if (extension is not (".png" or ".jpg" or ".webp" or ".pdf" or ".bak" or ".json")) throw new ArgumentException("Invalid extension.");
        var relative = Path.Combine(tenantId.ToString("N"), category, Guid.NewGuid().ToString("N") + extension);
        var full = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using var fs = File.Create(full);
        await content.CopyToAsync(fs, ct);
        return relative.Replace('\\', '/');
    }

    public Task<Stream?> OpenAsync(string path, CancellationToken ct = default)
    {
        var full = Resolve(path);
        return Task.FromResult<Stream?>(File.Exists(full) ? File.OpenRead(full) : null);
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var full = Resolve(path);
        if (File.Exists(full)) File.Delete(full);
        return Task.CompletedTask;
    }

    private string Resolve(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relative));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new UnauthorizedAccessException("Path escapes storage root.");
        return full;
    }
}
