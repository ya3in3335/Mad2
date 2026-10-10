using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Contracts;
using Veltrix.Contracts.Purchasing;
using Veltrix.Desktop.Core.Api;

namespace Veltrix.Desktop.Presentation.ViewModels;

public sealed partial class PurchasingViewModel(DesktopServices s) : PageViewModel(s)
{
    public override string TitleKey => "nav.purchasing";

    public ObservableCollection<PurchaseOrderDto> Orders { get; } = [];
    public ObservableCollection<SupplierDto> Suppliers { get; } = [];
    public ObservableCollection<SupplierInvoiceDto> Invoices { get; } = [];

    [ObservableProperty] private int _tabIndex;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _outstandingOnly;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmitSelected), nameof(CanApproveSelected), nameof(CanCancelSelected))]
    private PurchaseOrderDto? _selectedOrder;

    public bool CanSubmitSelected => SelectedOrder is { Status: "Draft" } && Can("purchasing.manage");
    public bool CanApproveSelected => SelectedOrder is { Status: "Draft" or "PendingApproval" } && Can("purchasing.approve");
    public bool CanCancelSelected => SelectedOrder is { Status: "Draft" or "PendingApproval" or "Approved" } && Can("purchasing.manage");

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        var q = ApiClient.Query(("search", Search.Trim()), ("pageSize", 100), ("outstanding", TabIndex == 0 && OutstandingOnly ? true : null),
            ("unpaid", TabIndex == 2 && OutstandingOnly ? true : null));
        switch (TabIndex)
        {
            case 0:
                Fill(Orders, (await S.Api.GetAsync<PagedResponse<PurchaseOrderDto>>("/api/v1/purchasing/orders" + q, ct)).Items);
                return Orders.Count > 0;
            case 1:
                Fill(Suppliers, (await S.Api.GetAsync<PagedResponse<SupplierDto>>("/api/v1/purchasing/suppliers" + q, ct)).Items);
                return Suppliers.Count > 0;
            default:
                Fill(Invoices, (await S.Api.GetAsync<PagedResponse<SupplierInvoiceDto>>("/api/v1/purchasing/invoices" + q, ct)).Items);
                return Invoices.Count > 0;
        }
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    partial void OnTabIndexChanged(int value) => _ = ReloadAsync();
    partial void OnOutstandingOnlyChanged(bool value) => _ = ReloadAsync();

    [RelayCommand]
    public Task SearchNowAsync() => ReloadAsync();

    [RelayCommand] public Task SubmitAsync() => OrderActionAsync("submit");
    [RelayCommand] public Task ApproveAsync() => OrderActionAsync("approve");
    [RelayCommand] public Task CancelOrderAsync() => OrderActionAsync("cancel", confirm: true);

    private Task OrderActionAsync(string action, bool confirm = false) => TryActionAsync(async () =>
    {
        if (SelectedOrder is not { } po) return;
        if (confirm && !S.Ui.Confirm(Loc.Get("dlg.confirmDelete"))) return;
        var updated = await S.Api.PostAsync<PurchaseOrderDto>($"/api/v1/purchasing/orders/{po.Id}/{action}", null);
        var idx = Orders.IndexOf(po);
        if (idx >= 0) Orders[idx] = updated;
        SelectedOrder = updated;
    }, "dk.saved");
}
