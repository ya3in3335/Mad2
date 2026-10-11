using System.Globalization;

namespace Veltrix.Desktop.Core.Barcodes;

/// <summary>One label to print: product name, code, price and optional shop name.</summary>
public sealed record LabelContent(string Name, string Code, decimal Price, string? ShopName)
{
    /// <summary>Upper-cased product name (invariant: Arabic text is unaffected).</summary>
    public string DisplayName => (Name ?? "").Trim().ToUpperInvariant();
    public string PriceText => LabelLayout.FormatPrice(Price);
}

public readonly record struct RectMm(double X, double Y, double Width, double Height);

public sealed record LabelPreset(string Key, double WidthMm, double HeightMm);

public static class LabelLayout
{
    public const double A4WidthMm = 210;
    public const double A4HeightMm = 297;
    public const double DipPerMm = 96.0 / 25.4;
    /// <summary>Thinnest bar we allow; below that cheap thermal heads (8 dots/mm) cannot resolve the symbol.</summary>
    public const double MinModuleMm = 0.25;

    public static readonly IReadOnlyList<LabelPreset> Presets =
    [
        new("40x30", 40, 30),
        new("50x30", 50, 30),
        new("58x40", 58, 40),
    ];

    public const string CustomKey = "custom";

    public static double MmToDip(double mm) => mm * DipPerMm;

    /// <summary>"90.00 DA": always two decimals, invariant separator, Algerian dinar suffix.</summary>
    public static string FormatPrice(decimal price) => price.ToString("0.00", CultureInfo.InvariantCulture) + " DA";

    /// <summary>Module (narrow bar) width that fits the symbol plus its quiet zones in the available width.</summary>
    public static double ModuleWidthMm(BarcodeSymbol symbol, double availableWidthMm) =>
        Math.Max(MinModuleMm, availableWidthMm / symbol.TotalWidth);

    /// <summary>Cells of an A4 sheet: rows × cols filling the page inside the margins with a gap between cells.</summary>
    public static IReadOnlyList<RectMm> SheetCells(int rows, int cols, double marginMm, double gapMm, double pageWidthMm = A4WidthMm, double pageHeightMm = A4HeightMm)
    {
        if (rows < 1 || cols < 1) throw new ArgumentOutOfRangeException(nameof(rows));
        marginMm = Math.Max(0, marginMm);
        gapMm = Math.Max(0, gapMm);
        var w = (pageWidthMm - 2 * marginMm - (cols - 1) * gapMm) / cols;
        var h = (pageHeightMm - 2 * marginMm - (rows - 1) * gapMm) / rows;
        if (w <= 5 || h <= 5) throw new InvalidOperationException("Label cells too small.");
        var cells = new List<RectMm>(rows * cols);
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
                cells.Add(new RectMm(marginMm + c * (w + gapMm), marginMm + r * (h + gapMm), w, h));
        return cells;
    }

    /// <summary>Splits labels into pages of <paramref name="perPage"/>.</summary>
    public static IReadOnlyList<IReadOnlyList<T>> Paginate<T>(IReadOnlyList<T> labels, int perPage) =>
        labels.Chunk(Math.Max(1, perPage)).Select(c => (IReadOnlyList<T>)c).ToList();

    /// <summary>Repeats each label by its quantity (quantities below 1 are skipped).</summary>
    public static IReadOnlyList<LabelContent> Expand(IEnumerable<(LabelContent Label, int Quantity)> items) =>
        items.SelectMany(i => Enumerable.Repeat(i.Label, Math.Max(0, i.Quantity))).ToList();
}
