using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Veltrix.SharedKernel;

namespace Veltrix.Api.Infrastructure;

/// <summary>Single unit-of-work for the modular monolith. Each module contributes entity configurations from its
/// own assembly. Tenant isolation is enforced through global query filters plus write-time ownership checks.</summary>
public sealed class VeltrixDbContext(DbContextOptions<VeltrixDbContext> options, ITenantContext tenant, IClock clock, IServiceProvider? services = null)
    : DbContext(options)
{
    public static readonly List<Assembly> ModuleAssemblies = new();

    /// <summary>Evaluated per query: the current tenant id (Guid.Empty when none, which matches nothing).</summary>
    public Guid CurrentTenantId => tenant.TenantId ?? Guid.Empty;

    /// <summary>Platform administrators may read across tenants only via explicit IgnoreQueryFilters calls.</summary>
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        foreach (var asm in ModuleAssemblies.Distinct()) b.ApplyConfigurationsFromAssembly(asm);
        b.ApplyConfigurationsFromAssembly(typeof(VeltrixDbContext).Assembly);

        foreach (var et in b.Model.GetEntityTypes().Where(e => e.ClrType is not null && !e.IsOwned()))
        {
            var clr = et.ClrType;
            // Default precision for money/quantities.
            foreach (var p in et.GetProperties().Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
                if (p.GetPrecision() is null) p.SetPrecision(19);
            foreach (var p in et.GetProperties().Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
                if (p.GetScale() is null) p.SetScale(4);

            if (typeof(ITenantOwned).IsAssignableFrom(clr) && et.BaseType is null)
            {
                b.Entity(clr).HasIndex(nameof(ITenantOwned.TenantId));
                b.Entity(clr).HasQueryFilter(BuildTenantFilter(clr));
            }
        }
    }

    private LambdaExpression BuildTenantFilter(Type clr)
    {
        var param = Expression.Parameter(clr, "e");
        var prop = Expression.Property(param, nameof(ITenantOwned.TenantId));
        var current = Expression.Property(Expression.Constant(this), nameof(CurrentTenantId));
        return Expression.Lambda(Expression.Equal(prop, current), param);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyRules();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        ApplyRules();
        var added = ChangeTracker.Entries<Veltrix.Modules.Notifications.Notification>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
        if (added.Count > 0 && services?.GetService(typeof(IRealtimePublisher)) is IRealtimePublisher rt)
            foreach (var n in added) await rt.NotifyTenantAsync(n.TenantId, "notification", new { n.Id, n.Type, n.Title, severity = n.Severity.ToString() });
        return result;
    }

    private void ApplyRules()
    {
        var now = clock.UtcNow;
        foreach (var e in ChangeTracker.Entries())
        {
            if (e.Entity is Entity entity)
            {
                if (e.State == EntityState.Added)
                {
                    if (entity.CreatedAt == default) entity.CreatedAt = now;
                    entity.UpdatedAt = now;
                    entity.CreatedBy ??= tenant.UserId;
                }
                else if (e.State == EntityState.Modified)
                {
                    entity.UpdatedAt = now;
                    entity.UpdatedBy = tenant.UserId;
                }
            }

            if (e.Entity is ITenantOwned owned && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            {
                if (e.State == EntityState.Added && owned.TenantId == Guid.Empty && tenant.TenantId is { } tid)
                    owned.TenantId = tid;
                // A tenant user can never write rows of another tenant, whatever the client sent.
                if (!tenant.IsPlatformAdmin && tenant.TenantId is { } current && owned.TenantId != current)
                    throw new ForbiddenException("tenant.isolation", "Cross-tenant write rejected.");
                if (owned.TenantId == Guid.Empty)
                    throw new ForbiddenException("tenant.required", "Tenant-owned data requires a company context.");
                if (e.State == EntityState.Modified && e.Property(nameof(ITenantOwned.TenantId)).IsModified)
                    throw new ForbiddenException("tenant.isolation", "Tenant ownership cannot be changed.");
            }

            if (e.Entity is IImmutableWhenFinal fin && e.State == EntityState.Deleted && fin.IsFinal)
                throw new ConflictException("record.final", "Finalized records cannot be deleted. Use a reversing document.");
        }
    }
}
