using System.Net;
using Veltrix.Contracts.Crm;
using Veltrix.Contracts.Operations;
using Veltrix.Contracts.Platform;
using Veltrix.Contracts.Subscriptions;
using Veltrix.Contracts.Tenants;
using Veltrix.SharedKernel;

namespace Veltrix.Api.IntegrationTests;

/// <summary>Own class (and therefore own database) so absolute dashboard figures can be asserted exactly.</summary>
[Collection("api")]
public sealed class OwnerDashboardTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Owner_dashboard_metrics_come_from_real_data()
    {
        var admin = await Scenario.PlatformAdminAsync(f);
        var empty = await admin.GetAsync<OwnerDashboardDto>("/api/v1/platform/dashboard/admin");
        Assert.Equal(0, empty.TotalTenants);
        Assert.Equal(0, empty.RegisteredDevices);
        Assert.Equal(0m, empty.MonthlyRecurringRevenue);

        // Tenant A: onboarded, annual license, one Windows device sending heartbeats, two CRM customers, one ticket, one critical error.
        var a = await Scenario.CreateTenantAsync(f, "Dash A");
        await a.Owner.PostAsync<CustomerDto>("/api/v1/customers", new UpsertCustomerRequest("Individual", "Client 1", "0661000001", null, null, null, null, null, null, null, null, 0, false, null));
        await a.Owner.PostAsync<CustomerDto>("/api/v1/customers", new UpsertCustomerRequest("Individual", "Client 2", "0661000002", null, null, null, null, null, null, null, null, 0, false, null));
        await a.Owner.PostAsync<DeviceHeartbeatResponse>("/api/v1/devices/heartbeat", StepUp.Heartbeat(a.Device, failed: 2));
        await a.Owner.PostAsync<SupportCaseDto>("/api/v1/support", new CreateSupportCaseRequest("Printer", "High", "Not printing"));
        await a.Owner.PostAsync<ErrorReportBatchResponse>("/api/v1/diagnostics/errors", new ErrorReportBatchRequest(
            [new ErrorReportItem("veltrix-pos", "0.2.0", "windows", null, "crash", "critical", "Fatal", "App crashed", null, null, null)]));

        // Tenant B: registered only (pending), paid monthly subscription 9000, then a renewal invoice awaiting manual payment.
        var b = await admin.PostAsync<CreateTenantResponse>("/api/v1/platform/tenants", new CreateTenantRequest("Dash B", null, null, null, null, null));
        var pro = (await admin.GetAsync<List<PlanDto>>("/api/v1/platform/plans")).Single(p => p.Code == "professional");
        await admin.PutAsync<PlanDto>($"/api/v1/platform/plans/{pro.Id}", new UpsertPlanRequest("professional", "Professional", null, 9000m, 90000m, "DZD", pro.Features, 5, 25, 5, 14, 14, true, true));
        var sub = await admin.PostAsync<SubscriptionDto>("/api/v1/platform/subscriptions", new CreateSubscriptionRequest(b.TenantId, "professional", "Monthly", 2, false, null));
        var inv = Assert.Single((await admin.GetAsync<PagedResult<BillingInvoiceDto>>($"/api/v1/platform/invoices?tenantId={b.TenantId}")).Items);
        Assert.Equal(HttpStatusCode.OK, (await StepUp.PostAsync(admin, $"/api/v1/platform/invoices/{inv.Id}/confirm-payment", new ConfirmPaymentRequest("bank_transfer", "VIR-1", null))).StatusCode);
        await admin.PostAsync<BillingInvoiceDto>($"/api/v1/platform/subscriptions/{sub.Id}/renewal-invoice", new IssueInvoiceRequest(null));
        await admin.PostAsync<AppReleaseDto>("/api/v1/platform/releases", new UpsertReleaseRequest("android", "0.3.0", "stable", "0.1.0", null, null, null, true));

        var d = await admin.GetAsync<OwnerDashboardDto>("/api/v1/platform/dashboard/admin");
        Assert.Equal(2, d.TotalTenants);
        Assert.Equal(1, d.ActiveTenants);
        Assert.Equal(0, d.SuspendedTenants);
        Assert.Equal(2, d.TotalCustomers);
        Assert.Equal(1, d.ActiveSubscriptions);
        Assert.Equal(0, d.PastDueSubscriptions);
        Assert.Equal(2, d.ActiveLicenses);
        Assert.Equal(0, d.SuspendedLicenses);
        Assert.Equal(1, d.RegisteredDevices);
        Assert.Equal(1, d.OnlineDevices);
        Assert.Equal(1, d.WindowsInstallations);
        Assert.Equal(0, d.AndroidInstallations);
        Assert.Equal(9000m, d.MonthlyRecurringRevenue);
        Assert.Equal(1, d.PendingManualPayments);
        Assert.Equal(9000m, d.PendingManualPaymentsAmount);
        Assert.Equal(1, d.NewSupportTickets);
        Assert.Equal(1, d.OpenSupportTickets);
        Assert.Equal(1, d.UnresolvedErrors);
        Assert.Equal(1, d.CriticalUnresolvedErrors);
        Assert.Equal(0, d.FailedSyncOperations24h);
        Assert.Equal(2, d.DeviceReportedFailedSyncOperations);
        Assert.Equal("healthy", d.BackendHealth);
        Assert.Equal("0.3.0", Assert.Single(d.LatestReleases).Version);

        // Suspending a tenant (existing step-up flow) is reflected; the original dashboard still works.
        Assert.Equal(HttpStatusCode.NoContent, (await StepUp.PostAsync(admin, $"/api/v1/platform/tenants/{a.TenantId}/suspend", new SuspendTenantRequest("non-payment"))).StatusCode);
        d = await admin.GetAsync<OwnerDashboardDto>("/api/v1/platform/dashboard/admin");
        Assert.Equal(1, d.SuspendedTenants);
        Assert.Equal(0, d.ActiveTenants);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/platform/dashboard")).StatusCode);

        // Alerts were recorded for the flows above (Telegram disabled in this host: kept as "skipped" history).
        var deliveries = await admin.GetAsync<PagedResult<Veltrix.Contracts.Telegram.TelegramDeliveryDto>>("/api/v1/platform/telegram/deliveries?pageSize=200");
        foreach (var type in new[] { AlertTypes.TenantRegistered, AlertTypes.DeviceActivated, AlertTypes.SupportTicket, AlertTypes.PaymentAwaiting, AlertTypes.ReleasePublished, AlertTypes.TenantOnboarded })
            Assert.Contains(deliveries.Items, x => x.Type == type && x.Status == "skipped");
        Assert.DoesNotContain(deliveries.Items, x => x.Text.Contains(a.OwnerEmail));
    }
}
