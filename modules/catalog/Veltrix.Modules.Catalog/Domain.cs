using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Catalog;

public enum ProductType { Physical, Service, Bundle }
public enum ProductStatus { Active, Inactive, Discontinued }

public sealed class Category : TenantEntity
{
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = "";
    public string? NameAr { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class Brand : TenantEntity
{
    public string Name { get; set; } = "";
}

public sealed class Unit : TenantEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool AllowsFraction { get; set; }
}

/// <summary>Configurable tax category. Rates are never hardcoded; they are tenant configuration.</summary>
public sealed class TaxCategory : TenantEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal RatePercent { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class Product : TenantEntity
{
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NameAr { get; set; }
    public string? Description { get; set; }
    public ProductType Type { get; set; }
    public ProductStatus Status { get; set; } = ProductStatus.Active;
    public Guid? CategoryId { get; set; }
    public Guid? BrandId { get; set; }
    public Guid? UnitId { get; set; }
    public Guid? TaxCategoryId { get; set; }
    /// <summary>Selling price, tax-inclusive or exclusive according to the tenant "pricesIncludeTax" setting.</summary>
    public decimal SalePrice { get; set; }
    public decimal? WholesalePrice { get; set; }
    /// <summary>Last purchase price (informational).</summary>
    public decimal PurchasePrice { get; set; }
    /// <summary>Company-wide moving average cost (warehouse-level average cost is kept by inventory).</summary>
    public decimal CostPrice { get; set; }
    public bool TrackStock { get; set; } = true;
    public bool TrackSerial { get; set; }
    public bool TrackLot { get; set; }
    public bool TrackExpiry { get; set; }
    public decimal LowStockThreshold { get; set; }
    public decimal ReorderQuantity { get; set; }
    public Guid? ParentProductId { get; set; }
    public string? VariantAttributes { get; set; }
    public string? ImagePath { get; set; }
    public string? ImageContentType { get; set; }
    public List<ProductBarcode> Barcodes { get; set; } = new();
    public List<BundleItem> Components { get; set; } = new();
}

public sealed class ProductBarcode : TenantEntity
{
    public Guid ProductId { get; set; }
    public string Code { get; set; } = "";
}

public sealed class BundleItem : TenantEntity
{
    public Guid BundleProductId { get; set; }
    public Guid ComponentProductId { get; set; }
    public decimal Quantity { get; set; }
}

public sealed class PriceList : TenantEntity
{
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
    public bool IsWholesale { get; set; }
    public List<PriceListItem> Items { get; set; } = new();
}

public sealed class PriceListItem : TenantEntity
{
    public Guid PriceListId { get; set; }
    public Guid ProductId { get; set; }
    public decimal Price { get; set; }
    public decimal MinQuantity { get; set; } = 1;
}

internal sealed class CategoryConfig : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> b)
    {
        b.ToTable("categories", "catalog");
        b.Property(x => x.Name).HasMaxLength(120);
        b.Property(x => x.NameAr).HasMaxLength(120);
        b.HasOne<Category>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BrandConfig : IEntityTypeConfiguration<Brand>
{
    public void Configure(EntityTypeBuilder<Brand> b)
    {
        b.ToTable("brands", "catalog");
        b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        b.Property(x => x.Name).HasMaxLength(120);
    }
}

internal sealed class UnitConfig : IEntityTypeConfiguration<Unit>
{
    public void Configure(EntityTypeBuilder<Unit> b)
    {
        b.ToTable("units", "catalog");
        b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        b.Property(x => x.Code).HasMaxLength(20);
        b.Property(x => x.Name).HasMaxLength(60);
    }
}

internal sealed class TaxCategoryConfig : IEntityTypeConfiguration<TaxCategory>
{
    public void Configure(EntityTypeBuilder<TaxCategory> b)
    {
        b.ToTable("tax_categories", "catalog");
        b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        b.Property(x => x.Code).HasMaxLength(20);
        b.Property(x => x.Name).HasMaxLength(80);
        b.Property(x => x.RatePercent).HasPrecision(7, 4);
    }
}

internal sealed class ProductConfig : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> b)
    {
        b.ToTable("products", "catalog");
        b.HasIndex(x => new { x.TenantId, x.Sku }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.Name });
        b.HasIndex(x => new { x.TenantId, x.UpdatedAt });
        b.Property(x => x.Sku).HasMaxLength(64);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.NameAr).HasMaxLength(200);
        b.Property(x => x.Description).HasMaxLength(2000);
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.VariantAttributes).HasMaxLength(500);
        b.Property(x => x.ImagePath).HasMaxLength(300);
        b.Property(x => x.ImageContentType).HasMaxLength(40);
        b.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Brand>().WithMany().HasForeignKey(x => x.BrandId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Unit>().WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TaxCategory>().WithMany().HasForeignKey(x => x.TaxCategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Product>().WithMany().HasForeignKey(x => x.ParentProductId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Barcodes).WithOne().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Components).WithOne().HasForeignKey(x => x.BundleProductId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ProductBarcodeConfig : IEntityTypeConfiguration<ProductBarcode>
{
    public void Configure(EntityTypeBuilder<ProductBarcode> b)
    {
        b.ToTable("product_barcodes", "catalog");
        b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        b.Property(x => x.Code).HasMaxLength(64);
    }
}

internal sealed class BundleItemConfig : IEntityTypeConfiguration<BundleItem>
{
    public void Configure(EntityTypeBuilder<BundleItem> b)
    {
        b.ToTable("bundle_items", "catalog");
        b.HasIndex(x => new { x.BundleProductId, x.ComponentProductId }).IsUnique();
        b.HasOne<Product>().WithMany().HasForeignKey(x => x.ComponentProductId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PriceListConfig : IEntityTypeConfiguration<PriceList>
{
    public void Configure(EntityTypeBuilder<PriceList> b)
    {
        b.ToTable("price_lists", "catalog");
        b.Property(x => x.Name).HasMaxLength(100);
        b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.PriceListId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PriceListItemConfig : IEntityTypeConfiguration<PriceListItem>
{
    public void Configure(EntityTypeBuilder<PriceListItem> b)
    {
        b.ToTable("price_list_items", "catalog");
        b.HasIndex(x => new { x.PriceListId, x.ProductId, x.MinQuantity }).IsUnique();
        b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
    }
}
