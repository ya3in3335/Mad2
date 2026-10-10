using System.Text.RegularExpressions;

namespace Veltrix.SharedKernel;

/// <summary>Server-side scrubbing of free text coming from clients (error reports, diagnostics) or going to external channels
/// (Telegram). Clients are expected to sanitize too, but the server never trusts that.</summary>
public static partial class Redaction
{
    public const string Mask = "[redacted]";

    // Order matters: the most specific patterns first.
    [GeneratedRegex(@"-----BEGIN [A-Z ]+-----[\s\S]*?(-----END [A-Z ]+-----|$)", RegexOptions.CultureInvariant)]
    private static partial Regex PemBlock();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}(\.[A-Za-z0-9_-]*)?", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex AuthHeader();

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|private[_-]?key|authorization|license[_-]?key|pin|cvv|otp)\b(\s*[""']?\s*[:=]\s*[""']?)[^\s""',;&}]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueSecret();

    [GeneratedRegex(@"(?i)\bbot\d{6,}:[A-Za-z0-9_-]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex TelegramToken();

    [GeneratedRegex(@"(?i)\bVLX(-?[A-Z0-9]{5}){4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex LicenseKey();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    /// <summary>13-19 digits optionally separated by spaces/dashes (payment card numbers); validated with Luhn before masking.</summary>
    [GeneratedRegex(@"\b\d(?:[ -]?\d){12,18}\b", RegexOptions.CultureInvariant)]
    private static partial Regex CardCandidate();

    /// <summary>Long hex or base64-ish runs (API keys, hashes, key material). GUIDs are left intact (useful, not secret).</summary>
    [GeneratedRegex(@"\b(?:[A-Fa-f0-9]{32,}|[A-Za-z0-9+/_-]{40,}={0,2})", RegexOptions.CultureInvariant)]
    private static partial Regex LongSecret();

    public static string Sanitize(string? input, int maxLength = 4000)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var s = input.Length > maxLength * 2 ? input[..(maxLength * 2)] : input;
        s = PemBlock().Replace(s, Mask);
        s = Jwt().Replace(s, Mask);
        s = AuthHeader().Replace(s, m => m.Groups[1].Value + " " + Mask);
        s = TelegramToken().Replace(s, Mask);
        s = KeyValueSecret().Replace(s, m => m.Groups[1].Value + m.Groups[2].Value + Mask);
        s = LicenseKey().Replace(s, Mask);
        s = Email().Replace(s, Mask);
        s = CardCandidate().Replace(s, m => Luhn(m.Value) ? Mask : m.Value);
        s = LongSecret().Replace(s, Mask);
        // Strip control characters other than newline/tab.
        s = new string(s.Where(c => c is '\n' or '\t' || !char.IsControl(c)).ToArray());
        return s.Length > maxLength ? s[..maxLength] : s;
    }

    public static bool Luhn(string candidate)
    {
        var digits = candidate.Where(char.IsDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19) return false;
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i];
            if (i % 2 == 1) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return sum % 10 == 0;
    }

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex Guid();

    [GeneratedRegex(@"""[^""]*""|'[^']*'", RegexOptions.CultureInvariant)]
    private static partial Regex Quoted();

    [GeneratedRegex(@"\b0x[0-9a-fA-F]+\b|\d+", RegexOptions.CultureInvariant)]
    private static partial Regex Number();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    /// <summary>Normalizes a (sanitized) message for grouping: ids, numbers and quoted literals become placeholders.</summary>
    public static string NormalizeForFingerprint(string sanitized)
    {
        var s = Guid().Replace(sanitized, "<id>");
        s = Quoted().Replace(s, "<str>");
        s = Number().Replace(s, "<n>");
        s = Spaces().Replace(s, " ").Trim().ToLowerInvariant();
        return s.Length > 500 ? s[..500] : s;
    }
}
