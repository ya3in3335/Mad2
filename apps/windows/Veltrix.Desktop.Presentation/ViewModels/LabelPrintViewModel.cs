using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Veltrix.Contracts.Catalog;
using Veltrix.Desktop.Core.Barcodes;
using Veltrix.Desktop.Core.Settings;

namespace Veltrix.Desktop.Presentation.ViewModels;

public sealed partial class LabelPrintItem : ObservableObject
{
    public required ProductDto Product { get; init; }
    /// <summary>First barcode, or the SKU (printed as Code 128) when the product has none.</summary>
    public required string Code { get; init; }
    public string Name => Product.Name;
    [ObservableProperty] private string _quantity = "1";
}

public sealed record LabelPresetOption(string Key, string Label);

/// <summary>Options and content of the barcode label dialog. Rendering/printing is done by the WPF view.</summary>
public sealed partial class LabelPrintViewModel : ObservableObject
{
    private readonly DesktopServices _s;

    public LabelPrintViewModel(DesktopServices s, IEnumerable<ProductDto> products)
    {
        _s = s;
        var l = s.Settings.Labels;
        Items = new(products.Select(p => new LabelPrintItem { Product = p, Code = p.Barcodes.FirstOrDefault(b => !string.IsNullOrWhiteSpace(b))?.Trim() ?? p.Sku }));
        Presets = [.. LabelLayout.Presets.Select(p => new LabelPresetOption(p.Key, p.Key.Replace("x", " × ") + " mm")),
            new LabelPresetOption(LabelLayout.CustomKey, s.Loc.Get("dk.labels.custom"))];
        _preset = Presets.Any(p => p.Key == l.Preset) ? l.Preset : LabelLayout.Presets[0].Key;
        _customWidth = F(l.CustomWidthMm);
        _customHeight = F(l.CustomHeightMm);
        _sheetMode = l.SheetMode;
        _rows = l.SheetRows.ToString(CultureInfo.InvariantCulture);
        _columns = l.SheetColumns.ToString(CultureInfo.InvariantCulture);
        _margin = F(l.SheetMarginMm);
        _gap = F(l.SheetGapMm);
        _showShopName = l.ShowShopName;
        PrinterName = l.PrinterName;
    }

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public ObservableCollection<LabelPrintItem> Items { get; }
    public IReadOnlyList<LabelPresetOption> Presets { get; }
    public string? ShopName => _s.Settings.TenantName;
    /// <summary>UI language of the dialog chrome (labels themselves are always rendered left-to-right).</summary>
    public string Language => _s.Settings.Language;
    public string? PrinterName { get; private set; }

    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsCustom))] private string _preset;
    [ObservableProperty] private string _customWidth;
    [ObservableProperty] private string _customHeight;
    [ObservableProperty] private bool _sheetMode;
    [ObservableProperty] private string _rows;
    [ObservableProperty] private string _columns;
    [ObservableProperty] private string _margin;
    [ObservableProperty] private string _gap;
    [ObservableProperty] private bool _showShopName;
    [ObservableProperty] private string? _message;

    public bool IsCustom => Preset == LabelLayout.CustomKey;

    /// <summary>Label size in mm (preset or custom). Throws a localized message when invalid.</summary>
    public (double Width, double Height) LabelSizeMm()
    {
        if (LabelLayout.Presets.FirstOrDefault(p => p.Key == Preset) is { } preset) return (preset.WidthMm, preset.HeightMm);
        if (Parse.TryDecimal(CustomWidth, out var w) && Parse.TryDecimal(CustomHeight, out var h) && w is >= 15 and <= 200 && h is >= 10 and <= 200)
            return ((double)w, (double)h);
        throw new InvalidOperationException(_s.Loc.Get("dk.labels.invalidSize"));
    }

    public (int Rows, int Columns, double Margin, double Gap) Sheet()
    {
        if (int.TryParse(Rows, NumberStyles.None, CultureInfo.InvariantCulture, out var r) && int.TryParse(Columns, NumberStyles.None, CultureInfo.InvariantCulture, out var c)
            && r is >= 1 and <= 30 && c is >= 1 and <= 10 && Parse.TryDecimal(Margin, out var m) && m is >= 0 and <= 40 && Parse.TryDecimal(Gap, out var g) && g is >= 0 and <= 20)
            return (r, c, (double)m, (double)g);
        throw new InvalidOperationException(_s.Loc.Get("dk.labels.invalidSize"));
    }

    /// <summary>Every label to print, each product repeated by its quantity.</summary>
    public IReadOnlyList<LabelContent> BuildLabels()
    {
        var shop = ShowShopName && !string.IsNullOrWhiteSpace(ShopName) ? ShopName!.Trim() : null;
        return LabelLayout.Expand(Items.Select(i =>
            (new LabelContent(i.Product.Name, i.Code, i.Product.SalePrice, shop),
             int.TryParse(i.Quantity, NumberStyles.None, CultureInfo.InvariantCulture, out var q) ? Math.Min(q, 1000) : 0)));
    }

    /// <summary>Remembers the options and the printer used for the next time.</summary>
    public void Remember(string? printerName)
    {
        var l = _s.Settings.Labels;
        l.Preset = Preset;
        if (IsCustom) (l.CustomWidthMm, l.CustomHeightMm) = LabelSizeMm();
        l.SheetMode = SheetMode;
        if (SheetMode) (l.SheetRows, l.SheetColumns, l.SheetMarginMm, l.SheetGapMm) = Sheet();
        l.ShowShopName = ShowShopName;
        if (!string.IsNullOrWhiteSpace(printerName)) l.PrinterName = PrinterName = printerName;
        _s.SaveSettings();
    }

    public string Describe(Exception e) => ErrorText.Describe(_s.Loc, e);
    public string Text(string key) => _s.Loc.Get(key);
    public string Format(string key, params object?[] args) => _s.Loc.Format(key, args);
}
