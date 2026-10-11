using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Licensing;
using Veltrix.Desktop.Core.Settings;

namespace Veltrix.Desktop.Presentation.ViewModels;

/// <summary>First run: language → privacy. The device binding already happened invisibly; next is the local owner account (login).</summary>
public sealed partial class WelcomeViewModel(DesktopServices s, AppViewModel app) : ObservableObject
{
    [ObservableProperty] private bool _privacyAccepted = s.Settings.FirstLaunchDone;
    [ObservableProperty] private string? _errorMessage;

    public IReadOnlyList<LanguageOption> Languages { get; } = Core.Settings.Languages.Supported.Select(l => new LanguageOption(l.Code, l.NativeName)).ToList();

    public string Language
    {
        get => s.Settings.Language;
        set
        {
            if (value == s.Settings.Language) return;
            s.Settings.Language = Core.Settings.Languages.Normalize(value);
            s.SaveSettings();
            s.Ui.ApplyLanguage(s.Settings.Language);
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    public void Continue()
    {
        ErrorMessage = null;
        if (!PrivacyAccepted) { ErrorMessage = s.Loc.Get("dk.activation.acceptRequired"); return; }
        s.Settings.FirstLaunchDone = true;
        s.SaveSettings();
        app.Navigate(StartRoute.Login);
    }
}

/// <summary>Shown when the device binding is missing, invalid or belongs to another computer. No data is opened.</summary>
public sealed class NotLicensedViewModel(DesktopServices s)
{
    public string Detail { get; } = s.Binding.Current?.Status.ToString() ?? nameof(BindingStatus.NotInstalled);
    public Guid InstallationId { get; } = s.Settings.InstallationId;
}

public static class LicenseText
{
    public static string? Describe(ILocalizer loc, LicenseEvaluation e) => e.State switch
    {
        LicenseState.Missing or LicenseState.Valid => null,
        LicenseState.RenewSoon => loc.Format("lic.renewSoon", e.DaysRemaining ?? 0),
        LicenseState.LicenseExpired => loc.Get("lic.expired"),
        LicenseState.CertificateExpired => loc.Get("dk.license.certExpired"),
        LicenseState.Revoked => loc.Get("lic.revoked"),
        LicenseState.Suspended => loc.Get("dk.license.suspended"),
        LicenseState.InvalidSignature => loc.Get("dk.license.invalid"),
        LicenseState.WrongDevice => loc.Get("dk.license.wrongDevice"),
        LicenseState.ClockRollback => loc.Get("lic.clockRollback"),
        _ => null,
    };
}
