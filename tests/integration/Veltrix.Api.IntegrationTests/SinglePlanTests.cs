using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Veltrix.Contracts.Licensing;
using Veltrix.Contracts.Subscriptions;
using Veltrix.SharedKernel;

namespace Veltrix.Api.IntegrationTests;

/// <summary>VELTRIX OS is sold as a single plan ("full") that includes every feature.</summary>
[Collection("api")]
public sealed class SinglePlanTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task New_tenant_on_full_plan_is_entitled_to_every_feature()
    {
        var admin = await Scenario.PlatformAdminAsync(f);
        var plan = Assert.Single(await admin.GetAsync<List<PlanDto>>("/api/v1/platform/plans"), p => p.IsActive);
        Assert.Equal("full", plan.Code);
        Assert.Equal(Features.All.OrderBy(x => x), plan.Features.OrderBy(x => x));

        // Legacy tier codes are no longer sellable.
        var t0 = await admin.PostAsync<Veltrix.Contracts.Tenants.CreateTenantResponse>("/api/v1/platform/tenants",
            new Veltrix.Contracts.Tenants.CreateTenantRequest("Legacy Tier Co", null, null, null, null, null));
        var legacy = await admin.PostAsJsonAsync("/api/v1/platform/licenses",
            new CreateLicenseRequest(t0.TenantId, "Monthly", "professional", 1, null, null, null, null, null), Json.Options);
        Assert.Equal(HttpStatusCode.BadRequest, legacy.StatusCode);

        var t = await Scenario.CreateTenantAsync(f, "Full Plan Co", plan: "full");
        var lic = await admin.GetAsync<LicenseDto>($"/api/v1/platform/licenses/{t.LicenseId}");
        Assert.Equal("full", lic.PlanCode);

        using var scope = f.Services.CreateScope();
        var checker = scope.ServiceProvider.GetRequiredService<IEntitlementChecker>();
        foreach (var feature in Features.All)
            Assert.True(await checker.HasFeatureAsync(t.TenantId, feature), $"Feature '{feature}' should be entitled on the full plan.");
    }
}
