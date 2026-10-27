using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Pos;

namespace Veltrix.Desktop.Presentation.ViewModels;

/// <summary>Receipt (ticket) customisation: every change is saved at once and refreshes the live <see cref="Preview"/>.</summary>
public sealed partial class ReceiptSettingsViewModel : ObservableObject
{
    private readonly DesktopServices _s;
    private ReceiptSettings R => _s.Settings.Receipt;

    public ReceiptSettingsViewModel(DesktopServices s)
    {
        _s = s;
        BuildOptions();
        Refresh();
    }

    public IReadOnlyList<LanguageOption> PaperOptions { get; } =
        [new("58", "58 mm"), new("80", "80 mm"), new(ReceiptPaper.A4.ToString(System.Globalization.CultureInfo.InvariantCulture), "A4")];
    public IReadOnlyList<int> CopyOptions { get; } = [1, 2, 3];
    public IReadOnlyList<double> MarginOptions { get; } = [0, 1, 2, 3, 4, 5, 8, 10];
    public IReadOnlyList<double> LogoWidthOptions { get; } = [20, 30, 40, 50, 60, 80];
    [ObservableProperty] private IReadOnlyList<LanguageOption> _fontSizeOptions = [];
    [ObservableProperty] private IReadOnlyList<LanguageOption> _languageOptions = [];
    [ObservableProperty] private IReadOnlyList<LanguageOption> _dateFormatOptions = [];
    /// <summary>Designs grouped: shop tickets first, then company documents (A4).</summary>
    [ObservableProperty] private IReadOnlyList<LanguageOption> _templateOptions = [];
    [ObservableProperty] private ReceiptDocument? _preview;

    /// <summary>The interface language changed: localized option names and a receipt that follows the app language.</summary>
    public void AppLanguageChanged()
    {
        BuildOptions();
        Refresh();
    }

    private void BuildOptions()
    {
        var loc = _s.Loc;
        FontSizeOptions = ReceiptFontSizes.All.Select(f => new LanguageOption(f, loc.Get("dk.rcpt.font." + f))).ToList();
        LanguageOptions = [new("app", loc.Get("dk.rcpt.langApp")), .. Core.Settings.Languages.Supported.Select(l => new LanguageOption(l.Code, l.NativeName))];
        var sample = new DateTime(2026, 3, 21, 14, 35, 9);
        DateFormatOptions = ReceiptDateFormats.All.Select(f => new LanguageOption(f, sample.ToString(f, System.Globalization.CultureInfo.InvariantCulture))).ToList();
        TemplateOptions = ReceiptTemplates.All.Select(t => new LanguageOption(t,
            loc.Get(ReceiptTemplates.IsCompany(t) ? "dk.rcpt.tpl.company" : "dk.rcpt.tpl.shop") + " — " + loc.Get("dk.rcpt.tpl." + t))).ToList();
        OnPropertyChanged(nameof(Template));
        // Rebinding the ComboBoxes to new lists: re-announce the selections.
        OnPropertyChanged(nameof(FontSize));
        OnPropertyChanged(nameof(Language));
        OnPropertyChanged(nameof(DateFormat));
    }

    private void Set(Action apply, [CallerMemberName] string? name = null)
    {
        apply();
        R.Normalize();
        _s.SaveSettings();
        OnPropertyChanged(name);
        Refresh();
    }

    public void Refresh() =>
        Preview = _s.ReceiptFor(ReceiptLayout.Sample(ReceiptLayout.ResolveLanguage(R, _s.Settings.Language), _s.Settings.TenantName, _s.Time.GetLocalNow()));

    public string PaperMm
    {
        get => _s.Settings.ReceiptPaperMm.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set => Set(() => _s.Settings.ReceiptPaperMm = ReceiptPaper.Normalize(int.TryParse(value, out var mm) ? mm : 80));
    }

    public double MarginMm { get => R.MarginMm; set => Set(() => R.MarginMm = value); }
    public string FontSize { get => R.FontSize; set => Set(() => R.FontSize = value); }
    public int Copies { get => R.Copies; set => Set(() => R.Copies = value); }

    public string? LogoPath => R.LogoPath;
    public bool HasLogo => !string.IsNullOrWhiteSpace(R.LogoPath);
    public double LogoMaxWidthMm { get => R.LogoMaxWidthMm; set => Set(() => R.LogoMaxWidthMm = value); }
    public string? ShopName { get => R.ShopName; set => Set(() => R.ShopName = Clean(value)); }
    public string? Address { get => R.Address; set => Set(() => R.Address = Clean(value)); }
    public string? Phone { get => R.Phone; set => Set(() => R.Phone = Clean(value)); }
    public string? Nif { get => R.Nif; set => Set(() => R.Nif = Clean(value)); }
    public string? Rc { get => R.Rc; set => Set(() => R.Rc = Clean(value)); }
    public string? Nis { get => R.Nis; set => Set(() => R.Nis = Clean(value)); }
    public string? Ai { get => R.Ai; set => Set(() => R.Ai = Clean(value)); }
    public string? HeaderLines { get => R.HeaderLines; set => Set(() => R.HeaderLines = Clean(value)); }

    public bool ShowItemCode { get => R.ShowItemCode; set => Set(() => R.ShowItemCode = value); }
    public bool ShowUnitPrice { get => R.ShowUnitPrice; set => Set(() => R.ShowUnitPrice = value); }
    public bool ShowDiscount { get => R.ShowDiscount; set => Set(() => R.ShowDiscount = value); }
    public bool ShowTax { get => R.ShowTax; set => Set(() => R.ShowTax = value); }
    public bool ShowCashier { get => R.ShowCashier; set => Set(() => R.ShowCashier = value); }
    public bool ShowCustomer { get => R.ShowCustomer; set => Set(() => R.ShowCustomer = value); }
    public bool ShowSaleNumber { get => R.ShowSaleNumber; set => Set(() => R.ShowSaleNumber = value); }
    public bool ShowDate { get => R.ShowDate; set => Set(() => R.ShowDate = value); }
    public string DateFormat { get => R.DateFormat; set => Set(() => R.DateFormat = value); }
    public bool TotalInWords { get => R.TotalInWords; set => Set(() => R.TotalInWords = value); }
    public bool ShowPayments { get => R.ShowPayments; set => Set(() => R.ShowPayments = value); }

    public string? FooterLines { get => R.FooterLines; set => Set(() => R.FooterLines = Clean(value)); }
    public bool ShowSaleBarcode { get => R.ShowSaleBarcode; set => Set(() => R.ShowSaleBarcode = value); }
    public string Language { get => R.Language; set => Set(() => R.Language = value); }

    /// <summary>Choosing a company document switches the paper to A4 (and a shop ticket back to 80 mm when the paper was A4).</summary>
    public string Template
    {
        get => R.Template;
        set => Set(() =>
        {
            R.Template = ReceiptTemplates.Normalize(value);
            if (ReceiptTemplates.IsCompany(R.Template)) _s.Settings.ReceiptPaperMm = ReceiptPaper.A4;
            else if (_s.Settings.ReceiptPaperMm == ReceiptPaper.A4) _s.Settings.ReceiptPaperMm = ReceiptPaper.Mm80;
            OnPropertyChanged(nameof(PaperMm));
        });
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;

    /// <summary>Copies the chosen image into the application data folder so the receipt never depends on the original file.</summary>
    [RelayCommand]
    public void PickLogo()
    {
        var source = _s.Ui.PickOpenFile(_s.Loc.Get("dk.rcpt.logoFilter") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif");
        if (source is null) return;
        var dir = Path.GetDirectoryName(Path.GetFullPath(_s.SettingsStore.Path))!;
        Directory.CreateDirectory(dir);
        // A new name each time: image caches key on the path.
        var target = Path.Combine(dir, $"receipt-logo-{_s.Time.GetUtcNow().ToUnixTimeMilliseconds()}{Path.GetExtension(source).ToLowerInvariant()}");
        File.Copy(source, target, overwrite: true);
        var old = R.LogoPath;
        Set(() => R.LogoPath = target, nameof(LogoPath));
        OnPropertyChanged(nameof(HasLogo));
        DeleteOwnedLogo(old, dir);
    }

    [RelayCommand]
    public void RemoveLogo()
    {
        var old = R.LogoPath;
        Set(() => R.LogoPath = null, nameof(LogoPath));
        OnPropertyChanged(nameof(HasLogo));
        DeleteOwnedLogo(old, Path.GetDirectoryName(Path.GetFullPath(_s.SettingsStore.Path))!);
    }

    private static void DeleteOwnedLogo(string? path, string dir)
    {
        if (string.IsNullOrWhiteSpace(path) || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), dir, StringComparison.OrdinalIgnoreCase)) return;
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // still open by the preview: left behind, harmless
    }

    [RelayCommand]
    public void TestPrint()
    {
        if (Preview is { } doc) _s.Ui.PrintReceipt("Test", doc);
    }
}
