using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Telegram;

public sealed record TelegramSendResult(bool Ok, int StatusCode, int? RetryAfterSeconds, string? Description);

/// <summary>Thin Bot API client over a named HttpClient ("telegram"). Tests replace the primary handler; the real API is never called in tests.</summary>
public sealed class TelegramClient(IHttpClientFactory factory, TelegramOptions options)
{
    public const string HttpClientName = "telegram";

    public async Task<TelegramSendResult> SendMessageAsync(long chatId, string text, CancellationToken ct)
    {
        var http = factory.CreateClient(HttpClientName);
        var url = $"{options.ApiBaseUrl.TrimEnd('/')}/bot{options.BotToken}/sendMessage";
        // Plain text (no parse_mode): alert content can never inject formatting or links.
        using var response = await http.PostAsJsonAsync(url, new { chat_id = chatId, text, disable_web_page_preview = true }, ct);
        string? description = null; int? retryAfter = null;
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (body.ValueKind == JsonValueKind.Object)
            {
                if (body.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String) description = d.GetString();
                if (body.TryGetProperty("parameters", out var p) && p.TryGetProperty("retry_after", out var r) && r.TryGetInt32(out var ra)) retryAfter = ra;
            }
        }
        catch (Exception e) when (e is JsonException or NotSupportedException) { /* non-JSON error body */ }
        if (retryAfter is null && response.Headers.RetryAfter?.Delta is { } delta) retryAfter = (int)Math.Ceiling(delta.TotalSeconds);
        return new TelegramSendResult(response.IsSuccessStatusCode, (int)response.StatusCode, retryAfter, description);
    }
}

/// <summary>Process-wide Bot API rate limits: at most one message per second per chat and N per minute overall,
/// plus per-chat pauses requested by Telegram (429 retry_after).</summary>
public sealed class TelegramRateLimiter(TelegramOptions options)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<long, DateTimeOffset> _nextAllowed = new();
    private readonly Queue<DateTimeOffset> _window = new();

    public bool TryAcquire(long chatId, DateTimeOffset now)
    {
        lock (_lock)
        {
            while (_window.Count > 0 && now - _window.Peek() >= TimeSpan.FromMinutes(1)) _window.Dequeue();
            if (_window.Count >= Math.Max(1, options.MaxMessagesPerMinute)) return false;
            if (_nextAllowed.TryGetValue(chatId, out var next) && now < next) return false;
            _window.Enqueue(now);
            _nextAllowed[chatId] = now.AddMilliseconds(Math.Max(0, options.PerChatIntervalMilliseconds));
            return true;
        }
    }

    public void PauseChat(long chatId, DateTimeOffset until)
    {
        lock (_lock)
        {
            if (!_nextAllowed.TryGetValue(chatId, out var current) || current < until) _nextAllowed[chatId] = until;
        }
    }
}

/// <summary>Sends due outbox rows. Retries 429 (honouring retry_after) and 5xx/network errors with exponential backoff;
/// other 4xx (bad chat, blocked bot) fail permanently. Delivery status is persisted per row and logged without secrets.</summary>
public sealed class TelegramDispatcher(DbContext db, TelegramClient client, TelegramRateLimiter limiter, TelegramOptions options, IClock clock,
    ILogger<TelegramDispatcher> logger)
{
    public async Task<int> DispatchAsync(CancellationToken ct)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.BotToken)) return 0;
        var now = clock.UtcNow;
        var due = await db.Set<TelegramOutboxMessage>()
            .Where(m => m.Status == OutboxStatuses.Pending && m.ChatId != null && (m.NextAttemptAt == null || m.NextAttemptAt <= now))
            .OrderBy(m => m.CreatedAt).Take(50).ToListAsync(ct);
        var sent = 0;
        foreach (var m in due)
        {
            var chat = m.ChatId!.Value;
            if (!options.AllowedChatIds.Contains(chat))
            {
                m.Status = OutboxStatuses.Failed; m.LastError = "chat no longer allowed";
                await db.SaveChangesAsync(ct);
                continue;
            }
            if (!limiter.TryAcquire(chat, clock.UtcNow)) continue;
            m.Attempts++;
            TelegramSendResult result;
            try { result = await client.SendMessageAsync(chat, m.Text, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                result = new TelegramSendResult(false, 0, null, ex.GetType().Name + ": " + ex.Message);
            }
            Apply(m, result);
            if (m.Status == OutboxStatuses.Sent) sent++;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Telegram delivery {OutboxId} type={Type} status={Status} attempts={Attempts} http={Http}",
                m.Id, m.Type, m.Status, m.Attempts, result.StatusCode);
        }
        return sent;
    }

    private void Apply(TelegramOutboxMessage m, TelegramSendResult r)
    {
        var now = clock.UtcNow;
        if (r.Ok)
        {
            m.Status = OutboxStatuses.Sent; m.SentAt = now; m.NextAttemptAt = null; m.LastError = null;
            return;
        }
        m.LastError = Redact($"HTTP {r.StatusCode}: {r.Description}");
        var transient = r.StatusCode is 0 or 429 or >= 500;
        if (!transient || m.Attempts >= Math.Max(1, options.MaxAttempts))
        {
            m.Status = OutboxStatuses.Failed; m.NextAttemptAt = null;
            return;
        }
        TimeSpan delay;
        if (r.StatusCode == 429)
        {
            delay = TimeSpan.FromSeconds(Math.Clamp(r.RetryAfterSeconds ?? 5, 1, 3600));
            limiter.PauseChat(m.ChatId!.Value, now + delay);
        }
        else delay = TimeSpan.FromSeconds(Math.Min(3600, 5 * Math.Pow(2, m.Attempts - 1)));
        m.NextAttemptAt = now + delay;
    }

    private string Redact(string s)
    {
        if (!string.IsNullOrEmpty(options.BotToken)) s = s.Replace(options.BotToken, "***");
        s = Redaction.Sanitize(s, 1000);
        return s;
    }

    /// <summary>Last-resort delivery that bypasses the database outbox (used when the database itself is unreachable).</summary>
    public static async Task<int> SendDirectAsync(TelegramClient client, TelegramRateLimiter limiter, TelegramOptions options, IClock clock, string text, CancellationToken ct)
    {
        if (!options.CanSend) return 0;
        var sent = 0;
        foreach (var chat in options.AllowedChatIds.Distinct())
        {
            if (!limiter.TryAcquire(chat, clock.UtcNow)) continue;
            try { if ((await client.SendMessageAsync(chat, Redaction.Sanitize(text, OutboxAlertPublisher.MaxTextLength), ct)).Ok) sent++; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { }
        }
        return sent;
    }
}

/// <summary>Background sender. Runs only when Telegram is enabled and workers are enabled on this node (Workers:Enabled).</summary>
public sealed class TelegramSenderService(IServiceScopeFactory scopes, TelegramOptions options, IConfiguration config, ILogger<TelegramSenderService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled || !config.GetValue("Workers:Enabled", true)) return;
        if (string.IsNullOrWhiteSpace(options.BotToken)) { logger.LogWarning("Telegram is enabled but Telegram:BotToken is not configured; alerts will not be sent."); return; }
        var interval = TimeSpan.FromSeconds(Math.Clamp(options.DispatchIntervalSeconds, 1, 300));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<TelegramDispatcher>().DispatchAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Telegram dispatch round failed");
            }
            try { await Task.Delay(interval, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
