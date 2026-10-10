using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Veltrix.Contracts.Catalog;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Catalog;

public sealed class CatalogService(DbContext db, ITenantContext ctx, IAuditWriter audit, IFileStorage storage)
{
    // ---------- Products ----------
    public async Task<PagedResult<ProductDto>> ListAsync(PageQuery q, Guid? categoryId, string? status, bool? lowStockOnly, CancellationToken ct)
    {
        var query = db.Set<Product>().Include(p => p.Barcodes).Include(p => p.Components).AsNoTracking().AsQueryable();
        if (categoryId is { } c) query = query.Where(p => p.CategoryId == c);
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ProductStatus>(status, true, out var st)) query = query.Where(p => p.Status == st);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim().ToUpper();
            query = query.Where(p => p.Sku.ToUpper().Contains(s) || p.Name.ToUpper().Contains(s) || (p.NameAr != null && p.NameAr.Contains(q.Search.Trim()))
                                     || p.Barcodes.Any(b => b.Code == q.Search.Trim()));
        }
        query = q.SortField switch
        {
            "sku" => q.Descending ? query.OrderByDescending(p => p.Sku) : query.OrderBy(p => p.Sku),
            "price" => q.Descending ? query.OrderByDescending(p => p.SalePrice) : query.OrderBy(p => p.SalePrice),
            "updated" => q.Descending ? query.OrderByDescending(p => p.UpdatedAt) : query.OrderBy(p => p.UpdatedAt),
            _ => q.Descending ? query.OrderByDescending(p => p.Name) : query.OrderBy(p => p.Name),
        };
        var page = await query.ToPagedAsync(q, ct);
        var rates = await TaxRatesAsync(ct);
        return new PagedResult<ProductDto>(page.Items.Select(p => ToDto(p, rates)).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<ProductDto> GetAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Set<Product>().Include(x => x.Barcodes).Include(x => x.Components).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("product.not_found", "Product not found.");
        return ToDto(p, await TaxRatesAsync(ct));
    }

    public async Task<ProductDto?> FindByCodeAsync(string code, CancellationToken ct)
    {
        code = code.Trim();
        var p = await db.Set<Product>().Include(x => x.Barcodes).Include(x => x.Components).AsNoTracking()
            .FirstOrDefaultAsync(x => x.Barcodes.Any(b => b.Code == code) || x.Sku == code, ct);
        return p is null ? null : ToDto(p, await TaxRatesAsync(ct));
    }

    public async Task<ProductDto> UpsertAsync(Guid? id, UpsertProductRequest req, CancellationToken ct)
    {
        var tid = ctx.RequireTenant();
        Validate(req);
        var sku = req.Sku.Trim();
        if (await db.Set<Product>().AnyAsync(p => p.Sku == sku && p.Id != id, ct))
            throw new ConflictException("product.sku_taken", $"SKU '{sku}' already exists.");
        var barcodes = (req.Barcodes ?? []).Select(b => b.Trim()).Where(b => b.Length > 0).Distinct().ToList();
        if (barcodes.Count > 0)
        {
            var clash = await db.Set<ProductBarcode>().Where(b => barcodes.Contains(b.Code) && b.ProductId != id).Select(b => b.Code).FirstOrDefaultAsync(ct);
            if (clash is not null) throw new ConflictException("product.barcode_taken", $"Barcode '{clash}' is already assigned to another product.");
        }
        await EnsureReferencesAsync(req, ct);

        Product p;
        if (id is { } pid)
        {
            p = await db.Set<Product>().Include(x => x.Barcodes).Include(x => x.Components).SingleOrDefaultAsync(x => x.Id == pid, ct)
                ?? throw new NotFoundException("product.not_found", "Product not found.");
        }
        else
        {
            p = new Product { TenantId = tid };
            if (req.Id is { } clientId && clientId != Guid.Empty && !await db.Set<Product>().IgnoreQueryFilters().AnyAsync(x => x.Id == clientId, ct)) p.Id = clientId;
            db.Set<Product>().Add(p);
        }
        var oldPrice = p.SalePrice;
        p.Sku = sku; p.Name = req.Name.Trim(); p.NameAr = req.NameAr?.Trim(); p.Description = req.Description;
        p.Type = Enum.Parse<ProductType>(req.Type, true); p.Status = Enum.Parse<ProductStatus>(req.Status, true);
        p.CategoryId = req.CategoryId; p.BrandId = req.BrandId; p.UnitId = req.UnitId; p.TaxCategoryId = req.TaxCategoryId;
        p.SalePrice = req.SalePrice; p.WholesalePrice = req.WholesalePrice; p.PurchasePrice = req.PurchasePrice;
        if (id is null && p.CostPrice == 0) p.CostPrice = req.PurchasePrice;
        p.TrackStock = p.Type == ProductType.Physical && req.TrackStock;
        p.TrackSerial = p.TrackStock && req.TrackSerial; p.TrackLot = p.TrackStock && req.TrackLot; p.TrackExpiry = p.TrackStock && req.TrackExpiry;
        p.LowStockThreshold = req.LowStockThreshold; p.ReorderQuantity = req.ReorderQuantity;
        p.ParentProductId = req.ParentProductId; p.VariantAttributes = req.VariantAttributes;

        foreach (var old in p.Barcodes.Where(b => !barcodes.Contains(b.Code)).ToList()) { p.Barcodes.Remove(old); db.Remove(old); }
        foreach (var code in barcodes.Where(c => p.Barcodes.All(b => b.Code != c)))
            p.Barcodes.Add(new ProductBarcode { TenantId = tid, ProductId = p.Id, Code = code });

        foreach (var old in p.Components.ToList()) { p.Components.Remove(old); db.Remove(old); }
        if (p.Type == ProductType.Bundle)
            foreach (var comp in req.Components ?? [])
                p.Components.Add(new BundleItem { TenantId = tid, BundleProductId = p.Id, ComponentProductId = comp.ProductId, Quantity = comp.Quantity });

        audit.Write(id is null ? "product.created" : "product.updated", nameof(Product), p.Id.ToString(),
            new { p.Sku, p.SalePrice, oldPrice = id is null ? (decimal?)null : oldPrice });
        await db.SaveChangesAsync(ct);
        return await GetAsync(p.Id, ct);
    }

    private static void Validate(UpsertProductRequest req) =>
        new Validator().NotEmpty(req.Sku, "sku", 64).NotEmpty(req.Name, "name").MaxLength(req.NameAr, "nameAr", 200)
            .MaxLength(req.Description, "description", 2000)
            .Require(Enum.TryParse<ProductType>(req.Type, true, out _), "type", "Unknown product type.")
            .Require(Enum.TryParse<ProductStatus>(req.Status, true, out _), "status", "Unknown status.")
            .NonNegative(req.SalePrice, "salePrice").NonNegative(req.PurchasePrice, "purchasePrice")
            .Require(req.WholesalePrice is null or >= 0, "wholesalePrice", "Must not be negative.")
            .NonNegative(req.LowStockThreshold, "lowStockThreshold").NonNegative(req.ReorderQuantity, "reorderQuantity")
            .Require(!string.Equals(req.Type, "bundle", StringComparison.OrdinalIgnoreCase) || req.Components is { Count: > 0 }, "components", "A bundle needs components.")
            .Require(req.Components is null || req.Components.All(c => c.Quantity > 0), "components", "Component quantities must be positive.")
            .Require((req.Barcodes ?? []).All(b => b.Length <= 64), "barcodes", "Barcode too long.")
            .ThrowIfInvalid();

    private async Task EnsureReferencesAsync(UpsertProductRequest req, CancellationToken ct)
    {
        var v = new Validator();
        if (req.CategoryId is { } c) v.Require(await db.Set<Category>().AnyAsync(x => x.Id == c, ct), "categoryId", "Unknown category.");
        if (req.BrandId is { } b) v.Require(await db.Set<Brand>().AnyAsync(x => x.Id == b, ct), "brandId", "Unknown brand.");
        if (req.UnitId is { } u) v.Require(await db.Set<Unit>().AnyAsync(x => x.Id == u, ct), "unitId", "Unknown unit.");
        if (req.TaxCategoryId is { } t) v.Require(await db.Set<TaxCategory>().AnyAsync(x => x.Id == t, ct), "taxCategoryId", "Unknown tax category.");
        if (req.ParentProductId is { } pp) v.Require(await db.Set<Product>().AnyAsync(x => x.Id == pp, ct), "parentProductId", "Unknown parent product.");
        if (req.Components is { Count: > 0 } comps)
        {
            var ids = comps.Select(x => x.ProductId).Distinct().ToList();
            var found = await db.Set<Product>().CountAsync(x => ids.Contains(x.Id) && x.Type != ProductType.Bundle, ct);
            v.Require(found == ids.Count, "components", "Components must be existing non-bundle products.");
        }
        v.ThrowIfInvalid();
    }

    public async Task<BulkEditResult> BulkEditAsync(BulkEditRequest req, CancellationToken ct)
    {
        new Validator().Require(req.ProductIds is { Count: > 0 and <= 1000 }, "productIds", "Select between 1 and 1000 products.")
            .Require(req.PriceChangePercent is null or (> -100 and <= 1000), "priceChangePercent", "Out of range.")
            .Require(req.Status is null || Enum.TryParse<ProductStatus>(req.Status, true, out _), "status", "Unknown status.").ThrowIfInvalid();
        var products = await db.Set<Product>().Where(p => req.ProductIds.Contains(p.Id)).ToListAsync(ct);
        foreach (var p in products)
        {
            if (req.CategoryId is { } c) p.CategoryId = c;
            if (req.TaxCategoryId is { } t) p.TaxCategoryId = t;
            if (req.Status is not null) p.Status = Enum.Parse<ProductStatus>(req.Status, true);
            if (req.PriceChangePercent is { } pct) p.SalePrice = Money.Round(p.SalePrice * (1 + pct / 100m));
        }
        audit.Write("product.bulk_edit", nameof(Product), null, new { count = products.Count, req.CategoryId, req.Status, req.PriceChangePercent });
        await db.SaveChangesAsync(ct);
        return new BulkEditResult(products.Count);
    }

    // ---------- Import / export (CSV, UTF-8, semicolon or comma separated) ----------
    public static readonly string[] CsvColumns = ["sku", "name", "name_ar", "type", "status", "category", "barcode", "sale_price", "wholesale_price", "purchase_price", "tax_code", "low_stock", "reorder_qty"];

    public async Task<byte[]> ExportCsvAsync(CancellationToken ct)
    {
        var products = await db.Set<Product>().Include(p => p.Barcodes).AsNoTracking().OrderBy(p => p.Sku).ToListAsync(ct);
        var cats = await db.Set<Category>().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var taxes = await db.Set<TaxCategory>().ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(';', CsvColumns));
        foreach (var p in products)
        {
            string[] row =
            [
                p.Sku, p.Name, p.NameAr ?? "", p.Type.ToString(), p.Status.ToString(), p.CategoryId is { } c ? cats.GetValueOrDefault(c, "") : "",
                p.Barcodes.FirstOrDefault()?.Code ?? "", F(p.SalePrice), p.WholesalePrice is { } w ? F(w) : "", F(p.PurchasePrice),
                p.TaxCategoryId is { } t ? taxes.GetValueOrDefault(t, "") : "", F(p.LowStockThreshold), F(p.ReorderQuantity),
            ];
            sb.AppendLine(string.Join(';', row.Select(Csv.Escape)));
        }
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        static string F(decimal d) => d.ToString(CultureInfo.InvariantCulture);
    }

    public async Task<ImportResult> ImportCsvAsync(Stream stream, CancellationToken ct)
    {
        var tid = ctx.RequireTenant();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(ct);
        var rows = Csv.Parse(text);
        if (rows.Count == 0) throw ValidationException.For("file", "The file is empty.");
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string n) => header.IndexOf(n);
        if (Col("sku") < 0 || Col("name") < 0) throw ValidationException.For("file", "Columns 'sku' and 'name' are required.");
        if (rows.Count > 20_001) throw ValidationException.For("file", "At most 20,000 rows per import.");

        var cats = await db.Set<Category>().ToListAsync(ct);
        var taxes = await db.Set<TaxCategory>().ToListAsync(ct);
        var existing = await db.Set<Product>().Include(p => p.Barcodes).ToDictionaryAsync(p => p.Sku, ct);
        var barcodeOwners = await db.Set<ProductBarcode>().ToDictionaryAsync(b => b.Code, b => b.ProductId, ct);
        var errors = new List<ImportError>();
        int created = 0, updated = 0;
        for (var i = 1; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.All(string.IsNullOrWhiteSpace)) continue;
            string Get(string n) => Col(n) is var idx and >= 0 && idx < r.Count ? r[idx].Trim() : "";
            decimal? Dec(string n) => decimal.TryParse(Get(n).Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
            var sku = Get("sku");
            var name = Get("name");
            if (sku.Length is 0 or > 64 || name.Length is 0 or > 200) { errors.Add(new(i + 1, "Invalid SKU or name.")); continue; }
            var price = Dec("sale_price") ?? 0;
            if (price < 0) { errors.Add(new(i + 1, "Negative price.")); continue; }
            var type = Enum.TryParse<ProductType>(Get("type"), true, out var t) ? t : ProductType.Physical;
            var status = Enum.TryParse<ProductStatus>(Get("status"), true, out var s) ? s : ProductStatus.Active;
            if (!existing.TryGetValue(sku, out var p))
            {
                p = new Product { TenantId = tid, Sku = sku, Type = type, TrackStock = type == ProductType.Physical };
                db.Set<Product>().Add(p);
                existing[sku] = p;
                created++;
            }
            else updated++;
            p.Name = name; p.NameAr = NullIfEmpty(Get("name_ar")); p.Status = status; p.SalePrice = price;
            p.WholesalePrice = Dec("wholesale_price"); p.PurchasePrice = Dec("purchase_price") ?? p.PurchasePrice;
            if (p.CostPrice == 0) p.CostPrice = p.PurchasePrice;
            p.LowStockThreshold = Dec("low_stock") ?? p.LowStockThreshold; p.ReorderQuantity = Dec("reorder_qty") ?? p.ReorderQuantity;
            var catName = Get("category");
            if (catName.Length > 0)
            {
                var cat = cats.FirstOrDefault(c => string.Equals(c.Name, catName, StringComparison.OrdinalIgnoreCase));
                if (cat is null) { cat = new Category { TenantId = tid, Name = catName.Length > 120 ? catName[..120] : catName }; db.Add(cat); cats.Add(cat); }
                p.CategoryId = cat.Id;
            }
            var taxCode = Get("tax_code");
            if (taxCode.Length > 0)
            {
                var tax = taxes.FirstOrDefault(x => string.Equals(x.Code, taxCode, StringComparison.OrdinalIgnoreCase));
                if (tax is null) errors.Add(new(i + 1, $"Unknown tax code '{taxCode}' (tax not set)."));
                else p.TaxCategoryId = tax.Id;
            }
            var barcode = Get("barcode");
            if (barcode.Length is > 0 and <= 64 && p.Barcodes.All(b => b.Code != barcode))
            {
                if (barcodeOwners.TryGetValue(barcode, out var owner) && owner != p.Id) errors.Add(new(i + 1, $"Barcode '{barcode}' belongs to another product."));
                else { p.Barcodes.Add(new ProductBarcode { TenantId = tid, ProductId = p.Id, Code = barcode }); barcodeOwners[barcode] = p.Id; }
            }
        }
        audit.Write("product.import", nameof(Product), null, new { created, updated, errors = errors.Count });
        await db.SaveChangesAsync(ct);
        return new ImportResult(created, updated, errors);
        static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
    }

    // ---------- Images ----------
    public async Task SetImageAsync(Guid productId, Stream content, string? contentType, CancellationToken ct)
    {
        var tid = ctx.RequireTenant();
        var p = await db.Set<Product>().SingleOrDefaultAsync(x => x.Id == productId, ct) ?? throw new NotFoundException("product.not_found", "Product not found.");
        var (ms, ext) = await UploadValidation.ReadValidatedAsync(content, contentType, 2 * 1024 * 1024, UploadValidation.ImageTypes, ct);
        var path = await storage.SaveAsync(tid, "products", ext, ms, ct);
        if (p.ImagePath is not null) await storage.DeleteAsync(p.ImagePath, ct);
        p.ImagePath = path;
        p.ImageContentType = contentType;
        await db.SaveChangesAsync(ct);
    }

    public async Task<(Stream Stream, string ContentType)?> GetImageAsync(Guid productId, CancellationToken ct)
    {
        var p = await db.Set<Product>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == productId, ct);
        if (p?.ImagePath is null) return null;
        var s = await storage.OpenAsync(p.ImagePath, ct);
        return s is null ? null : (s, p.ImageContentType ?? "application/octet-stream");
    }

    // ---------- Reference data ----------
    public async Task<IReadOnlyList<CategoryDto>> CategoriesAsync(CancellationToken ct) =>
        await db.Set<Category>().OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new CategoryDto(c.Id, c.ParentId, c.Name, c.NameAr, c.SortOrder, c.IsActive)).ToListAsync(ct);

    public async Task<CategoryDto> UpsertCategoryAsync(Guid? id, UpsertCategoryRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Name, "name", 120).MaxLength(req.NameAr, "nameAr", 120).Require(req.ParentId != id || id is null, "parentId", "A category cannot be its own parent.").ThrowIfInvalid();
        if (req.ParentId is { } parent && !await db.Set<Category>().AnyAsync(c => c.Id == parent, ct)) throw ValidationException.For("parentId", "Unknown parent.");
        var c = id is { } cid ? await db.Set<Category>().SingleOrDefaultAsync(x => x.Id == cid, ct) ?? throw new NotFoundException("category.not_found", "Category not found.")
            : db.Set<Category>().Add(new Category { TenantId = ctx.RequireTenant() }).Entity;
        c.ParentId = req.ParentId; c.Name = req.Name.Trim(); c.NameAr = req.NameAr; c.SortOrder = req.SortOrder; c.IsActive = req.IsActive;
        await db.SaveChangesAsync(ct);
        return new CategoryDto(c.Id, c.ParentId, c.Name, c.NameAr, c.SortOrder, c.IsActive);
    }

    public async Task<IReadOnlyList<BrandDto>> BrandsAsync(CancellationToken ct) =>
        await db.Set<Brand>().OrderBy(b => b.Name).Select(b => new BrandDto(b.Id, b.Name)).ToListAsync(ct);

    public async Task<BrandDto> UpsertBrandAsync(Guid? id, UpsertBrandRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Name, "name", 120).ThrowIfInvalid();
        if (await db.Set<Brand>().AnyAsync(b => b.Name == req.Name.Trim() && b.Id != id, ct)) throw new ConflictException("brand.exists", "Brand already exists.");
        var b = id is { } bid ? await db.Set<Brand>().SingleOrDefaultAsync(x => x.Id == bid, ct) ?? throw new NotFoundException("brand.not_found", "Brand not found.")
            : db.Set<Brand>().Add(new Brand { TenantId = ctx.RequireTenant() }).Entity;
        b.Name = req.Name.Trim();
        await db.SaveChangesAsync(ct);
        return new BrandDto(b.Id, b.Name);
    }

    public async Task<IReadOnlyList<UnitDto>> UnitsAsync(CancellationToken ct) =>
        await db.Set<Unit>().OrderBy(u => u.Code).Select(u => new UnitDto(u.Id, u.Code, u.Name, u.AllowsFraction)).ToListAsync(ct);

    public async Task<UnitDto> UpsertUnitAsync(Guid? id, UpsertUnitRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Code, "code", 20).NotEmpty(req.Name, "name", 60).ThrowIfInvalid();
        if (await db.Set<Unit>().AnyAsync(u => u.Code == req.Code.Trim() && u.Id != id, ct)) throw new ConflictException("unit.exists", "Unit code already exists.");
        var u = id is { } uid ? await db.Set<Unit>().SingleOrDefaultAsync(x => x.Id == uid, ct) ?? throw new NotFoundException("unit.not_found", "Unit not found.")
            : db.Set<Unit>().Add(new Unit { TenantId = ctx.RequireTenant() }).Entity;
        u.Code = req.Code.Trim(); u.Name = req.Name.Trim(); u.AllowsFraction = req.AllowsFraction;
        await db.SaveChangesAsync(ct);
        return new UnitDto(u.Id, u.Code, u.Name, u.AllowsFraction);
    }

    public async Task<IReadOnlyList<TaxCategoryDto>> TaxCategoriesAsync(CancellationToken ct) =>
        await db.Set<TaxCategory>().OrderBy(t => t.Code).Select(t => new TaxCategoryDto(t.Id, t.Code, t.Name, t.RatePercent, t.IsActive)).ToListAsync(ct);

    public async Task<TaxCategoryDto> UpsertTaxCategoryAsync(Guid? id, UpsertTaxCategoryRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Code, "code", 20).NotEmpty(req.Name, "name", 80)
            .Require(req.RatePercent is >= 0 and <= 100, "ratePercent", "Must be between 0 and 100.").ThrowIfInvalid();
        if (await db.Set<TaxCategory>().AnyAsync(t => t.Code == req.Code.Trim() && t.Id != id, ct)) throw new ConflictException("tax.exists", "Tax code already exists.");
        var t = id is { } tid ? await db.Set<TaxCategory>().SingleOrDefaultAsync(x => x.Id == tid, ct) ?? throw new NotFoundException("tax.not_found", "Tax category not found.")
            : db.Set<TaxCategory>().Add(new TaxCategory { TenantId = ctx.RequireTenant() }).Entity;
        var oldRate = t.RatePercent;
        t.Code = req.Code.Trim(); t.Name = req.Name.Trim(); t.RatePercent = req.RatePercent; t.IsActive = req.IsActive;
        audit.Write("tax.updated", nameof(TaxCategory), t.Id.ToString(), new { t.Code, oldRate, newRate = t.RatePercent });
        await db.SaveChangesAsync(ct);
        return new TaxCategoryDto(t.Id, t.Code, t.Name, t.RatePercent, t.IsActive);
    }

    public async Task<IReadOnlyList<PriceListDto>> PriceListsAsync(CancellationToken ct) =>
        (await db.Set<PriceList>().Include(p => p.Items).OrderBy(p => p.Name).ToListAsync(ct))
        .Select(p => new PriceListDto(p.Id, p.Name, p.IsDefault, p.IsWholesale, p.Items.Select(i => new PriceListItemDto(i.ProductId, i.Price, i.MinQuantity)).ToList())).ToList();

    public async Task<PriceListDto> UpsertPriceListAsync(Guid? id, UpsertPriceListRequest req, CancellationToken ct)
    {
        var tid = ctx.RequireTenant();
        new Validator().NotEmpty(req.Name, "name", 100).Require(req.Items.All(i => i.Price >= 0 && i.MinQuantity > 0), "items", "Invalid price or quantity.").ThrowIfInvalid();
        var pl = id is { } pid ? await db.Set<PriceList>().Include(p => p.Items).SingleOrDefaultAsync(x => x.Id == pid, ct) ?? throw new NotFoundException("pricelist.not_found", "Price list not found.")
            : db.Set<PriceList>().Add(new PriceList { TenantId = tid }).Entity;
        if (req.IsDefault) await db.Set<PriceList>().Where(p => p.Id != pl.Id && p.IsDefault).ForEachAsync(p => p.IsDefault = false, ct);
        pl.Name = req.Name.Trim(); pl.IsDefault = req.IsDefault; pl.IsWholesale = req.IsWholesale;
        foreach (var i in pl.Items.ToList()) { pl.Items.Remove(i); db.Remove(i); }
        foreach (var i in req.Items.GroupBy(i => (i.ProductId, i.MinQuantity)).Select(g => g.Last()))
            pl.Items.Add(new PriceListItem { TenantId = tid, PriceListId = pl.Id, ProductId = i.ProductId, Price = i.Price, MinQuantity = i.MinQuantity });
        audit.Write("pricelist.updated", nameof(PriceList), pl.Id.ToString());
        await db.SaveChangesAsync(ct);
        return (await PriceListsAsync(ct)).Single(p => p.Id == pl.Id);
    }

    private async Task<Dictionary<Guid, decimal>> TaxRatesAsync(CancellationToken ct) =>
        await db.Set<TaxCategory>().ToDictionaryAsync(t => t.Id, t => t.RatePercent, ct);

    public static ProductDto ToDto(Product p, IReadOnlyDictionary<Guid, decimal> rates) => new(
        p.Id, p.Sku, p.Name, p.NameAr, p.Description, p.Type.ToString(), p.Status.ToString(), p.CategoryId, p.BrandId, p.UnitId, p.TaxCategoryId,
        p.TaxCategoryId is { } t ? rates.GetValueOrDefault(t) : 0, p.SalePrice, p.WholesalePrice, p.PurchasePrice, p.CostPrice, p.TrackStock,
        p.TrackSerial, p.TrackLot, p.TrackExpiry, p.LowStockThreshold, p.ReorderQuantity, p.ParentProductId, p.VariantAttributes, p.ImagePath is not null,
        p.Barcodes.Select(b => b.Code).ToList(), p.Components.Select(c => new BundleComponentDto(c.ComponentProductId, c.Quantity)).ToList(), p.UpdatedAt);
}

public static class Csv
{
    public static string Escape(string s) =>
        s.IndexOfAny([';', ',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>RFC 4180-style parser; auto-detects ';' or ',' from the header line.</summary>
    public static List<List<string>> Parse(string text)
    {
        text = text.TrimStart('﻿');
        var firstLine = text.Split('\n')[0];
        var sep = firstLine.Count(c => c == ';') >= firstLine.Count(c => c == ',') ? ';' : ',';
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') inQuotes = false;
                else field.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == sep) { row.Add(field.ToString()); field.Clear(); }
            else if (ch == '\n') { row.Add(field.ToString().TrimEnd('\r')); field.Clear(); rows.Add(row); row = new List<string>(); }
            else field.Append(ch);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString().TrimEnd('\r')); rows.Add(row); }
        return rows;
    }
}
