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
    /// <summary>Prints monospaced text (receipts). Returns false when the user cancelled or no printer is available.</summary>
    bool PrintText(string title, string text, int widthChars);
    /// <summary>Asks for a destination file. Returns null when cancelled.</summary>
    string? PickSaveFile(string suggestedName, string filter);
    bool Confirm(string message);
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
    public required SessionService Session { get; init; }
    public required Func<LocalDb> Db { get; init; }
    public required SyncEngine Sync { get; init; }
    public required PosService Pos { get; init; }
    public required ILocalizer Loc { get; init; }
    public required IPlatformUi Ui { get; init; }
    public required TimeProvider Time { get; init; }
    public required AppLog Log { get; init; }
    public required string Version { get; init; }

    public void SaveSettings() => SettingsStore.Save(Settings);

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
