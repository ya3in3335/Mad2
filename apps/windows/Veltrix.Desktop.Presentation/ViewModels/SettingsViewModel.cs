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
    /// <summary>Receipt (ticket) customisation with live preview.</summary>
    public ReceiptSettingsViewModel Receipt { get; } = new(s);
    public IReadOnlyList<AccentInfo> AccentOptions { get; } = Accents.All;
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

    // ---- Standalone (offline license): local users and encrypted backups; server, sync and seat release do not apply.
    public bool IsStandalone => S.IsStandalone;
    public bool IsOnlineMode => !S.IsStandalone;
    public bool IsOwner => S.Session.LocalUser?.IsOwner == true;
    public IReadOnlyList<string> LocalRoleOptions { get; } = [Core.Standalone.LocalRoles.Cashier, Core.Standalone.LocalRoles.Owner];
    public ObservableCollection<Core.Standalone.LocalUser> LocalUsers { get; } = [];
    [ObservableProperty] private string _newUserName = "";
    [ObservableProperty] private string _newUserRole = Core.Standalone.LocalRoles.Cashier;
    /// <summary>Set by the view from PasswordBoxes.</summary>
    public string NewUserSecret { private get; set; } = "";
    public string BackupPassword { private get; set; } = "";

    [RelayCommand]
    public Task AddLocalUserAsync() => TryActionAsync(async () =>
    {
        if (!IsOwner) throw new InvalidOperationException(Loc.Get("err.permission"));
        try
        {
            var name = NewUserName; var secret = NewUserSecret; var role = NewUserRole;
            await Task.Run(() => S.Standalone.Auth.AddUser(name, secret, role));
        }
        catch (InvalidOperationException e) { throw new InvalidOperationException(Loc.Get(e.Message)); }
        NewUserName = "";
        LoadUsers();
    }, "dk.saved");

    [RelayCommand]
    public Task RemoveLocalUserAsync(Core.Standalone.LocalUser? user) => TryActionAsync(() =>
    {
        if (user is null || !IsOwner) return Task.CompletedTask;
        if (user.Id == S.Session.LocalUser?.Id) throw new InvalidOperationException(Loc.Get("dk.sa.err.lastOwner"));
        if (!S.Ui.Confirm(Loc.Format("dk.sa.removeConfirm", user.Name))) return Task.CompletedTask;
        try { S.Standalone.Auth.RemoveUser(user.Id); }
        catch (InvalidOperationException e) { throw new InvalidOperationException(Loc.Get(e.Message)); }
        LoadUsers();
        return Task.CompletedTask;
    }, "dk.saved");

    /// <summary>Owner only: writes an AES-GCM encrypted backup whose key is derived from the owner's password.</summary>
    [RelayCommand]
    public Task ExportBackupAsync() => TryActionAsync(async () =>
    {
        if (S.Session.LocalUser is not { IsOwner: true } owner) throw new InvalidOperationException(Loc.Get("err.permission"));
        if (BackupPassword.Length == 0) throw new InvalidOperationException(Loc.Get("dk.sa.backupPasswordPrompt"));
        var path = S.Ui.PickSaveFile($"veltrix-backup-{S.Time.GetLocalNow():yyyyMMdd-HHmm}{Core.Standalone.LocalBackup.FileExtension}",
            Loc.Get("dk.sa.backupFilter") + "|*" + Core.Standalone.LocalBackup.FileExtension);
        if (path is null) return;
        var password = BackupPassword;
        byte[] bytes;
        try { bytes = await Task.Run(() => S.Standalone.ExportBackupAsync(owner, password, S.Settings.TenantId?.ToString(), S.Settings.TenantName)); }
        catch (InvalidOperationException e) { throw new InvalidOperationException(Loc.Get(e.Message)); }
        await File.WriteAllBytesAsync(path, bytes);
    }, "dk.sa.backupSaved");

    [RelayCommand]
    public Task RestoreBackupAsync() => TryActionAsync(async () =>
    {
        if (!IsOwner) throw new InvalidOperationException(Loc.Get("err.permission"));
        if (BackupPassword.Length == 0) throw new InvalidOperationException(Loc.Get("dk.sa.backupPasswordPrompt"));
        if (!S.Ui.Confirm(Loc.Get("dk.sa.restoreConfirm"))) return;
        var path = S.Ui.PickOpenFile(Loc.Get("dk.sa.backupFilter") + "|*" + Core.Standalone.LocalBackup.FileExtension);
        if (path is null) return;
        var bytes = await File.ReadAllBytesAsync(path);
        var password = BackupPassword;
        try { await Task.Run(() => S.Standalone.RestoreBackupAsync(bytes, password)); }
        catch (InvalidOperationException e) { throw new InvalidOperationException(Loc.Get(e.Message)); }
        LoadUsers();
    }, "dk.sa.restored");

    private void LoadUsers()
    {
        LocalUsers.Clear();
        if (!IsStandalone) return;
        foreach (var u in S.Standalone.Auth.Users.OrderBy(u => u.Name)) LocalUsers.Add(u);
    }
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
            OnPropertyChanged(nameof(Accent)); // refreshes the localized accent name
            Receipt.AppLanguageChanged();
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

    /// <summary>Accent palette code; applied immediately (live preview) and persisted.</summary>
    public string Accent
    {
        get => Accents.Normalize(S.Settings.Accent);
        set
        {
            var code = Accents.Normalize(value);
            if (code == S.Settings.Accent) return;
            S.Settings.Accent = code;
            S.SaveSettings();
            S.Ui.ApplyAccent(code);
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

    /// <summary>CCTV preferences: shown to the owner when the camera feature is available.</summary>
    public bool CamerasAvailable => S.Cameras is not null && S.IsOwner;

    public bool SnapshotOnSale
    {
        get => S.Settings.Cameras.SnapshotOnSale;
        set { S.Settings.Cameras.SnapshotOnSale = value; S.SaveSettings(); OnPropertyChanged(); }
    }

    public string SnapshotRetentionDays
    {
        get => S.Settings.Cameras.RetentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var d) && d is >= 1 and <= 3650)
            {
                S.Settings.Cameras.RetentionDays = d;
                S.SaveSettings();
            }
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
        LoadUsers();
        if (IsStandalone)
        {
            // Device-bound standalone installation: no plan, expiry, seats or online renewal.
            var bound = S.Binding.Current?.Record?.BoundAt;
            LicenseStatus = bound is { } at ? Loc.Format("dk.bind.status", at.ToLocalTime().ToString("d", Loc.Culture)) : Loc.Get("dk.license.valid");
            LicensePlan = Loc.Get("dk.sa.localMode");
            LicenseExpires = "—";
            OfflineUntil = null;
            Seats = null;
            return true;
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
