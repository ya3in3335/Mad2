using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Veltrix.Contracts.Identity;
using Veltrix.Contracts.Licensing;
using Veltrix.Contracts.Operations;
using Veltrix.Contracts.Tenants;
using Veltrix.SharedKernel;

namespace Veltrix.Api.IntegrationTests;

public static class StepUp
{
    public static async Task<HttpResponseMessage> PostAsync(HttpClient c, string url, object body, string? password = ApiFactory.AdminPassword)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: Json.Options) };
        if (password is not null) req.Headers.Add("X-Confirm-Password", password);
        return await c.SendAsync(req);
    }

    public static async Task<HttpClient> NoDeviceSessionAsync(ApiFactory f, string email)
    {
        var c = f.CreateClient();
        return c.WithToken((await c.PostAsync<TokenResponse>("/api/v1/auth/login", new LoginRequest(email, ApiFactory.StrongPassword))).AccessToken);
    }

    public static DeviceHeartbeatRequest Heartbeat(TestDevice d, int failed = 0) =>
        new(d.InstallationId, "windows", "0.2.0", "Windows 11 23H2", DateTimeOffset.UtcNow.AddMinutes(-1), 0, failed);
}

[Collection("api")]
public sealed class AdminOpsTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Heartbeat_records_telemetry_and_drives_online_status()
    {
        var t = await Scenario.CreateTenantAsync(f, "Heartbeat Co");
        var admin = await Scenario.PlatformAdminAsync(f);

        // A second activated device that never sends a heartbeat stays offline.
        var idle = new TestDevice();
        await Scenario.ActivateAsync(f.CreateClient(), idle, t.LicenseKey, "android");

        var hb = await t.Owner.PostAsync<DeviceHeartbeatResponse>("/api/v1/devices/heartbeat", StepUp.Heartbeat(t.Device));
        Assert.False(hb.DiagnosticsConsent);
        Assert.Empty(hb.DiagnosticRequests);
        Assert.True(hb.NextHeartbeatSeconds > 0);

        var online = await admin.GetAsync<PagedResult<AdminDeviceDto>>($"/api/v1/platform/fleet/devices?tenantId={t.TenantId}&online=true");
        var d = Assert.Single(online.Items);
        Assert.Equal(t.Device.DeviceId, d.Id);
        Assert.True(d.Online);
        Assert.Equal("Windows 11 23H2", d.OsVersion);
        Assert.Equal("0.2.0", d.AppVersion);
        Assert.NotNull(d.LastHeartbeatAt);
        Assert.NotNull(d.LastSyncAt);

        var offline = await admin.GetAsync<PagedResult<AdminDeviceDto>>($"/api/v1/platform/fleet/devices?tenantId={t.TenantId}&online=false");
        Assert.Equal(idle.DeviceId, Assert.Single(offline.Items).Id);
        var android = await admin.GetAsync<PagedResult<AdminDeviceDto>>($"/api/v1/platform/fleet/devices?tenantId={t.TenantId}&platform=android");
        Assert.Single(android.Items);

        // Device detail never exposes key material.
        var raw = await (await admin.GetAsync($"/api/v1/platform/fleet/devices/{t.Device.DeviceId}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(t.Device.PublicKey, raw);
        Assert.DoesNotContain("thumbprint", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(t.LicenseKey, raw);
        var detail = await admin.GetAsync<AdminDeviceDetailDto>($"/api/v1/platform/fleet/devices/{t.Device.DeviceId}");
        Assert.Equal("Heartbeat Co", detail.Device.TenantName);
        Assert.Equal(t.LicenseId, detail.Device.LicenseId);
        Assert.Contains(detail.History, e => e.Type == "device.activated");

        // Wrong installation id and sessions without a device are rejected.
        var wrong = await t.Owner.PostAsJsonAsync("/api/v1/devices/heartbeat", StepUp.Heartbeat(t.Device) with { InstallationId = Guid.NewGuid() }, Json.Options);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var noDevice = await StepUp.NoDeviceSessionAsync(f, t.OwnerEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await noDevice.PostAsJsonAsync("/api/v1/devices/heartbeat", StepUp.Heartbeat(t.Device), Json.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().PostAsJsonAsync("/api/v1/devices/heartbeat", StepUp.Heartbeat(t.Device), Json.Options)).StatusCode);

        // Deactivation via the fleet API requires step-up and reuses licensing revocation.
        Assert.Equal(HttpStatusCode.Forbidden, (await StepUp.PostAsync(admin, $"/api/v1/platform/fleet/devices/{idle.DeviceId}/deactivate", new DeviceActionRequest("lost"), null)).StatusCode);
        var deact = await (await StepUp.PostAsync(admin, $"/api/v1/platform/fleet/devices/{idle.DeviceId}/replace", new DeviceActionRequest("hardware swap"))).ReadAsync<AdminDeviceDetailDto>();
        Assert.Equal("Replaced", deact.Device.Status);
        Assert.Contains(deact.History, e => e.Type == "device.replaced");
    }

    [Fact]
    public async Task Error_reports_are_sanitized_grouped_and_isolated_from_tenants()
    {
        var t = await Scenario.CreateTenantAsync(f, "Crashy SARL");
        var t2 = await Scenario.CreateTenantAsync(f, "Other SARL");
        var admin = await Scenario.PlatformAdminAsync(f);
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";
        ErrorReportItem Crash(string msg) => new("veltrix-pos", "0.2.0", "windows", "Windows 11", "crash", "error", "NullReferenceException", msg,
            $"at Pos.Checkout() card=4111 1111 1111 1111 Authorization: Bearer {jwt}", new Dictionary<string, string> { ["screen"] = "checkout", ["password"] = "hunter2", ["note"] = "mail owner@shop.dz" },
            DateTimeOffset.UtcNow.AddMinutes(-2));

        var res = await t.Owner.PostAsync<ErrorReportBatchResponse>("/api/v1/diagnostics/errors", new ErrorReportBatchRequest(
        [
            Crash("Order 1234 failed for client.one@example.com with token=s3cr3tTokenValue123 card 4111-1111-1111-1111"),
            Crash("Order 98765 failed for second.user@example.org with token=anotherSecret987 card 5500 0000 0000 0004"),
            new("veltrix-pos", "0.2.0", "windows", null, "sync", "warning", "SyncTimeout", "Push timed out after 30s", null, null, null),
            new("veltrix-pos", "0.2.0", "windows", null, "bogus-category", "error", "X", "Y", null, null, null),
        ]));
        Assert.Equal(3, res.Accepted);
        Assert.Equal(1, res.Rejected);
        Assert.Equal(2, res.IncidentIds.Count);

        var list = await admin.GetAsync<PagedResult<IncidentDto>>($"/api/v1/platform/incidents?tenantId={t.TenantId}");
        Assert.Equal(2, list.TotalCount);
        var crash = list.Items.Single(i => i.Category == "crash");
        Assert.Equal(2, crash.OccurrenceCount);
        Assert.Equal("Crashy SARL", crash.TenantName);
        Assert.Equal("open", crash.Status);

        var detailRaw = await (await admin.GetAsync($"/api/v1/platform/incidents/{crash.Id}")).Content.ReadAsStringAsync();
        foreach (var secret in new[] { "example.com", "example.org", "owner@shop.dz", "s3cr3tTokenValue123", "anotherSecret987", "4111 1111", "4111-1111", "5500 0000", jwt[..20], "hunter2" })
            Assert.DoesNotContain(secret, detailRaw);
        Assert.Contains("[redacted]", detailRaw);
        var detail = await admin.GetAsync<IncidentDetailDto>($"/api/v1/platform/incidents/{crash.Id}");
        Assert.Equal(2, detail.RecentEvents.Count);
        Assert.Contains("checkout", detail.RecentEvents[0].Context);
        Assert.Equal(1, detail.AffectedDevices);

        // Same error in another tenant: separate incident (tenant ref) but one cross-tenant group.
        await t2.Owner.PostAsync<ErrorReportBatchResponse>("/api/v1/diagnostics/errors", new ErrorReportBatchRequest([Crash("Order 5 failed for x@y.com with token=abcSecretValue card 4111 1111 1111 1111")]));
        var groups = await admin.GetAsync<List<IncidentGroupDto>>("/api/v1/platform/incidents/groups?category=crash");
        var g = Assert.Single(groups, x => x.Fingerprint == crash.Fingerprint);
        Assert.Equal(2, g.Tenants);
        Assert.Equal(3, g.Occurrences);

        // Tenant users can never read platform incidents, even with the "*" permission.
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Owner.GetAsync("/api/v1/platform/incidents")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Owner.GetAsync($"/api/v1/platform/incidents/{crash.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Owner.PostAsJsonAsync($"/api/v1/platform/incidents/{crash.Id}/status", new UpdateIncidentStatusRequest("resolved", null))).StatusCode);

        // Status workflow; a resolved incident reopens when it happens again.
        var ack = await admin.PostAsync<IncidentDetailDto>($"/api/v1/platform/incidents/{crash.Id}/status", new UpdateIncidentStatusRequest("acknowledged", "looking"));
        Assert.Equal("acknowledged", ack.Incident.Status);
        Assert.NotNull(ack.AcknowledgedAt);
        var resolved = await admin.PostAsync<IncidentDetailDto>($"/api/v1/platform/incidents/{crash.Id}/status", new UpdateIncidentStatusRequest("resolved", "fixed in 0.2.1"));
        Assert.Equal("resolved", resolved.Incident.Status);
        await t.Owner.PostAsync<ErrorReportBatchResponse>("/api/v1/diagnostics/errors", new ErrorReportBatchRequest([Crash("Order 77 failed for z@z.com with token=qSecretValue card 5500-0000-0000-0004")]));
        var reopened = await admin.GetAsync<IncidentDetailDto>($"/api/v1/platform/incidents/{crash.Id}");
        Assert.Equal("open", reopened.Incident.Status);
        Assert.Equal(3, reopened.Incident.OccurrenceCount);

        // Oversized batches are rejected.
        var big = Enumerable.Range(0, 51).Select(i => Crash("x")).ToList();
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Owner.PostAsJsonAsync("/api/v1/diagnostics/errors", new ErrorReportBatchRequest(big), Json.Options)).StatusCode);

        // Retention never touches open incidents; resolved + old ones go.
        using (var scope = f.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<Veltrix.Modules.Operations.DiagnosticsService>();
            await svc.PurgeAsync(90, CancellationToken.None);
        }
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/platform/incidents/{crash.Id}")).StatusCode);
    }

    [Fact]
    public async Task Remote_diagnostics_require_tenant_consent_and_are_sanitized()
    {
        var t = await Scenario.CreateTenantAsync(f, "Consent Co");
        var admin = await Scenario.PlatformAdminAsync(f);
        var req = await admin.PostAsync<DiagnosticRequestDto>($"/api/v1/platform/fleet/devices/{t.Device.DeviceId}/diagnostic-requests",
            new CreateDiagnosticRequestRequest("printer issue", 24));
        Assert.False(req.ConsentGranted);
        Assert.Equal("pending", req.Status);

        // Without consent: nothing is delivered and uploads are refused.
        var hb = await t.Owner.PostAsync<DeviceHeartbeatResponse>("/api/v1/devices/heartbeat", StepUp.Heartbeat(t.Device));
        Assert.Empty(hb.DiagnosticRequests);
        var upload = new DiagnosticReportUploadRequest("0.2.0", "Windows 11", new Dictionary<string, string> { ["printer.status"] = "offline", ["db.owner"] = "boss@corp.dz", ["apiKey"] = "zzz" },
            ["started", "token=abcdef123456 used", "ok"]);
        var refused = await t.Owner.PostAsJsonAsync($"/api/v1/devices/diagnostic-requests/{req.Id}/report", upload, Json.Options);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("diagnostics.consent_required", await refused.ProblemCodeAsync());

        // The tenant grants consent: the request is delivered on the next heartbeat and the upload is accepted (sanitized).
        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.PutAsJsonAsync($"/api/v1/settings/{SettingKeys.DiagnosticsConsent}", new SettingDto(SettingKeys.DiagnosticsConsent, "true"))).StatusCode);
        hb = await t.Owner.PostAsync<DeviceHeartbeatResponse>("/api/v1/devices/heartbeat", StepUp.Heartbeat(t.Device));
        Assert.True(hb.DiagnosticsConsent);
        Assert.Equal(req.Id, Assert.Single(hb.DiagnosticRequests).Id);
        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.PostAsJsonAsync($"/api/v1/devices/diagnostic-requests/{req.Id}/report", upload, Json.Options)).StatusCode);

        var report = await admin.GetAsync<DiagnosticReportDto>($"/api/v1/platform/diagnostic-requests/{req.Id}");
        Assert.Equal("uploaded", report.Request.Status);
        Assert.Equal("offline", report.Facts["printer.status"]);
        Assert.DoesNotContain("boss@corp.dz", report.Facts["db.owner"]);
        Assert.False(report.Facts.ContainsKey("apiKey"));
        Assert.DoesNotContain(report.RecentLogLines, l => l.Contains("abcdef123456"));

        // A closed request cannot be uploaded twice; another tenant cannot touch it.
        Assert.Equal(HttpStatusCode.Conflict, (await t.Owner.PostAsJsonAsync($"/api/v1/devices/diagnostic-requests/{req.Id}/report", upload, Json.Options)).StatusCode);
        var other = await Scenario.CreateTenantAsync(f, "Snoop Co");
        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.PostAsJsonAsync($"/api/v1/devices/diagnostic-requests/{req.Id}/report", upload, Json.Options)).StatusCode);
    }

    [Fact]
    public async Task Licensing_admin_demo_seats_reassign_and_failed_activations()
    {
        var t = await Scenario.CreateTenantAsync(f, "Licensee Co", devices: 2);
        var admin = await Scenario.PlatformAdminAsync(f);

        var demo = await admin.PostAsync<CreateLicenseResponse>("/api/v1/platform/licenses/demo", new IssueDemoLicenseRequest(t.TenantId, 7, 1, "trade fair"));
        Assert.Equal("Trial", demo.License.Type);
        Assert.Equal("trial", demo.License.PlanCode);
        Assert.InRange((demo.License.ExpiresAt!.Value - DateTimeOffset.UtcNow).TotalDays, 6.9, 7.1);

        // Seats cannot drop below active devices.
        var tooFew = await admin.PostAsJsonAsync($"/api/v1/platform/licenses/{t.LicenseId}/seats", new ChangeLicenseSeatsRequest(0, "x"), Json.Options);
        Assert.Equal(HttpStatusCode.BadRequest, tooFew.StatusCode);
        var seats = await admin.PostAsync<LicenseDto>($"/api/v1/platform/licenses/{t.LicenseId}/seats", new ChangeLicenseSeatsRequest(1, "downsize"));
        Assert.Equal(1, seats.MaxDevices);

        // Seats exhausted -> failed activation recorded (license recognized) + unknown key recorded too. No key material stored.
        var extra = new TestDevice();
        var anon = f.CreateClient();
        var ch = await anon.PostAsync<ChallengeResponse>("/api/v1/licensing/challenge", new ChallengeRequest(extra.InstallationId, "activate"));
        var fail = await anon.PostAsJsonAsync("/api/v1/licensing/activate", new ActivateRequest(t.LicenseKey, extra.InstallationId, "android", "Phone", "0.1.0",
            extra.PublicKey, extra.Fingerprint, 1, ch.ChallengeId, extra.Sign(ch.Nonce, "activate")), Json.Options);
        Assert.Equal(HttpStatusCode.Conflict, fail.StatusCode);
        ch = await anon.PostAsync<ChallengeResponse>("/api/v1/licensing/challenge", new ChallengeRequest(extra.InstallationId, "activate"));
        var unknown = await anon.PostAsJsonAsync("/api/v1/licensing/activate", new ActivateRequest("VLX-AAAAA-BBBBB-CCCCC-DDDDD", extra.InstallationId, "android", "Phone", "0.1.0",
            extra.PublicKey, extra.Fingerprint, 1, ch.ChallengeId, extra.Sign(ch.Nonce, "activate")), Json.Options);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var failuresRaw = await (await admin.GetAsync("/api/v1/platform/licenses/activation-failures?pageSize=100")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(t.LicenseKey, failuresRaw);
        Assert.DoesNotContain("AAAAA-BBBBB", failuresRaw);
        var failures = await admin.GetAsync<PagedResult<ActivationFailureDto>>("/api/v1/platform/licenses/activation-failures?pageSize=100");
        Assert.Contains(failures.Items, x => x.Code == "licensing.seats_exhausted" && x.LicenseId == t.LicenseId && x.TenantId == t.TenantId && x.InstallationId == extra.InstallationId);
        Assert.Contains(failures.Items, x => x.Code == "licensing.key_invalid" && x.LicenseId == null);
        var events = await admin.GetAsync<List<LicenseEventDto>>($"/api/v1/platform/licenses/{t.LicenseId}/events");
        Assert.Contains(events, e => e.Type == "device.activation_failed" && e.Details == "licensing.seats_exhausted");

        // Reassign the device to the demo license (same tenant), step-up required.
        Assert.Equal(HttpStatusCode.Forbidden, (await StepUp.PostAsync(admin, $"/api/v1/platform/fleet/devices/{t.Device.DeviceId}/reassign",
            new ReassignDeviceRequest(demo.License.Id, "move"), null)).StatusCode);
        var moved = await (await StepUp.PostAsync(admin, $"/api/v1/platform/fleet/devices/{t.Device.DeviceId}/reassign", new ReassignDeviceRequest(demo.License.Id, "move"))).ReadAsync<AdminDeviceDetailDto>();
        Assert.Equal(demo.License.Id, moved.Device.LicenseId);

        // Tenant 360 view aggregates licenses, devices and history.
        var overview = await admin.GetAsync<TenantOverviewDto>($"/api/v1/platform/tenants/{t.TenantId}/overview");
        Assert.Equal("Licensee Co", overview.Company.Name);
        Assert.Equal(2, overview.Licenses.Count);
        Assert.Single(overview.Devices);
        Assert.Contains(overview.ActivationHistory, e => e.Type == "device.reassigned_in");
        Assert.Equal(1, overview.Users);
        var overviewRaw = await (await admin.GetAsync($"/api/v1/platform/tenants/{t.TenantId}/overview")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(t.LicenseKey, overviewRaw);
        Assert.DoesNotContain(t.Device.PublicKey, overviewRaw);
    }

    [Fact]
    public async Task Non_platform_users_get_403_on_admin_endpoints()
    {
        var t = await Scenario.CreateTenantAsync(f, "Curious Co");
        foreach (var url in new[]
                 {
                     "/api/v1/platform/dashboard/admin", "/api/v1/platform/fleet/devices", $"/api/v1/platform/fleet/devices/{t.Device.DeviceId}",
                     $"/api/v1/platform/tenants/{t.TenantId}/overview", "/api/v1/platform/incidents", "/api/v1/platform/incidents/groups",
                     "/api/v1/platform/telegram/status", "/api/v1/platform/telegram/deliveries", "/api/v1/platform/licenses/activation-failures",
                 })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await t.Owner.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync(url)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Owner.PostAsJsonAsync($"/api/v1/platform/fleet/devices/{t.Device.DeviceId}/diagnostic-requests",
            new CreateDiagnosticRequestRequest(null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await StepUp.PostAsync(t.Owner, $"/api/v1/platform/fleet/devices/{t.Device.DeviceId}/deactivate",
            new DeviceActionRequest("x"), ApiFactory.StrongPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await StepUp.PostAsync(t.Owner, "/api/v1/platform/telegram/test", new Veltrix.Contracts.Telegram.TelegramTestMessageRequest(null),
            ApiFactory.StrongPassword)).StatusCode);
    }
}
