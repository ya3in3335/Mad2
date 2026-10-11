using System.Globalization;
using System.Text.Json;
using Veltrix.Desktop.Core.Api;

namespace Veltrix.Desktop.Core.Settings;

/// <summary>Non-secret, per-machine preferences. Secrets (tokens, certificate, device key) live in <see cref="Security.SecureStore"/>.</summary>
public sealed class AppSettings
{
    public string? ServerUrl { get; set; }
    public string Language { get; set; } = "en";
    public string Theme { get; set; } = "dark";
    /// <summary>Null: follow the operating system setting.</summary>
    public bool? ReducedMotion { get; set; }
    public Guid InstallationId { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid? TenantId { get; set; }
    public string? TenantName { get; set; }
    public bool SetupCompleted { get; set; } = true;
    public Guid? RegisterId { get; set; }
    public string? LastUserEmail { get; set; }
    public int ReceiptPaperMm { get; set; } = 80;
    public bool FirstLaunchDone { get; set; }
    /// <summary>Assign an in-store EAN-13 when a product is saved without a barcode.</summary>
    public bool AutoGenerateBarcode { get; set; } = true;
    public LabelPrintSettings Labels { get; set; } = new();

    public static string DefaultLanguage(CultureInfo culture) => culture.TwoLetterISOLanguageName switch
    {
        "ar" => "ar",
        "fr" => "fr",
        _ => "en",
    };
}

/// <summary>Barcode label printing preferences (last printer and size are remembered).</summary>
public sealed class LabelPrintSettings
{
    public string? PrinterName { get; set; }
    /// <summary>"40x30", "50x30", "58x40" or "custom".</summary>
    public string Preset { get; set; } = "40x30";
    public double CustomWidthMm { get; set; } = 40;
    public double CustomHeightMm { get; set; } = 30;
    /// <summary>Print on A4 label sheets instead of a roll of individual labels.</summary>
    public bool SheetMode { get; set; }
    public int SheetRows { get; set; } = 8;
    public int SheetColumns { get; set; } = 3;
    public double SheetMarginMm { get; set; } = 8;
    public double SheetGapMm { get; set; } = 2;
    public bool ShowShopName { get; set; } = true;
}

public static class Languages
{
    public static readonly IReadOnlyList<(string Code, string NativeName)> Supported = [("ar", "العربية"), ("fr", "Français"), ("en", "English")];

    public static bool IsRightToLeft(string code) => string.Equals(code, "ar", StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? code) => code?.ToLowerInvariant() switch
    {
        "ar" or "fr" or "en" => code.ToLowerInvariant(),
        _ => "en",
    };

    /// <summary>Formatting culture for numbers and dates. Arabic uses the Algerian locale with Latin digits, as invoices do.</summary>
    public static CultureInfo Culture(string code) => Normalize(code) switch
    {
        "ar" => CultureInfo.GetCultureInfo("ar-DZ"),
        "fr" => CultureInfo.GetCultureInfo("fr-DZ"),
        _ => CultureInfo.GetCultureInfo("en-US"),
    };
}

/// <summary>Loads and atomically saves <see cref="AppSettings"/> as JSON.</summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Path { get; } = path;

    public AppSettings Load()
    {
        AppSettings? s = null;
        try
        {
            if (File.Exists(Path)) s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Json);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt settings file must never block startup; defaults are used and rewritten on the next save.
        }
        s ??= new AppSettings { Language = AppSettings.DefaultLanguage(CultureInfo.CurrentUICulture) };
        if (s.InstallationId == Guid.Empty) s.InstallationId = Guid.NewGuid();
        s.Language = Languages.Normalize(s.Language);
        s.Theme = s.Theme == "light" ? "light" : "dark";
        if (s.ReceiptPaperMm is not (58 or 80)) s.ReceiptPaperMm = 80;
        s.Labels ??= new LabelPrintSettings();
        return s;
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, Path, overwrite: true);
    }
}

/// <summary>Exposes the configured server address to <see cref="ApiClient"/>.</summary>
public sealed class SettingsServerAddress(AppSettings settings) : IServerAddress
{
    public string? ServerUrl => settings.ServerUrl;
}

public static class ServerUrl
{
    /// <summary>Validates and normalises a user-typed server address. Plain HTTP is only accepted for local/LAN hosts.</summary>
    public static bool TryNormalize(string? input, out string normalized, out string? error)
    {
        normalized = "";
        error = null;
        var text = input?.Trim() ?? "";
        if (text.Length == 0) { error = "required"; return false; }
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || string.IsNullOrEmpty(uri.Host))
        {
            error = "invalid";
            return false;
        }
        if (uri.Scheme == Uri.UriSchemeHttp && !IsLocal(uri.Host))
        {
            error = "insecure";
            return false;
        }
        normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    private static bool IsLocal(string host)
    {
        if (host is "localhost" || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)) return true;
        if (!System.Net.IPAddress.TryParse(host, out var ip)) return false;
        if (System.Net.IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168));
    }
}
