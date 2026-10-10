using System.Reflection;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Veltrix.Api.Infrastructure;
using Veltrix.Modules.Identity;
using Veltrix.SharedKernel;

namespace Veltrix.Api;

public sealed record BuildInfo(string Product, string Version, string? Commit, string Environment)
{
    public static BuildInfo Current { get; } = new(
        "VELTRIX OS API",
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0",
        System.Environment.GetEnvironmentVariable("VELTRIX_COMMIT"),
        System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production");
}

public static class SecretBootstrap
{
    /// <summary>Production must supply Auth:SigningKey. Development generates and persists a local key outside source control.</summary>
    public static string ResolveSigningKey(string configured, IHostEnvironment env, IConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (configured.Length < 32) throw new InvalidOperationException("Auth:SigningKey must be at least 32 characters.");
            return configured;
        }
        if (!env.IsDevelopment() && !env.IsEnvironment("Testing"))
            throw new InvalidOperationException("Auth:SigningKey must be configured (environment variable Auth__SigningKey).");
        var dir = config["Veltrix:DataDirectory"] ?? Path.Combine(AppContext.BaseDirectory, ".veltrix-data");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "dev-jwt.key");
        if (!File.Exists(path)) File.WriteAllText(path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));
        return File.ReadAllText(path).Trim();
    }
}

public static class Bootstrap
{
    public static async Task RunAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var config = app.Configuration;
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Bootstrap");
        var db = scope.ServiceProvider.GetRequiredService<VeltrixDbContext>();

        if (config.GetValue("Database:MigrateOnStartup", app.Environment.IsDevelopment()))
        {
            logger.LogInformation("Applying database migrations");
            await db.Database.MigrateAsync();
        }

        // Plan templates (features/limits) so the vendor can start selling; prices are left at 0 and plans are private
        // until the vendor sets real prices in the Control Center.
        if (!await db.Set<Veltrix.Modules.Subscriptions.SubscriptionPlan>().AnyAsync())
        {
            foreach (var (code, def) in Veltrix.Modules.Licensing.PlanDefaults.Plans.Where(p => p.Key != "trial"))
                db.Add(new Veltrix.Modules.Subscriptions.SubscriptionPlan
                {
                    Code = code, Name = char.ToUpperInvariant(code[0]) + code[1..], Description = "Template — set pricing before publishing.",
                    Features = string.Join(',', def.Features), MaxDevices = def.MaxDevices, OfflineGraceDays = def.GraceDays, IsPublic = false,
                    MaxUsers = def.MaxDevices * 5, MaxBranches = code == "starter" ? 1 : code == "professional" ? 5 : 100,
                });
            await db.SaveChangesAsync();
        }

        // Optional one-time creation of the first platform administrator. No default credentials exist.
        var email = config["Bootstrap:PlatformAdminEmail"];
        var password = config["Bootstrap:PlatformAdminPassword"];
        if (!string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(password))
        {
            var normalized = AuthService.Normalize(email);
            if (!await db.Set<User>().AnyAsync(u => u.IsPlatformAdmin))
            {
                var policy = PasswordHasher.CheckPolicy(password);
                if (!policy.Ok) throw new InvalidOperationException("Bootstrap admin password does not satisfy the password policy: " + policy.Error);
                var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
                var role = await db.Set<Role>().Include(r => r.Permissions).SingleOrDefaultAsync(r => r.TenantId == null && r.Code == SystemRoles.PlatformSuperAdmin);
                if (role is null)
                {
                    role = new Role { Code = SystemRoles.PlatformSuperAdmin, Name = "Platform Super Admin", IsSystem = true };
                    role.Permissions = Permissions.PlatformPermissions.Select(p => new RolePermission { RoleId = role.Id, Permission = p }).ToList();
                    db.Add(role);
                }
                var user = new User { Email = email, NormalizedEmail = normalized, FullName = "Platform Administrator", IsPlatformAdmin = true, PasswordHash = hasher.Hash(password) };
                user.Roles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
                db.Add(user);
                db.AuditLogs.Add(new AuditLog { Action = "platform.admin_bootstrapped", EntityType = nameof(User), EntityId = user.Id.ToString(), OccurredAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
                logger.LogWarning("Created initial platform administrator {Email}. Remove Bootstrap__PlatformAdminPassword from configuration now.", email);
            }
        }
    }
}
