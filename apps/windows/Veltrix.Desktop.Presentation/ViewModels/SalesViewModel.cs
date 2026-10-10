using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Veltrix.Contracts;
using Veltrix.Contracts.Pos;
using Veltrix.Contracts.Sales;
using Veltrix.Desktop.Core.Api;
using Veltrix.Desktop.Core.Data;

namespace Veltrix.Desktop.Presentation.ViewModels;

public sealed record OfflineSaleRow(string Number, DateTimeOffset CreatedAt, decimal Total, string Status);

/// <summary>Sales documents (quotations, orders, invoices, credit notes) and POS receipts with reprint.</summary>
public sealed partial class SalesViewModel(DesktopServices s) : PageViewModel(s)
{
    public static IReadOnlyList<string> DocumentTypes { get; } = ["Invoice", "Order", "Quotation", "CreditNote"];

    public override string TitleKey => "nav.sales";

    public ObservableCollection<SalesDocumentDto> Documents { get; } = [];
    public ObservableCollection<SaleDto> Receipts { get; } = [];
    public ObservableCollection<OfflineSaleRow> OfflineSales { get; } = [];

    [ObservableProperty] private int _tabIndex;
    [ObservableProperty] private string _documentType = "Invoice";
    [ObservableProperty] private bool _outstandingOnly;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private SalesDocumentDto? _selectedDocument;
    [ObservableProperty] private SalesDocumentDto? _documentDetail;
    [ObservableProperty] private SaleDto? _selectedReceipt;

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        await LoadOfflineAsync(ct);
        if (TabIndex == 0)
        {
            var r = await S.Api.GetAsync<PagedResponse<SalesDocumentDto>>("/api/v1/sales/documents" +
                ApiClient.Query(("type", DocumentType), ("search", Search.Trim()), ("outstanding", OutstandingOnly ? true : null), ("pageSize", 100)), ct);
            Documents.Clear();
            foreach (var d in r.Items) Documents.Add(d);
            return Documents.Count > 0;
        }
        var from = S.Time.GetUtcNow().AddDays(-30);
        var p = await S.Api.GetAsync<PagedResponse<SaleDto>>("/api/v1/pos/sales" + ApiClient.Query(("search", Search.Trim()), ("from", from), ("pageSize", 100)), ct);
        Receipts.Clear();
        foreach (var x in p.Items) Receipts.Add(x);
        return Receipts.Count > 0 || OfflineSales.Count > 0;
    }

    private async Task LoadOfflineAsync(CancellationToken ct)
    {
        await using var db = S.Db();
        var pending = await db.Sales.Where(x => x.SyncStatus != OutboxStatus.Done).ToListAsync(ct);
        OfflineSales.Clear();
        foreach (var x in pending.OrderByDescending(x => x.CreatedAt)) OfflineSales.Add(new OfflineSaleRow(x.ClientNumber, x.CreatedAt, x.Total, x.SyncStatus));
    }

    partial void OnTabIndexChanged(int value) => _ = ReloadAsync();
    partial void OnDocumentTypeChanged(string value) => _ = ReloadAsync();
    partial void OnOutstandingOnlyChanged(bool value) => _ = ReloadAsync();

    [RelayCommand]
    public Task SearchNowAsync() => ReloadAsync();

    partial void OnSelectedDocumentChanged(SalesDocumentDto? value)
    {
        DocumentDetail = null;
        if (value is not null) _ = LoadDetailAsync(value.Id);
    }

    private async Task LoadDetailAsync(Guid id)
    {
        try
        {
            var d = await S.Api.GetAsync<SalesDocumentDto>($"/api/v1/sales/documents/{id}");
            if (SelectedDocument?.Id == id) DocumentDetail = d;
        }
        catch (Exception e) { StatusMessage = Describe(e); }
    }

    [RelayCommand]
    public Task ReprintAsync() => TryActionAsync(async () =>
    {
        if (SelectedReceipt is null) return;
        var width = Core.Pos.ReceiptText.CharsFor(S.Settings.ReceiptPaperMm);
        var r = await S.Api.GetAsync<ReceiptDto>($"/api/v1/pos/sales/{SelectedReceipt.Id}/receipt" + ApiClient.Query(("width", width), ("reprint", true)));
        S.Ui.PrintText(r.Number, r.Content, width);
    });
}
