namespace Veltrix.SharedKernel;

/// <summary>Monetary helpers. All amounts are <see cref="decimal"/>; binary floating point is never used for money.</summary>
public static class Money
{
    /// <summary>Default rounding for DZD amounts (2 decimal places, midpoint away from zero).</summary>
    public static decimal Round(decimal amount, int decimals = 2, MidpointRounding mode = MidpointRounding.AwayFromZero) =>
        Math.Round(amount, decimals, mode);

    /// <summary>Round to a cash increment (e.g. 0.05, 1, 5). Used for cash rounding policies.</summary>
    public static decimal RoundToIncrement(decimal amount, decimal increment)
    {
        if (increment <= 0) return amount;
        return Math.Round(amount / increment, 0, MidpointRounding.AwayFromZero) * increment;
    }

    /// <summary>Distribute an amount over weights so that the rounded parts sum exactly to the total.</summary>
    public static decimal[] Allocate(decimal total, IReadOnlyList<decimal> weights, int decimals = 2)
    {
        var result = new decimal[weights.Count];
        if (weights.Count == 0) return result;
        var sum = weights.Sum();
        if (sum == 0) { result[0] = total; return result; }
        decimal allocated = 0;
        var largest = 0;
        for (var i = 0; i < weights.Count; i++)
        {
            result[i] = Round(total * weights[i] / sum, decimals);
            allocated += result[i];
            if (weights[i] > weights[largest]) largest = i;
        }
        result[largest] += total - allocated;
        return result;
    }
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
