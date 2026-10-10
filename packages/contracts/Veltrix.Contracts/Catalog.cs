namespace Veltrix.Contracts.Catalog;

public sealed record CategoryDto(Guid Id, Guid? ParentId, string Name, string? NameAr, int SortOrder, bool IsActive);
public sealed record UpsertCategoryRequest(Guid? ParentId, string Name, string? NameAr, int SortOrder = 0, bool IsActive = true);

public sealed record BrandDto(Guid Id, string Name);
public sealed record UpsertBrandRequest(string Name);

public sealed record UnitDto(Guid Id, string Code, string Name, bool AllowsFraction);
public sealed record UpsertUnitRequest(string Code, string Name, bool AllowsFraction);

public sealed record TaxCategoryDto(Guid Id, string Code, string Name, decimal RatePercent, bool IsActive);
public sealed record UpsertTaxCategoryRequest(string Code, string Name, decimal RatePercent, bool IsActive = true);

public sealed record ProductDto(
    Guid Id, string Sku, string Name, string? NameAr, string? Description, string Type, string Status, Guid? CategoryId, Guid? BrandId,
    Guid? UnitId, Guid? TaxCategoryId, decimal TaxRatePercent, decimal SalePrice, decimal? WholesalePrice, decimal PurchasePrice, decimal CostPrice,
    bool TrackStock, bool TrackSerial, bool TrackLot, bool TrackExpiry, decimal LowStockThreshold, decimal ReorderQuantity,
    Guid? ParentProductId, string? VariantAttributes, bool HasImage, IReadOnlyList<string> Barcodes, IReadOnlyList<BundleComponentDto> Components,
    DateTimeOffset UpdatedAt);

public sealed record BundleComponentDto(Guid ProductId, decimal Quantity);

public sealed record UpsertProductRequest(
    string Sku, string Name, string? NameAr, string? Description, string Type, string Status, Guid? CategoryId, Guid? BrandId, Guid? UnitId,
    Guid? TaxCategoryId, decimal SalePrice, decimal? WholesalePrice, decimal PurchasePrice, bool TrackStock, bool TrackSerial, bool TrackLot,
    bool TrackExpiry, decimal LowStockThreshold, decimal ReorderQuantity, Guid? ParentProductId, string? VariantAttributes,
    IReadOnlyList<string>? Barcodes, IReadOnlyList<BundleComponentDto>? Components, Guid? Id = null);

public sealed record BulkEditRequest(IReadOnlyList<Guid> ProductIds, Guid? CategoryId, string? Status, decimal? PriceChangePercent, Guid? TaxCategoryId);
public sealed record BulkEditResult(int Updated);

public sealed record PriceListDto(Guid Id, string Name, bool IsDefault, bool IsWholesale, IReadOnlyList<PriceListItemDto> Items);
public sealed record PriceListItemDto(Guid ProductId, decimal Price, decimal MinQuantity);
public sealed record UpsertPriceListRequest(string Name, bool IsDefault, bool IsWholesale, IReadOnlyList<PriceListItemDto> Items);

public sealed record ImportResult(int Created, int Updated, IReadOnlyList<ImportError> Errors);
public sealed record ImportError(int Row, string Message);

public sealed record ProductLookupDto(Guid Id, string Sku, string Name, string? NameAr, decimal SalePrice, decimal TaxRatePercent, bool TrackStock, string Type);
