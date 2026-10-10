using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Veltrix.Contracts.Identity;
using Veltrix.Contracts.Licensing;
using Veltrix.Contracts.Tenants;

namespace Veltrix.Api.IntegrationTests;

/// <summary>Boots the real API against a disposable PostgreSQL database. Set VELTRIX_TEST_PG to override the server.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminEmail = "platform.admin@veltrix.test";
    public const string AdminPassword = "Adm1n!Platform#Test";
    public const string StrongPassword = "Str0ng!Passw0rd";

    private readonly string _dbName = "veltrix_test_" + Guid.NewGuid().ToString("N")[..12];
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "veltrix-test-" + Guid.NewGuid().ToString("N"));

    public static string ServerConnection =>
        Environment.GetEnvironmentVariable("VELTRIX_TEST_PG") ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres";

    public string ConnectionString => ServerConnection + ";Database=" + _dbName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (k, v) in Settings) builder.UseSetting(k, v);
    }

    private Dictionary<string, string?> Settings => new()
    {
            ["ConnectionStrings:Veltrix"] = ConnectionString,
            ["Database:MigrateOnStartup"] = "true",
            ["Bootstrap:PlatformAdminEmail"] = AdminEmail,
            ["Bootstrap:PlatformAdminPassword"] = AdminPassword,
            ["Auth:PasswordIterations"] = "1000",
            ["Veltrix:DataDirectory"] = _dataDir,
            ["RateLimits:AuthPerMinute"] = "10000",
            ["RateLimits:BurstPerClient"] = "100000",
            ["RateLimits:PerSecondPerClient"] = "100000",
            ["Workers:Enabled"] = "false",
    };

    public Task InitializeAsync() => Task.CompletedTask;

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(ServerConnection + ";Database=postgres");
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_dbName} WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}

/// <summary>A simulated client device holding an ECDSA P-256 key, like the Windows/Android apps.</summary>
public sealed class TestDevice : IDisposable
{
    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public Guid InstallationId { get; } = Guid.NewGuid();
    public string Fingerprint { get; init; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string PublicKey => Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());
    public Guid? DeviceId { get; set; }
    public string? Certificate { get; set; }

    public string Sign(string nonce, string purpose) =>
        Convert.ToBase64String(Key.SignData(Encoding.UTF8.GetBytes($"{nonce}|{InstallationId}|{purpose}"), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    public void Dispose() => Key.Dispose();
}

public static class ClientExtensions
{
    public static async Task<T> ReadAsync<T>(this HttpResponseMessage r)
    {
        if (!r.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)r.StatusCode} {r.StatusCode}: {await r.Content.ReadAsStringAsync()}", null, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<T>(Json.Options))!;
    }

    public static async Task<T> PostAsync<T>(this HttpClient c, string url, object body) =>
        await (await c.PostAsJsonAsync(url, body, Json.Options)).ReadAsync<T>();

    public static async Task<T> PutAsync<T>(this HttpClient c, string url, object body) =>
        await (await c.PutAsJsonAsync(url, body, Json.Options)).ReadAsync<T>();

    public static async Task<T> GetAsync<T>(this HttpClient c, string url) => await (await c.GetAsync(url)).ReadAsync<T>();

    public static async Task<string> ProblemCodeAsync(this HttpResponseMessage r)
    {
        var doc = await r.Content.ReadFromJsonAsync<JsonElement>();
        return doc.TryGetProperty("code", out var code) ? code.GetString()! : "";
    }

    public static HttpClient WithToken(this HttpClient c, string token)
    {
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }
}

/// <summary>A fully onboarded tenant with an owner session bound to an activated device.</summary>
public sealed record TenantFixture(Guid TenantId, Guid BranchId, Guid WarehouseId, string OwnerEmail, string LicenseKey, Guid LicenseId,
    TestDevice Device, HttpClient Owner, string OwnerAccessToken);

public static class Scenario
{
    public static async Task<HttpClient> PlatformAdminAsync(ApiFactory f)
    {
        var c = f.CreateClient();
        var tokens = await c.PostAsync<TokenResponse>("/api/v1/auth/login", new LoginRequest(ApiFactory.AdminEmail, ApiFactory.AdminPassword));
        return c.WithToken(tokens.AccessToken);
    }

    public static async Task<ActivateResponse> ActivateAsync(HttpClient anon, TestDevice device, string licenseKey, string platform = "windows")
    {
        var ch = await anon.PostAsync<ChallengeResponse>("/api/v1/licensing/challenge", new ChallengeRequest(device.InstallationId, "activate"));
        var act = await anon.PostAsync<ActivateResponse>("/api/v1/licensing/activate", new ActivateRequest(licenseKey, device.InstallationId,
            platform, "Test PC", "0.1.0", device.PublicKey, device.Fingerprint, 1, ch.ChallengeId, device.Sign(ch.Nonce, "activate")));
        device.DeviceId = act.DeviceId;
        device.Certificate = act.Certificate;
        return act;
    }

    public static async Task<TenantFixture> CreateTenantAsync(ApiFactory f, string name = "Alpha SARL", string plan = "enterprise", int devices = 3,
        string type = "Annual")
    {
        var admin = await PlatformAdminAsync(f);
        var tenant = await admin.PostAsync<CreateTenantResponse>("/api/v1/platform/tenants", new CreateTenantRequest(name, null, null, null, "Alger", null));
        var lic = await admin.PostAsync<CreateLicenseResponse>("/api/v1/platform/licenses",
            new CreateLicenseRequest(tenant.TenantId, type, plan, devices, null, null, null, null, null));

        var anon = f.CreateClient();
        var device = new TestDevice();
        var act = await ActivateAsync(anon, device, lic.LicenseKey);
        Assert.False(act.SetupCompleted);
        Assert.NotNull(act.SetupToken);

        var email = $"owner.{Guid.NewGuid():N}@example.dz";
        var onboard = await anon.PostAsync<OnboardingResult>("/api/v1/onboarding/complete", new CompleteOnboardingRequest(
            act.SetupToken!, name, name + " SARL", "000016001234567", "16/00-1234567B23", "1 Rue Didouche Mourad", "Alger", "Alger",
            "+213 21 00 00 00", "DZD", "fr-DZ", email, "Owner " + name, ApiFactory.StrongPassword, "fr", "Siège Alger", true));

        var owner = f.CreateClient();
        var tokens = await owner.PostAsync<TokenResponse>("/api/v1/auth/login", new LoginRequest(email, ApiFactory.StrongPassword, null, device.DeviceId));
        owner.WithToken(tokens.AccessToken);
        return new TenantFixture(onboard.TenantId, onboard.BranchId, onboard.WarehouseId, email, lic.LicenseKey, lic.License.Id, device, owner, tokens.AccessToken);
    }
}
