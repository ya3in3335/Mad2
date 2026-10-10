using Veltrix.SharedKernel;

namespace Veltrix.Modules.Identity;

public sealed class User : Entity
{
    /// <summary>Null for platform (vendor) administrators.</summary>
    public Guid? TenantId { get; set; }
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public string FullName { get; set; } = "";
    public string? Phone { get; set; }
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public bool IsPlatformAdmin { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutUntil { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public string? PreferredLanguage { get; set; }
    /// <summary>TOTP secret protected with ASP.NET Data Protection. Null when MFA is disabled.</summary>
    public string? MfaSecretProtected { get; set; }
    public bool MfaEnabled { get; set; }
    /// <summary>Bumped on password change / forced sign-out to invalidate refresh tokens.</summary>
    public int SecurityStamp { get; set; }
    /// <summary>Short numeric PIN hash for fast cashier switching on POS terminals (optional).</summary>
    public string? PinHash { get; set; }
    public Guid? EmployeeId { get; set; }
    public List<UserRole> Roles { get; set; } = new();
    public List<UserBranch> Branches { get; set; } = new();
}

public sealed class Role : Entity
{
    /// <summary>Null for platform roles.</summary>
    public Guid? TenantId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsSystem { get; set; }
    public List<RolePermission> Permissions { get; set; } = new();
}

public sealed class RolePermission
{
    public Guid RoleId { get; set; }
    public string Permission { get; set; } = "";
}

public sealed class UserRole
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
}

/// <summary>Branch assignment. A user with no branch rows and the flag AllBranches on the role set accesses all branches.</summary>
public sealed class UserBranch
{
    public Guid UserId { get; set; }
    public Guid BranchId { get; set; }
}

/// <summary>Refresh token (stored hashed). Tokens rotate on each use; reuse of a rotated token revokes the whole family.</summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public Guid FamilyId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
    public Guid? ReplacedById { get; set; }
    public int SecurityStamp { get; set; }
    public Guid? DeviceId { get; set; }
    public string? UserAgent { get; set; }
    public string? IpAddress { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
}

public sealed class PasswordResetToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}
