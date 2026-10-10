using Veltrix.Desktop.Core.Pos;

namespace Veltrix.Desktop.Tests;

public sealed class CartCalculatorTests
{
    [Fact]
    public void Tax_inclusive_prices_extract_tax()
    {
        var t = CartCalculator.Compute([new CartLineInput(119m, 2, 19)], 0, pricesIncludeTax: true);
        Assert.Equal(238m, t.Total);
        Assert.Equal(200m, t.Net);
        Assert.Equal(38m, t.Tax);
    }

    [Fact]
    public void Tax_exclusive_prices_add_tax()
    {
        var t = CartCalculator.Compute([new CartLineInput(100m, 3, 9)], 0, pricesIncludeTax: false);
        Assert.Equal(300m, t.Net);
        Assert.Equal(27m, t.Tax);
        Assert.Equal(327m, t.Total);
    }

    [Fact]
    public void Line_and_cart_discounts_are_allocated_exactly()
    {
        var lines = new[] { new CartLineInput(10m, 1, 0, DiscountPercent: 10), new CartLineInput(20m, 1, 0), new CartLineInput(30m, 1, 0) };
        var t = CartCalculator.Compute(lines, cartDiscount: 10m, pricesIncludeTax: true);
        Assert.Equal(60m, t.Gross);
        Assert.Equal(11m, t.Discount); // 1.00 line discount + 10.00 cart discount
        Assert.Equal(49m, t.Total);
        Assert.Equal(t.Total, t.Lines.Sum(l => l.Total));
    }

    [Fact]
    public void Allocation_parts_sum_to_total()
    {
        var parts = Money.Allocate(10m, [1m, 1m, 1m]);
        Assert.Equal(10m, parts.Sum());
        Assert.All(parts, p => Assert.InRange(p, 3.33m, 3.34m));
    }

    [Fact]
    public void Cash_payment_gives_change()
    {
        var s = CartCalculator.Settle(87.5m, [new PaymentLine("Cash", 100m)]);
        Assert.True(s.IsComplete);
        Assert.Equal(12.5m, s.Change);
    }

    [Fact]
    public void Split_payment_card_then_cash()
    {
        var s = CartCalculator.Settle(100m, [new PaymentLine("Card", 60m), new PaymentLine("Cash", 50m)]);
        Assert.True(s.IsComplete);
        Assert.Equal(10m, s.Change);
        var shortS = CartCalculator.Settle(100m, [new PaymentLine("Card", 60m), new PaymentLine("Cash", 30m)]);
        Assert.False(shortS.IsComplete);
        Assert.Equal(10m, shortS.Shortfall);
    }

    [Fact]
    public void Card_cannot_exceed_amount_due()
    {
        Assert.False(CartCalculator.Settle(50m, [new PaymentLine("Card", 60m)]).IsComplete);
    }

    [Fact]
    public void Cash_rounding_applies_only_to_pure_cash()
    {
        var s = CartCalculator.Settle(99.98m, [new PaymentLine("Cash", 100m)], cashRoundingIncrement: 0.05m);
        Assert.Equal(0.02m, s.Rounding);
        Assert.Equal(0m, s.Change);
        Assert.Equal(0m, CartCalculator.Settle(99.97m, [new PaymentLine("Card", 99.97m)], 0.05m).Rounding);
    }

    [Fact]
    public void Receipt_text_fits_paper_width()
    {
        var r = new ReceiptModel("VELTRIX DEMO", "Alger", "Merci", "W-0001", DateTimeOffset.UtcNow, "Amina", null,
            [new ReceiptLine("A very long product name that will not fit on thermal paper", 2, 150m, 0, 300m)],
            300m, 0, 47.9m, 300m, [new PaymentLine("Cash", 500m)], 200m, Offline: true);
        foreach (var width in new[] { 32, 48 })
            Assert.All(ReceiptText.Render(r, width).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries), l => Assert.True(l.Length <= width, l));
    }
}
