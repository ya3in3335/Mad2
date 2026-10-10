using System.Text;
using System.Text.Json;
using Veltrix.Contracts.Identity;
using Veltrix.Desktop.Core.Api;

namespace Veltrix.Desktop.Core.Security;

/// <summary>Encrypts secrets at rest. On Windows this is DPAPI (current user); tests use a reversible fake.</summary>
public interface IDataProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>Small key/value store where every value is encrypted with <see cref="IDataProtector"/> and written to its own file.</summary>
public sealed class SecureStore(string directory, IDataProtector protector)
{
    public const string AccessToken = "session.access";
    public const string RefreshToken = "session.refresh";
    public const string Profile = "session.profile";
    public const string Certificate = "license.certificate";
    public const string VendorKey = "license.vendorKey";
    public const string Watermark = "license.watermark";
    public const string LicenseSummary = "license.summary";
    public const string DeviceKey = "device.key";
    public const string OfflineCredential = "session.offlineCredential";
    public const string SetupToken = "onboarding.setupToken";

    private readonly object _gate = new();

    public string Directory { get; } = directory;

    public string? Get(string key)
    {
        var path = PathFor(key);
        lock (_gate)
        {
            if (!File.Exists(path)) return null;
            try
            {
                return Encoding.UTF8.GetString(protector.Unprotect(File.ReadAllBytes(path)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                // Unreadable (e.g. copied from another Windows account): behave as if the secret were absent.
                return null;
            }
        }
    }

    public void Set(string key, string? value)
    {
        var path = PathFor(key);
        lock (_gate)
        {
            if (value is null)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            System.IO.Directory.CreateDirectory(Directory);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, protector.Protect(Encoding.UTF8.GetBytes(value)));
            File.Move(tmp, path, overwrite: true);
        }
    }

    private string PathFor(string key)
    {
        if (key.Length == 0 || key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))) throw new ArgumentException("Invalid key.", nameof(key));
        return Path.Combine(Directory, key + ".bin");
    }
}

/// <summary><see cref="ITokenStore"/> persisted in the encrypted <see cref="SecureStore"/>, with an in-memory cache.</summary>
public sealed class SecureTokenStore(SecureStore store) : ITokenStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string? _access = store.Get(SecureStore.AccessToken);
    private string? _refresh = store.Get(SecureStore.RefreshToken);

    public string? AccessToken => _access;
    public string? RefreshToken => _refresh;

    /// <summary>The last signed-in profile (kept for offline sign-in and permission checks).</summary>
    public UserProfileDto? Profile
    {
        get
        {
            var json = store.Get(SecureStore.Profile);
            if (json is null) return null;
            try { return JsonSerializer.Deserialize<UserProfileDto>(json, Json); }
            catch (JsonException) { return null; }
        }
    }

    public event EventHandler? Changed;

    public void Save(TokenResponse tokens)
    {
        _access = tokens.AccessToken;
        _refresh = tokens.RefreshToken;
        store.Set(SecureStore.AccessToken, tokens.AccessToken);
        store.Set(SecureStore.RefreshToken, tokens.RefreshToken);
        SaveProfile(tokens.User);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SaveProfile(UserProfileDto profile) => store.Set(SecureStore.Profile, JsonSerializer.Serialize(profile, Json));

    public void Clear()
    {
        _access = null;
        _refresh = null;
        store.Set(SecureStore.AccessToken, null);
        store.Set(SecureStore.RefreshToken, null);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
