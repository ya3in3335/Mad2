using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Veltrix.Contracts.Telegram;
using Veltrix.Modules.Telegram;
using Veltrix.SharedKernel;

namespace Veltrix.Api.IntegrationTests;

/// <summary>Stands in for api.telegram.org. Responses are scripted; every request is recorded. The real API is never contacted.</summary>
public sealed class FakeTelegramHandler : HttpMessageHandler
{
    public ConcurrentQueue<(HttpStatusCode Status, string Body)> Script { get; } = new();
    public ConcurrentQueue<(string Url, string Body)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Enqueue((request.RequestUri!.ToString(), request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
        var (status, body) = Script.TryDequeue(out var next) ? next : (HttpStatusCode.OK, """{"ok":true,"result":{"message_id":1}}""");
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

[Collection("api")]
public sealed class TelegramTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private const string Token = "123456789:TEST-ONLY-token-not-real-abcdefghijk";
    private const string Secret = "webhook-secret-for-tests-0123456789";
    private const long OwnerChat = 424242;

    private (WebApplicationFactory<Program> Host, FakeTelegramHandler Fake) Host()
    {
        var fake = new FakeTelegramHandler();
        var host = f.WithWebHostBuilder(b =>
        {
            b.UseSetting("Telegram:Enabled", "true");
            b.UseSetting("Telegram:BotToken", Token);
            b.UseSetting("Telegram:WebhookSecret", Secret);
            b.UseSetting("Telegram:AllowedChatIds:0", OwnerChat.ToString());
            b.UseSetting("Telegram:ApiBaseUrl", "https://telegram.fake.invalid");
            b.ConfigureTestServices(s => s.AddHttpClient(TelegramClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => fake));
        });
        return (host, fake);
    }

    private static HttpRequestMessage Update(long chatId, string text, string? secret)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/telegram/webhook")
        {
            Content = JsonContent.Create(new { update_id = 1, message = new { message_id = 5, chat = new { id = chatId, type = "private" }, text } }),
        };
        if (secret is not null) req.Headers.Add(TelegramModule.SecretHeader, secret);
        return req;
    }

    [Fact]
    public async Task Webhook_requires_secret_ignores_unknown_chats_and_answers_read_only_commands()
    {
        var (host, fake) = Host();
        var c = host.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Update(OwnerChat, "/stats", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Update(OwnerChat, "/stats", "wrong-secret"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Update(OwnerChat, "/stats", Secret + "x"))).StatusCode);

        // Valid secret but chat not allowed: acknowledged (200 so Telegram stops retrying) and ignored — no reply, no data.
        var ignored = await c.SendAsync(Update(999, "/stats", Secret));
        Assert.Equal(HttpStatusCode.OK, ignored.StatusCode);
        Assert.DoesNotContain("sendMessage", await ignored.Content.ReadAsStringAsync());

        var stats = await c.SendAsync(Update(OwnerChat, "/stats", Secret));
        Assert.Equal(HttpStatusCode.OK, stats.StatusCode);
        var reply = await stats.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sendMessage", reply.GetProperty("method").GetString());
        Assert.Equal(OwnerChat, reply.GetProperty("chat_id").GetInt64());
        var text = reply.GetProperty("text").GetString()!;
        Assert.Contains("Tenants:", text);
        Assert.Contains("MRR:", text);

        foreach (var cmd in new[] { "/status", "/health", "/alerts", "/subscriptions", "/help", "/stats@VeltrixBot" })
        {
            var r = await (await c.SendAsync(Update(OwnerChat, cmd, Secret))).Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(string.IsNullOrWhiteSpace(r.GetProperty("text").GetString()), cmd);
        }
        var unknown = await (await c.SendAsync(Update(OwnerChat, "/suspend_tenant 1", Secret))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("read-only", unknown.GetProperty("text").GetString());
        // Replies go inline in the webhook response: the bot API itself was not called.
        Assert.Empty(fake.Requests);

        // Webhook is disabled entirely in a host without a configured secret.
        Assert.Equal(HttpStatusCode.NotFound, (await f.CreateClient().SendAsync(Update(OwnerChat, "/stats", Secret))).StatusCode);
    }

    [Fact]
    public async Task Outbox_deduplicates_and_retries_429_with_retry_after()
    {
        var (host, fake) = Host();
        _ = host.CreateClient();
        var publisher = host.Services.GetRequiredService<IAlertPublisher>();
        var alert = new AlertMessage("test.dedup", "Repeated thing", "Contact boss@corp.dz token=abc123secret", AlertSeverity.Warning, "dedup-key-1");
        await publisher.PublishAsync(alert);
        await publisher.PublishAsync(alert);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DbContext>();
            var rows = await db.Set<TelegramOutboxMessage>().Where(m => m.DedupKey == "dedup-key-1").ToListAsync();
            var row = Assert.Single(rows);
            Assert.Equal(OutboxStatuses.Pending, row.Status);
            Assert.Equal(OwnerChat, row.ChatId);
            Assert.DoesNotContain("boss@corp.dz", row.Text);
            Assert.DoesNotContain("abc123secret", row.Text);
            Assert.StartsWith("[WARNING] Repeated thing", row.Text);
        }

        // 1st attempt: Telegram answers 429 retry_after=1 -> stays pending with backoff honouring retry_after.
        fake.Script.Enqueue((HttpStatusCode.TooManyRequests, """{"ok":false,"error_code":429,"description":"Too Many Requests: retry after 1","parameters":{"retry_after":1}}"""));
        Assert.Equal(0, await DispatchAsync(host));
        var after429 = await RowAsync(host, "dedup-key-1");
        Assert.Equal(OutboxStatuses.Pending, after429.Status);
        Assert.Equal(1, after429.Attempts);
        Assert.Contains("429", after429.LastError);
        Assert.DoesNotContain(Token, after429.LastError);
        Assert.True(after429.NextAttemptAt > DateTimeOffset.UtcNow.AddMilliseconds(200));

        // Not due yet: nothing is sent.
        Assert.Equal(0, await DispatchAsync(host));
        Assert.Single(fake.Requests);

        await Task.Delay(1300);
        Assert.Equal(1, await DispatchAsync(host));
        var sent = await RowAsync(host, "dedup-key-1");
        Assert.Equal(OutboxStatuses.Sent, sent.Status);
        Assert.Equal(2, sent.Attempts);
        Assert.NotNull(sent.SentAt);
        Assert.Equal(2, fake.Requests.Count);
        var (url, body) = fake.Requests.Last();
        Assert.Equal($"https://telegram.fake.invalid/bot{Token}/sendMessage", url);
        Assert.Equal(OwnerChat, JsonDocument.Parse(body).RootElement.GetProperty("chat_id").GetInt64());

        // 5xx -> exponential backoff; permanent 4xx -> failed.
        await publisher.PublishAsync(new AlertMessage("test.5xx", "Server error", "x", DedupKey: "k-5xx"));
        fake.Script.Enqueue((HttpStatusCode.BadGateway, "bad gateway"));
        await Task.Delay(1100); // per-chat 1 msg/s
        await DispatchAsync(host);
        var r5 = await RowAsync(host, "k-5xx");
        Assert.Equal(OutboxStatuses.Pending, r5.Status);
        Assert.True(r5.NextAttemptAt > DateTimeOffset.UtcNow.AddSeconds(3));
        await publisher.PublishAsync(new AlertMessage("test.4xx", "Bad chat", "x", DedupKey: "k-4xx"));
        fake.Script.Enqueue((HttpStatusCode.BadRequest, """{"ok":false,"error_code":400,"description":"Bad Request: chat not found"}"""));
        await Task.Delay(1100);
        await DispatchAsync(host);
        var r4 = await RowAsync(host, "k-4xx");
        Assert.Equal(OutboxStatuses.Failed, r4.Status);
        Assert.Contains("chat not found", r4.LastError);

        // Admin endpoints: status never reveals the token; test message requires step-up.
        var admin = host.CreateClient();
        admin.WithToken((await admin.PostAsync<Veltrix.Contracts.Identity.TokenResponse>("/api/v1/auth/login",
            new Veltrix.Contracts.Identity.LoginRequest(ApiFactory.AdminEmail, ApiFactory.AdminPassword))).AccessToken);
        var statusRaw = await (await admin.GetAsync("/api/v1/platform/telegram/status")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(Token, statusRaw);
        Assert.DoesNotContain(Secret, statusRaw);
        var status = await admin.GetAsync<TelegramStatusDto>("/api/v1/platform/telegram/status");
        Assert.True(status.Enabled && status.BotTokenConfigured && status.WebhookSecretConfigured);
        Assert.Equal(1, status.AllowedChats);
        Assert.Equal(1, status.Failed);
        var log = await admin.GetAsync<PagedResult<TelegramDeliveryDto>>("/api/v1/platform/telegram/deliveries?status=sent");
        Assert.Contains(log.Items, x => x.DedupKey == "dedup-key-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await StepUp.PostAsync(admin, "/api/v1/platform/telegram/test", new TelegramTestMessageRequest("hello"), null)).StatusCode);
        var queued = await (await StepUp.PostAsync(admin, "/api/v1/platform/telegram/test", new TelegramTestMessageRequest("hello"))).ReadAsync<TelegramTestMessageResponse>();
        Assert.Equal(1, queued.Queued);
    }

    private static async Task<int> DispatchAsync(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TelegramDispatcher>().DispatchAsync(CancellationToken.None);
    }

    private static async Task<TelegramOutboxMessage> RowAsync(WebApplicationFactory<Program> host, string key)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DbContext>().Set<TelegramOutboxMessage>().AsNoTracking().SingleAsync(m => m.DedupKey == key);
    }
}
