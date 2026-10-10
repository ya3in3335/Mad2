using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Contracts;
using Veltrix.Contracts.Crm;
using Veltrix.Contracts.Sync;
using Veltrix.Desktop.Core.Api;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Sync;

namespace Veltrix.Desktop.Presentation.ViewModels;

public sealed partial class CustomerEditor : ObservableObject
{
    public CustomerDto? Original { get; init; }
    [ObservableProperty] private string _type = "Individual";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _city = "";
    [ObservableProperty] private string _wilaya = "";
    [ObservableProperty] private string _taxId = "";
    [ObservableProperty] private string _creditLimit = "0";
    [ObservableProperty] private bool _marketingConsent;
    [ObservableProperty] private string _notes = "";

    public bool IsNew => Original is null;
    public static IReadOnlyList<string> Types { get; } = ["Individual", "Business"];

    public static CustomerEditor For(CustomerDto c) => new()
    {
        Original = c, Type = c.Type, Name = c.Name, Phone = c.Phone ?? "", Email = c.Email ?? "", Address = c.Address ?? "", City = c.City ?? "",
        Wilaya = c.Wilaya ?? "", TaxId = c.TaxId ?? "", CreditLimit = c.CreditLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
        MarketingConsent = c.MarketingConsent, Notes = c.Notes ?? "",
    };

    public UpsertCustomerRequest ToRequest(Guid? id)
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidOperationException("Name");
        static string? O(string v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        var o = Original;
        return new UpsertCustomerRequest(Type, Name.Trim(), O(Phone), O(Email), O(Address), O(City), O(Wilaya), O(TaxId), o?.TradeRegisterNo, o?.GroupId,
            o?.PriceListId, Parse.DecimalOr(CreditLimit, 0), MarketingConsent, O(Notes), o?.Code, id);
    }
}

public sealed partial class CustomersViewModel(DesktopServices s) : ListPageViewModel<CustomerDto>(s)
{
    public override string TitleKey => "nav.customers";

    [ObservableProperty] private CustomerDto? _selected;
    [ObservableProperty] private CustomerEditor? _editor;
    [ObservableProperty] private IReadOnlyList<CustomerPurchaseDto> _purchases = [];

    public bool CanManage => Can("customers.manage");

    protected override Task<PagedResponse<CustomerDto>> FetchAsync(int page, string search, CancellationToken ct) =>
        S.Api.GetAsync<PagedResponse<CustomerDto>>("/api/v1/customers" + ApiClient.Query(("page", page), ("pageSize", PageSize), ("search", search)), ct);

    partial void OnSelectedChanged(CustomerDto? value)
    {
        Editor = value is null ? null : CustomerEditor.For(value);
        Purchases = [];
        if (value is not null) _ = LoadPurchasesAsync(value.Id);
    }

    private async Task LoadPurchasesAsync(Guid id)
    {
        try
        {
            var list = await S.Api.GetAsync<List<CustomerPurchaseDto>>($"/api/v1/customers/{id}/purchases");
            if (Selected?.Id == id) Purchases = list;
        }
        catch (Exception e) { S.Log.Warn("Customer purchases failed", e); }
    }

    [RelayCommand]
    public void New()
    {
        Selected = null;
        Editor = new CustomerEditor();
    }

    [RelayCommand]
    public Task SaveAsync() => TryActionAsync(async () =>
    {
        if (Editor is null) return;
        var id = Editor.Original?.Id;
        var req = Editor.ToRequest(id);
        try
        {
            var saved = id is null ? await S.Api.PostAsync<CustomerDto>("/api/v1/customers", req) : await S.Api.PutAsync<CustomerDto>($"/api/v1/customers/{id}", req);
            var idx = Items.ToList().FindIndex(c => c.Id == saved.Id);
            if (idx >= 0) Items[idx] = saved; else Items.Insert(0, saved);
            Selected = saved;
            if (State == LoadState.Empty) State = LoadState.Ready;
            StatusMessage = Loc.Get("dk.saved");
        }
        catch (ApiOfflineException)
        {
            // Queue the change; the server detects conflicting edits through the base version.
            var newId = id ?? Guid.NewGuid();
            await using var db = S.Db();
            Outbox.Enqueue(db, SyncOperationTypes.CustomerUpsert, req with { Id = newId }, S.Time.GetUtcNow(), Editor.Original?.UpdatedAt);
            var local = await db.Customers.FindAsync(newId);
            if (local is null) db.Customers.Add(local = new LocalCustomer { Id = newId });
            local.Name = req.Name; local.Type = req.Type; local.Phone = req.Phone; local.Email = req.Email; local.Address = req.Address; local.City = req.City;
            local.CreditLimit = req.CreditLimit; local.PendingSync = true; local.ServerVersion = Editor.Original?.UpdatedAt;
            await db.SaveChangesAsync();
            StatusMessage = Loc.Get("pos.savedOffline");
        }
    });
}
