using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Contracts;
using Veltrix.Contracts.Catalog;
using Veltrix.Desktop.Core.Api;

namespace Veltrix.Desktop.Presentation.ViewModels;

public sealed partial class ProductEditor : ObservableObject
{
    public ProductDto? Original { get; init; }
    [ObservableProperty] private string _sku = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _nameAr = "";
    [ObservableProperty] private string _salePrice = "0";
    [ObservableProperty] private string _purchasePrice = "0";
    [ObservableProperty] private string _barcodes = "";
    [ObservableProperty] private string _lowStockThreshold = "0";
    [ObservableProperty] private bool _trackStock = true;
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private Guid? _taxCategoryId;
    [ObservableProperty] private Guid? _categoryId;

    public bool IsNew => Original is null;

    public static ProductEditor For(ProductDto p) => new()
    {
        Original = p, Sku = p.Sku, Name = p.Name, NameAr = p.NameAr ?? "", SalePrice = p.SalePrice.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PurchasePrice = p.PurchasePrice.ToString(System.Globalization.CultureInfo.InvariantCulture), Barcodes = string.Join(Environment.NewLine, p.Barcodes),
        LowStockThreshold = p.LowStockThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture), TrackStock = p.TrackStock,
        IsActive = p.Status == "Active", TaxCategoryId = p.TaxCategoryId, CategoryId = p.CategoryId,
    };

    /// <summary>Builds the full upsert request, preserving every field this form does not edit.</summary>
    public UpsertProductRequest ToRequest()
    {
        if (string.IsNullOrWhiteSpace(Sku) || string.IsNullOrWhiteSpace(Name)) throw new InvalidOperationException("SKU / name");
        if (!Parse.TryDecimal(SalePrice, out var price) || price < 0) throw new InvalidOperationException("Price");
        var o = Original;
        var barcodes = Barcodes.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        return new UpsertProductRequest(Sku.Trim(), Name.Trim(), string.IsNullOrWhiteSpace(NameAr) ? null : NameAr.Trim(), o?.Description,
            o?.Type ?? "Standard", IsActive ? "Active" : "Inactive", CategoryId, o?.BrandId, o?.UnitId, TaxCategoryId, price, o?.WholesalePrice,
            Parse.DecimalOr(PurchasePrice, o?.PurchasePrice ?? 0), TrackStock, o?.TrackSerial ?? false, o?.TrackLot ?? false, o?.TrackExpiry ?? false,
            Parse.DecimalOr(LowStockThreshold, 0), o?.ReorderQuantity ?? 0, o?.ParentProductId, o?.VariantAttributes, barcodes, o?.Components, o?.Id);
    }
}

public sealed partial class ProductsViewModel(DesktopServices s) : ListPageViewModel<ProductDto>(s)
{
    public override string TitleKey => "nav.products";

    [ObservableProperty] private ProductDto? _selected;
    [ObservableProperty] private ProductEditor? _editor;
    [ObservableProperty] private IReadOnlyList<TaxCategoryDto> _taxCategories = [];
    [ObservableProperty] private IReadOnlyList<CategoryDto> _categories = [];

    public bool CanManage => Can("catalog.manage");

    protected override async Task<PagedResponse<ProductDto>> FetchAsync(int page, string search, CancellationToken ct)
    {
        if (TaxCategories.Count == 0)
        {
            TaxCategories = await S.Api.GetAsync<List<TaxCategoryDto>>("/api/v1/catalog/tax-categories", ct);
            Categories = await S.Api.GetAsync<List<CategoryDto>>("/api/v1/catalog/categories", ct);
        }
        return await S.Api.GetAsync<PagedResponse<ProductDto>>("/api/v1/catalog/products" + ApiClient.Query(("page", page), ("pageSize", PageSize), ("search", search)), ct);
    }

    partial void OnSelectedChanged(ProductDto? value) => Editor = value is null ? null : ProductEditor.For(value);

    [RelayCommand]
    public void New()
    {
        Selected = null;
        Editor = new ProductEditor { TaxCategoryId = TaxCategories.FirstOrDefault(t => t.IsActive)?.Id };
    }

    [RelayCommand]
    public Task SaveAsync() => TryActionAsync(async () =>
    {
        if (Editor is null) return;
        var req = Editor.ToRequest();
        var saved = Editor.IsNew
            ? await S.Api.PostAsync<ProductDto>("/api/v1/catalog/products", req)
            : await S.Api.PutAsync<ProductDto>($"/api/v1/catalog/products/{Editor.Original!.Id}", req);
        var idx = Items.ToList().FindIndex(p => p.Id == saved.Id);
        if (idx >= 0) Items[idx] = saved;
        else Items.Insert(0, saved);
        Selected = saved;
        if (State == LoadState.Empty) State = LoadState.Ready;
    }, "dk.saved");
}
