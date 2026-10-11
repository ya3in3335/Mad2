using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;

namespace Veltrix.Desktop.Core.Barcodes;

/// <summary>
/// Internal (in-store) EAN-13 codes: GS1 restricted-circulation prefix "21" + a 10-digit sequence + check digit.
/// The sequence is persisted in the local database and codes already used by any product (barcode or SKU) are skipped.
/// </summary>
public sealed class InStoreBarcodeGenerator(Func<LocalDb> dbFactory)
{
    public const string Prefix = "21";
    public const long MaxSequence = 9_999_999_999;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string Build(long sequence)
    {
        if (sequence is < 1 or > MaxSequence) throw new ArgumentOutOfRangeException(nameof(sequence));
        var twelve = Prefix + sequence.ToString("D10", CultureInfo.InvariantCulture);
        return twelve + Ean13.CheckDigit(twelve).ToString(CultureInfo.InvariantCulture);
    }

    public static bool IsInStore(string? code) => Ean13.IsValid(code) && code!.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Returns the next unused code and advances the persisted counter.</summary>
    public async Task<string> NextAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            await using var db = dbFactory();
            var seq = long.TryParse(await db.GetValueAsync(LocalDb.Keys.InStoreBarcodeCounter, ct), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
            while (true)
            {
                if (++seq > MaxSequence) throw new InvalidOperationException("In-store barcode range exhausted.");
                var code = Build(seq);
                if (!await IsUsedAsync(db, code, ct))
                {
                    await db.SetValueAsync(LocalDb.Keys.InStoreBarcodeCounter, seq.ToString(CultureInfo.InvariantCulture), ct);
                    return code;
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static async Task<bool> IsUsedAsync(LocalDb db, string code, CancellationToken ct) =>
        await db.Products.AnyAsync(p => p.Sku == code, ct)
        || (await db.Products.Where(p => p.Barcodes.Contains(code)).Select(p => p.Barcodes).ToListAsync(ct))
            .Any(b => b.Split('\n').Contains(code, StringComparer.Ordinal));
}
