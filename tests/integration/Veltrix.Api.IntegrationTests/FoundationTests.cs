using System.Net;
using System.Net.Http.Json;
using Veltrix.Contracts.Identity;
using Veltrix.Contracts.Licensing;
using Veltrix.Contracts.Tenants;
using Veltrix.Modules.Licensing;

namespace Veltrix.Api.IntegrationTests;

[Collection("api")]
public sealed class FoundationTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Health_and_version_endpoints_respond()
    {
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/version")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task Full_first_run_flow_activates_onboards_and_logs_in()
    {
        var t = await Scenario.CreateTenantAsync(f);
        var me = await t.Owner.GetAsync<UserProfileDto>("/api/v1/me");
        Assert.Equal(t.TenantId, me.TenantId);
        Assert.Contains("company_owner", me.Roles);
        var company = await t.Owner.GetAsync<CompanyDto>("/api/v1/company");
        Assert.True(company.SetupCompleted);
        Assert.Equal("DZD", company.Currency);
        var branches = await t.Owner.GetAsync<List<BranchDto>>("/api/v1/branches");
        Assert.Single(branches);
    }

    [Fact]
    public async Task Certificate_is_signed_by_vendor_key_and_bound_to_device()
    {
        var t = await Scenario.CreateTenantAsync(f);
        var anon = f.CreateClient();
        var key = await anon.GetFromJsonAsync<Dictionary<string, string>>("/api/v1/licensing/public-key");
        var payload = LicenseSigner.Verify(t.Device.Certificate!, key!["spki"]);
        Assert.NotNull(payload);
        Assert.Equal(t.TenantId, payload!.Tid);
        Assert.Equal(t.Device.DeviceId, payload.Did);
        Assert.Equal(LicenseSigner.Thumbprint(t.Device.PublicKey), payload.Kth);
        Assert.True(payload.Exp > payload.Iat);
        // Tampering breaks the signature.
        var parts = t.Device.Certificate!.Split('.');
        var tampered = LicenseSigner.B64Url(System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(LicenseSigner.FromB64Url(parts[0])).Replace("\"maxDev\":3", "\"maxDev\":99"))) + "." + parts[1];
        Assert.Null(LicenseSigner.Verify(tampered, key["spki"]));
    }

    [Fact]
    public async Task Challenge_cannot_be_replayed()
    {
        var t = await Scenario.CreateTenantAsync(f);
        var anon = f.CreateClient();
        var ch = await anon.PostAsync<ChallengeResponse>("/api/v1/licensing/challenge", new ChallengeRequest(t.Device.InstallationId, "refresh"));
        var req = new RefreshCertificateRequest(t.Device.DeviceId!.Value, t.Device.InstallationId, "0.1.0", ch.ChallengeId, t.Device.Sign(ch.Nonce, "refresh"));
        var first = await anon.PostAsJsonAsync("/api/v1/licensing/refresh", req, Json.Options);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var replay = await anon.PostAsJsonAsync("/api/v1/licensing/refresh", req, Json.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("licensing.challenge_invalid", await replay.ProblemCodeAsync());
    }

    [Fact]
    public async Task Signature_from_another_key_is_rejected()
    {
        var t = await Scenario.CreateTenantAsync(f);
        var anon = f.CreateClient();
        using var attacker = new TestDevice();
        var ch = await anon.PostAsync<ChallengeResponse>("/api/v1/licensing/challenge", new ChallengeRequest(t.Device.InstallationId, "refresh"));
        var forged = attacker.Sign(ch.Nonce, "refresh").Replace(attacker.InstallationId.ToString(), t.Device.InstallationId.ToString());
        var r = await anon.PostAsJsonAsync("/api/v1/licensing/refresh",
            new RefreshCertificateRequest(t.Device.DeviceId!.Value, t.Device.InstallationId, "0.1.0", ch.ChallengeId, forged), Json.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Seat_limit_is_enforced_server_side_and_reinstall_replaces_seat()
    {
        var admin = await Scenario.PlatformAdminAsync(f);
        var tenant = await admin.PostAsync<CreateTenantResponse>("/api/v1/platform/tenants", new CreateTenantRequest("Seat Co", null, null, null, null, null));
        var lic = await admin.PostAsync<CreateLicenseResponse>("/api/v1/platform/licenses",
            new CreateLicenseRequest(tenant.TenantId, "Monthly", "full", 1, null, null, null, null, null));
        var anon = f.CreateClient();
        using var d1 = new TestDevice();
        await Scenario.ActivateAsync(anon, d1, lic.LicenseKey);

        using var d2 = new TestDevice();
        var ch = await anon.PostAsync<ChallengeResponse>("/api/v1/licensing/challenge", new ChallengeRequest(d2.InstallationId, "activate"));
        var r = await anon.PostAsJsonAsync("/api/v1/licensing/activate", new ActivateRequest(lic.LicenseKey, d2.InstallationId, "windows", "PC2", "0.1.0",
            d2.PublicKey, d2.Fingerprint, 1, ch.ChallengeId, d2.Sign(ch.Nonce, "activate")), Json.Options);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("licensing.seats_exhausted", await r.ProblemCodeAsync());

        // Same hardware fingerprint, new installation (OS reinstall) replaces the seat.
        using var reinstall = new TestDevice { Fingerprint = d1.Fingerprint };
        var act = await Scenario.ActivateAsync(anon, reinstall, lic.LicenseKey);
        Assert.Equal(1, act.License.ActiveDevices);
        var devices = await admin.GetAsync<Veltrix.SharedKernel.PagedResult<DeviceDto>>($"/api/v1/platform/devices?licenseId={lic.License.Id}");
        Assert.Contains(devices.Items, d => d.Id == d1.DeviceId && d.Status == "Replaced");
    }

    [Fact]
    public async Task Revoked_device_cannot_refresh_or_log_in()
    {
        var t = await Scenario.CreateTenantAsync(f);
        var admin = await Scenario.PlatformAdminAsync(f);
        var url = $"/api/v1/platform/devices/{t.Device.DeviceId}/revoke";
        var noStepUp = await admin.PostAsJsonAsync(url, new RevokeDeviceRequest("lost"));
        Assert.NotEqual(HttpStatusCode.NoContent, noStepUp.StatusCode);
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(new RevokeDeviceRequest("lost")) };
        req.Headers.Add("X-Confirm-Password", ApiFactory.AdminPassword);
        var r = await admin.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);

        var anon = f.CreateClient();
        var ch = await anon.PostAsync<ChallengeResponse>("/api/v1/licensing/challenge", new ChallengeRequest(t.Device.InstallationId, "refresh"));
        var refresh = await anon.PostAsJsonAsync("/api/v1/licensing/refresh",
            new RefreshCertificateRequest(t.Device.DeviceId!.Value, t.Device.InstallationId, "0.1.0", ch.ChallengeId, t.Device.Sign(ch.Nonce, "refresh")), Json.Options);
        Assert.Equal(HttpStatusCode.Forbidden, refresh.StatusCode);
        Assert.Equal("licensing.device_revoked", await refresh.ProblemCodeAsync());

        var login = await anon.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(t.OwnerEmail, ApiFactory.StrongPassword, null, t.Device.DeviceId), Json.Options);
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
    }

    [Fact]
    public async Task Expired_license_blocks_activation_and_makes_tenant_read_only()
    {
        var t = await Scenario.CreateTenantAsync(f);
        var admin = await Scenario.PlatformAdminAsync(f);
        await admin.PutAsync<LicenseDto>($"/api/v1/platform/licenses/{t.LicenseId}",
            new UpdateLicenseRequest(3, DateTimeOffset.UtcNow.AddDays(-10), 7, ["pos", "inventory"], null));
        // Bypass the 30 s status cache by using a fresh login (status checked at login + per request).
        await Task.Delay(100);
        using var d = new TestDevice();
        var anon = f.CreateClient();
        var ch = await anon.PostAsync<ChallengeResponse>("/api/v1/licensing/challenge", new ChallengeRequest(d.InstallationId, "activate"));
        var r = await anon.PostAsJsonAsync("/api/v1/licensing/activate", new ActivateRequest(t.LicenseKey, d.InstallationId, "windows", "PC", "0.1.0",
            d.PublicKey, d.Fingerprint, 1, ch.ChallengeId, d.Sign(ch.Nonce, "activate")), Json.Options);
        Assert.Equal(HttpStatusCode.PaymentRequired, r.StatusCode);

        // Reads still work (data is never locked away), writes are refused.
        Assert.Equal(HttpStatusCode.OK, (await t.Owner.GetAsync("/api/v1/company")).StatusCode);
        var write = await t.Owner.PostAsJsonAsync("/api/v1/branches", new UpsertBranchRequest("ORN", "Oran", null, null));
        Assert.Equal(HttpStatusCode.PaymentRequired, write.StatusCode);
    }

    [Fact]
    public async Task Refresh_token_rotation_detects_reuse()
    {
        var t = await Scenario.CreateTenantAsync(f);
        var c = f.CreateClient();
        var login = await c.PostAsync<TokenResponse>("/api/v1/auth/login", new LoginRequest(t.OwnerEmail, ApiFactory.StrongPassword, null, t.Device.DeviceId));
        var rotated = await c.PostAsync<TokenResponse>("/api/v1/auth/refresh", new RefreshRequest(login.RefreshToken));
        Assert.NotEqual(login.RefreshToken, rotated.RefreshToken);
        // Reusing the old token revokes the whole family, including the newly rotated token.
        var reuse = await c.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(login.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        var after = await c.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(rotated.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Lockout_after_repeated_failures()
    {
        var t = await Scenario.CreateTenantAsync(f);
        var c = f.CreateClient();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(t.OwnerEmail, "wrong-password"))).StatusCode);
        var locked = await c.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(t.OwnerEmail, ApiFactory.StrongPassword));
        Assert.Equal((HttpStatusCode)423, locked.StatusCode);
    }

    [Fact]
    public async Task Weak_password_is_rejected_and_unknown_user_gets_generic_error()
    {
        var t = await Scenario.CreateTenantAsync(f);
        var r = await t.Owner.PostAsJsonAsync("/api/v1/users", new CreateUserRequest("cashier@example.dz", "Cashier", null, "short", ["cashier"], null, "ar"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var unknown = await f.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("nobody@example.dz", "whatever-123"));
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal("auth.invalid_credentials", await unknown.ProblemCodeAsync());
    }

    [Fact]
    public async Task Company_user_cannot_call_platform_endpoints()
    {
        var t = await Scenario.CreateTenantAsync(f);
        var r = await t.Owner.GetAsync("/api/v1/platform/licenses");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        var r2 = await t.Owner.PostAsJsonAsync("/api/v1/platform/tenants", new CreateTenantRequest("Evil", null, null, null, null, null));
        Assert.Equal(HttpStatusCode.Forbidden, r2.StatusCode);
    }

    [Fact]
    public async Task Cashier_permissions_are_enforced()
    {
        var t = await Scenario.CreateTenantAsync(f);
        var email = $"cashier.{Guid.NewGuid():N}@example.dz";
        await t.Owner.PostAsync<UserDto>("/api/v1/users", new CreateUserRequest(email, "Cashier One", null, ApiFactory.StrongPassword, ["cashier"], [t.BranchId], "ar"));
        var c = f.CreateClient();
        var tokens = await c.PostAsync<TokenResponse>("/api/v1/auth/login", new LoginRequest(email, ApiFactory.StrongPassword, null, t.Device.DeviceId));
        c.WithToken(tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PutAsJsonAsync("/api/v1/company", new UpdateCompanyRequest("X", null, null, null, null, null, null, null, null, null, null, null, null, null))).StatusCode);
        Assert.Contains(t.BranchId, tokens.User.Branches!);
    }
}
