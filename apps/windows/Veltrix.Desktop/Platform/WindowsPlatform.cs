using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;
using Veltrix.Desktop.Core.Security;
using Veltrix.Desktop.Core.Settings;
using Veltrix.Desktop.Presentation;

namespace Veltrix.Desktop.Platform;

/// <summary>Windows DPAPI (current-user scope) with an application-specific entropy.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiProtector : IDataProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VELTRIX OS desktop secrets v1");

    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] ciphertext) => ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
}

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VELTRIX OS");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Secrets => Path.Combine(Root, "secure");
    public static string Data => Path.Combine(Root, "data");
    public static string Database => Path.Combine(Data, "veltrix.db");
    public static string Logs => Path.Combine(Root, "logs");
}

[SupportedOSPlatform("windows")]
public static class WindowsFingerprint
{
    /// <summary>Limited, non-identifying inputs (no MAC, disk or board serials) — see DeviceFingerprint.</summary>
    public static FingerprintInputs Read()
    {
        string? machineGuid = null;
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            machineGuid = key?.GetValue("MachineGuid") as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return new FingerprintInputs(Environment.MachineName, Environment.ProcessorCount, Environment.OSVersion.Version.ToString(), machineGuid);
    }
}

/// <summary>Reads strings from the merged Strings.*.xaml dictionaries.</summary>
public sealed class WpfLocalizer(AppSettings settings) : ILocalizer
{
    public string Get(string key) => Application.Current?.TryFindResource(key) as string ?? key;
    public CultureInfo Culture => Languages.Culture(settings.Language);
    public event EventHandler? LanguageChanged;
    internal void RaiseChanged() => LanguageChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class WpfPlatformUi(WpfLocalizer localizer) : IPlatformUi
{
    private const string ThemeMarker = "/Themes/Colors.";
    private const string StringsMarker = "/Localization/Strings.";

    public bool SystemReducedMotion => !SystemParameters.ClientAreaAnimation;

    public void ApplyLanguage(string code)
    {
        code = Languages.Normalize(code);
        var dicts = Application.Current.Resources.MergedDictionaries;
        foreach (var d in dicts.Where(d => d.Source?.OriginalString.Contains(StringsMarker, StringComparison.Ordinal) == true).ToList())
        {
            var desktop = d.Source!.OriginalString.Contains("Strings.Desktop.", StringComparison.Ordinal);
            var index = dicts.IndexOf(d);
            dicts[index] = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Localization/Strings.{(desktop ? "Desktop." : "")}{code}.xaml") };
        }
        var culture = Languages.Culture(code);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        foreach (Window w in Application.Current.Windows) ApplyTo(w, code);
        localizer.RaiseChanged();
    }

    public static void ApplyTo(FrameworkElement element, string code)
    {
        element.FlowDirection = Languages.IsRightToLeft(code) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        element.Language = XmlLanguage.GetLanguage(Languages.Culture(code).IetfLanguageTag);
    }

    public void ApplyTheme(string theme)
    {
        var dicts = Application.Current.Resources.MergedDictionaries;
        var current = dicts.FirstOrDefault(d => d.Source?.OriginalString.Contains(ThemeMarker, StringComparison.Ordinal) == true);
        var next = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/Colors.{(theme == "light" ? "Light" : "Dark")}.xaml") };
        if (current is null) dicts.Insert(0, next);
        else dicts[dicts.IndexOf(current)] = next;
    }

    public bool PrintText(string title, string text, int widthChars)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return false;
        // Receipt layout is fixed-width text: keep it left-to-right even in Arabic so columns stay aligned.
        var fontSize = widthChars <= 32 ? 9.0 : 8.5;
        var doc = new FlowDocument(new Paragraph(new Run(text)))
        {
            FontFamily = new FontFamily("Consolas, Courier New"),
            FontSize = fontSize,
            FlowDirection = FlowDirection.LeftToRight,
            PagePadding = new Thickness(8),
            ColumnWidth = double.PositiveInfinity,
            PageWidth = Math.Min(dialog.PrintableAreaWidth, widthChars * fontSize * 0.62 + 24),
        };
        doc.PageHeight = dialog.PrintableAreaHeight;
        dialog.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, "VELTRIX OS - " + title);
        return true;
    }

    public string? PickSaveFile(string suggestedName, string filter)
    {
        var dlg = new SaveFileDialog { FileName = suggestedName, Filter = filter, AddExtension = true, OverwritePrompt = true };
        return dlg.ShowDialog(Application.Current.MainWindow) == true ? dlg.FileName : null;
    }

    public string? PickOpenFile(string filter)
    {
        var dlg = new OpenFileDialog { Filter = filter, CheckFileExists = true, Multiselect = false };
        return dlg.ShowDialog(Application.Current.MainWindow) == true ? dlg.FileName : null;
    }

    public void CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { } // clipboard busy: the user can still select the text
    }

    public bool Confirm(string message)
    {
        var owner = Application.Current.MainWindow;
        var rtl = owner?.FlowDirection == FlowDirection.RightToLeft;
        var options = rtl ? MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading : MessageBoxOptions.None;
        return MessageBox.Show(owner!, message, localizer.Get("app.name"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No, options)
               == MessageBoxResult.Yes;
    }
}
