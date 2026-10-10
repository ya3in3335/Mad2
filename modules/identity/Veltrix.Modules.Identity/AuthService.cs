using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Veltrix.Contracts.Identity;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Identity;

public sealed record ClientInfo(string? IpAddress, string? UserAgent);

/// <summary>Sends password reset links. Implemented by the notifications module (email when SMTP is configured).</summary>
public interface IPasswordResetNotifier
{
    Task SendResetAsync(User user, string token, CancellationToken ct);
}

public sealed class AuthService(
    DbContext db,
    JwtTokenService jwt,
    PasswordHasher hasher,
    IOptions<AuthOptions> options,
    IClock clock,
    IAuditWriter audit,
    ITenantStatusProvider tenantStatus,
    IDataProtectionProvider dataProtection,
    IPasswordResetNotifier resetNotifier,
    IDeviceAuthorization deviceAuthorization,
    ILogger<AuthService> logger)
{
    private readonly AuthOptions _o = options.Value;
    private IDataProtector MfaProtector => dataProtection.CreateProtector("veltrix.identity.mfa.v1");

    public static string Normalize(string email) => email.Trim().ToUpperInvariant();

    public async Task<object> LoginAsync(LoginRequest req, ClientInfo client, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Email, "email", 256).NotEmpty(req.Password, "password", 128).ThrowIfInvalid();
        var now = clock.UtcNow;
        var normalized = Normalize(req.Email);
        var user = await db.Set<User>().Include(u => u.Roles).ThenInclude(r => r.Role).ThenInclude(r => r!.Permissions)
            .Include(u => u.Branches).SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);

        // Constant-ish response for unknown users to avoid account enumeration.
        if (user is null)
        {
            hasher.Verify(req.Password, "v1$1000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", out _);
            throw new UnauthorizedException("auth.invalid_credentials", "Invalid email or password.");
        }

        if (user.LockoutUntil is { } until && until > now)
            throw new DomainException("auth.locked_out", "Account temporarily locked after repeated failed sign-ins. Try again later.", 423);

        if (!hasher.Verify(req.Password, user.PasswordHash, out var rehash))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= _o.MaxFailedLogins)
            {
                user.LockoutUntil = now.AddMinutes(_o.LockoutMinutes);
                user.FailedLoginCount = 0;
                audit.Write("auth.lockout", nameof(User), user.Id.ToString());
            }
            audit.Write("auth.login_failed", nameof(User), user.Id.ToString());
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedException("auth.invalid_credentials", "Invalid email or password.");
        }

        if (!user.IsActive) throw new UnauthorizedException("auth.inactive", "This account is disabled.");

        if (user.MfaEnabled)
        {
            if (string.IsNullOrEmpty(req.MfaCode)) return new MfaRequiredResponse(true);
            var secret = MfaProtector.Unprotect(user.MfaSecretProtected!);
            if (!Totp.Verify(secret, req.MfaCode, now))
            {
                user.FailedLoginCount++;
                await db.SaveChangesAsync(ct);
                throw new UnauthorizedException("auth.invalid_mfa", "Invalid verification code.");
            }
        }

        if (user.TenantId is { } tid)
        {
            var status = await tenantStatus.GetStatusAsync(tid, ct);
            if (!status.AllowsReadOnly())
                throw new ForbiddenException("tenant.suspended", "Your company account is suspended. Contact your vendor.");
        }

        // Sessions from client applications are bound to a licensed, active device of the same company.
        if (req.DeviceId is { } did && (user.TenantId is null || !await deviceAuthorization.IsDeviceActiveAsync(user.TenantId.Value, did, ct)))
            throw new ForbiddenException("licensing.device_not_active", "This device is not activated for your company. Activate the license on this device first.");

        if (rehash) user.PasswordHash = hasher.Hash(req.Password);
        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        user.LastLoginAt = now;
        var (response, _) = IssueTokens(user, Guid.NewGuid(), req.DeviceId, client, now);
        audit.Write("auth.login", nameof(User), user.Id.ToString(), new { req.DeviceId });
        await db.SaveChangesAsync(ct);
        return response;
    }

    public async Task<TokenResponse> RefreshAsync(string refreshToken, ClientInfo client, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) throw new UnauthorizedException("auth.invalid_refresh", "Invalid session.");
        var now = clock.UtcNow;
        var hash = TokenHashing.Sha256(refreshToken);
        var token = await db.Set<RefreshToken>().SingleOrDefaultAsync(t => t.TokenHash == hash, ct)
            ?? throw new UnauthorizedException("auth.invalid_refresh", "Invalid session.");

        if (token.RevokedAt is not null)
        {
            // Reuse of a rotated/revoked token: assume theft and revoke the whole family.
            if (token.ReplacedById is not null)
            {
                await RevokeFamilyAsync(token.UserId, token.FamilyId, "reuse_detected", now, ct);
                audit.Write("auth.refresh_reuse_detected", nameof(User), token.UserId.ToString());
                await db.SaveChangesAsync(ct);
                logger.LogWarning("Refresh token reuse detected for user {UserId}", token.UserId);
            }
            throw new UnauthorizedException("auth.invalid_refresh", "Session expired. Please sign in again.");
        }
        if (token.ExpiresAt <= now) throw new UnauthorizedException("auth.invalid_refresh", "Session expired. Please sign in again.");

        var user = await LoadUserAsync(token.UserId, ct);
        if (user is null || !user.IsActive || user.SecurityStamp != token.SecurityStamp)
        {
            await RevokeFamilyAsync(token.UserId, token.FamilyId, "stamp_changed", now, ct);
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedException("auth.invalid_refresh", "Session expired. Please sign in again.");
        }
        if (token.DeviceId is { } deviceId && user.TenantId is { } dt && !await deviceAuthorization.IsDeviceActiveAsync(dt, deviceId, ct))
        {
            await RevokeFamilyAsync(token.UserId, token.FamilyId, "device_revoked", now, ct);
            await db.SaveChangesAsync(ct);
            throw new ForbiddenException("licensing.device_revoked", "This device's activation was revoked.");
        }
        if (user.TenantId is { } tid && !(await tenantStatus.GetStatusAsync(tid, ct)).AllowsReadOnly())
            throw new ForbiddenException("tenant.suspended", "Your company account is suspended. Contact your vendor.");

        var (response, newToken) = IssueTokens(user, token.FamilyId, token.DeviceId, client, now);
        token.RevokedAt = now;
        token.RevokedReason = "rotated";
        token.ReplacedById = newToken.Id;
        await db.SaveChangesAsync(ct);
        return response;
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct)
    {
        var hash = TokenHashing.Sha256(refreshToken ?? "");
        var token = await db.Set<RefreshToken>().SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null) return;
        await RevokeFamilyAsync(token.UserId, token.FamilyId, "logout", clock.UtcNow, ct);
        audit.Write("auth.logout", nameof(User), token.UserId.ToString());
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SessionDto>> ListSessionsAsync(Guid userId, string? currentRefreshToken, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var currentHash = currentRefreshToken is null ? null : TokenHashing.Sha256(currentRefreshToken);
        var tokens = await db.Set<RefreshToken>()
            .Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now).ToListAsync(ct);
        return tokens.Select(t => new SessionDto(t.FamilyId, t.CreatedAt, t.LastUsedAt, t.UserAgent, t.IpAddress, t.DeviceId,
            t.TokenHash == currentHash)).OrderByDescending(s => s.LastUsedAt).ToList();
    }

    public async Task RevokeSessionAsync(Guid userId, Guid familyId, CancellationToken ct)
    {
        await RevokeFamilyAsync(userId, familyId, "user_revoked", clock.UtcNow, ct);
        audit.Write("auth.session_revoked", nameof(User), userId.ToString(), new { familyId });
        await db.SaveChangesAsync(ct);
    }

    public async Task ChangePasswordAsync(Guid userId, ChangePasswordRequest req, CancellationToken ct)
    {
        var user = await db.Set<User>().SingleAsync(u => u.Id == userId, ct);
        if (!hasher.Verify(req.CurrentPassword ?? "", user.PasswordHash, out _))
            throw ValidationException.For("currentPassword", "Current password is incorrect.");
        SetPassword(user, req.NewPassword);
        await RevokeAllAsync(user.Id, "password_changed", ct);
        audit.Write("auth.password_changed", nameof(User), user.Id.ToString());
        await db.SaveChangesAsync(ct);
    }

    public void SetPassword(User user, string? newPassword)
    {
        var policy = PasswordHasher.CheckPolicy(newPassword);
        if (!policy.Ok) throw ValidationException.For("newPassword", policy.Error!);
        user.PasswordHash = hasher.Hash(newPassword!);
        user.SecurityStamp++;
    }

    public async Task RequestPasswordResetAsync(string email, CancellationToken ct)
    {
        var normalized = Normalize(email ?? "");
        var user = await db.Set<User>().SingleOrDefaultAsync(u => u.NormalizedEmail == normalized && u.IsActive, ct);
        if (user is null) return; // no enumeration
        var token = TokenHashing.NewOpaqueToken();
        db.Set<PasswordResetToken>().Add(new PasswordResetToken
        {
            UserId = user.Id, TokenHash = TokenHashing.Sha256(token), ExpiresAt = clock.UtcNow.AddMinutes(30),
        });
        audit.Write("auth.password_reset_requested", nameof(User), user.Id.ToString());
        await db.SaveChangesAsync(ct);
        await resetNotifier.SendResetAsync(user, token, ct);
    }

    public async Task ConfirmPasswordResetAsync(PasswordResetConfirm req, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var hash = TokenHashing.Sha256(req.Token ?? "");
        var reset = await db.Set<PasswordResetToken>().SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (reset is null || reset.UsedAt is not null || reset.ExpiresAt <= now)
            throw new DomainException("auth.reset_invalid", "This reset link is invalid or has expired.");
        var user = await db.Set<User>().SingleAsync(u => u.Id == reset.UserId, ct);
        SetPassword(user, req.NewPassword);
        reset.UsedAt = now;
        user.LockoutUntil = null;
        user.FailedLoginCount = 0;
        await RevokeAllAsync(user.Id, "password_reset", ct);
        audit.Write("auth.password_reset", nameof(User), user.Id.ToString());
        await db.SaveChangesAsync(ct);
    }

    public async Task<MfaSetupResponse> BeginMfaSetupAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Set<User>().SingleAsync(u => u.Id == userId, ct);
        if (user.MfaEnabled) throw new ConflictException("mfa.already_enabled", "MFA is already enabled.");
        var secret = Totp.NewSecret();
        user.MfaSecretProtected = MfaProtector.Protect(secret);
        await db.SaveChangesAsync(ct);
        var uri = $"otpauth://totp/VELTRIX%20OS:{Uri.EscapeDataString(user.Email)}?secret={secret}&issuer=VELTRIX%20OS";
        return new MfaSetupResponse(secret, uri);
    }

    public async Task EnableMfaAsync(Guid userId, string code, CancellationToken ct)
    {
        var user = await db.Set<User>().SingleAsync(u => u.Id == userId, ct);
        if (user.MfaSecretProtected is null) throw new DomainException("mfa.not_started", "Start MFA setup first.");
        if (!Totp.Verify(MfaProtector.Unprotect(user.MfaSecretProtected), code, clock.UtcNow))
            throw ValidationException.For("code", "Invalid verification code.");
        user.MfaEnabled = true;
        audit.Write("auth.mfa_enabled", nameof(User), user.Id.ToString());
        await db.SaveChangesAsync(ct);
    }

    public async Task DisableMfaAsync(Guid userId, string code, CancellationToken ct)
    {
        var user = await db.Set<User>().SingleAsync(u => u.Id == userId, ct);
        if (!user.MfaEnabled || !Totp.Verify(MfaProtector.Unprotect(user.MfaSecretProtected!), code, clock.UtcNow))
            throw ValidationException.For("code", "Invalid verification code.");
        user.MfaEnabled = false;
        user.MfaSecretProtected = null;
        audit.Write("auth.mfa_disabled", nameof(User), user.Id.ToString());
        await db.SaveChangesAsync(ct);
    }

    public async Task<UserProfileDto> GetProfileAsync(Guid userId, CancellationToken ct)
    {
        var user = await LoadUserAsync(userId, ct) ?? throw new NotFoundException("user.not_found", "User not found.");
        var (roles, perms, branches) = Resolve(user);
        return ToProfile(user, roles, perms, branches);
    }

    public Task<User?> LoadUserAsync(Guid userId, CancellationToken ct) =>
        db.Set<User>().Include(u => u.Roles).ThenInclude(r => r.Role).ThenInclude(r => r!.Permissions)
            .Include(u => u.Branches).SingleOrDefaultAsync(u => u.Id == userId, ct);

    public static (List<string> Roles, List<string> Permissions, List<Guid>? Branches) Resolve(User user)
    {
        var roles = user.Roles.Where(r => r.Role is not null).Select(r => r.Role!.Code).ToList();
        var perms = user.Roles.Where(r => r.Role is not null).SelectMany(r => r.Role!.Permissions.Select(p => p.Permission))
            .Distinct().ToList();
        if (user.IsPlatformAdmin && perms.Count == 0) perms.AddRange(Permissions.PlatformPermissions);
        // Owners/admins (wildcard or branch management) see all branches; others are restricted to assigned branches.
        var allBranches = perms.Contains(Permissions.All) || perms.Contains(Permissions.BranchesManage) || user.Branches.Count == 0 && user.IsPlatformAdmin;
        List<Guid>? branches = allBranches ? null : user.Branches.Select(b => b.BranchId).ToList();
        return (roles, perms, branches);
    }

    private (TokenResponse, RefreshToken) IssueTokens(User user, Guid familyId, Guid? deviceId, ClientInfo client, DateTimeOffset now)
    {
        var (roles, perms, branches) = Resolve(user);
        var (access, accessExp) = jwt.CreateAccessToken(user, roles, perms, branches, deviceId, now);
        var refresh = TokenHashing.NewOpaqueToken();
        var refreshExp = now.AddDays(_o.RefreshTokenDays);
        var entity = new RefreshToken
        {
            UserId = user.Id, FamilyId = familyId, TokenHash = TokenHashing.Sha256(refresh), CreatedAt = now, ExpiresAt = refreshExp,
            SecurityStamp = user.SecurityStamp, DeviceId = deviceId, UserAgent = Truncate(client.UserAgent, 300),
            IpAddress = Truncate(client.IpAddress, 64), LastUsedAt = now,
        };
        db.Set<RefreshToken>().Add(entity);
        return (new TokenResponse(access, accessExp, refresh, refreshExp, ToProfile(user, roles, perms, branches)), entity);
    }

    private static UserProfileDto ToProfile(User user, List<string> roles, List<string> perms, List<Guid>? branches) =>
        new(user.Id, user.Email, user.FullName, user.TenantId, user.IsPlatformAdmin, roles, perms, branches, user.PreferredLanguage, user.MfaEnabled);

    private async Task RevokeFamilyAsync(Guid userId, Guid familyId, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var tokens = await db.Set<RefreshToken>().Where(t => t.UserId == userId && t.FamilyId == familyId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in tokens) { t.RevokedAt = now; t.RevokedReason = reason; }
    }

    public async Task RevokeAllAsync(Guid userId, string reason, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var tokens = await db.Set<RefreshToken>().Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in tokens) { t.RevokedAt = now; t.RevokedReason = reason; }
    }

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}

public sealed class PasswordStepUpVerifier(DbContext db, PasswordHasher hasher, IAuditWriter audit) : IStepUpVerifier
{
    public async Task<bool> VerifyAsync(Guid userId, string password, CancellationToken ct)
    {
        var hash = await db.Set<User>().Where(u => u.Id == userId && u.IsActive).Select(u => u.PasswordHash).SingleOrDefaultAsync(ct);
        var ok = hash is not null && hasher.Verify(password, hash, out _);
        if (!ok) audit.Write("auth.step_up_failed", nameof(User), userId.ToString());
        return ok;
    }
}
