using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Contracts.Crm;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Erp;
using Veltrix.Desktop.Core.Pos;

namespace Veltrix.Desktop.Presentation.ViewModels;

public sealed partial class ReturnLineEditor : ObservableObject
{
    public required ReturnableLine Source { get; init; }
    [ObservableProperty] private string _quantity = "0";
}

/// <summary>Customer accounts: balances, credit sales (on account), customer payments and sales returns.</summary>
public sealed partial class ReceivablesViewModel : ErpPageViewModel
{
    public ReceivablesViewModel(DesktopServices s) : base(s)
    {
        Picker = new ProductPicker(s);
        _payDate = TodayDateTime;
    }

    public override string TitleKey => "erp.nav.receivables";
    public static IReadOnlyList<string> RefundMethodsList => RefundMethods.All;

    public ProductPicker Picker { get; }
    public ObservableCollection<CustomerDto> Items { get; } = [];
    public ObservableCollection<LocalPayment> Payments { get; } = [];
    public ObservableCollection<DocLineEditor> SaleLines { get; } = [];
    public ObservableCollection<ReturnLineEditor> ReturnLines { get; } = [];

    /// <summary>0 balances &amp; payments, 1 credit sale, 2 sales return.</summary>
    [ObservableProperty] private int _tabIndex;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _withBalanceOnly;
    [ObservableProperty] private CustomerDto? _selected;
    [ObservableProperty] private decimal _totalReceivable;

    [ObservableProperty] private string _payAmount = "";
    [ObservableProperty] private string _payMethod = PayMethods.Cash;
    [ObservableProperty] private DateTime? _payDate;
    [ObservableProperty] private string _payNotes = "";

    [ObservableProperty] private decimal _saleTotal;
    [ObservableProperty] private string? _lastSaleNumber;

    [ObservableProperty] private string _receiptNumber = "";
    [ObservableProperty] private SaleForReturn? _returnSale;
    [ObservableProperty] private string _refundMethod = PayMethods.Cash;
    [ObservableProperty] private string _returnReason = "";

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        var all = (await S.Standalone.Store.CustomersAsync(1, 10_000, Search, ct)).Items;
        var keep = Selected?.Id;
        Fill(Items, all.Where(c => !WithBalanceOnly || c.Balance != 0).OrderByDescending(c => c.Balance));
        TotalReceivable = all.Where(c => c.Balance > 0).Sum(c => c.Balance);
        if (keep is { } id) Selected = Items.FirstOrDefault(c => c.Id == id);
        return Items.Count > 0 || TabIndex != 0;
    }

    partial void OnWithBalanceOnlyChanged(bool value) => _ = ReloadAsync();

    partial void OnSelectedChanged(CustomerDto? value)
    {
        Payments.Clear();
        FormError = null;
        if (value is not null) _ = LoadPaymentsAsync(value.Id);
    }

    private async Task LoadPaymentsAsync(Guid id)
    {
        try
        {
            var list = await Erp.PaymentsAsync(PartyKinds.Customer, id);
            if (Selected?.Id == id) Fill(Payments, list);
        }
        catch (Exception e) { S.Log.Warn("Customer payments failed", e); }
    }

    [RelayCommand] public Task SearchNowAsync() => ReloadAsync();

    [RelayCommand]
    public Task ReceivePaymentAsync() => SubmitAsync(async () =>
    {
        if (Selected is not { } c) throw new InvalidOperationException("erp.err.unknownCustomer");
        await Erp.ReceiveCustomerPaymentAsync(c.Id, ParseAmount(PayAmount), PayMethod, ToDate(PayDate, Erp.Today), PayNotes);
        PayAmount = "";
        PayNotes = "";
        await ReloadAsync();
    }, "erp.msg.paymentRecorded");

    // ------------------------------------------------------------------ credit sale

    [RelayCommand]
    public void AddSaleLine()
    {
        if (Picker.Selected is not { } p) return;
        var line = new DocLineEditor { ProductId = p.Id, Sku = p.Sku, Name = p.Name, Product = p, Quantity = "1", UnitPrice = DocLineEditor.Text(p.SalePrice), TaxRate = DocLineEditor.Text(p.TaxRatePercent) };
        line.PropertyChanged += (_, _) => RecalcSale();
        SaleLines.Add(line);
        RecalcSale();
    }

    [RelayCommand]
    public void RemoveSaleLine(DocLineEditor? line)
    {
        if (line is not null) SaleLines.Remove(line);
        RecalcSale();
    }

    private void RecalcSale() => SaleTotal = SaleLines.Sum(l => l.Total);

    /// <summary>Records the sale through the POS engine with an on-account payment: receipt, stock, posting (Dr 411) and customer balance.</summary>
    [RelayCommand]
    public Task CreditSaleAsync() => SubmitAsync(async () =>
    {
        if (Selected is not { } c) throw new InvalidOperationException("erp.err.customerRequired");
        if (SaleLines.Count == 0) throw new InvalidOperationException("erp.err.linesRequired");
        var cart = new Cart();
        foreach (var l in SaleLines)
        {
            if (!Parse.TryDecimal(l.Quantity, out var q) || q <= 0) throw new InvalidOperationException("erp.err.lineInvalid");
            cart.Add(SellableProduct.From(l.Product!), q);
        }
        var config = await S.Pos.LoadConfigAsync(default);
        var total = cart.Totals(config).Total;
        if (c.CreditLimit > 0 && c.Balance + total > c.CreditLimit && !S.Ui.Confirm(Loc.Get("erp.confirm.creditLimit"))) return;
        var reg = await S.Standalone.Store.EnsureRegisterAsync();
        var summary = await Erp.CashRegisterAsync();
        var shift = summary.Shift is { } sh ? new ShiftInfo(sh.Id, sh.RegisterId, sh.OpenedAt, sh.OpeningFloat, false) : new ShiftInfo(Guid.Empty, reg.Id, S.Time.GetUtcNow(), 0, false);
        var result = await S.Pos.CheckoutAsync(cart, config, reg.Id, reg.Code, shift, c.Id, c.Name, [new PaymentLine("OnAccount", total)],
            S.Settings.TenantName ?? "VELTRIX OS", S.Session.User?.FullName ?? "", S.Settings.Language, default);
        LastSaleNumber = result.Number;
        SaleLines.Clear();
        RecalcSale();
        await ReloadAsync();
    }, "erp.msg.saleRecorded");

    // ------------------------------------------------------------------ sales return

    [RelayCommand]
    public Task FindSaleAsync() => SubmitAsync(async () =>
    {
        ReturnSale = null;
        ReturnLines.Clear();
        if (string.IsNullOrWhiteSpace(ReceiptNumber)) throw new InvalidOperationException("erp.err.unknownSale");
        ReturnSale = await Erp.FindSaleForReturnAsync(ReceiptNumber);
        foreach (var l in ReturnSale.Lines.Where(l => l.Returnable > 0)) ReturnLines.Add(new ReturnLineEditor { Source = l });
        RefundMethod = ReturnSale.Sale.CustomerId is null ? PayMethods.Cash : RefundMethods.OnAccount;
    }, "erp.msg.saleFound");

    [RelayCommand]
    public Task SubmitReturnAsync() => SubmitAsync(async () =>
    {
        if (ReturnSale is not { } sale) throw new InvalidOperationException("erp.err.unknownSale");
        var lines = new List<SaleReturnLineInput>();
        foreach (var l in ReturnLines)
        {
            if (!Parse.TryDecimal(l.Quantity, out var q) || q < 0 || q > l.Source.Returnable) throw new InvalidOperationException("erp.err.returnExceeds");
            if (q > 0) lines.Add(new SaleReturnLineInput(l.Source.Line.Id, q));
        }
        if (lines.Count == 0) throw new InvalidOperationException("erp.err.linesRequired");
        if (!S.Ui.Confirm(Loc.Get("erp.confirm.return"))) return;
        await Erp.CreateSaleReturnAsync(new SaleReturnInput(sale.Sale.Id, lines, RefundMethod, Erp.Today, ReturnReason));
        ReturnSale = null;
        ReturnLines.Clear();
        ReceiptNumber = "";
        ReturnReason = "";
        await ReloadAsync();
    }, "erp.msg.returnRecorded");
}
