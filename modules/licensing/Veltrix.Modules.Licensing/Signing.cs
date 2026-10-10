using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Veltrix.Contracts.Licensing;

namespace Veltrix.Modules.Licensing;

public sealed class LicensingOptions
{
    public const string Section = "Licensing";
    /// <summary>PKCS#8 PEM of the ECDSA P-256 signing key. Server-side only; never shipped in clients.</summary>
    public string? SigningKeyPem { get; set; }
    public string? SigningKeyPath { get; set; }
    public int ChallengeSeconds { get; set; } = 300;
    /// <summary>Upper bound on certificate validity even if the plan grace is larger.</summary>
    public int MaxOfflineDays { get; set; } = 30;
}

/// <summary>Signs license certificates with the vendor key (ES256, DER signature) and verifies device signatures.</summary>
public sealed class LicenseSigner : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ECDsa _key;

    public LicenseSigner(LicensingOptions options, IHostEnvironment env, IConfiguration config)
    {
        _key = ECDsa.Create();
        var pem = options.SigningKeyPem;
        if (string.IsNullOrWhiteSpace(pem) && !string.IsNullOrWhiteSpace(options.SigningKeyPath) && File.Exists(options.SigningKeyPath))
            pem = File.ReadAllText(options.SigningKeyPath);
        if (string.IsNullOrWhiteSpace(pem))
        {
            if (!env.IsDevelopment() && !env.IsEnvironment("Testing"))
                throw new InvalidOperationException("Licensing signing key is not configured (Licensing__SigningKeyPem or Licensing__SigningKeyPath).");
            var dir = config["Veltrix:DataDirectory"] ?? Path.Combine(AppContext.BaseDirectory, ".veltrix-data");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "dev-license-signing.pem");
            if (!File.Exists(path))
            {
                using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                File.WriteAllText(path, k.ExportPkcs8PrivateKeyPem());
            }
            pem = File.ReadAllText(path);
        }
        _key.ImportFromPem(pem);
        if (_key.KeySize != 256) throw new InvalidOperationException("Licensing signing key must be ECDSA P-256.");
        PublicKeySpki = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
        PublicKeyPem = _key.ExportSubjectPublicKeyInfoPem();
    }

    public string PublicKeySpki { get; }
    public string PublicKeyPem { get; }

    public string Sign(LicenseCertificatePayload payload)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        var sig = _key.SignData(body, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return B64Url(body) + "." + B64Url(sig);
    }

    /// <summary>Verify a certificate with a public key (used by tests and the .NET desktop client).</summary>
    public static LicenseCertificatePayload? Verify(string certificate, string publicKeySpki)
    {
        var parts = certificate.Split('.');
        if (parts.Length != 2) return null;
        using var pub = ECDsa.Create();
        pub.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpki), out _);
        var body = FromB64Url(parts[0]);
        if (!pub.VerifyData(body, FromB64Url(parts[1]), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) return null;
        return JsonSerializer.Deserialize<LicenseCertificatePayload>(body, Json);
    }

    public static bool VerifyDeviceSignature(string publicKeySpki, string message, string signatureB64)
    {
        try
        {
            using var pub = ECDsa.Create();
            pub.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpki), out _);
            if (pub.KeySize != 256) return false;
            var sig = Convert.FromBase64String(signatureB64);
            return pub.VerifyData(Encoding.UTF8.GetBytes(message), sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    public static string Thumbprint(string publicKeySpki) =>
        Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(publicKeySpki))).ToLowerInvariant();

    public static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    public void Dispose() => _key.Dispose();
}

public static class LicenseKeys
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I

    /// <summary>Format VLX-XXXXX-XXXXX-XXXXX-XXXXX (100 bits of entropy).</summary>
    public static string New()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        var chars = bytes.Select(b => Alphabet[b % Alphabet.Length]).ToArray();
        return "VLX-" + string.Join('-', Enumerable.Range(0, 4).Select(i => new string(chars, i * 5, 5)));
    }

    public static string Normalize(string key) => new string(key.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(key))));
    public static string Hint(string key) => Normalize(key)[^4..];
}
