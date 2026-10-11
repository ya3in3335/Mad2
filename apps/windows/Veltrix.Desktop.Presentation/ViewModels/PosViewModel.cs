using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Contracts.Pos;
using Veltrix.Desktop.Core.Pos;

namespace Veltrix.Desktop.Presentation.ViewModels;

public sealed partial class CartLineViewModel(CartItem item, string name, LineTotals totals) : ObservableObject
{
    public CartItem Item { get; } = item;
    public string Name { get; } = name;
    public string Sku => Item.Product.Sku;
    public decimal Quantity => Item.Quantity;
    public decimal UnitPrice => Item.Product.SalePrice;
    public decimal DiscountPercent => Item.DiscountPercent;
    public decimal Discount { get; } = totals.Discount;
    public decimal Total { get; } = totals.Total;
}

/// <summary>Point of sale: register/shift, barcode or text lookup, cart, payment, receipt printing, offline queue.</summary>
public sealed partial class PosViewModel(DesktopServices s) : PageViewModel(s)
{
    private readonly Cart _cart = new();
    private PosConfig _config = PosConfig.Default;

    public override string TitleKey => "nav.pos";

    public ObservableCollection<RegisterDto> Registers { get; } = [];
    public ObservableCollection<CartLineViewModel> Lines { get; } = [];
    public ObservableCollection<SellableProduct> SearchResults { get; } = [];

    [ObservableProperty] private RegisterDto? _selectedRegister;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasShift), nameof(CanSell))] private ShiftInfo? _shift;
    [ObservableProperty] private string _openingFloat = "0";
    [ObservableProperty] private string _countedCash = "";
    [ObservableProperty] private string _scanText = "";
    [ObservableProperty] private CartLineViewModel? _selectedLine;
    [ObservableProperty] private decimal _subtotal;
    [ObservableProperty] private decimal _discount;
    [ObservableProperty] private decimal _tax;
    [ObservableProperty] private decimal _total;
    [ObservableProperty] private string _cashTendered = "";
    [ObservableProperty] private string _cardAmount = "";
    [ObservableProperty] private string _cartDiscountText = "";
    [ObservableProperty] private decimal _change;
    [ObservableProperty] private decimal _shortfall;
    [ObservableProperty] private decimal _amountDue;
    [ObservableProperty] private bool _paymentValid;
    [ObservableProperty] private bool _isPaying;
    [ObservableProperty] private string? _lastSaleNumber;
    [ObservableProperty] private bool _lastSaleOffline;
    [ObservableProperty] private int _saleCompletedCount;
    [ObservableProperty] private string? _scanError;

    public bool HasShift => Shift is not null;
    public bool LicenseAllowsSelling => S.IsStandalone || (S.License.Current?.CanSell ?? false);
    public bool CanSell => HasShift && LicenseAllowsSelling;
    public bool CanDiscount => Can("pos.discount");
    public string Currency => _config.Currency;
    public ReceiptModel? LastReceipt { get; private set; }

    /// <summary>Cashier camera (sub stream) for the small live tile; null when none is configured/enabled.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowCameraTile))] private CameraFeed? _cashierFeed;
    public bool ShowCameraTile => CashierFeed is not null && S.Settings.Cameras.ShowPosTile;

    /// <summary>The tile's close button: hides it and remembers the choice (re-enable in Settings).</summary>
    [RelayCommand]
    public void HideCameraTile()
    {
        S.Settings.Cameras.ShowPosTile = false;
        S.SaveSettings();
        OnPropertyChanged(nameof(ShowCameraTile));
    }

    /// <summary>Re-reads the cashier camera and the tile preference (called each time the POS screen is shown).</summary>
    public async Task RefreshCameraAsync()
    {
        try
        {
            var cam = S.Cameras is null ? null : await S.Cameras.Repository.CashierCameraAsync();
            CashierFeed = cam is null ? null : CameraFeed.For(cam, Core.Cameras.CameraStream.Sub);
        }
        catch (Exception e)
        {
            S.Log.Warn("Cashier camera lookup failed", e);
            CashierFeed = null;
        }
        OnPropertyChanged(nameof(ShowCameraTile));
    }

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        _config = await S.Pos.LoadConfigAsync(ct);
        OnPropertyChanged(nameof(Currency));
        var (registers, offline) = await S.Pos.RegistersAsync(ct);
        IsOfflineData = offline;
        Registers.Clear();
        foreach (var r in registers.Where(r => r.IsActive)) Registers.Add(r);
        SelectedRegister = Registers.FirstOrDefault(r => r.Id == S.Settings.RegisterId) ?? (Registers.Count == 1 ? Registers[0] : null);
        await LoadShiftAsync(ct);
        await RefreshCameraAsync();
        OnPropertyChanged(nameof(LicenseAllowsSelling));
        OnPropertyChanged(nameof(CanSell));
        return Registers.Count > 0;
    }

    partial void OnSelectedRegisterChanged(RegisterDto? value)
    {
        if (value is null) return;
        if (S.Settings.RegisterId != value.Id)
        {
            S.Settings.RegisterId = value.Id;
            S.SaveSettings();
        }
        if (State != LoadState.Loading) _ = LoadShiftAsync(CancellationToken.None);
    }

    private async Task LoadShiftAsync(CancellationToken ct) => Shift = SelectedRegister is null ? null : await S.Pos.CurrentShiftAsync(SelectedRegister, ct);

    [RelayCommand]
    public Task OpenShiftAsync() => TryActionAsync(async () =>
    {
        if (SelectedRegister is null) throw new InvalidOperationException(Loc.Get("dk.pos.selectRegister"));
        Shift = await S.Pos.OpenShiftAsync(SelectedRegister.Id, Math.Max(0, Parse.DecimalOr(OpeningFloat, 0)), CancellationToken.None);
        if (Shift.Offline) StatusMessage = Loc.Get("pos.savedOffline");
    });

    [RelayCommand]
    public Task CloseShiftAsync() => TryActionAsync(async () =>
    {
        if (Shift is null) return;
        if (!Parse.TryDecimal(CountedCash, out var counted)) throw new InvalidOperationException(Loc.Get("err.required"));
        if (!S.Ui.Confirm(Loc.Get("pos.closeShift") + "?")) return;
        var closed = await S.Pos.CloseShiftAsync(Shift.Id, counted, null, CancellationToken.None);
        Shift = null;
        CountedCash = "";
        StatusMessage = closed is null
            ? Loc.Get("pos.savedOffline")
            : $"{Loc.Get("pos.expectedCash")}: {closed.ExpectedCash?.ToString("N2", Loc.Culture)} — {Loc.Get("pos.variance")}: {closed.Variance?.ToString("N2", Loc.Culture)}";
    }, null);

    /// <summary>Enter in the scan box: exact barcode/SKU match adds the item, otherwise a text search lists candidates.</summary>
    [RelayCommand]
    public async Task ScanAsync()
    {
        var text = ScanText.Trim();
        ScanError = null;
        if (text.Length == 0) return;
        try
        {
            var qty = 1m;
            // "3*12345" sells three units of 12345.
            var star = text.IndexOf('*');
            if (star > 0 && Parse.TryDecimal(text[..star], out var q) && q > 0) { qty = q; text = text[(star + 1)..].Trim(); }
            var product = await S.Pos.FindByCodeAsync(text, CancellationToken.None);
            if (product is not null)
            {
                AddToCart(product, qty);
                ScanText = "";
                SearchResults.Clear();
                return;
            }
            var (items, offline) = await S.Pos.SearchAsync(text, CancellationToken.None);
            IsOfflineData = offline;
            SearchResults.Clear();
            foreach (var i in items) SearchResults.Add(i);
            if (items.Count == 0) ScanError = Loc.Get("dk.pos.productNotFound");
        }
        catch (Exception e)
        {
            ScanError = Describe(e);
        }
    }

    [RelayCommand]
    public void AddProduct(SellableProduct? product)
    {
        if (product is null) return;
        AddToCart(product, 1);
        SearchResults.Clear();
        ScanText = "";
    }

    public void AddToCart(SellableProduct product, decimal quantity)
    {
        if (!CanSell) { ScanError = Loc.Get(HasShift ? "dk.pos.cannotSell" : "dk.pos.noShift"); return; }
        _cart.Add(product, quantity);
        Recalculate();
        SelectedLine = Lines.FirstOrDefault(l => l.Item.Product.Id == product.Id);
    }

    [RelayCommand]
    public void Increment(CartLineViewModel? line)
    {
        if (line is null) return;
        _cart.SetQuantity(line.Item, line.Item.Quantity + 1);
        Recalculate();
    }

    [RelayCommand]
    public void Decrement(CartLineViewModel? line)
    {
        if (line is null) return;
        _cart.SetQuantity(line.Item, line.Item.Quantity - 1);
        Recalculate();
    }

    [RelayCommand]
    public void RemoveLine(CartLineViewModel? line)
    {
        if (line is null) return;
        _cart.Remove(line.Item);
        Recalculate();
    }

    [RelayCommand]
    public void SetLineDiscount(string? percentText)
    {
        if (SelectedLine is null || !CanDiscount || !Parse.TryDecimal(percentText, out var pct)) return;
        _cart.SetLineDiscount(SelectedLine.Item, pct, _config.MaxDiscountPercent);
        Recalculate();
    }

    partial void OnCartDiscountTextChanged(string value)
    {
        if (!CanDiscount) return;
        _cart.SetCartDiscount(Parse.DecimalOr(value, 0));
        Recalculate();
    }

    partial void OnCashTenderedChanged(string value) => UpdateSettlement();
    partial void OnCardAmountChanged(string value) => UpdateSettlement();

    [RelayCommand]
    public void ClearCart()
    {
        _cart.Clear();
        CartDiscountText = "";
        CashTendered = "";
        CardAmount = "";
        IsPaying = false;
        Recalculate();
    }

    [RelayCommand]
    public void BeginPayment()
    {
        if (_cart.IsEmpty || !CanSell) return;
        IsPaying = true;
        UpdateSettlement();
    }

    [RelayCommand]
    public void CancelPayment() => IsPaying = false;

    [RelayCommand]
    public void ExactCash()
    {
        CardAmount = "";
        CashTendered = CartCalculator.Settle(Total, [new PaymentLine("Cash", Total)], _config.CashRoundingIncrement).Due.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    public void FullCard()
    {
        CashTendered = "";
        CardAmount = Total.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<PaymentLine> Payments()
    {
        var list = new List<PaymentLine>();
        if (Parse.TryDecimal(CardAmount, out var card) && card > 0) list.Add(new PaymentLine("Card", card));
        if (Parse.TryDecimal(CashTendered, out var cash) && cash > 0) list.Add(new PaymentLine("Cash", cash));
        return list;
    }

    private void UpdateSettlement()
    {
        var payments = Payments();
        var st = CartCalculator.Settle(Total, payments, _config.CashRoundingIncrement);
        AmountDue = st.Due;
        Change = st.Change;
        Shortfall = st.NonCashExceedsDue ? 0 : st.Shortfall;
        PaymentValid = !_cart.IsEmpty && payments.Count > 0 && st.IsComplete;
    }

    [RelayCommand]
    public async Task CompleteSaleAsync()
    {
        if (!PaymentValid || SelectedRegister is null || Shift is null) return;
        var payments = Payments();
        var ok = await TryActionAsync(async () =>
        {
            var result = await S.Pos.CheckoutAsync(_cart, _config, SelectedRegister.Id, SelectedRegister.Code, Shift, null, null, payments,
                S.Settings.TenantName ?? "VELTRIX OS", S.Session.User?.FullName ?? "", S.Settings.Language, CancellationToken.None);
            LastReceipt = result.Receipt;
            // Cashier-camera photo: fire-and-forget, never blocks or fails the sale.
            try { S.Cameras?.Snapshots.CaptureForSaleInBackground(result.SaleId); }
            catch (Exception e) { S.Log.Warn("Sale snapshot not started", e); }
            LastSaleNumber = result.Number;
            LastSaleOffline = result.Offline;
            StatusMessage = Loc.Get(result.Offline ? "pos.savedOffline" : "pos.saleComplete") + " — " + result.Number;
        });
        if (!ok) return;
        SaleCompletedCount++;
        ClearCart();
        PrintLastReceipt();
    }

    [RelayCommand]
    public void PrintLastReceipt()
    {
        if (LastReceipt is null) return;
        var width = ReceiptText.CharsFor(S.Settings.ReceiptPaperMm);
        if (!S.Ui.PrintText(LastReceipt.Number, ReceiptText.Render(LastReceipt, width), width)) StatusMessage = Loc.Get("dk.pos.printSkipped");
    }

    private void Recalculate()
    {
        var totals = _cart.Totals(_config);
        var selectedId = SelectedLine?.Item;
        Lines.Clear();
        for (var i = 0; i < _cart.Items.Count; i++)
        {
            var item = _cart.Items[i];
            Lines.Add(new CartLineViewModel(item, item.Product.DisplayName(S.Settings.Language), totals.Lines[i]));
        }
        SelectedLine = Lines.FirstOrDefault(l => ReferenceEquals(l.Item, selectedId)) ?? Lines.LastOrDefault();
        Subtotal = totals.Gross;
        Discount = totals.Discount;
        Tax = totals.Tax;
        Total = totals.Total;
        if (_cart.IsEmpty) IsPaying = false;
        UpdateSettlement();
    }
}
