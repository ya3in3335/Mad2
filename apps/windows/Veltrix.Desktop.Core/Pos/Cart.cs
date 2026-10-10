using System.Globalization;
using Veltrix.Contracts.Pos;
using Veltrix.Desktop.Core.Data;

namespace Veltrix.Desktop.Core.Pos;

/// <summary>Minimal product information the POS needs to sell an item (from the API or the local catalog).</summary>
public sealed record SellableProduct(Guid Id, string Sku, string Name, string? NameAr, decimal SalePrice, decimal TaxRatePercent, bool TrackStock, string Type)
{
    public static SellableProduct From(LocalProduct p) => new(p.Id, p.Sku, p.Name, p.NameAr, p.SalePrice, p.TaxRatePercent, p.TrackStock, p.Type);

    public static SellableProduct From(Veltrix.Contracts.Catalog.ProductDto p) =>
        new(p.Id, p.Sku, p.Name, p.NameAr, p.SalePrice, p.TaxRatePercent, p.TrackStock, p.Type);

    public string DisplayName(string language) => language == "ar" && !string.IsNullOrWhiteSpace(NameAr) ? NameAr! : Name;
}

public sealed class CartItem(SellableProduct product, decimal quantity)
{
    public SellableProduct Product { get; } = product;
    public decimal Quantity { get; set; } = quantity;
    public decimal DiscountPercent { get; set; }
}

/// <summary>Tenant POS settings pulled from the server (<c>pos.*</c>, <c>company.*</c>).</summary>
public sealed record PosConfig(bool PricesIncludeTax, decimal CashRoundingIncrement, string? ReceiptHeader, string? ReceiptFooter, string Currency,
    decimal MaxDiscountPercent)
{
    public static readonly PosConfig Default = new(true, 0, null, null, "DZD", 100);

    public static PosConfig FromSettings(IReadOnlyDictionary<string, string> s)
    {
        string? Get(string k) => s.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim().Trim('"') : null;
        decimal Dec(string k, decimal d) => decimal.TryParse(Get(k), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : d;
        return new PosConfig(
            !string.Equals(Get("pos.pricesIncludeTax"), "false", StringComparison.OrdinalIgnoreCase),
            Dec("pos.cashRoundingIncrement", 0), Get("pos.receiptHeader"), Get("pos.receiptFooter"), Get("company.currency") ?? "DZD",
            Dec("pos.maxDiscountPct", 100));
    }
}

/// <summary>The POS basket. All money is computed by <see cref="CartCalculator"/> so offline receipts match the server.</summary>
public sealed class Cart
{
    private readonly List<CartItem> _items = [];

    public IReadOnlyList<CartItem> Items => _items;
    public decimal CartDiscount { get; private set; }
    public bool IsEmpty => _items.Count == 0;

    public CartItem Add(SellableProduct product, decimal quantity = 1)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        var existing = _items.FirstOrDefault(i => i.Product.Id == product.Id && i.DiscountPercent == 0);
        if (existing is not null)
        {
            existing.Quantity += quantity;
            return existing;
        }
        var item = new CartItem(product, quantity);
        _items.Add(item);
        return item;
    }

    public void SetQuantity(CartItem item, decimal quantity)
    {
        if (quantity <= 0) _items.Remove(item);
        else item.Quantity = quantity;
    }

    public void SetLineDiscount(CartItem item, decimal percent, decimal maxPercent) => item.DiscountPercent = Math.Clamp(percent, 0, Math.Min(100, maxPercent));

    public void SetCartDiscount(decimal amount) => CartDiscount = Math.Max(0, Money.Round(amount));

    public void Remove(CartItem item) => _items.Remove(item);

    public void Clear()
    {
        _items.Clear();
        CartDiscount = 0;
    }

    public CartTotals Totals(PosConfig config) =>
        CartCalculator.Compute(_items.Select(i => new CartLineInput(i.Product.SalePrice, i.Quantity, i.Product.TaxRatePercent, i.DiscountPercent)).ToList(),
            CartDiscount, config.PricesIncludeTax);

    public CompleteSaleRequest ToSaleRequest(string idempotencyKey, Guid registerId, Guid shiftId, Guid? customerId, IReadOnlyList<PaymentLine> payments,
        Guid saleId, DateTimeOffset clientTime, bool offline, string? clientNumber) =>
        new(idempotencyKey, registerId, shiftId, customerId,
            _items.Select(i => new SaleLineInput(i.Product.Id, i.Quantity, i.Product.SalePrice, i.DiscountPercent)).ToList(),
            payments.Where(p => p.Amount > 0).Select(p => new PaymentInput(p.Method, p.Amount)).ToList(),
            CartDiscount, null, saleId, clientTime, offline, clientNumber);

    public ReceiptModel ToReceipt(PosConfig config, string companyName, string number, DateTimeOffset date, string cashier, string? customer,
        IReadOnlyList<PaymentLine> payments, Settlement settlement, bool offline, string language)
    {
        var totals = Totals(config);
        var lines = _items.Select((i, idx) => new ReceiptLine(i.Product.DisplayName(language), i.Quantity, i.Product.SalePrice, totals.Lines[idx].Discount,
            totals.Lines[idx].Total)).ToList();
        return new ReceiptModel(companyName, config.ReceiptHeader, config.ReceiptFooter, number, date, cashier, customer, lines,
            totals.Gross, totals.Discount, totals.Tax, totals.Total + settlement.Rounding, payments.Where(p => p.Amount > 0).ToList(), settlement.Change, offline,
            config.Currency);
    }
}
