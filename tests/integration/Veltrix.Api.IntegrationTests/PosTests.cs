using System.Net;
using System.Net.Http.Json;
using Veltrix.Contracts.Accounting;
using Veltrix.Contracts.Catalog;
using Veltrix.Contracts.Crm;
using Veltrix.Contracts.Identity;
using Veltrix.Contracts.Pos;

namespace Veltrix.Api.IntegrationTests;

[Collection("api")]
public sealed class PosTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Cash_sale_updates_stock_and_posts_balanced_entries()
    {
        var t = await Scenario.CreateTenantAsync(f);
        var tva = await Biz.Tva19Async(t.Owner);
        var p = await Biz.ProductAsync(t.Owner, "SKU-1", 1190m, tva.Id);
        await Biz.OpeningStockAsync(t.Owner, t.WarehouseId, p.Id, 10, 600m);
        Assert.Equal(10, await Biz.StockAsync(t.Owner, t.WarehouseId, p.Id));

        var (reg, shift) = await Biz.OpenRegisterAsync(t.Owner, t.BranchId, t.WarehouseId);
        var sale = await t.Owner.PostAsync<SaleDto>("/api/v1/pos/sales", Biz.Sale(reg, shift, [new SaleLineInput(p.Id, 2, null)], [new PaymentInput("Cash", 3000m)]));

        Assert.Equal(2380m, sale.Total);
        Assert.Equal(380m, sale.TaxTotal);
        Assert.Equal(620m, sale.Change);
        Assert.Equal(8, await Biz.StockAsync(t.Owner, t.WarehouseId, p.Id));

        // Revenue 2000 net, VAT 380, COGS 1200, inventory 6000 - 1200 = 4800, cash +2380.
        Assert.Equal(2000m, await Biz.AccountBalanceAsync(t.Owner, "700"));
        Assert.Equal(380m, await Biz.AccountBalanceAsync(t.Owner, "4457"));
        Assert.Equal(1200m, await Biz.AccountBalanceAsync(t.Owner, "600"));
        Assert.Equal(4800m, await Biz.AccountBalanceAsync(t.Owner, "30"));
        Assert.Equal(2380m, await Biz.AccountBalanceAsync(t.Owner, "530"));
        await Biz.AssertBooksBalancedAsync(t.Owner);

        var receipt = await t.Owner.GetAsync<ReceiptDto>($"/api/v1/pos/sales/{sale.Id}/receipt?width=42");
        Assert.Contains(sale.Number, receipt.Content);
        Assert.Contains("TOTAL TTC", receipt.Content);
    }

    [Fact]
    public async Task Replayed_sale_with_same_idempotency_key_is_not_duplicated()
    {
        var t = await Scenario.CreateTenantAsync(f);
        var p = await Biz.ProductAsync(t.Owner, "IDEM", 100m, null);
        await Biz.OpeningStockAsync(t.Owner, t.WarehouseId, p.Id, 5, 50m);
        var (reg, shift) = await Biz.OpenRegisterAsync(t.Owner, t.BranchId, t.WarehouseId);
        var req = Biz.Sale(reg, shift, [new SaleLineInput(p.Id, 1, null)], [new PaymentInput("Cash", 100m)], key: "offline-device-1-sale-0001");
        var first = await t.Owner.PostAsync<SaleDto>("/api/v1/pos/sales", req);
        var second = await t.Owner.PostAsync<SaleDto>("/api/v1/pos/sales", req);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => t.Owner.PostAsJsonAsync("/api/v1/pos/sales", req, Json.Options)));
        Assert.Equal(first.Id, second.Id);
        Assert.All(concurrent, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(4, await Biz.StockAsync(t.Owner, t.WarehouseId, p.Id));
        await Biz.AssertBooksBalancedAsync(t.Owner);
    }

    [Fact]
    public async Task Concurrent_sales_cannot_oversell_the_last_unit()
    {
        var t = await Scenario.CreateTenantAsync(f);
        var p = await Biz.ProductAsync(t.Owner, "LAST", 100m, null);
        await Biz.OpeningStockAsync(t.Owner, t.WarehouseId, p.Id, 1, 50m);
        var (reg, shift) = await Biz.OpenRegisterAsync(t.Owner, t.BranchId, t.WarehouseId);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            t.Owner.PostAsJsonAsync("/api/v1/pos/sales", Biz.Sale(reg, shift, [new SaleLineInput(p.Id, 1, null)], [new PaymentInput("Cash", 100m)]), Json.Options)));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(0, await Biz.StockAsync(t.Owner, t.WarehouseId, p.Id));
    }

    [Fact]
    public async Task Return_restocks_and_reverses_revenue_and_void_reverses_everything()
    {
        var t = await Scenario.CreateTenantAsync(f);
        var tva = await Biz.Tva19Async(t.Owner);
        var p = await Biz.ProductAsync(t.Owner, "RET", 119m, tva.Id);
        await Biz.OpeningStockAsync(t.Owner, t.WarehouseId, p.Id, 10, 40m);
        var (reg, shift) = await Biz.OpenRegisterAsync(t.Owner, t.BranchId, t.WarehouseId);
        var sale = await t.Owner.PostAsync<SaleDto>("/api/v1/pos/sales", Biz.Sale(reg, shift, [new SaleLineInput(p.Id, 3, null)], [new PaymentInput("Card", 357m)]));
        var ret = await t.Owner.PostAsync<SaleDto>($"/api/v1/pos/sales/{sale.Id}/return",
            new ReturnSaleRequest(Guid.NewGuid().ToString("N"), reg.Id, shift.Id, [new ReturnLineInput(sale.Lines[0].Id, 1)], "Cash", "Defective? no, changed mind"));
        Assert.Equal(-119m, ret.Total);
        Assert.Equal(8, await Biz.StockAsync(t.Owner, t.WarehouseId, p.Id));

        // Cannot return more than remaining.
        var over = await t.Owner.PostAsJsonAsync($"/api/v1/pos/sales/{sale.Id}/return",
            new ReturnSaleRequest(Guid.NewGuid().ToString("N"), reg.Id, shift.Id, [new ReturnLineInput(sale.Lines[0].Id, 3)], "Cash", "x"), Json.Options);
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);

        var sale2 = await t.Owner.PostAsync<SaleDto>("/api/v1/pos/sales", Biz.Sale(reg, shift, [new SaleLineInput(p.Id, 2, null)], [new PaymentInput("Cash", 238m)]));
        await t.Owner.PostAsync<SaleDto>($"/api/v1/pos/sales/{sale2.Id}/void", new VoidSaleRequest("Wrong item scanned"));
        Assert.Equal(8, await Biz.StockAsync(t.Owner, t.WarehouseId, p.Id));
        var voided = await t.Owner.GetAsync<SaleDto>($"/api/v1/pos/sales/{sale2.Id}");
        Assert.Equal("Voided", voided.Status);

        // Revenue net = 3*100 - 1*100 (sales returns) ; voided sale contributes 0.
        Assert.Equal(300m, await Biz.AccountBalanceAsync(t.Owner, "700"));
        Assert.Equal(-100m, await Biz.AccountBalanceAsync(t.Owner, "709"));
        await Biz.AssertBooksBalancedAsync(t.Owner);

        // Close shift with short cash: expected = 5000 float - 119 refund (cash) = 4881.
        var report = await t.Owner.PostAsync<ShiftReportDto>($"/api/v1/pos/shifts/{shift.Id}/close", new CloseShiftRequest(4870m, "short 11"));
        Assert.Equal(4881m, report.ExpectedCash);
        Assert.Equal(-11m, report.Shift.Variance);
        Assert.Equal(11m, await Biz.AccountBalanceAsync(t.Owner, "6583"));
        await Biz.AssertBooksBalancedAsync(t.Owner);
    }

    [Fact]
    public async Task Credit_sale_respects_customer_credit_limit()
    {
        var t = await Scenario.CreateTenantAsync(f);
        var p = await Biz.ProductAsync(t.Owner, "CRED", 1000m, null);
        await Biz.OpeningStockAsync(t.Owner, t.WarehouseId, p.Id, 10, 500m);
        var (reg, shift) = await Biz.OpenRegisterAsync(t.Owner, t.BranchId, t.WarehouseId);
        var cust = await t.Owner.PostAsync<CustomerDto>("/api/v1/customers", new UpsertCustomerRequest("Business", "SARL Client", "0550000000", null, null, null, "Oran", null, null, null, null, 1500m, false, null));

        var noCustomer = await t.Owner.PostAsJsonAsync("/api/v1/pos/sales", Biz.Sale(reg, shift, [new SaleLineInput(p.Id, 1, null)], [new PaymentInput("OnAccount", 1000m)]), Json.Options);
        Assert.Equal(HttpStatusCode.BadRequest, noCustomer.StatusCode);

        await t.Owner.PostAsync<SaleDto>("/api/v1/pos/sales", Biz.Sale(reg, shift, [new SaleLineInput(p.Id, 1, null)], [new PaymentInput("OnAccount", 1000m)], cust.Id));
        var overLimit = await t.Owner.PostAsJsonAsync("/api/v1/pos/sales", Biz.Sale(reg, shift, [new SaleLineInput(p.Id, 1, null)], [new PaymentInput("OnAccount", 1000m)], cust.Id), Json.Options);
        Assert.Equal(HttpStatusCode.Conflict, overLimit.StatusCode);
        Assert.Equal("customer.credit_limit", await overLimit.ProblemCodeAsync());

        var c = await t.Owner.GetAsync<CustomerDto>($"/api/v1/customers/{cust.Id}");
        Assert.Equal(1000m, c.Balance);
        Assert.Equal(10m, c.LoyaltyPoints);
        var aging = await t.Owner.GetAsync<List<AgingRow>>("/api/v1/accounting/reports/aging/customer");
        Assert.Equal(1000m, aging.Single(a => a.PartyId == cust.Id).Current);
    }

    [Fact]
    public async Task Bundle_sale_issues_components_and_service_has_no_stock()
    {
        var t = await Scenario.CreateTenantAsync(f);
        var a = await Biz.ProductAsync(t.Owner, "COMP-A", 100m, null);
        var b = await Biz.ProductAsync(t.Owner, "COMP-B", 50m, null);
        var bundle = await Biz.ProductAsync(t.Owner, "BUNDLE", 120m, null, "Bundle", [new BundleComponentDto(a.Id, 1), new BundleComponentDto(b.Id, 2)]);
        var svc = await Biz.ProductAsync(t.Owner, "SVC", 500m, null, "Service");
        await Biz.OpeningStockAsync(t.Owner, t.WarehouseId, a.Id, 5, 60m);
        await Biz.OpeningStockAsync(t.Owner, t.WarehouseId, b.Id, 5, 20m);
        var (reg, shift) = await Biz.OpenRegisterAsync(t.Owner, t.BranchId, t.WarehouseId);
        await t.Owner.PostAsync<SaleDto>("/api/v1/pos/sales", Biz.Sale(reg, shift, [new SaleLineInput(bundle.Id, 2, null), new SaleLineInput(svc.Id, 1, null)], [new PaymentInput("Cash", 740m)]));
        Assert.Equal(3, await Biz.StockAsync(t.Owner, t.WarehouseId, a.Id));
        Assert.Equal(1, await Biz.StockAsync(t.Owner, t.WarehouseId, b.Id));
        Assert.Equal(240m, await Biz.AccountBalanceAsync(t.Owner, "700"));
        Assert.Equal(500m, await Biz.AccountBalanceAsync(t.Owner, "706"));
        Assert.Equal(2 * 60m + 4 * 20m, await Biz.AccountBalanceAsync(t.Owner, "600"));
        await Biz.AssertBooksBalancedAsync(t.Owner);
    }

    [Fact]
    public async Task Cashier_cannot_discount_or_void_and_sales_need_open_shift()
    {
        var t = await Scenario.CreateTenantAsync(f);
        var p = await Biz.ProductAsync(t.Owner, "DISC", 1000m, null);
        await Biz.OpeningStockAsync(t.Owner, t.WarehouseId, p.Id, 10, 500m);
        var (reg, shift) = await Biz.OpenRegisterAsync(t.Owner, t.BranchId, t.WarehouseId);
        var email = $"cash.{Guid.NewGuid():N}@example.dz";
        await t.Owner.PostAsync<UserDto>("/api/v1/users", new CreateUserRequest(email, "Caissier", null, ApiFactory.StrongPassword, ["cashier"], [t.BranchId], "ar"));
        var cashier = f.CreateClient();
        cashier.WithToken((await cashier.PostAsync<TokenResponse>("/api/v1/auth/login", new LoginRequest(email, ApiFactory.StrongPassword, null, t.Device.DeviceId))).AccessToken);

        var discounted = await cashier.PostAsJsonAsync("/api/v1/pos/sales", Biz.Sale(reg, shift, [new SaleLineInput(p.Id, 1, null, 10)], [new PaymentInput("Cash", 1000m)]), Json.Options);
        Assert.Equal(HttpStatusCode.Forbidden, discounted.StatusCode);
        var priceCut = await cashier.PostAsJsonAsync("/api/v1/pos/sales", Biz.Sale(reg, shift, [new SaleLineInput(p.Id, 1, 500m)], [new PaymentInput("Cash", 1000m)]), Json.Options);
        Assert.Equal(HttpStatusCode.Forbidden, priceCut.StatusCode);
        var ok = await cashier.PostAsync<SaleDto>("/api/v1/pos/sales", Biz.Sale(reg, shift, [new SaleLineInput(p.Id, 1, null)], [new PaymentInput("Cash", 1000m)]));
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync($"/api/v1/pos/sales/{ok.Id}/void", new VoidSaleRequest("x"))).StatusCode);

        // Owner discount generates an unusual-activity notification when above the threshold.
        await t.Owner.PostAsync<SaleDto>("/api/v1/pos/sales", Biz.Sale(reg, shift, [new SaleLineInput(p.Id, 1, null, 50)], [new PaymentInput("Cash", 500m)]));
        var notes = await t.Owner.GetAsync<List<Veltrix.Contracts.Notifications.NotificationDto>>("/api/v1/notifications");
        Assert.Contains(notes, n => n.Type == "pos.unusual_transaction");

        await t.Owner.PostAsync<ShiftReportDto>($"/api/v1/pos/shifts/{shift.Id}/close", new CloseShiftRequest(6500m, null));
        var closed = await t.Owner.PostAsJsonAsync("/api/v1/pos/sales", Biz.Sale(reg, shift, [new SaleLineInput(p.Id, 1, null)], [new PaymentInput("Cash", 1000m)]), Json.Options);
        Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);
    }

    [Fact]
    public async Task Low_stock_notification_and_insufficient_stock_rejection()
    {
        var t = await Scenario.CreateTenantAsync(f);
        var p = await Biz.ProductAsync(t.Owner, "LOW", 10m, null, lowStock: 3);
        await Biz.OpeningStockAsync(t.Owner, t.WarehouseId, p.Id, 5, 5m);
        var (reg, shift) = await Biz.OpenRegisterAsync(t.Owner, t.BranchId, t.WarehouseId);
        await t.Owner.PostAsync<SaleDto>("/api/v1/pos/sales", Biz.Sale(reg, shift, [new SaleLineInput(p.Id, 3, null)], [new PaymentInput("Cash", 30m)]));
        var notes = await t.Owner.GetAsync<List<Veltrix.Contracts.Notifications.NotificationDto>>("/api/v1/notifications");
        Assert.Contains(notes, n => n.Type == "inventory.low_stock");
        var low = await t.Owner.GetAsync<List<Veltrix.Contracts.Inventory.LowStockDto>>("/api/v1/inventory/low-stock");
        Assert.Contains(low, l => l.ProductId == p.Id);
        var tooMuch = await t.Owner.PostAsJsonAsync("/api/v1/pos/sales", Biz.Sale(reg, shift, [new SaleLineInput(p.Id, 5, null)], [new PaymentInput("Cash", 50m)]), Json.Options);
        Assert.Equal(HttpStatusCode.Conflict, tooMuch.StatusCode);
        Assert.Equal("inventory.insufficient_stock", await tooMuch.ProblemCodeAsync());
    }
}
