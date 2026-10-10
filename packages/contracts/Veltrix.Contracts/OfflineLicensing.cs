using System.Text;

namespace Veltrix.Contracts.Licensing;

/// <summary>
/// Offline (server-less) device-bound activation helpers shared by the clients and the owner's issuing tool.
/// Device code = base64url(16-byte installation id (RFC 4122 / big-endian) || 32-byte raw SHA-256 key thumbprint).
/// </summary>
public static class OfflineLicensing
{
    public const string OfflineType = "Offline";
    /// <summary>Length of an un-grouped device code (48 bytes → 64 base64url characters, no padding).</summary>
    public const int DeviceCodeLength = 64;
    private const int GroupSize = 8;

    public static string EncodeDeviceCode(Guid installationId, string keyThumbprintHex)
    {
        var kth = Convert.FromHexString(keyThumbprintHex.Trim());
        if (kth.Length != 32) throw new ArgumentException("The key thumbprint must be a 32-byte SHA-256 hex string.", nameof(keyThumbprintHex));
        var bytes = new byte[48];
        installationId.ToByteArray(bigEndian: true).CopyTo(bytes, 0);
        kth.CopyTo(bytes, 16);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Splits a device code into space-separated blocks of 8 characters for display and copy.</summary>
    public static string FormatDeviceCode(string code)
    {
        var clean = Strip(code);
        var sb = new StringBuilder(clean.Length + clean.Length / GroupSize);
        for (var i = 0; i < clean.Length; i++)
        {
            if (i > 0 && i % GroupSize == 0) sb.Append(' ');
            sb.Append(clean[i]);
        }
        return sb.ToString();
    }

    /// <summary>Decodes a (possibly grouped) device code. The thumbprint is returned as lower-case hex.</summary>
    public static bool TryDecodeDeviceCode(string? text, out Guid installationId, out string keyThumbprint)
    {
        installationId = Guid.Empty;
        keyThumbprint = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var clean = Strip(text);
        if (clean.Length != DeviceCodeLength || clean.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return false;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(clean.Replace('-', '+').Replace('_', '/')); }
        catch (FormatException) { return false; }
        if (bytes.Length != 48) return false;
        installationId = new Guid(bytes.AsSpan(0, 16), bigEndian: true);
        keyThumbprint = Convert.ToHexString(bytes, 16, 32).ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// Extracts the certificate from pasted text or an activation file: '#' comment lines are ignored and whitespace
    /// (line wrapping) is removed. Returns null when nothing looks like <c>base64url.base64url</c>.
    /// </summary>
    public static string? ExtractActivationCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var joined = string.Concat(text.Split('\n').Select(l => l.Trim()).Where(l => !l.StartsWith('#')).Select(Strip));
        var parts = joined.Split('.');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) return null;
        return joined.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ? joined : null;
    }

    private static string Strip(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
}
