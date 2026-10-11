using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Contracts.Catalog;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Erp;

namespace Veltrix.Desktop.Presentation.ViewModels;

/// <summary>Base for the local ERP screens (standalone mode): access to the ERP service, form-error text and date helpers.</summary>
public abstract partial class ErpPageViewModel(DesktopServices s) : PageViewModel(s)
{
    protected ErpService Erp => S.Standalone.Erp;
    protected string Lang => S.Settings.Language;

    /// <summary>Validation message of the open form (null when valid).</summary>
    [ObservableProperty] private string? _formError;

    public static IReadOnlyList<string> PayMethodsList => PayMethods.All;

    protected static DateOnly ToDate(DateTime? d, DateOnly fallback) => d is { } v ? DateOnly.FromDateTime(v) : fallback;
    protected static DateTime FromDate(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);
    protected DateTime TodayDateTime => FromDate(Erp.Today);
    protected DateTime MonthStart => new(Erp.Today.Year, Erp.Today.Month, 1);

    /// <summary>Runs a form submit: clears/sets <see cref="FormError"/> (localized) and the status message.</summary>
    protected async Task<bool> SubmitAsync(Func<Task> action, string successKey = "dk.saved")
    {
        FormError = null;
        var ok = await TryActionAsync(action, successKey);
        if (!ok) FormError = StatusMessage;
        return ok;
    }

    /// <summary>Parses a required amount; throws a localized validation error otherwise.</summary>
    protected static decimal ParseAmount(string? text, bool allowZero = false)
    {
        if (!Parse.TryDecimal(text, out var v) || v < 0 || (!allowZero && v == 0)) throw new InvalidOperationException("erp.err.amountPositive");
        return v;
    }

    protected static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }
}

/// <summary>Product search used by document line editors (purchases, credit sales).</summary>
public sealed partial class ProductPicker(DesktopServices s) : ObservableObject
{
    public ObservableCollection<ProductDto> Results { get; } = [];
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private ProductDto? _selected;

    [RelayCommand]
    public async Task SearchAsync()
    {
        try
        {
            var r = await s.Standalone.Store.ProductsAsync(1, 30, Search.Trim());
            Results.Clear();
            foreach (var p in r.Items) Results.Add(p);
            Selected = Results.FirstOrDefault();
        }
        catch (Exception e) { s.Log.Warn("Product search failed", e); }
    }
}

/// <summary>One editable document line (quantity, unit price/cost and VAT rate as typed text).</summary>
public sealed partial class DocLineEditor : ObservableObject
{
    public required Guid ProductId { get; init; }
    public required string Sku { get; init; }
    public required string Name { get; init; }
    /// <summary>Set for credit-sale lines (needed to rebuild the POS cart).</summary>
    public ProductDto? Product { get; init; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Net), nameof(Tax), nameof(Total))] private string _quantity = "1";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Net), nameof(Tax), nameof(Total))] private string _unitPrice = "0";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Net), nameof(Tax), nameof(Total))] private string _taxRate = "0";

    public decimal Net => Core.Erp.Money.Round(Parse.DecimalOr(Quantity, 0) * Parse.DecimalOr(UnitPrice, 0));
    public decimal Tax => Core.Erp.Money.Round(Net * Parse.DecimalOr(TaxRate, 0) / 100m);
    public decimal Total => Net + Tax;

    public DocLineInput ToInput()
    {
        if (!Parse.TryDecimal(Quantity, out var q) || q <= 0 || !Parse.TryDecimal(UnitPrice, out var c) || c < 0
            || !Parse.TryDecimal(TaxRate, out var t) || t is < 0 or > 100) throw new InvalidOperationException("erp.err.lineInvalid");
        return new DocLineInput(ProductId, q, c, t);
    }

    public static string Text(decimal v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}

/// <summary>Option shown in a combo box: value + display text.</summary>
public sealed record Choice<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

public static class ErpText
{
    public static string AccountLabel(LocalAccount a, string lang) => $"{a.Code} — {ErpService.AccountName(a, lang)}";
}
