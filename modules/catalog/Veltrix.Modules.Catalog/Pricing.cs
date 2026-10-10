using Microsoft.EntityFrameworkCore;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Catalog;

/// <summary>Tax arithmetic on decimals. Supports tax-inclusive and tax-exclusive pricing.</summary>
public static class TaxMath
{
    public readonly record struct Breakdown(decimal Net, decimal Tax, decimal Gross);

    /// <summary>Compute a line's net/tax/gross from a line amount (after discount).</summary>
    public static Breakdown FromAmount(decimal amount, decimal ratePercent, bool amountIncludesTax, int decimals = 2)
    {
        if (ratePercent < 0) throw new ArgumentOutOfRangeException(nameof(ratePercent));
        if (amountIncludesTax)
        {
            var gross = Money.Round(amount, decimals);
            var net = Money.Round(gross / (1 + ratePercent / 100m), decimals);
            return new Breakdown(net, gross - net, gross);
        }
        else
        {
            var net = Money.Round(amount, decimals);
            var tax = Money.Round(net * ratePercent / 100m, decimals);
            return new Breakdown(net, tax, net + tax);
        }
    }
}

public sealed record ResolvedPrice(Guid ProductId, decimal UnitPrice, decimal TaxRatePercent, string Source);

/// <summary>Resolves the applicable unit price: explicit price list → default price list → product sale price.</summary>
public sealed class PricingService(DbContext db)
{
    public async Task<IReadOnlyDictionary<Guid, ResolvedPrice>> ResolveAsync(
        IReadOnlyCollection<(Guid ProductId, decimal Quantity)> lines, Guid? priceListId, CancellationToken ct)
    {
        var ids = lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await db.Set<Product>().AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync(ct);
        var taxIds = products.Where(p => p.TaxCategoryId != null).Select(p => p.TaxCategoryId!.Value).Distinct().ToList();
        var rates = await db.Set<TaxCategory>().Where(t => taxIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.RatePercent, ct);
        var listId = priceListId ?? await db.Set<PriceList>().Where(p => p.IsDefault).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        var items = listId is null ? [] : await db.Set<PriceListItem>().Where(i => i.PriceListId == listId && ids.Contains(i.ProductId)).ToListAsync(ct);

        var result = new Dictionary<Guid, ResolvedPrice>();
        foreach (var (productId, qty) in lines)
        {
            var p = products.SingleOrDefault(x => x.Id == productId) ?? throw ValidationException.For("lines", $"Unknown product {productId}.");
            var rate = p.TaxCategoryId is { } t ? rates.GetValueOrDefault(t) : 0m;
            var tier = items.Where(i => i.ProductId == productId && i.MinQuantity <= Math.Abs(qty)).OrderByDescending(i => i.MinQuantity).FirstOrDefault();
            result[productId] = tier is not null
                ? new ResolvedPrice(productId, tier.Price, rate, "price_list")
                : new ResolvedPrice(productId, p.SalePrice, rate, "product");
        }
        return result;
    }
}

/// <summary>Seeds units, a default price list and tax categories for a new company.</summary>
public sealed class CatalogOnboardingContributor(DbContext db) : IOnboardingContributor
{
    public int Order => 20;

    public async Task ContributeAsync(OnboardingContext c, CancellationToken ct)
    {
        var t = c.TenantId;
        db.AddRange(
            new Unit { TenantId = t, Code = "PCS", Name = c.Locale.StartsWith("ar") ? "قطعة" : c.Locale.StartsWith("fr") ? "Pièce" : "Piece" },
            new Unit { TenantId = t, Code = "KG", Name = "kg", AllowsFraction = true },
            new Unit { TenantId = t, Code = "L", Name = "L", AllowsFraction = true },
            new Unit { TenantId = t, Code = "M", Name = "m", AllowsFraction = true },
            new Unit { TenantId = t, Code = "BOX", Name = c.Locale.StartsWith("fr") ? "Carton" : "Box" });
        db.Add(new PriceList { TenantId = t, Name = "Standard", IsDefault = true });
        db.Add(new TaxCategory { TenantId = t, Code = "EXO", Name = "Exempt / Exonéré", RatePercent = 0 });
        if (c.ApplyAlgeriaTaxTemplate)
        {
            // Template only: rates must be validated by the company's accountant against current Algerian law.
            db.Add(new TaxCategory { TenantId = t, Code = "TVA19", Name = "TVA 19% (template — verify)", RatePercent = 19 });
            db.Add(new TaxCategory { TenantId = t, Code = "TVA9", Name = "TVA 9% (template — verify)", RatePercent = 9 });
        }
        await db.SaveChangesAsync(ct);
    }
}
