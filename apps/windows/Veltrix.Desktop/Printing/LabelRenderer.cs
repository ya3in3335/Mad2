using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using Veltrix.Desktop.Core.Barcodes;

namespace Veltrix.Desktop.Printing;

/// <summary>Hosts a <see cref="DrawingVisual"/> inside a FixedPage.</summary>
internal sealed class VisualHost : FrameworkElement
{
    private readonly Visual _visual;
    public VisualHost(Visual visual, double width, double height)
    {
        _visual = visual;
        Width = width;
        Height = height;
        AddVisualChild(visual);
    }
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;
}

/// <summary>
/// Vector label rendering (device-independent units, 96/inch, converted from mm): shop name, upper-cased product name,
/// bars as rectangles with quiet zones, human-readable digits and the price bottom-right. Label content is always LTR.
/// </summary>
public static class LabelRenderer
{
    private static readonly Typeface Regular = new(new FontFamily("Arial"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface Bold = new(new FontFamily("Arial"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static double D(double mm) => LabelLayout.MmToDip(mm);

    /// <summary>One label per page (roll printers) of <paramref name="widthMm"/> × <paramref name="heightMm"/>.</summary>
    public static FixedDocument LabelRoll(IReadOnlyList<LabelContent> labels, double widthMm, double heightMm)
    {
        var doc = NewDocument(widthMm, heightMm);
        foreach (var label in labels)
            AddPage(doc, widthMm, heightMm, dc => DrawLabel(dc, label, new RectMm(0, 0, widthMm, heightMm)));
        return doc;
    }

    /// <summary>A4 sheets: rows × columns per page.</summary>
    public static FixedDocument Sheet(IReadOnlyList<LabelContent> labels, int rows, int cols, double marginMm, double gapMm)
    {
        var cells = LabelLayout.SheetCells(rows, cols, marginMm, gapMm);
        var doc = NewDocument(LabelLayout.A4WidthMm, LabelLayout.A4HeightMm);
        foreach (var page in LabelLayout.Paginate(labels, cells.Count))
            AddPage(doc, LabelLayout.A4WidthMm, LabelLayout.A4HeightMm, dc =>
            {
                for (var i = 0; i < page.Count; i++) DrawLabel(dc, page[i], cells[i]);
            });
        return doc;
    }

    private static FixedDocument NewDocument(double wMm, double hMm)
    {
        var doc = new FixedDocument();
        doc.DocumentPaginator.PageSize = new Size(D(wMm), D(hMm));
        return doc;
    }

    private static void AddPage(FixedDocument doc, double wMm, double hMm, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, D(wMm), D(hMm)));
            draw(dc);
        }
        var page = new FixedPage { Width = D(wMm), Height = D(hMm), Background = Brushes.White, FlowDirection = FlowDirection.LeftToRight };
        page.Children.Add(new VisualHost(visual, D(wMm), D(hMm)));
        var content = new PageContent();
        ((IAddChild)content).AddChild(page);
        doc.Pages.Add(content);
    }

    private static bool HasArabic(string s) => s.Any(c => c is >= '\u0600' and <= '\u06FF' or >= '\u0750' and <= '\u077F' or >= '\uFB50' and <= '\uFEFF');

    private static FormattedText Text(string text, Typeface face, double sizeMm, double maxWidthMm, int maxLines = 1)
    {
        var rtl = HasArabic(text);
        var ft = new FormattedText(text, rtl ? CultureInfo.GetCultureInfo("ar-DZ") : CultureInfo.InvariantCulture,
            rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, face, D(sizeMm), Brushes.Black, 1.0)
        {
            MaxLineCount = maxLines,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        if (maxWidthMm > 0) ft.MaxTextWidth = D(maxWidthMm);
        return ft;
    }

    /// <summary>Draws one label inside <paramref name="cell"/> (mm).</summary>
    public static void DrawLabel(DrawingContext dc, LabelContent label, RectMm cell)
    {
        var pad = Math.Clamp(Math.Min(cell.Width, cell.Height) * 0.05, 1, 3);
        var x = cell.X + pad;
        var w = cell.Width - 2 * pad;
        var top = cell.Y + pad;
        var bottom = cell.Y + cell.Height - pad;
        var unit = Math.Clamp(Math.Min(cell.Height / 30.0, cell.Width / 40.0), 0.6, 1.6);

        dc.PushClip(new RectangleGeometry(new Rect(D(cell.X), D(cell.Y), D(cell.Width), D(cell.Height))));
        var y = top;
        if (!string.IsNullOrWhiteSpace(label.ShopName))
        {
            var shop = Text(label.ShopName!.Trim(), Regular, 2.2 * unit, w);
            shop.TextAlignment = TextAlignment.Center;
            dc.DrawText(shop, new Point(D(x) + (shop.FlowDirection == FlowDirection.RightToLeft ? D(w) : 0), D(y)));
            y += shop.Height / LabelLayout.DipPerMm;
        }
        // Name: up to 2 lines on tall labels, measured so the barcode never overlaps it.
        var name = Text(label.DisplayName, Bold, 2.9 * unit, w, cell.Height >= 35 ? 2 : 1);
        dc.DrawText(name, new Point(D(x) + (name.FlowDirection == FlowDirection.RightToLeft ? D(w) : 0), D(y)));
        y += name.Height / LabelLayout.DipPerMm + 0.3;

        var priceSize = 4.8 * unit;
        var price = Text(label.PriceText, Bold, priceSize, 0);
        var priceTop = bottom - price.Height / LabelLayout.DipPerMm;
        dc.DrawText(price, new Point(D(x + w) - price.Width, D(priceTop)));

        var digitSize = 2.3 * unit;
        var digitH = digitSize * 1.15;
        var barTop = y + 0.4;
        var barBottom = priceTop - digitH - 0.3;
        if (barBottom - barTop < 4) barBottom = barTop + 4; // always keep a scannable bar height; price sits beside the digits
        if (!string.IsNullOrWhiteSpace(label.Code))
            DrawBarcode(dc, BarcodeEncoder.Encode(label.Code), x, w, barTop, barBottom - barTop, digitSize);
        dc.Pop();
    }

    private static void DrawBarcode(DrawingContext dc, BarcodeSymbol symbol, double xMm, double widthMm, double topMm, double barHeightMm, double digitSizeMm)
    {
        var m = LabelLayout.SnapToDots(LabelLayout.ModuleWidthMm(symbol, widthMm));
        var x0 = xMm + (widthMm - symbol.TotalWidth * m) / 2 + symbol.QuietLeft * m;
        var isEan = symbol.Kind == BarcodeKind.Ean13;
        var guardExtra = isEan ? digitSizeMm * 0.6 : 0;
        foreach (var bar in symbol.Bars)
            dc.DrawRectangle(Brushes.Black, null, new Rect(D(x0 + bar.Start * m), D(topMm), D(bar.Width * m), D(barHeightMm + (bar.IsGuard ? guardExtra : 0))));

        var textTop = topMm + barHeightMm + 0.15;
        if (isEan)
        {
            // First digit in the left quiet zone, then two groups of six under the left and right halves.
            DrawCentered(dc, symbol.Text[0].ToString(), x0 - 4 * m, textTop, digitSizeMm);
            for (var i = 1; i <= 6; i++) DrawCentered(dc, symbol.Text[i].ToString(), x0 + (3 + (i - 1) * 7 + 3.5) * m, textTop, digitSizeMm);
            for (var i = 7; i <= 12; i++) DrawCentered(dc, symbol.Text[i].ToString(), x0 + (50 + (i - 7) * 7 + 3.5) * m, textTop, digitSizeMm);
        }
        else
        {
            DrawCentered(dc, symbol.Text, x0 + symbol.Width * m / 2, textTop, digitSizeMm);
        }
    }

    private static void DrawCentered(DrawingContext dc, string text, double centerMm, double topMm, double sizeMm)
    {
        var ft = Text(text, Regular, sizeMm, 0);
        dc.DrawText(ft, new Point(D(centerMm) - ft.Width / 2, D(topMm)));
    }
}
