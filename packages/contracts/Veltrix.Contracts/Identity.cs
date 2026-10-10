namespace Veltrix.Contracts.Identity;

public sealed record LoginRequest(string Email, string Password, string? MfaCode = null, Guid? DeviceId = null);
public sealed record TokenResponse(
    string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt,
    UserProfileDto User);
public sealed record MfaRequiredResponse(bool MfaRequired);
public sealed record RefreshRequest(string RefreshToken);
public sealed record LogoutRequest(string RefreshToken);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record PasswordResetRequest(string Email);
public sealed record PasswordResetConfirm(string Token, string NewPassword);
public sealed record MfaSetupResponse(string Secret, string OtpAuthUri);
public sealed record MfaVerifyRequest(string Code);

public sealed record UserProfileDto(
    Guid Id, string Email, string FullName, Guid? TenantId, bool IsPlatformAdmin, IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions, IReadOnlyList<Guid>? Branches, string? PreferredLanguage, bool MfaEnabled);

public sealed record SessionDto(Guid FamilyId, DateTimeOffset CreatedAt, DateTimeOffset LastUsedAt, string? UserAgent, string? IpAddress, Guid? DeviceId, bool Current);

public sealed record UserDto(Guid Id, string Email, string FullName, string? Phone, bool IsActive, IReadOnlyList<string> Roles,
    IReadOnlyList<Guid> Branches, DateTimeOffset? LastLoginAt, bool MfaEnabled, bool LockedOut);
public sealed record CreateUserRequest(string Email, string FullName, string? Phone, string Password, IReadOnlyList<string> Roles,
    IReadOnlyList<Guid>? Branches, string? PreferredLanguage);
public sealed record UpdateUserRequest(string FullName, string? Phone, bool IsActive, IReadOnlyList<string> Roles, IReadOnlyList<Guid>? Branches);
public sealed record AdminResetPasswordRequest(string NewPassword);

public sealed record RoleDto(Guid Id, string Code, string Name, bool IsSystem, IReadOnlyList<string> Permissions);
public sealed record UpsertRoleRequest(string Code, string Name, IReadOnlyList<string> Permissions);
