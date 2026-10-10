using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Veltrix.Contracts.Crm;
using Veltrix.Contracts.Platform;
using Veltrix.Contracts.Pos;
using Veltrix.Contracts.Subscriptions;
using Veltrix.Contracts.Sync;
using Veltrix.Contracts.Tenants;
using Veltrix.SharedKernel;

namespace Veltrix.Api.IntegrationTests;

[Collection("api")]
public sealed class PlatformTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o, Json.Options);

    [Fact]
    public async Task Offline_sync_push_is_idempotent_and_reports_conflicts()
    {
        var t = await Scenario.CreateTenantAsync(f);
        var p = await Biz.ProductAsync(t.Owner, "SYNC-1", 100m, null);
        await Biz.OpeningStockAsync(t.Owner, t.WarehouseId, p.Id, 5, 50m);
        var reg = await t.Owner.PostAsync<RegisterDto>("/api/v1/pos/registers", new UpsertRegisterRequest(t.BranchId, t.WarehouseId, "OFF1", "Offline"));
        var shiftId = Guid.NewGuid();
        var saleOp = new SyncOperation("op-sale-000001", SyncOperationTypes.Sale, J(new CompleteSaleRequest("op-sale-000001", reg.Id, shiftId, null,
            [new SaleLineInput(p.Id, 7, null)], [new PaymentInput("Cash", 700m)], Offline: true)), DateTimeOffset.UtcNow.AddMinutes(-30));
        var ops = new List<SyncOperation>
        {
            new("op-shift-00001", SyncOperationTypes.ShiftOpen, J(new OpenShiftRequest(reg.Id, 1000m, shiftId, DateTimeOffset.UtcNow.AddHours(-1))), DateTimeOffset.UtcNow.AddHours(-1)),
            saleOp,
            new("op-cust-000001", SyncOperationTypes.CustomerUpsert, J(new UpsertCustomerRequest("Individual", "Client hors-ligne", "0661000000", null, null, null, null, null, null, null, null, 0, false, null, null, Guid.NewGuid())), DateTimeOffset.UtcNow),
            new("op-bad-0000001", "pos.unknown", J(new { }), DateTimeOffset.UtcNow),
        };
        var res = await t.Owner.PostAsync<SyncPushResponse>("/api/v1/sync/push", new SyncPushRequest(ops));
        Assert.Equal(["applied", "applied", "applied", "rejected"], res.Results.Select(r => r.Status).ToArray());
        // Offline oversell (7 > 5) is accepted and flagged, stock goes negative explicitly rather than losing the sale.
        Assert.Equal(-2, await Biz.StockAsync(t.Owner, t.WarehouseId, p.Id));

        var replay = await t.Owner.PostAsync<SyncPushResponse>("/api/v1/sync/push", new SyncPushRequest([saleOp]));
        Assert.Equal("duplicate", replay.Results[0].Status);
        Assert.Equal(-2, await Biz.StockAsync(t.Owner, t.WarehouseId, p.Id));

        // Customer edited on server after the device's base version -> conflict, server wins.
        var custId = Guid.Parse(res.Results[2].EntityId!);
        var baseVersion = DateTimeOffset.UtcNow.AddMinutes(-5);
        await t.Owner.PutAsync<CustomerDto>($"/api/v1/customers/{custId}", new UpsertCustomerRequest("Individual", "Server edit", "0661000000", null, null, null, null, null, null, null, null, 0, false, null));
        var conflict = await t.Owner.PostAsync<SyncPushResponse>("/api/v1/sync/push", new SyncPushRequest([new SyncOperation("op-cust-000002", SyncOperationTypes.CustomerUpsert,
            J(new UpsertCustomerRequest("Individual", "Device edit", null, null, null, null, null, null, null, null, null, 0, false, null, null, custId)), DateTimeOffset.UtcNow, baseVersion)]));
        Assert.Equal("conflict", conflict.Results[0].Status);
        Assert.Equal("Server edit", (await t.Owner.GetAsync<CustomerDto>($"/api/v1/customers/{custId}")).Name);

        var pull = await t.Owner.GetAsync<SyncPullResponse>($"/api/v1/sync/pull?warehouseId={t.WarehouseId}");
        Assert.Contains(pull.Products, x => x.Id == p.Id);
        Assert.Contains(pull.StockBalances, b => b.ProductId == p.Id);
        var notes = await t.Owner.GetAsync<List<Veltrix.Contracts.Notifications.NotificationDto>>("/api/v1/notifications");
        Assert.DoesNotContain(notes, n => n.Type == "x");
        await Biz.AssertBooksBalancedAsync(t.Owner);

        // A session without a device cannot sync.
        var noDevice = f.CreateClient();
        noDevice.WithToken((await noDevice.PostAsync<Veltrix.Contracts.Identity.TokenResponse>("/api/v1/auth/login", new Veltrix.Contracts.Identity.LoginRequest(t.OwnerEmail, ApiFactory.StrongPassword))).AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await noDevice.GetAsync("/api/v1/sync/pull")).StatusCode);
    }

    [Fact]
    public async Task Subscription_lifecycle_with_manual_payment_and_step_up()
    {
        var admin = await Scenario.PlatformAdminAsync(f);
        var tenant = await admin.PostAsync<CreateTenantResponse>("/api/v1/platform/tenants", new CreateTenantRequest("Sub Co", null, null, null, null, null));
        var plans = await admin.GetAsync<List<PlanDto>>("/api/v1/platform/plans");
        Assert.Contains(plans, x => x.Code == "professional");
        var pro = plans.Single(x => x.Code == "professional");
        await admin.PutAsync<PlanDto>($"/api/v1/platform/plans/{pro.Id}", new UpsertPlanRequest("professional", "Professional", null, 9000m, 90000m, "DZD", pro.Features, 5, 25, 5, 14, 14, true, true));

        var sub = await admin.PostAsync<SubscriptionDto>("/api/v1/platform/subscriptions", new CreateSubscriptionRequest(tenant.TenantId, "professional", "Monthly", 3, false, null));
        Assert.Equal("PastDue", sub.Status);
        var invoices = await admin.GetAsync<PagedResult<BillingInvoiceDto>>($"/api/v1/platform/invoices?tenantId={tenant.TenantId}");
        var invoice = Assert.Single(invoices.Items);
        Assert.Equal(9000m, invoice.Total);

        var noStepUp = await admin.PostAsJsonAsync($"/api/v1/platform/invoices/{invoice.Id}/confirm-payment", new ConfirmPaymentRequest("bank_transfer", "VIR-778", null));
        Assert.Equal(HttpStatusCode.Forbidden, noStepUp.StatusCode);
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/platform/invoices/{invoice.Id}/confirm-payment") { Content = JsonContent.Create(new ConfirmPaymentRequest("bank_transfer", "VIR-778", null)) };
        req.Headers.Add("X-Confirm-Password", ApiFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(req)).StatusCode);
        sub = await admin.GetAsync<SubscriptionDto>($"/api/v1/platform/subscriptions/{sub.Id}");
        Assert.Equal("Active", sub.Status);

        sub = await admin.PostAsync<SubscriptionDto>($"/api/v1/platform/subscriptions/{sub.Id}/plan", new ChangePlanRequest("enterprise", null, true));
        Assert.Equal("enterprise", sub.PlanCode);
        sub = await admin.PostAsync<SubscriptionDto>($"/api/v1/platform/subscriptions/{sub.Id}/seats", new ChangeSeatsRequest(30));
        var lic = await admin.GetAsync<Veltrix.Contracts.Licensing.LicenseDto>($"/api/v1/platform/licenses/{sub.LicenseId}");
        Assert.Equal(30, lic.MaxDevices);
        var history = await admin.GetAsync<List<EntitlementChangeDto>>($"/api/v1/platform/subscriptions/{sub.Id}/history");
        Assert.True(history.Count >= 3);
    }

    [Fact]
    public async Task Control_center_endpoints_and_tenant_export()
    {
        var t = await Scenario.CreateTenantAsync(f);
        var admin = await Scenario.PlatformAdminAsync(f);
        var dash = await admin.GetAsync<PlatformDashboardDto>("/api/v1/platform/dashboard");
        Assert.True(dash.Tenants >= 1 && dash.ActiveDevices >= 1);
        var tenants = await admin.GetAsync<PagedResult<TenantSummaryDto>>("/api/v1/platform/tenants?pageSize=200");
        Assert.Contains(tenants.Items, x => x.Id == t.TenantId && x.ActiveDevices == 1);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/platform/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/platform/security-alerts")).StatusCode);
        var health = await admin.GetAsync<SystemHealthDto>("/api/v1/platform/health");
        Assert.True(health.DatabaseReachable);
        Assert.Empty(health.PendingMigrations);

        await admin.PostAsync<AppReleaseDto>("/api/v1/platform/releases", new UpsertReleaseRequest("windows", "0.2.0", "stable", "0.1.5", "Fixes", "https://example.com/setup.exe", null, true));
        var check = await f.CreateClient().GetAsync<UpdateCheckDto>("/api/v1/releases/check?platform=windows&currentVersion=0.1.0");
        Assert.True(check.UpdateAvailable);
        Assert.True(check.Mandatory);

        await admin.PostAsync<AnnouncementDto>("/api/v1/platform/announcements", new UpsertAnnouncementRequest("Maintenance", "Sunday 02:00", "Info", null, DateTimeOffset.UtcNow.AddMinutes(-1), null, true));
        Assert.Contains(await t.Owner.GetAsync<List<AnnouncementDto>>("/api/v1/announcements"), a => a.Title == "Maintenance");

        var sc = await t.Owner.PostAsync<SupportCaseDto>("/api/v1/support", new CreateSupportCaseRequest("Printer issue", "High", "Receipt printer not printing"));
        await admin.PostAsync<SupportCaseDto>($"/api/v1/platform/support/{sc.Id}/reply", new ReplySupportCaseRequest("Check the driver", "Pending"));
        var mine = await t.Owner.GetAsync<List<SupportCaseDto>>("/api/v1/support");
        Assert.Equal(2, mine.Single(x => x.Id == sc.Id).Messages.Count);

        var zip = await t.Owner.GetAsync("/api/v1/company/export");
        Assert.Equal(HttpStatusCode.OK, zip.StatusCode);
        using var archive = new System.IO.Compression.ZipArchive(await zip.Content.ReadAsStreamAsync());
        Assert.Contains(archive.Entries, e => e.FullName == "tenants/branches.jsonl");
        Assert.Contains(archive.Entries, e => e.FullName == "accounting/accounts.jsonl");
        var users = new StreamReader(archive.GetEntry("identity/users.json")!.Open()).ReadToEnd();
        Assert.DoesNotContain("PasswordHash", users);
    }
}
