using System.Net;
using System.Net.Http.Json;
using Veltrix.Contracts.Identity;
using Veltrix.Contracts.Tenants;

namespace Veltrix.Api.IntegrationTests;

/// <summary>Proves one company cannot read or modify another company's records.</summary>
[Collection("api")]
public sealed class TenantIsolationTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Tenant_cannot_see_or_modify_other_tenant_data()
    {
        var a = await Scenario.CreateTenantAsync(f, "Tenant A");
        var b = await Scenario.CreateTenantAsync(f, "Tenant B");

        var aBranches = await a.Owner.GetAsync<List<BranchDto>>("/api/v1/branches");
        Assert.All(aBranches, br => Assert.NotEqual(b.BranchId, br.Id));

        // Updating B's branch from A's session must fail as not found (row invisible).
        var r = await a.Owner.PutAsJsonAsync($"/api/v1/branches/{b.BranchId}", new UpsertBranchRequest("HQ", "Hijacked", null, null));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);

        // Users of B are invisible to A.
        var bUsers = await b.Owner.GetAsync<Veltrix.SharedKernel.PagedResult<UserDto>>("/api/v1/users");
        var victim = bUsers.Items.First();
        Assert.Equal(HttpStatusCode.NotFound, (await a.Owner.GetAsync($"/api/v1/users/{victim.Id}")).StatusCode);

        // Assigning a user to another tenant's branch is rejected.
        var create = await a.Owner.PostAsJsonAsync("/api/v1/users", new CreateUserRequest($"x{Guid.NewGuid():N}@a.dz", "X", null,
            ApiFactory.StrongPassword, ["cashier"], [b.BranchId], null));
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);

        // Company profile always resolves from the token, never from client input.
        var company = await a.Owner.GetAsync<CompanyDto>("/api/v1/company");
        Assert.Equal(a.TenantId, company.Id);
    }

    [Fact]
    public async Task Tenant_scoped_license_view_shows_only_own_licenses()
    {
        var a = await Scenario.CreateTenantAsync(f, "Tenant C");
        var b = await Scenario.CreateTenantAsync(f, "Tenant D");
        var mine = await a.Owner.GetAsync<List<Veltrix.Contracts.Licensing.LicenseSummaryDto>>("/api/v1/licensing/my");
        Assert.Single(mine);
        Assert.Equal(a.LicenseId, mine[0].Id);
        var r = await a.Owner.PostAsJsonAsync($"/api/v1/licensing/my/devices/{b.Device.DeviceId}/revoke", new Veltrix.Contracts.Licensing.RevokeDeviceRequest("x"));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }
}
