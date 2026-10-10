using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using Veltrix.Api;
using Veltrix.Api.Infrastructure;
using Veltrix.Modules.Identity;
using Veltrix.SharedKernel;

if (args is ["decrypt-backup", var input, var output, ..])
{
    // Usage: VELTRIX_BACKUP_KEY=<base64> Veltrix.Api decrypt-backup <file.vbk> <out.dump>  (then: pg_restore -d <db> out.dump)
    var key = Veltrix.Workers.BackupService.Key(new Veltrix.Workers.BackupOptions { EncryptionKey = Environment.GetEnvironmentVariable("VELTRIX_BACKUP_KEY") });
    await using var inStream = File.OpenRead(input);
    await using var outStream = File.Create(output);
    await Veltrix.Workers.BackupService.DecryptAsync(inStream, outStream, key, CancellationToken.None);
    Console.WriteLine($"Decrypted {input} -> {output}");
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => { o.IncludeScopes = true; o.TimestampFormat = "O"; });

var modules = ModuleCatalog.All;
VeltrixDbContext.ModuleAssemblies.AddRange(modules.Select(m => m.GetType().Assembly));

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
builder.Services.PostConfigure<AuthOptions>(o => o.SigningKey = SecretBootstrap.ResolveSigningKey(o.SigningKey, builder.Environment, builder.Configuration));

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();

builder.Services.AddDbContext<VeltrixDbContext>((sp, o) => o.UseNpgsql(
    sp.GetRequiredService<IConfiguration>().GetConnectionString("Veltrix") is { Length: > 0 } cs
        ? cs : throw new InvalidOperationException("ConnectionStrings:Veltrix is not configured."), npg =>
{
    npg.MigrationsHistoryTable("__ef_migrations", "public");
}));
builder.Services.AddScoped<DbContext>(sp => sp.GetRequiredService<VeltrixDbContext>());
builder.Services.AddScoped<IAuditWriter, AuditWriter>();

var dataProtection = builder.Services.AddDataProtection().SetApplicationName("veltrix");
var keysDir = builder.Configuration["DataProtection:KeysDirectory"];
if (!string.IsNullOrWhiteSpace(keysDir)) dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysDir));

foreach (var m in modules) m.Register(builder.Services);
HostServices.Register(builder.Services, builder.Configuration);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<JwtTokenService>((o, jwt) =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = jwt.ValidationParameters();
    // SignalR sends the token as a query string parameter for WebSocket connections.
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            var token = ctx.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs")) ctx.Token = token;
            return Task.CompletedTask;
        },
    };
});
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("RateLimits:AuthPerMinute", 20),
            Window = TimeSpan.FromMinutes(1),
        }));
    // Client telemetry (heartbeats, error reports, diagnostic uploads): per user/device, independent of the general budget.
    o.AddPolicy(Veltrix.Modules.Operations.OperationsModule.TelemetryRateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
        (http.User.FindFirst("sub")?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "anon") + ":" + http.User.FindFirst("did")?.Value,
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("RateLimits:TelemetryPerMinute", 60),
            Window = TimeSpan.FromMinutes(1),
        }));
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http => RateLimitPartition.GetTokenBucketLimiter(
        http.User.FindFirst("sub")?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = builder.Configuration.GetValue("RateLimits:BurstPerClient", 300),
            TokensPerPeriod = builder.Configuration.GetValue("RateLimits:PerSecondPerClient", 50),
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        }));
});

builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
});
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<VeltrixDbContext>("database");
builder.Services.AddCors(o => o.AddPolicy("control-center", p =>
{
    var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
    p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing")) app.UseHsts();
app.Use(async (http, next) =>
{
    http.Response.Headers["X-Content-Type-Options"] = "nosniff";
    http.Response.Headers["X-Frame-Options"] = "DENY";
    http.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});
app.UseCors("control-center");
app.UseAuthentication();
app.UseMiddleware<TenantContextMiddleware>();
app.UseRateLimiter();
app.UseMiddleware<TenantAccessMiddleware>();
app.UseAuthorization();

app.MapOpenApi("/openapi/{documentName}.json");
if (app.Environment.IsDevelopment()) app.MapScalarApiReference("/docs");
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");
app.MapGet("/api/v1/version", () => BuildInfo.Current).AllowAnonymous().WithTags("System");

foreach (var m in modules) m.MapEndpoints(app);
HostServices.MapEndpoints(app);

await Bootstrap.RunAsync(app);
app.Run();

public partial class Program;
