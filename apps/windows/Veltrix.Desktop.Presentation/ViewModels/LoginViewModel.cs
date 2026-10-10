using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Session;

namespace Veltrix.Desktop.Presentation.ViewModels;

public sealed partial class LoginViewModel(DesktopServices s, AppViewModel app) : ObservableObject
{
    [ObservableProperty] private string _email = s.Settings.LastUserEmail ?? "";
    [ObservableProperty] private string _mfaCode = "";
    [ObservableProperty] private bool _mfaRequired;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isBusy;
    /// <summary>Incremented on each failed attempt so the view can play the error shake.</summary>
    [ObservableProperty] private int _failureCount;

    public string? TenantName => s.Settings.TenantName;
    public string? LicenseBanner => s.License.Current is { } e ? LicenseText.Describe(s.Loc, e) : null;

    /// <summary>Set by the view from the PasswordBox.</summary>
    public string Password { private get; set; } = "";

    [RelayCommand]
    public async Task SignInAsync()
    {
        if (IsBusy) return;
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(Email) || Password.Length == 0) { Fail(s.Loc.Get("err.required")); return; }
        if (MfaRequired && string.IsNullOrWhiteSpace(MfaCode)) { Fail(s.Loc.Get("err.required")); return; }
        IsBusy = true;
        try
        {
            var r = await s.Session.SignInAsync(Email, Password, MfaRequired ? MfaCode : null, s.Settings.DeviceId);
            switch (r.Status)
            {
                case SignInStatus.MfaRequired:
                    MfaRequired = true;
                    break;
                case SignInStatus.SignedIn or SignInStatus.SignedInOffline:
                    s.Settings.LastUserEmail = Email.Trim();
                    if (r.User?.PreferredLanguage is { } lang && Core.Settings.Languages.Normalize(lang) != s.Settings.Language && !s.Settings.FirstLaunchDone)
                    {
                        s.Settings.Language = Core.Settings.Languages.Normalize(lang);
                        s.Ui.ApplyLanguage(s.Settings.Language);
                    }
                    s.SaveSettings();
                    Password = "";
                    app.Navigate(StartRoute.Shell);
                    break;
                default:
                    Fail(r.ErrorCode switch
                    {
                        "offline" => s.Loc.Get("dk.login.offlineFailed"),
                        "required" => s.Loc.Get("err.required"),
                        "auth.locked_out" => s.Loc.Get("auth.lockedOut"),
                        "auth.invalid_credentials" => s.Loc.Get("auth.invalid"),
                        "auth.invalid_mfa" => s.Loc.Get("dk.login.mfaInvalid"),
                        _ => r.Message ?? s.Loc.Get("auth.invalid"),
                    });
                    break;
            }
        }
        catch (Exception e)
        {
            s.Log.Warn("Sign-in failed", e);
            Fail(ErrorText.Describe(s.Loc, e));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void ChangeServer() => app.Navigate(StartRoute.Activation);

    private void Fail(string message)
    {
        ErrorMessage = message;
        FailureCount++;
    }
}
