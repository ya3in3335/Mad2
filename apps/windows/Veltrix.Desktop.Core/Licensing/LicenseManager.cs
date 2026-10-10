using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Veltrix.Contracts.Licensing;
using Veltrix.Desktop.Core.Api;
using Veltrix.Desktop.Core.Security;
using Veltrix.Desktop.Core.Settings;

namespace Veltrix.Desktop.Core.Licensing;

/// <summary>The device's persistent ECDSA P-256 key (stored encrypted) and installation identity.</summary>
public sealed class DeviceIdentity : IDisposable
{
    private readonly ECDsa _key;

    public DeviceIdentity(SecureStore store, Guid installationId, FingerprintInputs fingerprint)
    {
        InstallationId = installationId;
        _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var stored = store.Get(SecureStore.DeviceKey);
        var loaded = false;
        if (stored is not null)
        {
            try
            {
                _key.ImportPkcs8PrivateKey(Convert.FromBase64String(stored), out _);
                loaded = true;
            }
            catch (Exception e) when (e is FormatException or CryptographicException) { }
        }
        if (!loaded) store.Set(SecureStore.DeviceKey, Convert.ToBase64String(_key.ExportPkcs8PrivateKey()));
        Spki = _key.ExportSubjectPublicKeyInfo();
        FingerprintHash = DeviceFingerprint.Compute(fingerprint);
        DeviceName = fingerprint.MachineName;
    }

    public Guid InstallationId { get; }
    public byte[] Spki { get; }
    public string PublicKeyBase64 => Convert.ToBase64String(Spki);
    public string Thumbprint => DeviceKeys.Thumbprint(Spki);
    public string FingerprintHash { get; }
    public string DeviceName { get; }

    public string Sign(string nonce, string purpose) => DeviceKeys.SignChallenge(_key, nonce, InstallationId, purpose);

    public void Dispose() => _key.Dispose();
}

/// <summary>
/// Challenge/response activation, certificate renewal (heartbeat), voluntary release and offline evaluation of the
/// vendor-signed certificate against a monotonic "last trusted time" watermark.
/// </summary>
public sealed class LicenseManager(ApiClient api, SecureStore store, AppSettings settings, Action saveSettings, DeviceIdentity identity,
    TimeProvider time, string appVersion, string? pinnedVendorKeyBase64 = null)
{
    public const string Platform = "windows";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public LicenseEvaluation? Current { get; private set; }
    public event EventHandler<LicenseEvaluation>? Changed;

    /// <summary>The pinned vendor key: compiled in (recommended) or trusted on first activation.</summary>
    public byte[]? VendorKey
    {
        get
        {
            var b64 = string.IsNullOrWhiteSpace(pinnedVendorKeyBase64) ? store.Get(SecureStore.VendorKey) : pinnedVendorKeyBase64;
            if (string.IsNullOrWhiteSpace(b64)) return null;
            try { return Convert.FromBase64String(b64.Trim()); }
            catch (FormatException) { return null; }
        }
    }

    public LicenseSummaryDto? Summary
    {
        get
        {
            var json = store.Get(SecureStore.LicenseSummary);
            if (json is null) return null;
            try { return JsonSerializer.Deserialize<LicenseSummaryDto>(json, Json); }
            catch (JsonException) { return null; }
        }
    }

    public LicenseEvaluation Evaluate()
    {
        var cert = store.Get(SecureStore.Certificate);
        var key = VendorKey;
        var watermark = long.TryParse(store.Get(SecureStore.Watermark), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) ? w : 0;
        LicenseEvaluation result;
        if (string.IsNullOrWhiteSpace(cert)) result = new LicenseEvaluation(LicenseState.Missing, null, watermark, null, null);
        else if (key is null) result = new LicenseEvaluation(LicenseState.InvalidSignature, null, watermark, null, null);
        else result = LicenseVerifier.Evaluate(cert, key, identity.InstallationId, identity.Thumbprint, time.GetUtcNow(), watermark);
        if (result.Watermark > watermark) store.Set(SecureStore.Watermark, result.Watermark.ToString(CultureInfo.InvariantCulture));
        Current = result;
        Changed?.Invoke(this, result);
        return result;
    }

    public async Task<ActivateResponse> ActivateAsync(string licenseKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pinnedVendorKeyBase64) && store.Get(SecureStore.VendorKey) is null)
        {
            var pk = await api.SendAsync<PublicKeyResponse>(HttpMethod.Get, "/api/v1/licensing/public-key", null, ct, authenticate: false);
            if (pk?.Spki is not { Length: > 0 } spki || pk.Algorithm != "ES256") throw new ApiException(0, "license.vendor_key", "The server did not provide a valid vendor key.");
            store.Set(SecureStore.VendorKey, spki);
        }

        var ch = await ChallengeAsync("activate", ct);
        var request = new ActivateRequest(NormalizeKey(licenseKey), identity.InstallationId, Platform, identity.DeviceName, appVersion,
            identity.PublicKeyBase64, identity.FingerprintHash, DeviceFingerprint.Version, ch.ChallengeId, identity.Sign(ch.Nonce, "activate"));
        var res = await api.SendAsync<ActivateResponse>(HttpMethod.Post, "/api/v1/licensing/activate", request, ct, authenticate: false);

        // Never store a certificate that does not verify against the pinned key and this device.
        var key = VendorKey ?? throw new ApiException(0, "license.vendor_key", "No vendor key is pinned.");
        var check = LicenseVerifier.Evaluate(res.Certificate, key, identity.InstallationId, identity.Thumbprint, time.GetUtcNow(), 0);
        if (check.State is LicenseState.InvalidSignature or LicenseState.WrongDevice)
            throw new ApiException(0, "license.invalid_certificate", "The certificate returned by the server could not be verified.");

        store.Set(SecureStore.Certificate, res.Certificate);
        store.Set(SecureStore.LicenseSummary, JsonSerializer.Serialize(res.License, Json));
        store.Set(SecureStore.SetupToken, res.SetupToken);
        settings.DeviceId = res.DeviceId;
        settings.TenantId = res.TenantId;
        settings.TenantName = res.TenantName;
        settings.SetupCompleted = res.SetupCompleted;
        saveSettings();
        api.DeviceId = res.DeviceId;
        Evaluate();
        return res;
    }

    /// <summary>The vendor key compiled into this build (licensing/vendor-public-key.pem); required for offline activation.</summary>
    public byte[]? EmbeddedVendorKey
    {
        get
        {
            if (string.IsNullOrWhiteSpace(pinnedVendorKeyBase64)) return null;
            try { return Convert.FromBase64String(pinnedVendorKeyBase64.Trim()); }
            catch (FormatException) { return null; }
        }
    }

    public bool OfflineActivationConfigured => EmbeddedVendorKey is not null;

    /// <summary>This device's code for offline activation, grouped for display.</summary>
    public string DeviceCode => OfflineLicensing.FormatDeviceCode(OfflineLicensing.EncodeDeviceCode(identity.InstallationId, identity.Thumbprint));

    /// <summary>True when the stored certificate is a vendor-issued offline license (never refreshed online).</summary>
    public bool IsOfflineLicense =>
        store.Get(SecureStore.Certificate) is { Length: > 0 } cert && VendorKey is { } key
        && LicenseVerifier.ReadVerified(cert, key)?.Type == OfflineLicensing.OfflineType;

    /// <summary>
    /// Server-less activation: verifies an owner-issued certificate against the embedded vendor key and this device, then
    /// stores it exactly like an online activation.
    /// </summary>
    public OfflineActivationResult ActivateOffline(string activationText)
    {
        if (EmbeddedVendorKey is not { } key) return OfflineActivationResult.NotConfigured;
        var cert = OfflineLicensing.ExtractActivationCode(activationText);
        if (cert is null) return OfflineActivationResult.Invalid;
        var check = LicenseVerifier.Evaluate(cert, key, identity.InstallationId, identity.Thumbprint, time.GetUtcNow(), 0);
        if (check.State == LicenseState.WrongDevice) return OfflineActivationResult.WrongDevice;
        if (check.Payload is not { } p || check.State is LicenseState.InvalidSignature or LicenseState.Revoked or LicenseState.Suspended or LicenseState.ClockRollback)
            return OfflineActivationResult.Invalid;
        if (check.State is LicenseState.LicenseExpired or LicenseState.CertificateExpired) return OfflineActivationResult.Expired;

        var summary = new LicenseSummaryDto(p.Lid, "OFFLINE", p.Type, p.Status, p.Status, p.Plan, p.MaxDev, 1, DateTimeOffset.FromUnixTimeSeconds(p.Iat),
            DateTimeOffset.FromUnixTimeSeconds(p.Lexp ?? p.Exp), 0, p.Ent, check.DaysRemaining);
        store.Set(SecureStore.Certificate, cert);
        store.Set(SecureStore.LicenseSummary, JsonSerializer.Serialize(summary, Json));
        store.Set(SecureStore.SetupToken, null);
        settings.DeviceId = null; // no server seat: never send heartbeats for this license
        settings.TenantId = p.Tid;
        settings.TenantName = p.TenantName;
        saveSettings();
        api.DeviceId = null;
        Evaluate();
        return OfflineActivationResult.Activated;
    }

    /// <summary>Renews the certificate when the server is reachable. Returns false when offline (the grace period applies).
    /// Offline (vendor-issued) licenses are only re-evaluated locally: they are valid until their expiry.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        if (IsOfflineLicense)
        {
            Evaluate();
            return true;
        }
        if (settings.DeviceId is not { } deviceId || VendorKey is not { } key) return false;
        try
        {
            var ch = await ChallengeAsync("refresh", ct);
            var res = await api.SendAsync<RefreshCertificateResponse>(HttpMethod.Post, "/api/v1/licensing/refresh",
                new RefreshCertificateRequest(deviceId, identity.InstallationId, appVersion, ch.ChallengeId, identity.Sign(ch.Nonce, "refresh")), ct, authenticate: false);
            var check = LicenseVerifier.Evaluate(res.Certificate, key, identity.InstallationId, identity.Thumbprint, time.GetUtcNow(), 0);
            if (check.State is not (LicenseState.InvalidSignature or LicenseState.WrongDevice))
            {
                store.Set(SecureStore.Certificate, res.Certificate);
                store.Set(SecureStore.LicenseSummary, JsonSerializer.Serialize(res.License, Json));
            }
            Evaluate();
            return true;
        }
        catch (ApiOfflineException)
        {
            return false;
        }
    }

    /// <summary>Frees this device's seat on the server and forgets the local activation.</summary>
    public async Task ReleaseAsync(CancellationToken ct = default)
    {
        if (settings.DeviceId is { } deviceId)
        {
            var ch = await ChallengeAsync("release", ct);
            await api.SendAsync<JsonElement?>(HttpMethod.Post, "/api/v1/licensing/release",
                new ReleaseDeviceRequest(deviceId, identity.InstallationId, ch.ChallengeId, identity.Sign(ch.Nonce, "release")), ct, authenticate: false);
        }
        Forget();
    }

    public void Forget()
    {
        store.Set(SecureStore.Certificate, null);
        store.Set(SecureStore.LicenseSummary, null);
        store.Set(SecureStore.SetupToken, null);
        settings.DeviceId = null;
        saveSettings();
        Evaluate();
    }

    public static string NormalizeKey(string key) => new string(key.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

    private Task<ChallengeResponse> ChallengeAsync(string purpose, CancellationToken ct) =>
        api.SendAsync<ChallengeResponse>(HttpMethod.Post, "/api/v1/licensing/challenge", new ChallengeRequest(identity.InstallationId, purpose), ct, authenticate: false);

    private sealed record PublicKeyResponse(string Algorithm, string Spki, string? Pem);
}

public enum OfflineActivationResult
{
    Activated,
    NotConfigured,
    Invalid,
    WrongDevice,
    Expired,
}
