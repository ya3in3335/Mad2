using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Session;
using Veltrix.Desktop.Core.Standalone;

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

    /// <summary>Offline license: local accounts only (no e-mail, MFA or server).</summary>
    public bool IsStandalone { get; } = s.IsStandalone;
    public bool IsOnlineMode => !IsStandalone;
    /// <summary>First run of a standalone installation: create the owner account (or restore a backup).</summary>
    [ObservableProperty] private bool _needsOwnerSetup = s.IsStandalone && !s.Standalone.Auth.HasOwner;
    [ObservableProperty] private string? _infoMessage;

    /// <summary>Set by the view from the confirmation PasswordBox (owner setup).</summary>
    public string ConfirmPassword { private get; set; } = "";
    public string? LicenseBanner => s.License.Current is { } e ? LicenseText.Describe(s.Loc, e) : null;

    /// <summary>Set by the view from the PasswordBox.</summary>
    public string Password { private get; set; } = "";

    [RelayCommand]
    public async Task SignInAsync()
    {
        if (IsBusy) return;
        ErrorMessage = null;
        if (IsStandalone) { await SignInLocalAsync(); return; }
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

    private async Task SignInLocalAsync()
    {
        var auth = s.Standalone.Auth;
        var name = Email.Trim();
        var secret = Password;
        if (name.Length == 0 || secret.Length == 0) { Fail(s.Loc.Get("err.required")); return; }
        IsBusy = true;
        try
        {
            LocalUser user;
            if (NeedsOwnerSetup)
            {
                if (LocalAuthService.ValidateName(name) is { } nameError) { Fail(s.Loc.Get(nameError)); return; }
                if (LocalAuthService.ValidateSecret(secret) is { } secretError) { Fail(s.Loc.Get(secretError)); return; }
                if (secret != ConfirmPassword) { Fail(s.Loc.Get("dk.sa.err.mismatch")); return; }
                user = await Task.Run(() => auth.CreateOwner(name, secret));
                NeedsOwnerSetup = false;
            }
            else
            {
                var r = await Task.Run(() => auth.SignIn(name, secret));
                switch (r.Status)
                {
                    case LocalSignInStatus.SignedIn: user = r.User!; break;
                    case LocalSignInStatus.LockedOut:
                        Fail(s.Loc.Format("dk.sa.lockedFor", Math.Ceiling(r.RetryAfter?.TotalSeconds ?? 30)));
                        return;
                    case LocalSignInStatus.NoUsers:
                        NeedsOwnerSetup = true;
                        return;
                    default:
                        Fail(s.Loc.Get("auth.invalid"));
                        return;
                }
            }
            s.Session.SignInLocal(user);
            s.Settings.LastUserEmail = user.Name;
            s.SaveSettings();
            Password = "";
            ConfirmPassword = "";
            app.Navigate(StartRoute.Shell);
        }
        catch (InvalidOperationException e)
        {
            Fail(s.Loc.Get(e.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>New device: restore an encrypted backup (its password is typed in the password box). Needs a valid license here.</summary>
    [RelayCommand]
    public async Task RestoreBackupAsync()
    {
        if (!IsStandalone || IsBusy) return;
        ErrorMessage = null;
        if (Password.Length == 0) { Fail(s.Loc.Get("dk.sa.backupPasswordPrompt")); return; }
        var path = s.Ui.PickOpenFile(s.Loc.Get("dk.sa.backupFilter") + "|*" + LocalBackup.FileExtension);
        if (path is null) return;
        IsBusy = true;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            var secret = Password;
            await Task.Run(() => s.Standalone.RestoreBackupAsync(bytes, secret));
            NeedsOwnerSetup = !s.Standalone.Auth.HasOwner;
            InfoMessage = s.Loc.Get("dk.sa.restored");
        }
        catch (InvalidOperationException e) { Fail(s.Loc.Get(e.Message)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { s.Log.Warn("Backup restore failed", e); Fail(s.Loc.Get("dk.sa.err.backupFile")); }
        finally { IsBusy = false; }
    }

    private void Fail(string message)
    {
        ErrorMessage = message;
        FailureCount++;
    }
}
