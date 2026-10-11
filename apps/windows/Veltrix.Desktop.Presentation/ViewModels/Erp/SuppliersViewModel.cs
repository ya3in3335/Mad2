using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Erp;

namespace Veltrix.Desktop.Presentation.ViewModels;

public sealed partial class SupplierEditor : ObservableObject
{
    public LocalSupplier? Original { get; init; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _taxId = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private bool _isActive = true;

    public bool IsNew => Original is null;

    public static SupplierEditor For(LocalSupplier s) => new()
    {
        Original = s, Name = s.Name, Phone = s.Phone ?? "", Email = s.Email ?? "", Address = s.Address ?? "", TaxId = s.TaxId ?? "", Notes = s.Notes ?? "", IsActive = s.IsActive,
    };

    public SupplierInput ToInput()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidOperationException("erp.err.nameRequired");
        if (!string.IsNullOrWhiteSpace(Email) && !Email.Contains('@')) throw new InvalidOperationException("erp.err.email");
        return new SupplierInput(Name, Phone, Email, Address, TaxId, Notes, IsActive);
    }
}

/// <summary>Suppliers: searchable list, create/edit form, account statement and payments (cash/bank).</summary>
public sealed partial class SuppliersViewModel(DesktopServices s) : ErpPageViewModel(s)
{
    public override string TitleKey => "erp.nav.suppliers";

    public ObservableCollection<LocalSupplier> Items { get; } = [];
    public ObservableCollection<PartyStatementRow> Statement { get; } = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _activeOnly;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelection))] private LocalSupplier? _selected;
    [ObservableProperty] private SupplierEditor? _editor;
    [ObservableProperty] private string _payAmount = "";
    [ObservableProperty] private string _payMethod = PayMethods.Cash;
    [ObservableProperty] private string _payNotes = "";
    [ObservableProperty] private DateTime? _payDate;

    public bool HasSelection => Selected is not null;
    public decimal TotalOwed => Items.Where(i => i.Balance > 0).Sum(i => i.Balance);

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        var list = await Erp.SuppliersAsync(Search, ActiveOnly, ct);
        var keep = Selected?.Id;
        Fill(Items, list);
        OnPropertyChanged(nameof(TotalOwed));
        if (keep is { } id) Selected = Items.FirstOrDefault(i => i.Id == id);
        return Items.Count > 0;
    }

    partial void OnActiveOnlyChanged(bool value) => _ = ReloadAsync();

    partial void OnSelectedChanged(LocalSupplier? value)
    {
        FormError = null;
        Editor = value is null ? null : SupplierEditor.For(value);
        PayAmount = "";
        PayDate = TodayDateTime;
        _ = LoadStatementAsync(value?.Id);
    }

    private async Task LoadStatementAsync(Guid? id)
    {
        Statement.Clear();
        if (id is null) return;
        try
        {
            var rows = await Erp.SupplierStatementAsync(id.Value);
            if (Selected?.Id == id) Fill(Statement, rows.Reverse());
        }
        catch (Exception e) { S.Log.Warn("Supplier statement failed", e); }
    }

    [RelayCommand]
    public Task SearchNowAsync() => ReloadAsync();

    [RelayCommand]
    public void New()
    {
        Selected = null;
        FormError = null;
        Editor = new SupplierEditor();
    }

    [RelayCommand]
    public Task SaveAsync() => SubmitAsync(async () =>
    {
        if (Editor is null) return;
        var saved = await Erp.UpsertSupplierAsync(Editor.Original?.Id, Editor.ToInput());
        await ReloadAsync();
        Selected = Items.FirstOrDefault(i => i.Id == saved.Id);
    });

    [RelayCommand]
    public Task PayAsync() => SubmitAsync(async () =>
    {
        if (Selected is not { } sup) return;
        await Erp.PaySupplierAsync(sup.Id, ParseAmount(PayAmount), PayMethod, ToDate(PayDate, Erp.Today), PayNotes);
        PayAmount = "";
        PayNotes = "";
        await ReloadAsync();
    }, "erp.msg.paymentRecorded");
}
