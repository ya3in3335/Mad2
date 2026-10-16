using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Standalone;

namespace Veltrix.Desktop.Presentation.ViewModels;

public enum SectionLockMode { Unlock, Setup, WaitForOwner, Reset, Change }

/// <summary>
/// Gate shown instead of a protected page (HR, workforce, cameras) until the section password is entered. At the first opening an owner
/// creates that password; other users are asked to wait for an owner. Passwords come from PasswordBoxes (never bound back).
/// </summary>
public sealed partial class SectionLockViewModel : PageViewModel
{
    private readonly Action _onUnlocked;

    public SectionLockViewModel(DesktopServices s, Action onUnlocked) : base(s)
    {
        _onUnlocked = onUnlocked;
        Mode = !Lock.IsConfigured ? (IsOwner ? SectionLockMode.Setup : SectionLockMode.WaitForOwner) : SectionLockMode.Unlock;
    }

    public override string TitleKey => "sec.title";
    private SectionLockService Lock => S.Standalone.SectionLock;
    private LocalUser? User => S.Session.LocalUser;
    public bool IsOwner => User?.IsOwner == true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnlockMode), nameof(IsSetupMode), nameof(IsWaitMode), nameof(IsResetMode), nameof(IsChangeMode), nameof(NeedsConfirm),
        nameof(NeedsCurrent), nameof(NeedsOwnerPassword), nameof(Headline))]
    private SectionLockMode _mode;
    [ObservableProperty] private string? _formError;

    public string Password { get; set; } = "";
    public string Confirm { get; set; } = "";
    public string Current { get; set; } = "";
    public string OwnerPassword { get; set; } = "";

    public bool IsUnlockMode => Mode == SectionLockMode.Unlock;
    public bool IsSetupMode => Mode == SectionLockMode.Setup;
    public bool IsWaitMode => Mode == SectionLockMode.WaitForOwner;
    public bool IsResetMode => Mode == SectionLockMode.Reset;
    public bool IsChangeMode => Mode == SectionLockMode.Change;
    public bool NeedsConfirm => Mode is SectionLockMode.Setup or SectionLockMode.Reset or SectionLockMode.Change;
    public bool NeedsCurrent => Mode == SectionLockMode.Change;
    public bool NeedsOwnerPassword => Mode == SectionLockMode.Reset;
    public string Headline => Loc.Get(HeadlineKey);
    public string HeadlineKey => Mode switch
    {
        SectionLockMode.Setup => "sec.setupTitle",
        SectionLockMode.WaitForOwner => "sec.waitTitle",
        SectionLockMode.Reset => "sec.resetTitle",
        SectionLockMode.Change => "sec.changeTitle",
        _ => "sec.unlockTitle",
    };

    /// <summary>Raised after an action so the view clears its password boxes.</summary>
    public event EventHandler? ClearRequested;

    protected override Task<bool> LoadAsync(CancellationToken ct) => Task.FromResult(true);

    [RelayCommand]
    public void Submit()
    {
        FormError = null;
        try
        {
            switch (Mode)
            {
                case SectionLockMode.Unlock:
                    var r = Lock.Unlock(Password);
                    if (r.Status != SectionUnlockStatus.Unlocked)
                    {
                        FormError = r.Status == SectionUnlockStatus.LockedOut
                            ? Loc.Format("sec.err.retryAfter", Math.Ceiling(r.RetryAfter?.TotalSeconds ?? 30))
                            : Loc.Get("sec.err.invalid");
                        return;
                    }
                    break;
                case SectionLockMode.Setup:
                    Lock.Setup(RequireUser(), Password, Confirm);
                    break;
                case SectionLockMode.Reset:
                    Lock.Reset(RequireUser(), S.Standalone.Auth, OwnerPassword, Password, Confirm);
                    break;
                case SectionLockMode.Change:
                    Lock.Change(RequireUser(), Current, Password, Confirm);
                    StatusMessage = Loc.Get("sec.changed");
                    Mode = SectionLockMode.Unlock;
                    return;
                default:
                    return;
            }
            _onUnlocked();
        }
        catch (InvalidOperationException e) { FormError = Describe(e); }
        finally
        {
            (Password, Confirm, Current, OwnerPassword) = ("", "", "", "");
            ClearRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private LocalUser RequireUser() => User ?? throw new InvalidOperationException("sec.err.ownerOnly");

    [RelayCommand]
    public void ShowReset() => (Mode, FormError) = (SectionLockMode.Reset, null);

    [RelayCommand]
    public void ShowChange() => (Mode, FormError) = (SectionLockMode.Change, null);

    [RelayCommand]
    public void BackToUnlock() => (Mode, FormError) = (Lock.IsConfigured ? SectionLockMode.Unlock : IsOwner ? SectionLockMode.Setup : SectionLockMode.WaitForOwner, null);
}
