using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Api;
using Veltrix.Desktop.Core.Licensing;
using Veltrix.Desktop.Core.Settings;

namespace Veltrix.Desktop.Presentation.ViewModels;

/// <summary>Server address + license key → challenge/response activation of this device.</summary>
public sealed partial class ActivationViewModel : ObservableObject
{
    private readonly DesktopServices _s;
    private readonly AppViewModel _app;

    public ActivationViewModel(DesktopServices s, AppViewModel app)
    {
        _s = s;
        _app = app;
        _serverUrl = s.Settings.ServerUrl ?? "";
        var eval = s.License.Current;
        _licenseMessage = eval is null ? null : LicenseText.Describe(s.Loc, eval);
        _privacyAccepted = s.Settings.FirstLaunchDone;
        OfflineConfigured = s.License.OfflineActivationConfigured;
        DeviceCode = s.License.DeviceCode;
    }

    // ---- Offline activation (no server): device code → owner issues an activation code → paste or open it here ----
    public bool OfflineConfigured { get; }
    public string DeviceCode { get; }
    [ObservableProperty] private string _activationCode = "";
    [ObservableProperty] private bool _showOffline;

    [RelayCommand]
    public void CopyDeviceCode() => _s.Ui.CopyToClipboard(DeviceCode);

    [RelayCommand]
    public void OpenActivationFile()
    {
        var path = _s.Ui.PickOpenFile(_s.Loc.Get("dk.offline.fileFilter") + "|*.txt|*.*|*.*");
        if (path is null) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 64 * 1024) { ErrorMessage = _s.Loc.Get("dk.offline.invalid"); return; }
            ActivationCode = File.ReadAllText(path).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = _s.Loc.Get("err.generic");
        }
    }

    [RelayCommand(CanExecute = nameof(CanActivate))]
    public async Task ActivateOfflineAsync()
    {
        ErrorMessage = null;
        if (!PrivacyAccepted) { ErrorMessage = _s.Loc.Get("dk.activation.acceptRequired"); return; }
        var result = _s.License.ActivateOffline(ActivationCode);
        if (result != OfflineActivationResult.Activated)
        {
            ErrorMessage = _s.Loc.Get(result switch
            {
                OfflineActivationResult.NotConfigured => "dk.offline.notConfigured",
                OfflineActivationResult.WrongDevice => "dk.license.wrongDevice",
                OfflineActivationResult.Expired => "lic.expired",
                _ => "dk.offline.invalid",
            });
            return;
        }
        _s.Settings.FirstLaunchDone = true;
        _s.SaveSettings();
        _s.Log.Info("Device activated offline");
        Activated = true;
        ActivationCode = "";
        await Task.Delay(_s.ReducedMotion ? 150 : 900);
        _app.Navigate(StartRoute.Login);
    }

    [ObservableProperty] private string _serverUrl;
    [ObservableProperty] private string _licenseKey = "";
    [ObservableProperty] private bool _privacyAccepted;
    [ObservableProperty] private string? _licenseMessage;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ActivateCommand))] [NotifyCanExecuteChangedFor(nameof(ActivateOfflineCommand))] private bool _isBusy;
    [ObservableProperty] private bool _activated;

    public IReadOnlyList<LanguageOption> Languages { get; } = Core.Settings.Languages.Supported.Select(l => new LanguageOption(l.Code, l.NativeName)).ToList();

    public string Language
    {
        get => _s.Settings.Language;
        set
        {
            if (value == _s.Settings.Language) return;
            _s.Settings.Language = Core.Settings.Languages.Normalize(value);
            _s.SaveSettings();
            _s.Ui.ApplyLanguage(_s.Settings.Language);
            OnPropertyChanged();
        }
    }

    private bool CanActivate() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanActivate))]
    public async Task ActivateAsync()
    {
        ErrorMessage = null;
        if (!PrivacyAccepted) { ErrorMessage = _s.Loc.Get("dk.activation.acceptRequired"); return; }
        if (!Core.Settings.ServerUrl.TryNormalize(ServerUrl, out var url, out var why))
        {
            ErrorMessage = _s.Loc.Get(why switch { "insecure" => "dk.err.serverInsecure", "required" => "err.required", _ => "dk.err.serverInvalid" });
            return;
        }
        var key = LicenseManager.NormalizeKey(LicenseKey);
        if (key.Length < 10) { ErrorMessage = _s.Loc.Get("dk.err.licenseKey"); return; }

        IsBusy = true;
        try
        {
            if (!string.Equals(_s.Settings.ServerUrl, url, StringComparison.OrdinalIgnoreCase))
            {
                _s.Settings.ServerUrl = url;
                _s.Tokens.Clear();
            }
            _s.Settings.FirstLaunchDone = true;
            _s.SaveSettings();
            var res = await _s.License.ActivateAsync(key);
            _s.Log.Info($"Device activated for tenant {res.TenantId}");
            Activated = true;
            LicenseKey = "";
            await Task.Delay(_s.ReducedMotion ? 150 : 900); // "license activated" micro-interaction
            _app.Navigate(!res.SetupCompleted && res.SetupToken is not null ? StartRoute.Onboarding : StartRoute.Login);
        }
        catch (ApiException e) when (e.Code is not null && e.Code.Contains("seat", StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = _s.Loc.Get("lic.seatsExhausted");
        }
        catch (Exception e)
        {
            _s.Log.Warn("Activation failed", e);
            ErrorMessage = ErrorText.Describe(_s.Loc, e);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Re-checks an existing activation (clock rollback, suspension) by renewing the certificate online.</summary>
    [RelayCommand]
    public async Task CheckAgainAsync()
    {
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            if (!await _s.License.RefreshAsync()) { ErrorMessage = _s.Loc.Get("err.network"); return; }
            var eval = _s.License.Current!;
            LicenseMessage = LicenseText.Describe(_s.Loc, eval);
            if (!eval.RequiresActivation && eval.State is not (LicenseState.ClockRollback or LicenseState.Suspended)) _app.Navigate(StartRoute.Login);
        }
        catch (Exception e)
        {
            ErrorMessage = ErrorText.Describe(_s.Loc, e);
        }
        finally
        {
            IsBusy = false;
        }
    }
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
