using System.Globalization;
using Veltrix.Desktop.Core.Api;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Diagnostics;
using Veltrix.Desktop.Core.Licensing;
using Veltrix.Desktop.Core.Pos;
using Veltrix.Desktop.Core.Security;
using Veltrix.Desktop.Core.Session;
using Veltrix.Desktop.Core.Settings;
using Veltrix.Desktop.Core.Sync;

namespace Veltrix.Desktop.Presentation;

/// <summary>Looks up UI strings for the active language (WPF: the merged Strings.*.xaml dictionaries).</summary>
public interface ILocalizer
{
    string Get(string key);
    CultureInfo Culture { get; }
    event EventHandler? LanguageChanged;
}

public static class LocalizerExtensions
{
    public static string Format(this ILocalizer loc, string key, params object?[] args) => string.Format(loc.Culture, loc.Get(key), args);
}

/// <summary>Operations that need the UI framework (dialogs, printing, resource dictionaries).</summary>
public interface IPlatformUi
{
    void ApplyLanguage(string code);
    void ApplyTheme(string theme);
    /// <summary>Swaps the accent palette (Accents.All code) for the current theme.</summary>
    void ApplyAccent(string accent);
    /// <summary>Prints a receipt layout (as many copies as its page asks). Returns false when the user cancelled or no printer is available.</summary>
    bool PrintReceipt(string title, ReceiptDocument receipt);
    /// <summary>Asks for a destination file. Returns null when cancelled.</summary>
    string? PickSaveFile(string suggestedName, string filter);
    /// <summary>Asks for a file to open. Returns null when cancelled.</summary>
    string? PickOpenFile(string filter);
    void CopyToClipboard(string text);
    bool Confirm(string message);
    /// <summary>Shows an image file (e.g. a sale's camera photo) in a viewer window.</summary>
    void ShowImage(string path, string title);
    bool SystemReducedMotion { get; }
}

/// <summary>Everything the view models need, wired once in the composition root (App.xaml.cs) or by tests.</summary>
public sealed class DesktopServices
{
    public required ApiClient Api { get; init; }
    public required AppSettings Settings { get; init; }
    public required SettingsStore SettingsStore { get; init; }
    public required SecureStore Secrets { get; init; }
    public required SecureTokenStore Tokens { get; init; }
    public required LicenseManager License { get; init; }
    /// <summary>Trust-on-first-use device binding (anti-copy): verified before any local data is opened.</summary>
    public required DeviceBindingManager Binding { get; init; }
    public required SessionService Session { get; init; }
    public required Func<LocalDb> Db { get; init; }
    public required SyncEngine Sync { get; init; }
    public required PosService Pos { get; init; }
    public required ILocalizer Loc { get; init; }
    public required IPlatformUi Ui { get; init; }
    public required TimeProvider Time { get; init; }
    public required AppLog Log { get; init; }
    public required string Version { get; init; }
    /// <summary>Local accounts, repository and backups (standalone mode, the only customer mode).</summary>
    public required Core.Standalone.StandaloneService Standalone { get; init; }
    /// <summary>CCTV over the shop LAN (cameras, sale photos). Null when the video runtime is unavailable.</summary>
    public Core.Cameras.CameraService? Cameras { get; init; }

    /// <summary>Owner of the shop: local owner account, or an online profile with full permissions.</summary>
    public bool IsOwner => Session.LocalUser is { } lu ? lu.IsOwner
        : Session.User is { } u && (u.IsPlatformAdmin || u.Permissions.Contains("*") || u.Roles.Any(r => string.Equals(r, "owner", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Standalone mode: single device bound on first run, local data only, no server and no sync.</summary>
    public bool IsStandalone => Binding.IsBound;

    public void SaveSettings() => SettingsStore.Save(Settings);

    /// <summary>Receipt layout for a sale with the current receipt settings (POS printing, reprint, preview).</summary>
    public ReceiptDocument ReceiptFor(ReceiptModel receipt, bool reprint = false) =>
        ReceiptLayout.Build(receipt, Settings.Receipt, Settings.ReceiptPaperMm, Settings.Language, reprint);

    public ReceiptPage ReceiptPage => ReceiptLayout.Page(Settings.ReceiptPaperMm, Settings.Receipt, Settings.Language);

    public bool ReducedMotion => Settings.ReducedMotion ?? Ui.SystemReducedMotion;
}

public static class Parse
{
    /// <summary>Parses a user-typed amount accepting either ',' or '.' as the decimal separator.</summary>
    public static bool TryDecimal(string? text, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().Replace(" ", "").Replace(" ", "").Replace(" ", "");
        var lastComma = t.LastIndexOf(',');
        var lastDot = t.LastIndexOf('.');
        if (lastComma > lastDot) t = t.Replace(".", "").Replace(',', '.');
        else t = t.Replace(",", "");
        return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    public static decimal DecimalOr(string? text, decimal fallback) => TryDecimal(text, out var v) ? v : fallback;
}
