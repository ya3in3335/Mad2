using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Diagnostics;
using Veltrix.Desktop.Core.Licensing;
using Veltrix.Desktop.Core.Settings;

namespace Veltrix.Desktop.Presentation.ViewModels;

public sealed record OutboxIssue(long Seq, string Type, string Status, int Attempts, string? Error, DateTimeOffset ClientTime);
public sealed record LanguageOption(string Code, string Name);

public sealed partial class SettingsViewModel(DesktopServices s, ShellViewModel shell) : PageViewModel(s)
{
    public override string TitleKey => "nav.settings";

    public IReadOnlyList<LanguageOption> LanguageOptions { get; } = Languages.Supported.Select(l => new LanguageOption(l.Code, l.NativeName)).ToList();
    public IReadOnlyList<int> PaperWidths { get; } = [58, 80];
    public ObservableCollection<OutboxIssue> Issues { get; } = [];
    public ShellViewModel Shell { get; } = shell;

    [ObservableProperty] private string _serverUrl = s.Settings.ServerUrl ?? "";
    [ObservableProperty] private string? _lastSync;
    [ObservableProperty] private int _pending;
    [ObservableProperty] private string? _licenseStatus;
    [ObservableProperty] private string? _licensePlan;
    [ObservableProperty] private string? _licenseExpires;
    [ObservableProperty] private string? _offlineUntil;
    [ObservableProperty] private string? _seats;

    public string Version => S.Version;
    public string InstallationId => S.Settings.InstallationId.ToString();
    public string? DeviceId => S.Settings.DeviceId?.ToString();

    public string Language
    {
        get => S.Settings.Language;
        set
        {
            var code = Languages.Normalize(value);
            if (code == S.Settings.Language) return;
            S.Settings.Language = code;
            S.SaveSettings();
            S.Ui.ApplyLanguage(code);
            OnPropertyChanged();
            _ = ReloadAsync();
        }
    }

    public bool IsDarkTheme
    {
        get => S.Settings.Theme != "light";
        set
        {
            S.Settings.Theme = value ? "dark" : "light";
            S.SaveSettings();
            S.Ui.ApplyTheme(S.Settings.Theme);
            OnPropertyChanged();
        }
    }

    public bool ReducedMotion
    {
        get => S.ReducedMotion;
        set
        {
            S.Settings.ReducedMotion = value;
            S.SaveSettings();
            OnPropertyChanged();
        }
    }

    public int ReceiptPaperMm
    {
        get => S.Settings.ReceiptPaperMm;
        set
        {
            S.Settings.ReceiptPaperMm = value is 58 ? 58 : 80;
            S.SaveSettings();
            OnPropertyChanged();
        }
    }

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        await using (var db = S.Db())
        {
            var last = await db.GetValueAsync(LocalDb.Keys.LastSyncAt, ct);
            LastSync = last is not null && DateTimeOffset.TryParse(last, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t)
                ? t.ToLocalTime().ToString("g", Loc.Culture) : "—";
            Pending = await db.Outbox.CountAsync(e => e.Status == OutboxStatus.Pending, ct);
            var issues = await db.Outbox.Where(e => e.Status == OutboxStatus.Rejected || e.Status == OutboxStatus.Conflict || (e.Status == OutboxStatus.Pending && e.Attempts > 0))
                .OrderByDescending(e => e.Seq).Take(50).ToListAsync(ct);
            Issues.Clear();
            foreach (var e in issues) Issues.Add(new OutboxIssue(e.Seq, e.Type, e.Status, e.Attempts, e.LastError, e.ClientTime));
        }
        var eval = S.License.Current ?? S.License.Evaluate();
        LicenseStatus = LicenseText.Describe(Loc, eval) ?? Loc.Get("dk.license.valid");
        var summary = S.License.Summary;
        LicensePlan = eval.Payload?.Plan ?? summary?.PlanCode;
        LicenseExpires = summary?.ExpiresAt?.ToLocalTime().ToString("d", Loc.Culture) ?? (eval.Payload?.Lexp is { } l ? DateTimeOffset.FromUnixTimeSeconds(l).ToLocalTime().ToString("d", Loc.Culture) : "—");
        OfflineUntil = eval.OfflineUntil?.ToLocalTime().ToString("g", Loc.Culture);
        Seats = summary is null ? null : $"{summary.ActiveDevices} / {summary.MaxDevices}";
        return true;
    }

    [RelayCommand]
    public Task SaveServerAsync() => TryActionAsync(async () =>
    {
        if (!Core.Settings.ServerUrl.TryNormalize(ServerUrl, out var url, out var why))
            throw new InvalidOperationException(Loc.Get(why == "insecure" ? "dk.err.serverInsecure" : "dk.err.serverInvalid"));
        if (string.Equals(url, S.Settings.ServerUrl, StringComparison.OrdinalIgnoreCase)) return;
        if (!S.Ui.Confirm(Loc.Get("dk.set.serverChangeConfirm"))) return;
        S.Settings.ServerUrl = url;
        S.SaveSettings();
        ServerUrl = url;
        if (!await S.Api.PingAsync()) throw new InvalidOperationException(Loc.Get("err.network"));
    }, "dk.saved");

    [RelayCommand]
    public async Task SyncNowAsync()
    {
        await Shell.SyncNowAsync();
        await ReloadAsync();
    }

    [RelayCommand]
    public Task RenewLicenseAsync() => TryActionAsync(async () =>
    {
        if (!await S.License.RefreshAsync()) throw new InvalidOperationException(Loc.Get("err.network"));
        await ReloadAsync();
    }, "dk.saved");

    [RelayCommand]
    public Task ExportDiagnosticsAsync() => TryActionAsync(async () =>
    {
        var path = S.Ui.PickSaveFile(DiagnosticsBundle.FileName(S.Time.GetUtcNow()), "ZIP (*.zip)|*.zip");
        if (path is null) return;
        var eval = S.License.Current ?? S.License.Evaluate();
        var info = new DiagnosticsInfo(S.Version, Environment.OSVersion.VersionString, System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            S.Settings.Language, S.Settings.Theme, S.Settings.ServerUrl, S.Settings.InstallationId, S.Settings.DeviceId, eval.State.ToString(),
            eval.DaysRemaining, eval.OfflineUntil, S.Api.IsOnline);
        await using var fs = File.Create(path);
        await using var db = S.Db();
        await DiagnosticsBundle.WriteAsync(fs, info, S.Log.Directory, db, CancellationToken.None);
    }, "dk.set.diagnosticsSaved");

    [RelayCommand]
    public Task ReleaseDeviceAsync() => TryActionAsync(async () =>
    {
        if (!S.Ui.Confirm(Loc.Get("dk.set.releaseConfirm"))) return;
        await S.License.ReleaseAsync();
        await Shell.SignOutAsync();
    });

    [RelayCommand]
    public Task SignOutAsync() => Shell.SignOutAsync();
}
