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
    private const string ThemeExtrasMarker = "/Themes/Theme.";
    private const string StringsMarker = "/Localization/Strings.";

    public bool SystemReducedMotion => !SystemParameters.ClientAreaAnimation;

    public void ApplyLanguage(string code)
    {
        code = Languages.Normalize(code);
        var dicts = Application.Current.Resources.MergedDictionaries;
        foreach (var d in dicts.Where(d => d.Source?.OriginalString.Contains(StringsMarker, StringComparison.Ordinal) == true).ToList())
        {
            var src = d.Source!.OriginalString;
            var tag = src.Contains("Strings.Desktop.", StringComparison.Ordinal) ? "Desktop." : src.Contains("Strings.Erp.", StringComparison.Ordinal) ? "Erp." : "";
            var index = dicts.IndexOf(d);
            dicts[index] = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Localization/Strings.{tag}{code}.xaml") };
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
        var name = theme == "light" ? "Light" : "Dark";
        Swap(ThemeMarker, $"pack://application:,,,/Themes/Colors.{name}.xaml", 0);
        // Hand-written companion palette (gradients, tints, elevation) follows the generated colours.
        Swap(ThemeExtrasMarker, $"pack://application:,,,/Themes/Theme.{name}.xaml", 1);
    }

    private static void Swap(string marker, string uri, int insertAt)
    {
        var dicts = Application.Current.Resources.MergedDictionaries;
        var current = dicts.FirstOrDefault(d => d.Source?.OriginalString.Contains(marker, StringComparison.Ordinal) == true);
        var next = new ResourceDictionary { Source = new Uri(uri) };
        if (current is null) dicts.Insert(Math.Min(insertAt, dicts.Count), next);
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

    public void ShowImage(string path, string title)
    {
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; // file not kept locked (retention can delete it)
        image.UriSource = new Uri(path);
        image.EndInit();
        var window = new Window
        {
            Title = title, Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Width = 900, Height = 620, Background = Brushes.Black,
            Content = new Image { Source = image, Stretch = Stretch.Uniform },
        };
        window.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) window.Close(); };
        window.ShowDialog();
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

/// <summary>DPAPI, LocalMachine scope: any account on this PC can decrypt, no other PC can (used for the device binding).</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiMachineProtector : IDataProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VELTRIX OS device binding v1");

    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.LocalMachine);
    public byte[] Unprotect(byte[] ciphertext) => ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.LocalMachine);
}

/// <summary>
/// Device binding storage on Windows, admin-only (no per-user fallbacks, which a normal user could forge):
/// "file" = %ProgramData%\VELTRIX\binding.dat (the folder ACL is reset to SYSTEM/Administrators full control, Users read) and
/// "registry" = HKLM\SOFTWARE\VELTRIX, value Binding (HKLM\SOFTWARE is read-only for Users by default). Written only by
/// <c>VeltrixOS.exe --bind</c>, which the elevated installer runs.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsBindingStore : Core.Licensing.IDeviceBindingStore
{
    private const string FileLoc = "file";
    private const string RegLoc = "registry";
    private const string SubKey = @"SOFTWARE\VELTRIX";
    private const string ValueName = "Binding";

    public static string MachineDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VELTRIX");
    private static string MachineFile => Path.Combine(MachineDir, "binding.dat");

    public IReadOnlyList<string> Locations { get; } = [FileLoc, RegLoc];

    public byte[]? Read(string location) => location == FileLoc ? ReadFile() : ReadReg();

    public bool Write(string location, byte[] data) => location == FileLoc ? WriteFile(data) : WriteReg(data);

    /// <summary>True when the process runs elevated (administrator token).</summary>
    public static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static byte[]? ReadFile()
    {
        try { return File.Exists(MachineFile) ? File.ReadAllBytes(MachineFile) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool WriteFile(byte[] data)
    {
        try
        {
            var dir = Directory.CreateDirectory(MachineDir);
            // Admin-only writes: drop inherited ProgramData rights (Users may create files there by default).
            var acl = new System.Security.AccessControl.DirectorySecurity();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const System.Security.AccessControl.InheritanceFlags inherit =
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
            void Allow(System.Security.Principal.WellKnownSidType sid, System.Security.AccessControl.FileSystemRights rights) =>
                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(sid, null),
                    rights, inherit, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            Allow(System.Security.Principal.WellKnownSidType.LocalSystemSid, System.Security.AccessControl.FileSystemRights.FullControl);
            Allow(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, System.Security.AccessControl.FileSystemRights.FullControl);
            Allow(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, System.Security.AccessControl.FileSystemRights.ReadAndExecute);
            dir.SetAccessControl(acl);
            File.WriteAllBytes(MachineFile, data);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { return false; }
    }

    private static byte[]? ReadReg()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(SubKey);
            return key?.GetValue(ValueName) as byte[];
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    private static bool WriteReg(byte[] data)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).CreateSubKey(SubKey, writable: true);
            key.SetValue(ValueName, data, RegistryValueKind.Binary);
            return true;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }
}
