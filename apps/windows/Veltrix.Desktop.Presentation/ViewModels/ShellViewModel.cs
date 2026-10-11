using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Licensing;

namespace Veltrix.Desktop.Presentation.ViewModels;

public sealed partial class NavItem(string key, string titleKey, string icon, Func<PageViewModel> factory) : ObservableObject
{
    private PageViewModel? _page;

    public string Key { get; } = key;
    public string TitleKey { get; } = titleKey;
    /// <summary>Resource key of the icon geometry (Icon.* in Styles.xaml).</summary>
    public string Icon { get; } = icon;
    [ObservableProperty] private string _title = "";
    public PageViewModel Page => _page ??= factory();
}

/// <summary>Main shell: permission-filtered navigation, connectivity, license banner and the background sync loop.</summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(60);
    private readonly DesktopServices _s;
    private readonly AppViewModel _app;
    private readonly CancellationTokenSource _life = new();

    public ShellViewModel(DesktopServices s, AppViewModel app)
    {
        _s = s;
        _app = app;
        var all = new List<(string Key, string Title, string Icon, string? Permission, Func<PageViewModel> Make)>
        {
            ("dashboard", "nav.dashboard", "Icon.Dashboard", null, () => new DashboardViewModel(s)),
            ("pos", "nav.pos", "Icon.Pos", "pos.sell", () => new PosViewModel(s)),
            ("products", "nav.products", "Icon.Products", "catalog.view", () => new ProductsViewModel(s)),
            ("customers", "nav.customers", "Icon.Customers", "customers.view", () => new CustomersViewModel(s)),
            ("inventory", "nav.inventory", "Icon.Inventory", "inventory.view", () => new InventoryViewModel(s)),
            ("sales", "nav.sales", "Icon.Sales", "sales.view", () => new SalesViewModel(s)),
            ("purchasing", "nav.purchasing", "Icon.Purchasing", "purchasing.view", () => new PurchasingViewModel(s)),
            ("reports", "nav.reports", "Icon.Reports", null, () => new ReportsViewModel(s)),
            ("settings", "nav.settings", "Icon.Settings", null, () => new SettingsViewModel(s, this)),
        };
        // Standalone (offline license): purchasing documents live on the server, so the module is hidden.
        if (s.IsStandalone)
        {
            all.RemoveAll(a => a.Key == "purchasing");
            // Local ERP (standalone only: online installations keep these documents on the server).
            all.InsertRange(all.FindIndex(a => a.Key == "reports"),
            [
                ("erp.cash", "erp.nav.cash", "Icon.Pos", "cash.register", () => new CashRegisterViewModel(s)),
                ("erp.receivables", "erp.nav.receivables", "Icon.Customers", "customers.manage", () => new ReceivablesViewModel(s)),
                ("erp.suppliers", "erp.nav.suppliers", "Icon.Purchasing", "purchasing.view", () => new SuppliersViewModel(s)),
                ("erp.purchases", "erp.nav.purchases", "Icon.Purchasing", "purchasing.manage", () => new PurchasesViewModel(s)),
                ("erp.expenses", "erp.nav.expenses", "Icon.Sales", "expenses.manage", () => new ExpensesViewModel(s)),
                ("erp.accounting", "erp.nav.accounting", "Icon.Reports", "accounting.view", () => new AccountingViewModel(s)),
                ("erp.hr", "erp.nav.hr", "Icon.Customers", "hr.manage", () => new EmployeesViewModel(s)),
            ]);
        }
        // CCTV (owner only): live view of the shop cameras over the LAN.
        if (s.Cameras is not null && s.IsOwner)
            all.Insert(all.FindIndex(a => a.Key == "settings"), ("cameras", "cam.nav", "Icon.Settings", null, () => new CamerasViewModel(s)));
        NavItems = new ObservableCollection<NavItem>(all.Where(a => a.Permission is null || s.Session.HasPermission(a.Permission))
            .Select(a => new NavItem(a.Key, a.Title, a.Icon, a.Make)));
        UpdateTexts();
        _isOnline = !s.Session.IsOfflineSession && s.Api.IsOnline;
        s.Api.ConnectivityChanged += OnConnectivityChanged;
        s.Api.SessionExpired += OnSessionExpired;
        s.Loc.LanguageChanged += OnLanguageChanged;
        s.License.Changed += OnLicenseChanged;
        UpdateLicense(s.License.Current);
        _ = NavigateAsync(NavItems.FirstOrDefault(n => n.Key == "pos" && s.Session.HasPermission("pos.sell") && !s.Session.HasPermission("reports.sales")) ?? NavItems[0]);
        if (s.IsStandalone) SyncStatus = s.Loc.Get("dk.sa.localMode");
        else _ = SyncLoopAsync(_life.Token);
    }

    public ObservableCollection<NavItem> NavItems { get; }
    public string UserName => _s.Session.User?.FullName ?? "";
    public string? TenantName => _s.Settings.TenantName;
    public string Version => _s.Version;

    [ObservableProperty] private NavItem? _selected;
    [ObservableProperty] private PageViewModel? _currentPage;
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private int _pendingOperations;
    [ObservableProperty] private string? _syncStatus;
    [ObservableProperty] private DateTimeOffset? _lastSyncAt;
    [ObservableProperty] private string? _licenseBanner;
    [ObservableProperty] private bool _licenseBannerIsError;

    partial void OnSelectedChanged(NavItem? value)
    {
        if (value is not null) _ = NavigateAsync(value);
    }

    public async Task NavigateAsync(NavItem item)
    {
        if (!ReferenceEquals(Selected, item)) Selected = item;
        CurrentPage = item.Page;
        await item.Page.OnNavigatedToAsync();
    }

    public void Navigate(string key)
    {
        if (NavItems.FirstOrDefault(n => n.Key == key) is { } item) Selected = item;
    }

    [RelayCommand]
    public async Task SyncNowAsync() => await RunSyncAsync(_life.Token);

    [RelayCommand]
    public async Task SignOutAsync()
    {
        Dispose();
        try { await _s.Session.SignOutAsync(); }
        catch (Exception e) { _s.Log.Warn("Sign-out failed", e); }
        _app.Navigate(StartRoute.Login);
    }

    private async Task SyncLoopAsync(CancellationToken ct)
    {
        try
        {
            await RunSyncAsync(ct);
            using var timer = new PeriodicTimer(SyncInterval, _s.Time);
            while (await timer.WaitForNextTickAsync(ct))
            {
                await RunSyncAsync(ct);
                // Opportunistic certificate renewal keeps the offline grace window fresh.
                if (IsOnline && _s.License.Current is { OfflineUntil: { } until } && until - _s.Time.GetUtcNow() < TimeSpan.FromDays(3))
                {
                    try { await _s.License.RefreshAsync(ct); } catch (Exception e) when (e is not OperationCanceledException) { _s.Log.Warn("License refresh failed", e); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public bool IsStandalone => _s.IsStandalone;

    public async Task RunSyncAsync(CancellationToken ct)
    {
        if (_s.IsStandalone) { SyncStatus = _s.Loc.Get("dk.sa.localMode"); return; }
        if (IsSyncing || _s.Session.User is null || _s.Tokens.RefreshToken is null) { await UpdatePendingAsync(ct); return; }
        IsSyncing = true;
        try
        {
            Guid? warehouseId = null;
            if (_s.Settings.RegisterId is { } rid)
            {
                await using var db = _s.Db();
                warehouseId = (await db.Registers.FindAsync([rid], ct))?.WarehouseId;
            }
            var r = await _s.Sync.RunOnceAsync(warehouseId, ct);
            PendingOperations = r.Pending;
            if (r.Offline) SyncStatus = _s.Loc.Get("err.network");
            else if (r.Error is not null) SyncStatus = r.Error;
            else if (r.Rejected + r.Conflicts > 0) SyncStatus = _s.Loc.Get("sync.error");
            else SyncStatus = r.Pending > 0 ? _s.Loc.Format("sync.pending", r.Pending) : _s.Loc.Get("sync.done");
            if (!r.Offline && r.Error is null) LastSyncAt = _s.Time.GetUtcNow();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            _s.Log.Warn("Sync failed", e);
            SyncStatus = _s.Loc.Get("sync.error");
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private async Task UpdatePendingAsync(CancellationToken ct)
    {
        await using var db = _s.Db();
        PendingOperations = await db.Outbox.CountAsync(e => e.Status == OutboxStatus.Pending, ct);
    }

    private void UpdateLicense(LicenseEvaluation? e)
    {
        LicenseBanner = e is null ? null : LicenseText.Describe(_s.Loc, e);
        LicenseBannerIsError = e is { CanSell: false };
    }

    private void UpdateTexts()
    {
        foreach (var n in NavItems) n.Title = _s.Loc.Get(n.TitleKey);
    }

    private void OnConnectivityChanged(object? sender, bool online) => IsOnline = online;
    private void OnSessionExpired(object? sender, EventArgs e) => _app.Navigate(StartRoute.Login);
    private void OnLicenseChanged(object? sender, LicenseEvaluation e) => UpdateLicense(e);

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        UpdateTexts();
        UpdateLicense(_s.License.Current);
    }

    public void Dispose()
    {
        if (_life.IsCancellationRequested) return;
        _life.Cancel();
        _s.Api.ConnectivityChanged -= OnConnectivityChanged;
        _s.Api.SessionExpired -= OnSessionExpired;
        _s.Loc.LanguageChanged -= OnLanguageChanged;
        _s.License.Changed -= OnLicenseChanged;
    }
}
