using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Veltrix.Desktop.Core.Barcodes;
using Veltrix.Desktop.Core.Pos;

namespace Veltrix.Desktop.Printing;

/// <summary>
/// Draws a <see cref="ReceiptDocument"/> as a FlowDocument (used for both the settings preview and printing). The monospace font is
/// sized so the layout's column count fills the content width. Arabic receipts flow right-to-left; amounts, quantities and codes stay LTR.
/// </summary>
public static class ReceiptRenderer
{
    private const double DipPerMm = 96 / 25.4;
    /// <summary>Advance of the widest fallback monospace font (Courier New) in em; Consolas (0.55) leaves a little slack.</summary>
    private const double CharAdvanceEm = 0.6;
    private static readonly FontFamily Mono = new("Consolas, Courier New");

    public static FlowDocument Render(ReceiptDocument receipt)
    {
        var page = receipt.Page;
        var content = page.ContentWidthMm * DipPerMm;
        var fontSize = content / (page.Columns * CharAdvanceEm);
        var doc = new FlowDocument
        {
            FontFamily = Mono,
            FontSize = fontSize,
            Foreground = Brushes.Black,
            Background = Brushes.White,
            FlowDirection = page.RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            Language = System.Windows.Markup.XmlLanguage.GetLanguage(page.Language),
            PagePadding = new Thickness(page.MarginMm * DipPerMm),
            PageWidth = page.PrintableWidthMm * DipPerMm,
            ColumnWidth = double.PositiveInfinity,
            TextAlignment = TextAlignment.Left,
        };
        foreach (var row in receipt.Rows)
        {
            var block = Row(row, page, fontSize, content);
            if (block is not null) doc.Blocks.Add(block);
        }
        return doc;
    }

    private static Block? Row(ReceiptRow row, ReceiptPage page, double fontSize, double contentDip)
    {
        var indent = new Thickness(row.Indent * fontSize * CharAdvanceEm, 0, 0, 0);
        switch (row.Kind)
        {
            case ReceiptRowKind.Separator:
                return Para(new string('-', page.Columns), false, false, false, new Thickness(0));
            case ReceiptRowKind.Text:
                return Para(row.Text, row.Center, row.Bold, row.Ltr, indent);
            case ReceiptRowKind.Pair:
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var left = Cell(row.Text, row.Bold, fontSize);
                left.Margin = indent;
                left.TextTrimming = TextTrimming.None;
                // Alignment is resolved in the (mirrored) parent space, so "Left" stays the line start in Arabic too.
                left.HorizontalAlignment = HorizontalAlignment.Left;
                if (row.Ltr) left.FlowDirection = FlowDirection.LeftToRight;
                var right = Cell(row.Right ?? "", row.Bold, fontSize);
                right.FlowDirection = FlowDirection.LeftToRight; // amounts are always read left to right
                Grid.SetColumn(right, 1);
                grid.Children.Add(left);
                grid.Children.Add(right);
                return new BlockUIContainer(grid) { Margin = new Thickness(0) };
            case ReceiptRowKind.Logo:
                var logo = LoadGrayscale(row.Text);
                if (logo is null) return null;
                var width = Math.Min(page.LogoMaxWidthMm * DipPerMm, contentDip);
                var height = width * logo.PixelHeight / Math.Max(1, logo.PixelWidth);
                return new BlockUIContainer(new Image { Source = logo, Width = width, Height = height, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center })
                { Margin = new Thickness(0, 0, 0, fontSize * 0.4) };
            case ReceiptRowKind.Barcode:
                return Barcode(row.Text, fontSize, contentDip);
            default:
                return null;
        }
    }

    private static Paragraph Para(string text, bool center, bool bold, bool ltr, Thickness margin)
    {
        // An LTR run is embedded in the paragraph's direction: codes keep their order but sit at the line start of an Arabic receipt.
        var run = new Run(text);
        if (ltr) run.FlowDirection = FlowDirection.LeftToRight;
        var p = new Paragraph(run) { Margin = margin, TextAlignment = center ? TextAlignment.Center : TextAlignment.Left };
        if (bold) p.FontWeight = FontWeights.Bold;
        return p;
    }

    private static TextBlock Cell(string text, bool bold, double fontSize) => new()
    {
        Text = text,
        FontFamily = Mono,
        FontSize = fontSize,
        Foreground = Brushes.Black,
        FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
        TextWrapping = TextWrapping.NoWrap,
    };

    private static Block? Barcode(string text, double fontSize, double contentDip)
    {
        BarcodeSymbol symbol;
        try { symbol = BarcodeEncoder.Encode(text); }
        catch (ArgumentException) { return null; }
        var module = Math.Min(0.33 * DipPerMm, contentDip / symbol.TotalWidth);
        var height = 10 * DipPerMm;
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, symbol.TotalWidth * module, height));
            foreach (var bar in symbol.Bars)
                dc.DrawRectangle(Brushes.Black, null, new Rect((symbol.QuietLeft + bar.Start) * module, 0, bar.Width * module, height));
        }
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
        panel.Children.Add(new Image { Source = new DrawingImage(group), Width = symbol.TotalWidth * module, Height = height, Stretch = Stretch.Fill });
        var caption = Cell(text, false, fontSize);
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(caption);
        return new BlockUIContainer(panel) { Margin = new Thickness(0, fontSize * 0.4, 0, 0) };
    }

    /// <summary>Logo flattened on white (transparent areas would otherwise turn black) then converted to 8-bit gray.</summary>
    private static BitmapSource? LoadGrayscale(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // the file is not kept locked
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = 600;
            image.EndInit();
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var r = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
                dc.DrawRectangle(Brushes.White, null, r);
                dc.DrawImage(image, r);
            }
            var rtb = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            var gray = new FormatConvertedBitmap(rtb, PixelFormats.Gray8, null, 0);
            gray.Freeze();
            return gray;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException)
        {
            return null; // unreadable logo: the receipt prints without it
        }
    }
}

/// <summary>Binds a <see cref="ReceiptDocument"/> to a FlowDocument viewer (settings preview).</summary>
public sealed class ReceiptPreviewConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ReceiptDocument doc ? ReceiptRenderer.Render(doc) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
