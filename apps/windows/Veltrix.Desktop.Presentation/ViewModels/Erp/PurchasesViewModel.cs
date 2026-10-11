using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Erp;

namespace Veltrix.Desktop.Presentation.ViewModels;

/// <summary>Purchase invoice (draft) or purchase return being edited.</summary>
public sealed partial class PurchaseEditor : ObservableObject
{
    public PurchaseEditor()
    {
        Lines.CollectionChanged += OnLinesChanged;
    }

    public Guid? DraftId { get; init; }
    public bool IsReturn { get; init; }
    public ObservableCollection<DocLineEditor> Lines { get; } = [];

    [ObservableProperty] private LocalSupplier? _supplier;
    [ObservableProperty] private DateTime? _date;
    [ObservableProperty] private string _reference = "";
    [ObservableProperty] private string _notes = "";
    /// <summary>Returns: the received invoice the goods came from (optional; limits quantities).</summary>
    [ObservableProperty] private PurchaseRow? _originalInvoice;
    [ObservableProperty] private DocLineEditor? _selectedLine;

    public decimal Subtotal => Lines.Sum(l => l.Net);
    public decimal Tax => Lines.Sum(l => l.Tax);
    public decimal Total => Lines.Sum(l => l.Total);

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (DocLineEditor l in e.NewItems ?? Array.Empty<DocLineEditor>()) l.PropertyChanged += OnLineChanged;
        foreach (DocLineEditor l in e.OldItems ?? Array.Empty<DocLineEditor>()) l.PropertyChanged -= OnLineChanged;
        OnLineChanged(null, new PropertyChangedEventArgs(null));
    }

    private void OnLineChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Subtotal));
        OnPropertyChanged(nameof(Tax));
        OnPropertyChanged(nameof(Total));
    }

    [RelayCommand]
    public void RemoveLine(DocLineEditor? line)
    {
        if ((line ?? SelectedLine) is { } l) Lines.Remove(l);
    }

    public IReadOnlyList<DocLineInput> ToLines()
    {
        if (Lines.Count == 0) throw new InvalidOperationException("erp.err.linesRequired");
        return Lines.Select(l => l.ToInput()).ToList();
    }
}

/// <summary>Purchases: invoices (draft → received: stock in, moving-average cost, posting) and returns to suppliers.</summary>
public sealed partial class PurchasesViewModel : ErpPageViewModel
{
    public PurchasesViewModel(DesktopServices s) : base(s)
    {
        Picker = new ProductPicker(s);
        _from = MonthStart.AddMonths(-2);
        _to = TodayDateTime;
    }

    public override string TitleKey => "erp.nav.purchases";

    public ProductPicker Picker { get; }
    public ObservableCollection<PurchaseRow> Items { get; } = [];
    public ObservableCollection<LocalPurchaseLine> SelectedLines { get; } = [];
    public ObservableCollection<LocalSupplier> Suppliers { get; } = [];
    public ObservableCollection<PurchaseRow> ReceivedInvoices { get; } = [];

    /// <summary>0 = invoices, 1 = returns.</summary>
    [ObservableProperty] private int _tabIndex;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private DateTime? _from;
    [ObservableProperty] private DateTime? _to;
    [ObservableProperty] private LocalSupplier? _supplierFilter;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanReceive), nameof(CanEditDraft))] private PurchaseRow? _selected;
    [ObservableProperty] private PurchaseEditor? _editor;

    public bool CanReceive => Selected is { Doc: { Status: PurchaseStatuses.Draft, Kind: PurchaseKinds.Invoice } };
    public bool CanEditDraft => CanReceive;

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        Fill(Suppliers, await Erp.SuppliersAsync(null, true, ct));
        var kind = TabIndex == 1 ? PurchaseKinds.Return : PurchaseKinds.Invoice;
        var rows = await Erp.PurchasesAsync(kind, SupplierFilter?.Id, Search, From is null ? null : ToDate(From, Erp.Today), To is null ? null : ToDate(To, Erp.Today), ct);
        Fill(Items, rows);
        return Items.Count > 0;
    }

    partial void OnTabIndexChanged(int value) => _ = ReloadAsync();
    partial void OnSupplierFilterChanged(LocalSupplier? value) => _ = ReloadAsync();

    partial void OnSelectedChanged(PurchaseRow? value)
    {
        SelectedLines.Clear();
        if (value is not null) _ = LoadLinesAsync(value.Doc.Id);
    }

    private async Task LoadLinesAsync(Guid id)
    {
        try
        {
            var d = await Erp.PurchaseAsync(id);
            if (Selected?.Doc.Id == id) Fill(SelectedLines, d.Lines);
        }
        catch (Exception e) { S.Log.Warn("Purchase lines failed", e); }
    }

    [RelayCommand]
    public Task SearchNowAsync() => ReloadAsync();

    [RelayCommand]
    public void NewInvoice()
    {
        FormError = null;
        Editor = new PurchaseEditor { Date = TodayDateTime };
    }

    [RelayCommand]
    public async Task NewReturnAsync()
    {
        FormError = null;
        Editor = new PurchaseEditor { Date = TodayDateTime, IsReturn = true };
        try { Fill(ReceivedInvoices, (await Erp.PurchasesAsync(PurchaseKinds.Invoice)).Where(r => r.Doc.Status == PurchaseStatuses.Received)); }
        catch (Exception e) { S.Log.Warn("Invoices failed", e); }
    }

    [RelayCommand]
    public async Task EditDraftAsync()
    {
        if (!CanEditDraft || Selected is null) return;
        var d = await Erp.PurchaseAsync(Selected.Doc.Id);
        var ed = new PurchaseEditor
        {
            DraftId = d.Doc.Id, Date = FromDate(d.Doc.Date), Reference = d.Doc.SupplierReference ?? "", Notes = d.Doc.Notes ?? "",
            Supplier = Suppliers.FirstOrDefault(x => x.Id == d.Doc.SupplierId),
        };
        foreach (var l in d.Lines)
            ed.Lines.Add(new DocLineEditor { ProductId = l.ProductId, Sku = l.Sku, Name = l.Name, Quantity = DocLineEditor.Text(l.Quantity), UnitPrice = DocLineEditor.Text(l.UnitCost), TaxRate = DocLineEditor.Text(l.TaxRatePercent) });
        FormError = null;
        Editor = ed;
    }

    /// <summary>Returns with an original invoice: prefill its lines (quantities editable).</summary>
    partial void OnEditorChanged(PurchaseEditor? value)
    {
        if (value is not null) value.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName != nameof(PurchaseEditor.OriginalInvoice) || value.OriginalInvoice is not { } inv) return;
            var d = await Erp.PurchaseAsync(inv.Doc.Id);
            value.Supplier = Suppliers.FirstOrDefault(x => x.Id == d.Doc.SupplierId);
            value.Lines.Clear();
            foreach (var l in d.Lines)
                value.Lines.Add(new DocLineEditor { ProductId = l.ProductId, Sku = l.Sku, Name = l.Name, Quantity = DocLineEditor.Text(l.Quantity), UnitPrice = DocLineEditor.Text(l.UnitCost), TaxRate = DocLineEditor.Text(l.TaxRatePercent) });
        };
    }

    [RelayCommand]
    public void AddLine()
    {
        if (Editor is null || Picker.Selected is not { } p) return;
        Editor.Lines.Add(new DocLineEditor
        {
            ProductId = p.Id, Sku = p.Sku, Name = p.Name, Quantity = "1", UnitPrice = DocLineEditor.Text(p.PurchasePrice > 0 ? p.PurchasePrice : p.CostPrice),
            TaxRate = DocLineEditor.Text(p.TaxRatePercent),
        });
    }

    [RelayCommand]
    public void CancelEdit()
    {
        Editor = null;
        FormError = null;
    }

    private PurchaseInput DraftInput(PurchaseEditor ed) =>
        new(ed.Supplier?.Id ?? throw new InvalidOperationException("erp.err.unknownSupplier"), ToDate(ed.Date, Erp.Today), ed.ToLines(), ed.Reference, ed.Notes);

    [RelayCommand]
    public Task SaveDraftAsync() => SubmitAsync(async () =>
    {
        if (Editor is not { IsReturn: false } ed) return;
        await Erp.SavePurchaseDraftAsync(ed.DraftId, DraftInput(ed));
        Editor = null;
        TabIndex = 0;
        await ReloadAsync();
    });

    /// <summary>Saves the draft being edited and receives it at once.</summary>
    [RelayCommand]
    public Task SaveAndReceiveAsync() => SubmitAsync(async () =>
    {
        if (Editor is not { IsReturn: false } ed) return;
        if (!S.Ui.Confirm(Loc.Get("erp.confirm.receive"))) return;
        var doc = await Erp.SavePurchaseDraftAsync(ed.DraftId, DraftInput(ed));
        await Erp.ReceivePurchaseAsync(doc.Id);
        Editor = null;
        TabIndex = 0;
        await ReloadAsync();
    }, "erp.msg.received");

    [RelayCommand]
    public Task ReceiveAsync() => SubmitAsync(async () =>
    {
        if (!CanReceive || Selected is null) return;
        if (!S.Ui.Confirm(Loc.Get("erp.confirm.receive"))) return;
        await Erp.ReceivePurchaseAsync(Selected.Doc.Id);
        await ReloadAsync();
    }, "erp.msg.received");

    [RelayCommand]
    public Task DeleteDraftAsync() => SubmitAsync(async () =>
    {
        if (!CanEditDraft || Selected is null) return;
        if (!S.Ui.Confirm(Loc.Get("dlg.confirmDelete"))) return;
        await Erp.DeletePurchaseDraftAsync(Selected.Doc.Id);
        await ReloadAsync();
    });

    [RelayCommand]
    public Task SubmitReturnAsync() => SubmitAsync(async () =>
    {
        if (Editor is not { IsReturn: true } ed) return;
        var supplier = ed.Supplier?.Id ?? throw new InvalidOperationException("erp.err.unknownSupplier");
        if (!S.Ui.Confirm(Loc.Get("erp.confirm.return"))) return;
        await Erp.CreatePurchaseReturnAsync(new PurchaseReturnInput(supplier, ToDate(ed.Date, Erp.Today), ed.ToLines(), ed.OriginalInvoice?.Doc.Id, ed.Notes));
        Editor = null;
        TabIndex = 1;
        await ReloadAsync();
    });
}
