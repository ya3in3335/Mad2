namespace Veltrix.Desktop.Core.Pos;

public static class Money
{
    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    public static decimal RoundToIncrement(decimal amount, decimal increment) =>
        increment <= 0 ? amount : Math.Round(amount / increment, 0, MidpointRounding.AwayFromZero) * increment;

    /// <summary>Split <paramref name="total"/> proportionally so the rounded parts add up exactly (same rule as the server).</summary>
    public static decimal[] Allocate(decimal total, IReadOnlyList<decimal> weights)
    {
        var result = new decimal[weights.Count];
        if (weights.Count == 0) return result;
        var sum = weights.Sum();
        if (sum == 0) { result[0] = total; return result; }
        decimal allocated = 0;
        var largest = 0;
        for (var i = 0; i < weights.Count; i++)
        {
            result[i] = Round(total * weights[i] / sum);
            allocated += result[i];
            if (weights[i] > weights[largest]) largest = i;
        }
        result[largest] += total - allocated;
        return result;
    }
}

public sealed record CartLineInput(decimal UnitPrice, decimal Quantity, decimal TaxRatePercent, decimal DiscountPercent = 0, decimal DiscountAmount = 0);
public sealed record LineTotals(decimal Gross, decimal Discount, decimal Net, decimal Tax, decimal Total);
public sealed record CartTotals(IReadOnlyList<LineTotals> Lines, decimal Gross, decimal Discount, decimal Net, decimal Tax, decimal Total)
{
    public static readonly CartTotals Empty = new([], 0, 0, 0, 0, 0);
}

public sealed record PaymentLine(string Method, decimal Amount);
public sealed record Settlement(decimal Due, decimal Rounding, decimal Paid, decimal Change, decimal Shortfall, bool NonCashExceedsDue = false)
{
    public bool IsComplete => Shortfall == 0 && !NonCashExceedsDue;
}

/// <summary>Client-side mirror of the server's POS pricing so receipts printed offline match what the server books.</summary>
public static class CartCalculator
{
    public static CartTotals Compute(IReadOnlyList<CartLineInput> lines, decimal cartDiscount, bool pricesIncludeTax)
    {
        if (lines.Count == 0) return CartTotals.Empty;
        var work = lines.Select(l =>
        {
            var gross = Money.Round(l.UnitPrice * l.Quantity);
            var discount = Math.Clamp(Money.Round(gross * l.DiscountPercent / 100m) + Money.Round(l.DiscountAmount), 0, Math.Max(gross, 0));
            return (Line: l, Gross: gross, Discount: discount);
        }).ToList();

        if (cartDiscount > 0)
        {
            var weights = work.Select(w => w.Gross - w.Discount).ToList();
            var shares = Money.Allocate(Math.Min(cartDiscount, weights.Sum()), weights);
            for (var i = 0; i < work.Count; i++) work[i] = work[i] with { Discount = work[i].Discount + shares[i] };
        }

        var result = work.Select(w =>
        {
            var amount = w.Gross - w.Discount;
            decimal net, tax, total;
            if (pricesIncludeTax)
            {
                total = Money.Round(amount);
                net = Money.Round(total / (1 + w.Line.TaxRatePercent / 100m));
                tax = total - net;
            }
            else
            {
                net = Money.Round(amount);
                tax = Money.Round(net * w.Line.TaxRatePercent / 100m);
                total = net + tax;
            }
            return new LineTotals(w.Gross, w.Discount, net, tax, total);
        }).ToList();

        return new CartTotals(result, result.Sum(r => r.Gross), result.Sum(r => r.Discount), result.Sum(r => r.Net), result.Sum(r => r.Tax), result.Sum(r => r.Total));
    }

    /// <summary>Applies payments: non-cash may not exceed the amount due, cash covers the rest and produces change.</summary>
    public static Settlement Settle(decimal total, IReadOnlyList<PaymentLine> payments, decimal cashRoundingIncrement = 0)
    {
        var cash = payments.Where(p => IsCash(p.Method)).Sum(p => p.Amount);
        var nonCash = payments.Where(p => !IsCash(p.Method)).Sum(p => p.Amount);
        var rounding = cashRoundingIncrement > 0 && cash > 0 && nonCash == 0 ? Money.RoundToIncrement(total, cashRoundingIncrement) - total : 0;
        var due = total + rounding;
        if (nonCash > due) return new Settlement(due, rounding, cash + nonCash, 0, 0, NonCashExceedsDue: true);
        var cashNeeded = due - nonCash;
        var change = Math.Max(0, cash - cashNeeded);
        var shortfall = Math.Max(0, cashNeeded - cash);
        return new Settlement(due, rounding, cash + nonCash, change, shortfall);
    }

    private static bool IsCash(string method) => string.Equals(method, "Cash", StringComparison.OrdinalIgnoreCase);
}
