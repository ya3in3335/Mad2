using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Veltrix.Contracts.Identity;
using Veltrix.Desktop.Core.Security;

namespace Veltrix.Desktop.Core.Standalone;

public static class LocalRoles
{
    public const string Owner = "owner";
    public const string Cashier = "cashier";

    /// <summary>Permissions granted to a local cashier (the owner gets "*").</summary>
    public static readonly IReadOnlyList<string> CashierPermissions =
        ["pos.sell", "catalog.view", "customers.view", "customers.manage", "sales.view", "inventory.view"];
}

/// <summary>A user of a standalone (offline-licensed) installation. Only the salted PBKDF2 hash of the secret is stored.</summary>
public sealed record LocalUser(Guid Id, string Name, string Role, string SecretHash, DateTimeOffset CreatedAt, bool IsActive = true)
{
    public bool IsOwner => Role == LocalRoles.Owner;

    public UserProfileDto ToProfile() => new(Id, Name, Name, null, false, [Role],
        IsOwner ? ["*"] : LocalRoles.CashierPermissions, null, null, false);
}

/// <summary>Salted PBKDF2-SHA256 hashes: <c>pbkdf2-sha256$iterations$salt(b64)$hash(b64)</c>.</summary>
public static class PasswordHasher
{
    public const int DefaultIterations = 210_000;
    public const int MinimumIterations = 10_000;
    private const string Prefix = "pbkdf2-sha256";

    public static string Hash(string secret, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Derive(secret, salt, iterations);
        return string.Join('$', Prefix, iterations.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public static bool Verify(string secret, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var it) || it < MinimumIterations)
            return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(Derive(secret, Convert.FromBase64String(parts[2]), it), Convert.FromBase64String(parts[3]));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Derive(string secret, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, iterations, HashAlgorithmName.SHA256, 32);
}

public enum LocalSignInStatus { SignedIn, Invalid, LockedOut, NoUsers }

public sealed record LocalSignInResult(LocalSignInStatus Status, LocalUser? User = null, TimeSpan? RetryAfter = null);

/// <summary>
/// Local accounts for standalone mode: the owner created at first run plus optional cashiers. Users, hashes and the failed-attempt
/// counters live in the encrypted <see cref="SecureStore"/> (DPAPI on Windows). After <see cref="MaxFailures"/> consecutive failures
/// a name is locked with exponential backoff (30 s, 60 s, 120 s … capped at 15 min); the counter survives restarts.
/// </summary>
public sealed class LocalAuthService(SecureStore store, TimeProvider time, int iterations = PasswordHasher.DefaultIterations)
{
    public const string UsersKey = "standalone.users";
    public const string LockoutKey = "standalone.lockout";
    public const int MaxFailures = 5;
    public static readonly TimeSpan BaseLockout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxLockout = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();

    private sealed record Failure(int Count, DateTimeOffset? LockedUntil);

    public IReadOnlyList<LocalUser> Users => Load();
    public bool HasOwner => Load().Any(u => u.IsOwner && u.IsActive);

    /// <summary>Password: at least 8 characters. PIN (digits only): at least 6 digits. Returns an error key or null.</summary>
    public static string? ValidateSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return "dk.sa.err.secretRequired";
        if (secret.All(char.IsAsciiDigit)) return secret.Length >= 6 ? null : "dk.sa.err.pinShort";
        return secret.Length >= 8 ? null : "dk.sa.err.passwordShort";
    }

    public static string? ValidateName(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Length < 2 || name.Trim().Length > 60 ? "dk.sa.err.name" : null;

    public LocalUser CreateOwner(string name, string secret)
    {
        lock (_gate)
        {
            if (HasOwner) throw new InvalidOperationException("An owner account already exists.");
            return AddUserCore(name, secret, LocalRoles.Owner);
        }
    }

    public LocalUser AddUser(string name, string secret, string role)
    {
        if (role is not (LocalRoles.Owner or LocalRoles.Cashier)) throw new ArgumentException("Unknown role.", nameof(role));
        lock (_gate)
        {
            if (!HasOwner) throw new InvalidOperationException("Create the owner account first.");
            return AddUserCore(name, secret, role);
        }
    }

    private LocalUser AddUserCore(string name, string secret, string role)
    {
        if (ValidateName(name) is { } n) throw new InvalidOperationException(n);
        if (ValidateSecret(secret) is { } s) throw new InvalidOperationException(s);
        var users = Load().ToList();
        if (users.Any(u => Same(u.Name, name))) throw new InvalidOperationException("dk.sa.err.nameTaken");
        var user = new LocalUser(Guid.NewGuid(), name.Trim(), role, PasswordHasher.Hash(secret, iterations), time.GetUtcNow());
        users.Add(user);
        Save(users);
        return user;
    }

    public void RemoveUser(Guid id)
    {
        lock (_gate)
        {
            var users = Load().ToList();
            var u = users.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("Unknown user.");
            if (u.IsOwner && users.Count(x => x.IsOwner && x.IsActive) <= 1) throw new InvalidOperationException("dk.sa.err.lastOwner");
            users.Remove(u);
            Save(users);
        }
    }

    public void ChangeSecret(Guid id, string currentSecret, string newSecret)
    {
        if (ValidateSecret(newSecret) is { } s) throw new InvalidOperationException(s);
        lock (_gate)
        {
            var users = Load().ToList();
            var i = users.FindIndex(x => x.Id == id);
            if (i < 0 || !PasswordHasher.Verify(currentSecret, users[i].SecretHash)) throw new InvalidOperationException("auth.invalid");
            users[i] = users[i] with { SecretHash = PasswordHasher.Hash(newSecret, iterations) };
            Save(users);
        }
    }

    /// <summary>Verifies a secret without touching the lockout counters (used to confirm the owner before a backup).</summary>
    public bool VerifySecret(Guid id, string secret) => Load().FirstOrDefault(u => u.Id == id) is { } u && PasswordHasher.Verify(secret, u.SecretHash);

    public LocalSignInResult SignIn(string name, string secret)
    {
        lock (_gate)
        {
            var users = Load();
            if (users.Count == 0) return new LocalSignInResult(LocalSignInStatus.NoUsers);
            var key = Normalize(name);
            var failures = LoadFailures();
            var now = time.GetUtcNow();
            if (failures.TryGetValue(key, out var f) && f.LockedUntil is { } until && until > now)
                return new LocalSignInResult(LocalSignInStatus.LockedOut, RetryAfter: until - now);

            var user = users.FirstOrDefault(u => u.IsActive && Same(u.Name, name));
            // Always run the KDF so unknown names cannot be told apart by timing.
            var ok = PasswordHasher.Verify(secret ?? "", user?.SecretHash ?? Dummy.Value);
            if (user is not null && ok)
            {
                if (failures.Remove(key)) SaveFailures(failures);
                return new LocalSignInResult(LocalSignInStatus.SignedIn, user);
            }

            var count = (f?.Count ?? 0) + 1;
            DateTimeOffset? locked = null;
            if (count >= MaxFailures)
            {
                var factor = Math.Pow(2, Math.Min(count - MaxFailures, 10));
                var span = TimeSpan.FromTicks((long)Math.Min(BaseLockout.Ticks * factor, MaxLockout.Ticks));
                locked = now + span;
            }
            failures[key] = new Failure(count, locked);
            SaveFailures(failures);
            return locked is { } l ? new LocalSignInResult(LocalSignInStatus.LockedOut, RetryAfter: l - now) : new LocalSignInResult(LocalSignInStatus.Invalid);
        }
    }

    /// <summary>The raw (encrypted at rest) users document, included in backups.</summary>
    public string ExportUsers() => store.Get(UsersKey) ?? "[]";

    public void ImportUsers(string json)
    {
        var users = JsonSerializer.Deserialize<List<LocalUser>>(json, Json) ?? throw new InvalidOperationException("Invalid users document.");
        if (!users.Any(u => u.IsOwner)) throw new InvalidOperationException("The backup contains no owner account.");
        lock (_gate)
        {
            Save(users);
            store.Set(LockoutKey, null);
        }
    }

    private readonly Lazy<string> _dummy = new(() => PasswordHasher.Hash(Guid.NewGuid().ToString("N"), iterations));
    private Lazy<string> Dummy => _dummy;

    private IReadOnlyList<LocalUser> Load()
    {
        var json = store.Get(UsersKey);
        if (json is null) return [];
        try { return JsonSerializer.Deserialize<List<LocalUser>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    private void Save(List<LocalUser> users) => store.Set(UsersKey, JsonSerializer.Serialize(users, Json));

    private Dictionary<string, Failure> LoadFailures()
    {
        var json = store.Get(LockoutKey);
        if (json is null) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, Failure>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    private void SaveFailures(Dictionary<string, Failure> f) => store.Set(LockoutKey, JsonSerializer.Serialize(f, Json));

    private static string Normalize(string? name) => (name ?? "").Trim().ToLowerInvariant();
    private static bool Same(string a, string b) => Normalize(a) == Normalize(b);
}
