using System.Text.Json;
using Veltrix.Desktop.Core.Security;

namespace Veltrix.Desktop.Core.Standalone;

public enum SectionUnlockStatus { Unlocked, Invalid, LockedOut, NotConfigured }

public sealed record SectionUnlockResult(SectionUnlockStatus Status, TimeSpan? RetryAfter = null);

/// <summary>
/// Second password protecting the sensitive section (human resources, workforce and attendance, cameras), separate from the sign-in password.
/// Only an owner (administrator) creates it, at the first opening of the section, and only an owner changes or resets it. It is stored
/// as a salted PBKDF2 hash in the encrypted <see cref="SecureStore"/>, never in plaintext. An unlock lasts for the session and expires
/// after <see cref="IdleTimeout"/> without use. Consecutive failures lock the section with exponential backoff (survives restarts).
/// </summary>
public sealed class SectionLockService(SecureStore store, TimeProvider time, int iterations = PasswordHasher.DefaultIterations)
{
    public const string HashKey = "section.hr.hash";
    public const string LockoutKey = "section.hr.lockout";
    public const int MinLength = 6;
    public const int MaxFailures = 5;
    public static readonly TimeSpan BaseLockout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxLockout = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private DateTimeOffset? _lastUse;

    private sealed record Failure(int Count, DateTimeOffset? LockedUntil);

    /// <summary>Section page keys behind the lock: every workforce page, the ERP HR page and the cameras.</summary>
    public static bool IsProtected(string pageKey) =>
        pageKey.StartsWith("wf.", StringComparison.Ordinal) || pageKey is "erp.hr" or "cameras";

    public bool IsConfigured => store.Get(HashKey) is { Length: > 0 };

    /// <summary>True while unlocked and used within <see cref="IdleTimeout"/>; each check counts as use.</summary>
    public bool IsUnlocked
    {
        get
        {
            lock (_gate)
            {
                var now = time.GetUtcNow();
                if (_lastUse is not { } last || now - last > IdleTimeout) { _lastUse = null; return false; }
                _lastUse = now;
                return true;
            }
        }
    }

    public void Lock()
    {
        lock (_gate) _lastUse = null;
    }

    /// <summary>First setup (owner only). Unlocks the section.</summary>
    public void Setup(LocalUser actor, string password, string confirm)
    {
        if (!actor.IsOwner) throw new InvalidOperationException("sec.err.ownerOnly");
        if (IsConfigured) throw new InvalidOperationException("sec.err.alreadySet");
        Validate(password, confirm);
        lock (_gate)
        {
            store.Set(HashKey, PasswordHasher.Hash(password, iterations));
            store.Set(LockoutKey, null);
            _lastUse = time.GetUtcNow();
        }
    }

    public SectionUnlockResult Unlock(string password)
    {
        lock (_gate)
        {
            if (store.Get(HashKey) is not { Length: > 0 } hash) return new(SectionUnlockStatus.NotConfigured);
            var now = time.GetUtcNow();
            var f = LoadFailure();
            if (f.LockedUntil is { } until && until > now) return new(SectionUnlockStatus.LockedOut, until - now);
            if (PasswordHasher.Verify(password ?? "", hash))
            {
                store.Set(LockoutKey, null);
                _lastUse = now;
                return new(SectionUnlockStatus.Unlocked);
            }
            var count = f.Count + 1;
            DateTimeOffset? lockedUntil = null;
            if (count >= MaxFailures)
            {
                var factor = Math.Pow(2, Math.Min(count - MaxFailures, 10));
                lockedUntil = now + TimeSpan.FromSeconds(Math.Min(BaseLockout.TotalSeconds * factor, MaxLockout.TotalSeconds));
            }
            store.Set(LockoutKey, JsonSerializer.Serialize(new Failure(count, lockedUntil), Json));
            return lockedUntil is { } u ? new(SectionUnlockStatus.LockedOut, u - now) : new(SectionUnlockStatus.Invalid);
        }
    }

    /// <summary>Owner changes the section password with the current one.</summary>
    public void Change(LocalUser actor, string current, string password, string confirm)
    {
        if (!actor.IsOwner) throw new InvalidOperationException("sec.err.ownerOnly");
        var r = Unlock(current);
        if (r.Status != SectionUnlockStatus.Unlocked) throw new InvalidOperationException(r.Status == SectionUnlockStatus.LockedOut ? "sec.err.lockedOut" : "sec.err.invalid");
        Validate(password, confirm);
        lock (_gate) store.Set(HashKey, PasswordHasher.Hash(password, iterations));
    }

    /// <summary>Forgotten section password: an owner re-enters their own sign-in password, then sets a new section password.</summary>
    public void Reset(LocalUser actor, LocalAuthService auth, string ownerPassword, string password, string confirm)
    {
        if (!actor.IsOwner) throw new InvalidOperationException("sec.err.ownerOnly");
        if (!auth.VerifySecret(actor.Id, ownerPassword ?? "")) throw new InvalidOperationException("auth.invalid");
        Validate(password, confirm);
        lock (_gate)
        {
            store.Set(HashKey, PasswordHasher.Hash(password, iterations));
            store.Set(LockoutKey, null);
            _lastUse = time.GetUtcNow();
        }
    }

    private static void Validate(string password, string confirm)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength) throw new InvalidOperationException("sec.err.tooShort");
        if (password != confirm) throw new InvalidOperationException("sec.err.mismatch");
    }

    private Failure LoadFailure()
    {
        try { return store.Get(LockoutKey) is { Length: > 0 } j ? JsonSerializer.Deserialize<Failure>(j, Json) ?? new(0, null) : new(0, null); }
        catch (JsonException) { return new(0, null); }
    }
}
