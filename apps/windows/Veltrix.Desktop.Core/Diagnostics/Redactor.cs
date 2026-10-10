using System.Text.RegularExpressions;

namespace Veltrix.Desktop.Core.Diagnostics;

/// <summary>Removes secrets and personal data from text before it leaves the machine in a diagnostics bundle.</summary>
public static partial class Redactor
{
    [GeneratedRegex(@"(?i)\bbearer\s+[A-Za-z0-9\-_\.=]+")] private static partial Regex Bearer();
    [GeneratedRegex(@"\beyJ[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]+")] private static partial Regex Jwt();
    [GeneratedRegex(@"(?i)\bVLX(-[A-Z0-9]{5}){4}\b")] private static partial Regex LicenseKey();
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")] private static partial Regex Email();
    [GeneratedRegex(@"(?i)(""?(password|refreshToken|accessToken|setupToken|certificate|secret|pin|token)""?\s*[:=]\s*)(""[^""]*""|[^\s,;}]+)")] private static partial Regex KeyValue();
    [GeneratedRegex(@"\b[A-Za-z0-9\-_]{40,}\.[A-Za-z0-9\-_]{40,}\b")] private static partial Regex Certificate();

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = KeyValue().Replace(text, m => m.Groups[1].Value + "\"[redacted]\"");
        text = Bearer().Replace(text, "Bearer [redacted]");
        text = Jwt().Replace(text, "[jwt]");
        text = Certificate().Replace(text, "[certificate]");
        text = LicenseKey().Replace(text, "VLX-[redacted]");
        text = Email().Replace(text, m => MaskEmail(m.Value));
        return text;
    }

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        return (at <= 1 ? "*" : email[0] + new string('*', at - 1)) + email[at..];
    }
}
