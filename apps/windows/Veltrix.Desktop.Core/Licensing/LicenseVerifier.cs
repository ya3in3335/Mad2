using System.Security.Cryptography;
using System.Text.Json;
using Veltrix.Contracts.Licensing;
using Veltrix.Desktop.Core.Security;

namespace Veltrix.Desktop.Core.Licensing;

public enum LicenseState
{
    /// <summary>No certificate stored: the device must be activated.</summary>
    Missing,
    Valid,
    /// <summary>Valid, but the license ends within <see cref="LicenseVerifier.RenewalWarningDays"/> days.</summary>
    RenewSoon,
    /// <summary>The license itself has expired: the app stays usable read-only until renewed.</summary>
    LicenseExpired,
    /// <summary>The offline grace window is over: connect to the server to renew the certificate.</summary>
    CertificateExpired,
    Revoked,
    Suspended,
    InvalidSignature,
    WrongDevice,
    ClockRollback,
}

public sealed record LicenseEvaluation(LicenseState State, LicenseCertificatePayload? Payload, long Watermark, int? DaysRemaining, DateTimeOffset? OfflineUntil)
{
    public bool CanSell => State is LicenseState.Valid or LicenseState.RenewSoon;
    public bool ReadOnly => State is LicenseState.LicenseExpired or LicenseState.CertificateExpired;
    public bool RequiresActivation => State is LicenseState.Missing or LicenseState.Revoked or LicenseState.InvalidSignature or LicenseState.WrongDevice;
}

/// <summary>
/// Offline verification of the vendor-signed license certificate (<c>base64url(payload).base64url(ES256 DER signature)</c>).
/// Pure logic: the caller supplies the pinned vendor key, the device identity, the current time and the last trusted
/// time ("watermark") and persists the returned watermark.
/// </summary>
public static class LicenseVerifier
{
    public const int ClockToleranceSeconds = 600;
    public const int RenewalWarningDays = 14;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Checks the signature only and returns the payload, or null when the certificate is malformed or forged.</summary>
    public static LicenseCertificatePayload? ReadVerified(string certificate, byte[] vendorSpki)
    {
        var parts = certificate.Split('.');
        if (parts.Length != 2) return null;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(vendorSpki, out _);
            var body = Base64Url.Decode(parts[0]);
            if (!key.VerifyData(body, Base64Url.Decode(parts[1]), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) return null;
            return JsonSerializer.Deserialize<LicenseCertificatePayload>(body, Json);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or JsonException)
        {
            return null;
        }
    }

    public static LicenseEvaluation Evaluate(string? certificate, byte[] vendorSpki, Guid installationId, string keyThumbprint,
        DateTimeOffset now, long watermark)
    {
        if (string.IsNullOrWhiteSpace(certificate)) return new(LicenseState.Missing, null, watermark, null, null);
        var p = ReadVerified(certificate, vendorSpki);
        if (p is null) return new(LicenseState.InvalidSignature, null, watermark, null, null);
        if (p.Iid != installationId || !string.Equals(p.Kth, keyThumbprint, StringComparison.OrdinalIgnoreCase))
            return new(LicenseState.WrongDevice, p, watermark, null, null);

        var t = now.ToUnixTimeSeconds();
        var offlineUntil = DateTimeOffset.FromUnixTimeSeconds(p.Exp);
        int? days = p.Lexp is { } le ? (int)Math.Ceiling((le - t) / 86400d) : null;

        // The clock must never run backwards past the last trusted time, nor before the certificate was issued.
        if (t + ClockToleranceSeconds < watermark || t + ClockToleranceSeconds < p.Iat)
            return new(LicenseState.ClockRollback, p, watermark, days, offlineUntil);
        var newWatermark = Math.Max(watermark, Math.Max(t, p.Iat));

        var state = p.Status switch
        {
            "Revoked" or "Transferred" => LicenseState.Revoked,
            "Suspended" => LicenseState.Suspended,
            "Expired" => LicenseState.LicenseExpired,
            _ when p.Lexp is { } lexp && t >= lexp => LicenseState.LicenseExpired,
            _ when t >= p.Exp => LicenseState.CertificateExpired,
            _ when days is { } d && d <= RenewalWarningDays => LicenseState.RenewSoon,
            _ => LicenseState.Valid,
        };
        return new(state, p, newWatermark, days, offlineUntil);
    }
}
