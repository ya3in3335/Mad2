namespace Veltrix.SharedKernel;

/// <summary>Request-scoped identity and tenant information resolved from the authenticated principal.
/// Tenant IDs supplied by clients in request bodies are never trusted; the tenant always comes from here.</summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    Guid? UserId { get; }
    bool IsAuthenticated { get; }
    bool IsPlatformAdmin { get; }
    IReadOnlySet<string> Permissions { get; }
    /// <summary>Branches the user may access. Null means all branches of the tenant.</summary>
    IReadOnlySet<Guid>? AllowedBranches { get; }
    Guid? DeviceId { get; }
    string? CorrelationId { get; }

    bool HasPermission(string permission);
    bool CanAccessBranch(Guid branchId);
    Guid RequireTenant();
    Guid RequireUser();
    void EnsureBranch(Guid branchId);
    void EnsurePermission(string permission);
}

/// <summary>Mutable implementation populated by middleware. Also used by background jobs via <see cref="RunAs"/>.</summary>
public sealed class TenantContext : ITenantContext
{
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();

    public Guid? TenantId { get; set; }
    public Guid? UserId { get; set; }
    public bool IsAuthenticated { get; set; }
    public bool IsPlatformAdmin { get; set; }
    public IReadOnlySet<string> Permissions { get; set; } = Empty;
    public IReadOnlySet<Guid>? AllowedBranches { get; set; }
    public Guid? DeviceId { get; set; }
    public string? CorrelationId { get; set; }

    public bool HasPermission(string permission) =>
        Permissions.Contains(permission) || Permissions.Contains(Veltrix.SharedKernel.Permissions.All);

    public bool CanAccessBranch(Guid branchId) => AllowedBranches is null || AllowedBranches.Contains(branchId);

    public Guid RequireTenant() => TenantId ?? throw new ForbiddenException("tenant.required", "A company context is required.");
    public Guid RequireUser() => UserId ?? throw new ForbiddenException("auth.required", "Authentication is required.");

    public void EnsureBranch(Guid branchId)
    {
        if (!CanAccessBranch(branchId))
            throw new ForbiddenException("branch.forbidden", "You are not authorized for this branch.");
    }

    public void EnsurePermission(string permission)
    {
        if (!HasPermission(permission))
            throw new ForbiddenException("permission.denied", $"Missing permission '{permission}'.");
    }

    /// <summary>Configure the context for system/background execution within a tenant.</summary>
    public void RunAs(Guid tenantId, Guid? userId = null)
    {
        TenantId = tenantId;
        UserId = userId;
        IsAuthenticated = true;
        Permissions = new HashSet<string> { Veltrix.SharedKernel.Permissions.All };
        AllowedBranches = null;
    }
}

public enum TenantAccessStatus { Active, Trial, GracePeriod, Suspended, Expired, NotFound }

/// <summary>Resolves whether a tenant may currently use the platform (implemented by tenants/subscriptions).</summary>
public interface ITenantStatusProvider
{
    Task<TenantAccessStatus> GetStatusAsync(Guid tenantId, CancellationToken ct = default);
    void Invalidate(Guid tenantId);
}

public static class TenantAccessStatusExtensions
{
    public static bool AllowsAccess(this TenantAccessStatus s) =>
        s is TenantAccessStatus.Active or TenantAccessStatus.Trial or TenantAccessStatus.GracePeriod;

    /// <summary>Expired tenants keep read access to their data (business data is never deleted on expiry).</summary>
    public static bool AllowsReadOnly(this TenantAccessStatus s) => s.AllowsAccess() || s == TenantAccessStatus.Expired;
}

/// <summary>Lookup of a tenant's branches/warehouses for cross-module validation (implemented by the tenants module).</summary>
public interface IBranchDirectory
{
    Task<IReadOnlyList<Guid>> GetBranchIdsAsync(Guid tenantId, CancellationToken ct = default);
    Task<bool> WarehouseBelongsToTenantAsync(Guid tenantId, Guid warehouseId, CancellationToken ct = default);
    Task<Guid?> GetWarehouseBranchAsync(Guid warehouseId, CancellationToken ct = default);
    Task<Guid?> GetDefaultWarehouseAsync(Guid branchId, CancellationToken ct = default);
}

/// <summary>Device activation state used to bind sessions to licensed devices.</summary>
public interface IDeviceAuthorization
{
    Task<bool> IsDeviceActiveAsync(Guid tenantId, Guid deviceId, CancellationToken ct = default);
}

/// <summary>Plan entitlements (feature flags + limits) resolved from the tenant's active licenses.</summary>
public interface IEntitlementChecker
{
    Task<bool> HasFeatureAsync(Guid tenantId, string feature, CancellationToken ct = default);
    Task<int?> GetLimitAsync(Guid tenantId, string limit, CancellationToken ct = default);
}

public static class Features
{
    public const string Pos = "pos";
    public const string Inventory = "inventory";
    public const string Purchasing = "purchasing";
    public const string Crm = "crm";
    public const string Sales = "sales";
    public const string Accounting = "accounting";
    public const string Hr = "hr";
    public const string Reports = "reports";
    public const string Intelligence = "intelligence";
    public const string MultiBranch = "multi_branch";
    public const string Mobile = "mobile";
    public const string Api = "api";

    public static readonly IReadOnlyList<string> All = [Pos, Inventory, Purchasing, Crm, Sales, Accounting, Hr, Reports, Intelligence, MultiBranch, Mobile, Api];
}

public static class Limits
{
    public const string MaxBranches = "max_branches";
    public const string MaxUsers = "max_users";
}
