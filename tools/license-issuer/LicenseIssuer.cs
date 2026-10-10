using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Veltrix.Contracts.Licensing;
using Veltrix.SharedKernel;

namespace Veltrix.LicenseIssuer;

/// <summary>Signs offline licenses exactly like the server's LicenseSigner (Web JSON, ES256, DER signature).</summary>
public static class OfflineIssuer
{
    public const string PrivateKeyFile = "vendor-private-key.pem";
    public const string PublicKeyFile = "vendor-public-key.pem";
    public const string FullPlan = "full";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Creates a P-256 key pair in <paramref name="dir"/>. Never overwrites existing files.</summary>
    public static (string PrivatePath, string PublicPath) GenerateKeys(string dir)
    {
        Directory.CreateDirectory(dir);
        var priv = Path.Combine(dir, PrivateKeyFile);
        var pub = Path.Combine(dir, PublicKeyFile);
        if (File.Exists(priv) || File.Exists(pub))
            throw new IOException($"Refusing to overwrite an existing key in {Path.GetFullPath(dir)}.");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        WriteNew(priv, key.ExportPkcs8PrivateKeyPem(), secret: true);
        WriteNew(pub, key.ExportSubjectPublicKeyInfoPem(), secret: false);
        return (priv, pub);
    }

    public static LicenseCertificatePayload BuildPayload(string deviceCode, string shopName, int days, DateTimeOffset now)
    {
        if (days <= 0) throw new ArgumentOutOfRangeException(nameof(days), "Days must be positive.");
        if (string.IsNullOrWhiteSpace(shopName)) throw new ArgumentException("The shop name is required.", nameof(shopName));
        if (!OfflineLicensing.TryDecodeDeviceCode(deviceCode, out var iid, out var kth))
            throw new FormatException("Invalid device code.");
        var iat = now.ToUnixTimeSeconds();
        var exp = now.AddDays(days).ToUnixTimeSeconds();
        return new LicenseCertificatePayload(1, Guid.NewGuid(), Guid.NewGuid(), shopName.Trim(), Guid.NewGuid(), iid, kth, "",
            FullPlan, OfflineLicensing.OfflineType, Features.All.ToList(), 1, iat, exp, exp, "Active");
    }

    public static string Sign(LicenseCertificatePayload payload, string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        if (key.KeySize != 256) throw new CryptographicException("The vendor key must be ECDSA P-256.");
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        var sig = key.SignData(body, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return B64Url(body) + "." + B64Url(sig);
    }

    public static string Issue(string privateKeyPem, string deviceCode, string shopName, int days, DateTimeOffset now) =>
        Sign(BuildPayload(deviceCode, shopName, days, now), privateKeyPem);

    /// <summary>Activation file: '#' comment lines (ignored by the apps) followed by the code.</summary>
    public static string ActivationFileText(LicenseCertificatePayload p, string code)
    {
        var sb = new StringBuilder();
        sb.Append("# VELTRIX OS offline activation code\n");
        sb.Append("# Shop: ").Append(p.TenantName).Append('\n');
        sb.Append("# Plan: ").Append(p.Plan).Append(" (all features)\n");
        sb.Append("# Valid until: ").Append(DateTimeOffset.FromUnixTimeSeconds(p.Exp).ToString("yyyy-MM-dd HH:mm 'UTC'")).Append('\n');
        sb.Append("# License id: ").Append(p.Lid).Append('\n');
        sb.Append(code).Append('\n');
        return sb.ToString();
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Trim().Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '-' : c).ToArray()).Trim('-', '.');
        return s.Length == 0 ? "shop" : s;
    }

    private static void WriteNew(string path, string content, bool secret)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = secret ? UnixFileMode.UserRead | UnixFileMode.UserWrite
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        using var fs = new FileStream(path, options);
        using var w = new StreamWriter(fs, new UTF8Encoding(false));
        w.Write(content);
        w.Write('\n');
    }

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
