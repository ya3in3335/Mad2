using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Contracts.Tenants;
using Veltrix.Desktop.Core.Api;
using Veltrix.Desktop.Core.Security;

namespace Veltrix.Desktop.Presentation.ViewModels;

/// <summary>First-run company setup, authorised by the one-time setup token returned at activation.</summary>
public sealed partial class OnboardingViewModel(DesktopServices s, AppViewModel app) : ObservableObject
{
    [ObservableProperty] private string _companyName = s.Settings.TenantName ?? "";
    [ObservableProperty] private string _taxId = "";
    [ObservableProperty] private string _tradeRegisterNo = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _city = "";
    [ObservableProperty] private string _wilaya = "";
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _currency = "DZD";
    [ObservableProperty] private bool _applyAlgeriaTaxTemplate = true;
    [ObservableProperty] private string _headOfficeName = "Head office";
    [ObservableProperty] private string _ownerFullName = "";
    [ObservableProperty] private string _ownerEmail = "";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isBusy;

    /// <summary>Set by the view from the PasswordBox (never bound as plain text).</summary>
    public string OwnerPassword { private get; set; } = "";
    public string OwnerPasswordConfirm { private get; set; } = "";

    public static bool MeetsPasswordPolicy(string p)
    {
        if (p.Length < 10) return false;
        var classes = (p.Any(char.IsUpper) ? 1 : 0) + (p.Any(char.IsLower) ? 1 : 0) + (p.Any(char.IsDigit) ? 1 : 0) + (p.Any(c => !char.IsLetterOrDigit(c)) ? 1 : 0);
        return classes >= 3;
    }

    [RelayCommand]
    public async Task CompleteAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(CompanyName) || string.IsNullOrWhiteSpace(OwnerFullName) || string.IsNullOrWhiteSpace(OwnerEmail) || string.IsNullOrWhiteSpace(HeadOfficeName))
        { ErrorMessage = s.Loc.Get("err.required"); return; }
        if (!OwnerEmail.Contains('@')) { ErrorMessage = s.Loc.Get("err.invalidEmail"); return; }
        if (!MeetsPasswordPolicy(OwnerPassword)) { ErrorMessage = s.Loc.Get("err.passwordPolicy"); return; }
        if (OwnerPassword != OwnerPasswordConfirm) { ErrorMessage = s.Loc.Get("dk.err.passwordMismatch"); return; }
        var token = s.Secrets.Get(SecureStore.SetupToken);
        if (token is null) { app.Navigate(StartRoute.Login); return; }

        IsBusy = true;
        try
        {
            var locale = s.Settings.Language switch { "ar" => "ar-DZ", "fr" => "fr-DZ", _ => "en" };
            await s.Api.SendAsync<OnboardingResult>(HttpMethod.Post, "/api/v1/onboarding/complete", new CompleteOnboardingRequest(token, CompanyName.Trim(), null,
                Opt(TaxId), Opt(TradeRegisterNo), Opt(Address), Opt(City), Opt(Wilaya), Opt(Phone), Currency.Trim().ToUpperInvariant(), locale,
                OwnerEmail.Trim(), OwnerFullName.Trim(), OwnerPassword, s.Settings.Language, HeadOfficeName.Trim(), ApplyAlgeriaTaxTemplate), authenticate: false);
            s.Secrets.Set(SecureStore.SetupToken, null);
            s.Settings.SetupCompleted = true;
            s.Settings.TenantName = CompanyName.Trim();
            s.Settings.LastUserEmail = OwnerEmail.Trim();
            s.SaveSettings();
            app.Navigate(StartRoute.Login);
        }
        catch (Exception e)
        {
            s.Log.Warn("Onboarding failed", e);
            ErrorMessage = ErrorText.Describe(s.Loc, e);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string? Opt(string v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
