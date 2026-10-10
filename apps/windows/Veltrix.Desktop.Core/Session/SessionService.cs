using Veltrix.Contracts.Identity;
using Veltrix.Desktop.Core.Api;
using Veltrix.Desktop.Core.Security;

namespace Veltrix.Desktop.Core.Session;

public enum SignInStatus { SignedIn, SignedInOffline, MfaRequired, Failed }

public sealed record SignInResult(SignInStatus Status, UserProfileDto? User = null, string? ErrorCode = null, string? Message = null);

/// <summary>Online sign-in (with MFA), session restore and an offline unlock for the last verified user.</summary>
public sealed class SessionService(ApiClient api, SecureTokenStore tokens, SecureStore store)
{
    public UserProfileDto? User { get; private set; }
    public bool IsOfflineSession { get; private set; }
    /// <summary>Signed in with a local account of a standalone (offline-licensed) installation.</summary>
    public bool IsStandaloneSession { get; private set; }
    public Standalone.LocalUser? LocalUser { get; private set; }

    /// <summary>Starts a session for a local account that <see cref="Standalone.LocalAuthService"/> has verified.</summary>
    public void SignInLocal(Standalone.LocalUser user)
    {
        LocalUser = user;
        User = user.ToProfile();
        IsOfflineSession = false;
        IsStandaloneSession = true;
    }

    public bool HasPermission(string permission) =>
        User is { } u && (u.IsPlatformAdmin || u.Permissions.Contains("*") || u.Permissions.Contains(permission));

    public async Task<SignInResult> SignInAsync(string email, string password, string? mfaCode, Guid? deviceId, CancellationToken ct = default)
    {
        email = email.Trim();
        if (email.Length == 0 || password.Length == 0) return new SignInResult(SignInStatus.Failed, ErrorCode: "required");
        try
        {
            var outcome = await api.LoginAsync(new LoginRequest(email, password, string.IsNullOrWhiteSpace(mfaCode) ? null : mfaCode.Trim(), deviceId), ct);
            if (outcome.MfaRequired) return new SignInResult(SignInStatus.MfaRequired);
            User = outcome.Tokens!.User;
            IsOfflineSession = false;
            // Remember a slow PBKDF2 verifier so this user can unlock the till while the server is unreachable.
            store.Set(SecureStore.OfflineCredential, OfflineCredential.Create(email, password));
            return new SignInResult(SignInStatus.SignedIn, User);
        }
        catch (ApiOfflineException)
        {
            var stored = store.Get(SecureStore.OfflineCredential);
            var profile = tokens.Profile;
            if (stored is not null && profile is not null && string.Equals(profile.Email, email, StringComparison.OrdinalIgnoreCase)
                && OfflineCredential.Verify(email, password, stored))
            {
                User = profile;
                IsOfflineSession = true;
                return new SignInResult(SignInStatus.SignedInOffline, User);
            }
            return new SignInResult(SignInStatus.Failed, ErrorCode: "offline");
        }
        catch (ApiException e)
        {
            return new SignInResult(SignInStatus.Failed, ErrorCode: e.Code ?? (e.Status == 401 ? "auth.invalid_credentials" : null), Message: e.Message);
        }
    }

    /// <summary>Resumes a stored session at startup. Returns null when the user has to sign in.</summary>
    public async Task<UserProfileDto?> TryRestoreAsync(CancellationToken ct = default)
    {
        if (tokens.RefreshToken is null) return null;
        try
        {
            User = await api.GetAsync<UserProfileDto>("/api/v1/me", ct);
            tokens.SaveProfile(User);
            IsOfflineSession = false;
            return User;
        }
        catch (ApiOfflineException)
        {
            // Offline: keep the stored session; requests will be retried once the server is reachable.
            User = tokens.Profile;
            IsOfflineSession = User is not null;
            return User;
        }
        catch (ApiException)
        {
            tokens.Clear();
            return null;
        }
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        User = null;
        IsOfflineSession = false;
        if (IsStandaloneSession) { IsStandaloneSession = false; LocalUser = null; return; }
        await api.LogoutAsync(ct);
    }
}
