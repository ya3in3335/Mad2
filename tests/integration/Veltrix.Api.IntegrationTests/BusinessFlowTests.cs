using System.Net;
using System.Net.Http.Json;
using Veltrix.Contracts.Accounting;
using Veltrix.Contracts.Crm;
using Veltrix.Contracts.Hr;
using Veltrix.Contracts.Identity;
using Veltrix.Contracts.Purchasing;
using Veltrix.Contracts.Reporting;
using Veltrix.Contracts.Sales;
using Veltrix.SharedKernel;

namespace Veltrix.Api.IntegrationTests;

[Collection("api")]
public sealed class BusinessFlowTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private static async Task<Guid> AccountIdAsync(HttpClient c, string code) =>
        (await c.GetAsync<List<AccountDto>>("/api/v1/accounting/accounts")).Single(a => a.Code == code).Id;

    [Fact]
    public async Task Purchase_to_pay_with_partial_receipt_price_variance_and_return()
    {
        var t = await Scenario.CreateTenantAsync(f);
        var tva = await Biz.Tva19Async(t.Owner);
        var p = await Biz.ProductAsync(t.Owner, "PUR-1", 300m, tva.Id);
        var sup = await t.Owner.PostAsync<SupplierDto>("/api/v1/purchasing/suppliers", new UpsertSupplierRequest("Fournisseur Alger", "021000000", null, null, "000016000000001", null, 30, true, null));
        var po = await t.Owner.PostAsync<PurchaseOrderDto>("/api/v1/purchasing/orders",
            new CreatePurchaseOrderRequest(sup.Id, t.WarehouseId, null, null, [new DocLineInput(p.Id, null, 10, 100m, 19)]));
        Assert.Equal(1190m, po.Total);
        po = await t.Owner.PostAsync<PurchaseOrderDto>($"/api/v1/purchasing/orders/{po.Id}/submit", new { });
        Assert.Equal("Approved", po.Status);

        var gr = await t.Owner.PostAsync<GoodsReceiptDto>($"/api/v1/purchasing/orders/{po.Id}/receive", new ReceiveGoodsRequest([new ReceiptLineInput(po.Lines[0].Id, 6, null, null, null)], null));
        Assert.Equal(600m, gr.Value);
        Assert.Equal(6, await Biz.StockAsync(t.Owner, t.WarehouseId, p.Id));
        po = await t.Owner.GetAsync<PurchaseOrderDto>($"/api/v1/purchasing/orders/{po.Id}");
        Assert.Equal("PartiallyReceived", po.Status);

        // Supplier bills 6 units at 105 (price variance 30), VAT 19%.
        var inv = await t.Owner.PostAsync<SupplierInvoiceDto>("/api/v1/purchasing/invoices", new CreateSupplierInvoiceRequest(sup.Id, "F-2026-001", new DateOnly(2026, 10, 1), null, po.Id,
            [new DocLineInput(p.Id, null, 6, 105m, 19)]));
        Assert.Equal(749.70m, inv.Total);
        Assert.Equal(0m, await Biz.AccountBalanceAsync(t.Owner, "408"));
        Assert.Equal(30m, await Biz.AccountBalanceAsync(t.Owner, "6582"));
        Assert.Equal(749.70m, await Biz.AccountBalanceAsync(t.Owner, "401"));

        var dup = await t.Owner.PostAsJsonAsync("/api/v1/purchasing/invoices", new CreateSupplierInvoiceRequest(sup.Id, "F-2026-001", new DateOnly(2026, 10, 1), null, null,
            [new DocLineInput(null, "x", 1, 1m, 0)]), Json.Options);
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        var bank = await AccountIdAsync(t.Owner, "512");
        var pay = await t.Owner.PostAsync<PaymentResultDto>("/api/v1/purchasing/payments", new SupplierPaymentRequest(sup.Id, new DateOnly(2026, 10, 5), 749.70m, bank, "transfer", "VIR-1",
            [new AllocationInput(inv.Id, 749.70m)]));
        Assert.Equal(0m, pay.Unallocated);
        Assert.Equal(0m, await Biz.AccountBalanceAsync(t.Owner, "401"));
        inv = await t.Owner.GetAsync<SupplierInvoiceDto>($"/api/v1/purchasing/invoices/{inv.Id}");
        Assert.Equal("Paid", inv.Status);

        await t.Owner.PostAsync<PurchaseReturnDto>("/api/v1/purchasing/returns", new PurchaseReturnRequest(sup.Id, t.WarehouseId, new DateOnly(2026, 10, 6), "Damaged on arrival",
            [new DocLineInput(p.Id, null, 1, 100m, 19)]));
        Assert.Equal(5, await Biz.StockAsync(t.Owner, t.WarehouseId, p.Id));
        await Biz.AssertBooksBalancedAsync(t.Owner);
    }

    [Fact]
    public async Task Quote_to_cash_with_reservation_partial_delivery_invoice_payment_and_credit_note()
    {
        var t = await Scenario.CreateTenantAsync(f);
        var tva = await Biz.Tva19Async(t.Owner);
        var p = await Biz.ProductAsync(t.Owner, "SAL-1", 1000m, tva.Id);
        await Biz.OpeningStockAsync(t.Owner, t.WarehouseId, p.Id, 10, 400m);
        var cust = await t.Owner.PostAsync<CustomerDto>("/api/v1/customers", new UpsertCustomerRequest("Business", "EURL Client", null, null, null, null, null, null, null, null, null, 100000m, false, null));

        var quote = await t.Owner.PostAsync<SalesDocumentDto>("/api/v1/sales/documents", new CreateSalesDocumentRequest("Quotation", cust.Id, t.WarehouseId, null, null,
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)), null, null, [new SalesLineInput(p.Id, 4, null, 10)]));
        Assert.Equal(3600m, quote.Subtotal - quote.DiscountTotal);
        var order = await t.Owner.PostAsync<SalesDocumentDto>($"/api/v1/sales/quotations/{quote.Id}/convert", new { });
        order = await t.Owner.PostAsync<SalesDocumentDto>($"/api/v1/sales/orders/{order.Id}/submit", new { });
        Assert.Equal("Approved", order.Status);
        var bal = await t.Owner.GetAsync<PagedResult<Veltrix.Contracts.Inventory.StockBalanceDto>>($"/api/v1/inventory/balances?warehouseId={t.WarehouseId}&productId={p.Id}");
        Assert.Equal(6, bal.Items[0].Available);

        await t.Owner.PostAsync<DeliveryNoteDto>($"/api/v1/sales/orders/{order.Id}/deliver", new DeliverRequest([new DeliveryLineInput(order.Lines[0].Id, 3)], null));
        Assert.Equal(7, await Biz.StockAsync(t.Owner, t.WarehouseId, p.Id));
        var inv = await t.Owner.PostAsync<SalesDocumentDto>($"/api/v1/sales/orders/{order.Id}/invoice", new { });
        Assert.Equal(3, inv.Lines[0].Quantity);
        Assert.Equal(3213m, inv.Total); // 3 x 900 net = 2700 + 19% = 3213
        Assert.Equal(3213m, (await t.Owner.GetAsync<CustomerDto>($"/api/v1/customers/{cust.Id}")).Balance);

        var cash = await AccountIdAsync(t.Owner, "530");
        await t.Owner.PostAsync<PaymentResultDto>("/api/v1/sales/payments", new CustomerPaymentRequest(cust.Id, DateOnly.FromDateTime(DateTime.UtcNow), 2000m, cash, "cash", null,
            [new AllocationInput(inv.Id, 2000m)]));
        var cn = await t.Owner.PostAsync<SalesDocumentDto>($"/api/v1/sales/invoices/{inv.Id}/credit-note", new CreateCreditNoteRequest([new CreditLineInput(inv.Lines[0].Id, 1)], "Returned 1", true));
        Assert.Equal(1071m, cn.Total);
        Assert.Equal(8, await Biz.StockAsync(t.Owner, t.WarehouseId, p.Id));
        Assert.Equal(142m, (await t.Owner.GetAsync<CustomerDto>($"/api/v1/customers/{cust.Id}")).Balance);
        inv = await t.Owner.GetAsync<SalesDocumentDto>($"/api/v1/sales/documents/{inv.Id}");
        Assert.Equal(142m, inv.Outstanding);
        await Biz.AssertBooksBalancedAsync(t.Owner);

        // Finalized invoice numbers are unique and sequential per branch.
        var docs = await t.Owner.GetAsync<PagedResult<SalesDocumentDto>>("/api/v1/sales/documents?type=Invoice");
        Assert.Equal(docs.Items.Count, docs.Items.Select(d => d.Number).Distinct().Count());
    }

    [Fact]
    public async Task Payroll_from_configured_structure_posts_balanced_journal_and_hides_sensitive_fields()
    {
        var t = await Scenario.CreateTenantAsync(f);
        var structure = await t.Owner.PostAsync<SalaryStructureDto>("/api/v1/hr/salary-structures", new UpsertSalaryStructureRequest("Standard", "DZ (verify)",
            [new SalaryComponentDto("IEP", "Experience bonus", "Earning", "PercentOfBase", 5, true), new SalaryComponentDto("SS", "Social security (configured)", "Deduction", "PercentOfGross", 9, false)]));
        var emp = await t.Owner.PostAsync<EmployeeDto>("/api/v1/hr/employees", new UpsertEmployeeRequest("Karim B.", null, null, null, null, null, t.BranchId,
            new DateOnly(2025, 1, 1), "Active", "Permanent", null, null, structure.Id, null, "1234567890", new DateOnly(1990, 5, 5), 60000m, "RIB-123", null));
        Assert.Equal(60000m, emp.BaseSalary);
        await t.Owner.PostAsync<PayrollInputDto>("/api/v1/hr/payroll/inputs", new CreatePayrollInputRequest(emp.Id, 2026, 9, "PRIME", "Earning", 2000m, "Performance"));
        var run = await t.Owner.PostAsync<PayrollRunDto>("/api/v1/hr/payroll/runs/2026/9/calculate", new { });
        // Gross = 60000 + 3000 + 2000 = 65000; deduction 9% = 5850; net 59150.
        Assert.Equal(65000m, run.TotalGross);
        Assert.Equal(5850m, run.TotalDeductions);
        run = await t.Owner.PostAsync<PayrollRunDto>($"/api/v1/hr/payroll/runs/{run.Id}/post", new { });
        Assert.Equal("Posted", run.Status);
        Assert.Equal(59150m, await Biz.AccountBalanceAsync(t.Owner, "421"));
        await Biz.AssertBooksBalancedAsync(t.Owner);
        Assert.Equal(HttpStatusCode.Conflict, (await t.Owner.PostAsJsonAsync("/api/v1/hr/payroll/runs/2026/9/calculate", new { })).StatusCode);

        var email = $"bm.{Guid.NewGuid():N}@example.dz";
        await t.Owner.PostAsync<UserDto>("/api/v1/users", new CreateUserRequest(email, "Branch Manager", null, ApiFactory.StrongPassword, ["branch_manager"], [t.BranchId], null));
        var bm = f.CreateClient();
        bm.WithToken((await bm.PostAsync<TokenResponse>("/api/v1/auth/login", new LoginRequest(email, ApiFactory.StrongPassword, null, t.Device.DeviceId))).AccessToken);
        var seen = await bm.GetAsync<EmployeeDto>($"/api/v1/hr/employees/{emp.Id}");
        Assert.Null(seen.BaseSalary);
        Assert.Null(seen.NationalId);
        Assert.Equal(HttpStatusCode.Forbidden, (await bm.GetAsync("/api/v1/hr/payroll/runs")).StatusCode);

        var annual = (await t.Owner.GetAsync<List<LeaveTypeDto>>("/api/v1/hr/leave-types")).Single(l => l.Code == "SICK");
        var leave = await t.Owner.PostAsync<LeaveRequestDto>("/api/v1/hr/leave", new CreateLeaveRequest(emp.Id, annual.Id, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5), "Flu"));
        Assert.Equal("Pending", leave.Status);
        var decided = await bm.PostAsync<LeaveRequestDto>($"/api/v1/hr/leave/{leave.Id}/decide", new DecideRequest(true, "Get well"));
        Assert.Equal("Approved", decided.Status);
    }

    [Fact]
    public async Task Reports_export_in_all_formats_and_intelligence_runs_on_real_data()
    {
        var t = await Scenario.CreateTenantAsync(f);
        var p = await Biz.ProductAsync(t.Owner, "REP-1", 500m, null, lowStock: 2);
        await Biz.OpeningStockAsync(t.Owner, t.WarehouseId, p.Id, 20, 200m);
        var (reg, shift) = await Biz.OpenRegisterAsync(t.Owner, t.BranchId, t.WarehouseId);
        for (var i = 0; i < 3; i++)
            await t.Owner.PostAsync<Veltrix.Contracts.Pos.SaleDto>("/api/v1/pos/sales", Biz.Sale(reg, shift, [new Veltrix.Contracts.Pos.SaleLineInput(p.Id, 2, null)], [new Veltrix.Contracts.Pos.PaymentInput("Cash", 1000m)]));
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var table = await t.Owner.GetAsync<ReportTable>($"/api/v1/reports/run/sales-summary?from=2026-01-01&to={today}&groupBy=month");
        Assert.NotEmpty(table.Rows);
        var gp = await t.Owner.GetAsync<ReportTable>($"/api/v1/reports/run/gross-profit?from=2026-01-01&to={today}");
        Assert.Equal(1800m, ((System.Text.Json.JsonElement)gp.Summary["Gross profit"]!).GetDecimal()); // 3000 - 6*200

        foreach (var (format, magic) in new[] { ("xlsx", "PK"), ("pdf", "%PDF"), ("csv", "﻿") })
        {
            var r = await t.Owner.GetAsync($"/api/v1/reports/run/top-products?from=2026-01-01&to={today}&format={format}");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var bytes = await r.Content.ReadAsByteArrayAsync();
            Assert.StartsWith(magic, System.Text.Encoding.UTF8.GetString(bytes[..Math.Min(8, bytes.Length)]));
        }
        var dash = await t.Owner.GetAsync<DashboardDto>("/api/v1/reports/dashboard");
        Assert.Equal(3000m, dash.SalesToday);
        Assert.Equal(3, dash.TransactionsToday);
        var trend = await t.Owner.GetAsync<TrendDto>("/api/v1/intelligence/sales-trend?days=30");
        Assert.Equal(3000m, trend.CurrentPeriod);
        var forecast = await t.Owner.GetAsync<List<StockForecastDto>>("/api/v1/intelligence/stock-forecast");
        Assert.Contains(forecast, x => x.ProductId == p.Id && x.AverageDailySales > 0);
        var summary = await t.Owner.GetAsync<BusinessSummaryDto>("/api/v1/intelligence/summary?lang=ar");
        Assert.NotEmpty(summary.Highlights);
    }
}
