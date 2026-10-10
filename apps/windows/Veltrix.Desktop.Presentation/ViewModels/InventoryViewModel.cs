using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Contracts;
using Veltrix.Contracts.Inventory;
using Veltrix.Contracts.Tenants;
using Veltrix.Desktop.Core.Api;

namespace Veltrix.Desktop.Presentation.ViewModels;

public sealed partial class InventoryViewModel(DesktopServices s) : ListPageViewModel<StockBalanceDto>(s)
{
    public static IReadOnlyList<string> Reasons { get; } = ["Correction", "Damage", "Expired", "Found", "Theft", "Other"];

    public override string TitleKey => "nav.inventory";

    [ObservableProperty] private IReadOnlyList<WarehouseDto> _warehouses = [];
    [ObservableProperty] private WarehouseDto? _warehouse;
    [ObservableProperty] private bool _inStockOnly;
    [ObservableProperty] private IReadOnlyList<LowStockDto> _lowStock = [];
    [ObservableProperty] private StockBalanceDto? _selected;
    [ObservableProperty] private string _adjustQuantity = "";
    [ObservableProperty] private string _adjustReason = "Correction";
    [ObservableProperty] private string _adjustNotes = "";

    public bool CanAdjust => Can("inventory.adjust");

    protected override async Task<PagedResponse<StockBalanceDto>> FetchAsync(int page, string search, CancellationToken ct)
    {
        if (S.IsStandalone)
        {
            if (Warehouses.Count == 0) Warehouses = await S.Standalone.Store.WarehousesAsync(ct);
            LowStock = await S.Standalone.Store.LowStockAsync(ct);
            return await S.Standalone.Store.StockAsync(page, PageSize, search, InStockOnly, ct);
        }
        if (Warehouses.Count == 0) Warehouses = await S.Api.GetAsync<List<WarehouseDto>>("/api/v1/warehouses", ct);
        var wid = Warehouse?.Id;
        LowStock = await S.Api.GetAsync<List<LowStockDto>>("/api/v1/inventory/low-stock" + ApiClient.Query(("warehouseId", wid)), ct);
        return await S.Api.GetAsync<PagedResponse<StockBalanceDto>>("/api/v1/inventory/balances" +
            ApiClient.Query(("page", page), ("pageSize", PageSize), ("search", search), ("warehouseId", wid), ("inStock", InStockOnly ? true : null)), ct);
    }

    partial void OnWarehouseChanged(WarehouseDto? value) => _ = SearchNowAsync();
    partial void OnInStockOnlyChanged(bool value) => _ = SearchNowAsync();

    [RelayCommand]
    public Task AdjustAsync() => TryActionAsync(async () =>
    {
        if (Selected is null) return;
        if (!Parse.TryDecimal(AdjustQuantity, out var qty) || qty == 0) throw new InvalidOperationException(Loc.Get("dk.inv.adjustInvalid"));
        if (!S.Ui.Confirm(Loc.Format("dk.inv.adjustConfirm", qty.ToString("+0.###;-0.###", Loc.Culture), Selected.ProductName, Selected.WarehouseName))) return;
        if (S.IsStandalone) await S.Standalone.Store.AdjustStockAsync(Selected.ProductId, qty);
        else await S.Api.PostAsync<AdjustmentDto>("/api/v1/inventory/adjustments", new CreateAdjustmentRequest(Selected.WarehouseId, AdjustReason,
            string.IsNullOrWhiteSpace(AdjustNotes) ? null : AdjustNotes.Trim(), [new AdjustmentLineInput(Selected.ProductId, qty, null, null, null, null)], Guid.NewGuid()));
        AdjustQuantity = "";
        AdjustNotes = "";
        await ReloadAsync();
    }, "dk.saved");
}
