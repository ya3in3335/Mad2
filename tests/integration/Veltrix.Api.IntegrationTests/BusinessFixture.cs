using Veltrix.Contracts.Accounting;
using Veltrix.Contracts.Catalog;
using Veltrix.Contracts.Inventory;
using Veltrix.Contracts.Pos;
using Veltrix.SharedKernel;

namespace Veltrix.Api.IntegrationTests;

public static class Biz
{
    public static async Task<TaxCategoryDto> Tva19Async(HttpClient c) =>
        (await c.GetAsync<List<TaxCategoryDto>>("/api/v1/catalog/tax-categories")).Single(t => t.Code == "TVA19");

    public static async Task<ProductDto> ProductAsync(HttpClient c, string sku, decimal price, Guid? taxId, string type = "Physical",
        IReadOnlyList<BundleComponentDto>? components = null, bool trackSerial = false, decimal lowStock = 0) =>
        await c.PostAsync<ProductDto>("/api/v1/catalog/products", new UpsertProductRequest(sku, "Product " + sku, "منتج " + sku, null, type, "Active", null, null, null,
            taxId, price, null, price / 2, type == "Physical", trackSerial, false, false, lowStock, 0, null, null, [sku + "-EAN"], components));

    public static async Task OpeningStockAsync(HttpClient c, Guid warehouseId, Guid productId, decimal qty, decimal unitCost, IReadOnlyList<string>? serials = null) =>
        await c.PostAsync<AdjustmentDto>("/api/v1/inventory/adjustments", new CreateAdjustmentRequest(warehouseId, "Opening", null,
            [new AdjustmentLineInput(productId, qty, unitCost, null, null, serials)]));

    public static async Task<decimal> StockAsync(HttpClient c, Guid warehouseId, Guid productId)
    {
        var page = await c.GetAsync<PagedResult<StockBalanceDto>>($"/api/v1/inventory/balances?warehouseId={warehouseId}&productId={productId}");
        return page.Items.SingleOrDefault()?.Quantity ?? 0;
    }

    public static async Task<(RegisterDto Register, ShiftDto Shift)> OpenRegisterAsync(HttpClient c, Guid branchId, Guid warehouseId, decimal float_ = 5000)
    {
        var reg = await c.PostAsync<RegisterDto>("/api/v1/pos/registers", new UpsertRegisterRequest(branchId, warehouseId, "R" + Random.Shared.Next(1000, 9999), "Caisse 1"));
        var shift = await c.PostAsync<ShiftDto>("/api/v1/pos/shifts", new OpenShiftRequest(reg.Id, float_));
        return (reg, shift);
    }

    public static CompleteSaleRequest Sale(RegisterDto reg, ShiftDto shift, IReadOnlyList<SaleLineInput> lines, IReadOnlyList<PaymentInput> payments,
        Guid? customerId = null, string? key = null, decimal cartDiscount = 0) =>
        new(key ?? Guid.NewGuid().ToString("N"), reg.Id, shift.Id, customerId, lines, payments, cartDiscount);

    public static async Task AssertBooksBalancedAsync(HttpClient c)
    {
        var tb = await c.GetAsync<TrialBalanceDto>("/api/v1/accounting/reports/trial-balance?from=2000-01-01&to=2100-12-31");
        Assert.True(tb.Balanced, $"Trial balance not balanced: D={tb.TotalDebit} C={tb.TotalCredit}");
        var bs = await c.GetAsync<BalanceSheetDto>("/api/v1/accounting/reports/balance-sheet?asOf=2100-12-31");
        Assert.True(bs.Balanced, $"Balance sheet not balanced: A={bs.TotalAssets} L={bs.TotalLiabilities} E={bs.TotalEquity}");
    }

    public static async Task<decimal> AccountBalanceAsync(HttpClient c, string code)
    {
        var tb = await c.GetAsync<TrialBalanceDto>("/api/v1/accounting/reports/trial-balance?from=2000-01-01&to=2100-12-31");
        return tb.Rows.SingleOrDefault(r => r.Code == code)?.Balance ?? 0;
    }
}
