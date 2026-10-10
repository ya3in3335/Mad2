using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Veltrix.Contracts.Telegram;
using Veltrix.Modules.Operations;
using Veltrix.Modules.Platform;
using Veltrix.Modules.Subscriptions;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Telegram;

/// <summary>Read-only bot commands. Answers contain aggregate figures only — never customer records, keys or personal data.</summary>
public sealed class TelegramCommandService(DbContext db, OwnerDashboardService dashboard, PlatformService platform, IClock clock)
{
    public const string Help =
        "VELTRIX Admin bot (read-only)\n/status - overall status\n/health - backend health\n/stats - key figures\n/alerts - recent alerts\n/subscriptions - subscription summary\n/help - this help";

    public async Task<string> ExecuteAsync(string commandText, CancellationToken ct)
    {
        var cmd = commandText.Trim().Split(' ', 2)[0].Split('@')[0].ToLowerInvariant();
        return cmd switch
        {
            "/start" or "/help" => Help,
            "/status" => await StatusAsync(ct),
            "/health" => await HealthAsync(ct),
            "/stats" => await StatsAsync(ct),
            "/alerts" => await AlertsAsync(ct),
            "/subscriptions" => await SubscriptionsAsync(ct),
            _ => "Unknown command. Send /help. This bot is read-only.",
        };
    }

    private async Task<string> StatusAsync(CancellationToken ct)
    {
        var d = await dashboard.GetAsync(null, ct);
        return $"Status: {d.BackendHealth}\nTenants: {d.TotalTenants} ({d.SuspendedTenants} suspended)\nDevices online: {d.OnlineDevices}/{d.RegisteredDevices}\n" +
               $"Unresolved errors: {d.UnresolvedErrors} ({d.CriticalUnresolvedErrors} critical)\nPending payments: {d.PendingManualPayments}\nNew tickets (7d): {d.NewSupportTickets}";
    }

    private async Task<string> HealthAsync(CancellationToken ct)
    {
        var h = await platform.HealthAsync(OwnerDashboardService.ServerVersion, ct);
        var sb = new StringBuilder();
        sb.AppendLine($"Health: {h.Status}");
        sb.AppendLine($"Database: {(h.DatabaseReachable ? "reachable" : "UNREACHABLE")}");
        sb.AppendLine($"Pending migrations: {h.PendingMigrations.Count}");
        foreach (var w in h.Workers.OrderBy(w => w.Name)) sb.AppendLine($"Job {w.Name}: {w.LastStatus}{(w.Stale ? " (stale)" : "")} at {w.LastRunAt:yyyy-MM-dd HH:mm}Z");
        sb.Append(h.LastBackup is null ? "Last backup: none" : $"Last backup: {h.LastBackup.Status} at {h.LastBackup.StartedAt:yyyy-MM-dd HH:mm}Z");
        return sb.ToString();
    }

    private async Task<string> StatsAsync(CancellationToken ct)
    {
        var d = await dashboard.GetAsync(null, ct);
        return $"Tenants: {d.TotalTenants} (active {d.ActiveTenants}, suspended {d.SuspendedTenants})\nCustomers (all tenants): {d.TotalCustomers}\n" +
               $"Subscriptions: active {d.ActiveSubscriptions}, trial {d.TrialingSubscriptions}, past due {d.PastDueSubscriptions}, expired {d.ExpiredSubscriptions}\n" +
               $"Licenses: active {d.ActiveLicenses}, suspended {d.SuspendedLicenses}, expiring 14d {d.ExpiringLicenses14d}\n" +
               $"Devices: {d.RegisteredDevices} (online {d.OnlineDevices}; android {d.AndroidInstallations}, windows {d.WindowsInstallations})\n" +
               $"MRR: {d.MonthlyRecurringRevenue:0.00}\nPending payments: {d.PendingManualPayments} ({d.PendingManualPaymentsAmount:0.00})\n" +
               $"Support: {d.OpenSupportTickets} open, {d.NewSupportTickets} new\nErrors: {d.UnresolvedErrors} unresolved\nFailed sync ops (24h): {d.FailedSyncOperations24h}";
    }

    private async Task<string> AlertsAsync(CancellationToken ct)
    {
        var since = clock.UtcNow.AddDays(-7);
        var rows = await db.Set<TelegramOutboxMessage>().AsNoTracking().Where(m => m.CreatedAt >= since && m.Type != AlertTypes.Test)
            .OrderByDescending(m => m.CreatedAt).Take(100).ToListAsync(ct);
        var alerts = rows.GroupBy(r => r.AlertId).Select(g => g.First()).Take(10).ToList();
        if (alerts.Count == 0) return "No alerts in the last 7 days.";
        return "Recent alerts:\n" + string.Join("\n", alerts.Select(a => $"{a.CreatedAt:MM-dd HH:mm}Z {a.Severity} - {a.Text.Split('\n')[0]}"));
    }

    private async Task<string> SubscriptionsAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var subs = await db.Set<Subscription>().IgnoreQueryFilters().AsNoTracking().Select(s => new { s.Status, s.CurrentPeriodEnd }).ToListAsync(ct);
        var expiring = subs.Count(s => s.Status is SubscriptionStatus.Active or SubscriptionStatus.Trialing && s.CurrentPeriodEnd > now && s.CurrentPeriodEnd <= now.AddDays(7));
        var pending = await db.Set<BillingInvoice>().IgnoreQueryFilters().CountAsync(i => i.Status == BillingInvoiceStatus.Issued, ct);
        return $"Subscriptions\nActive: {subs.Count(s => s.Status == SubscriptionStatus.Active)}\nTrialing: {subs.Count(s => s.Status == SubscriptionStatus.Trialing)}\n" +
               $"Past due: {subs.Count(s => s.Status == SubscriptionStatus.PastDue)}\nExpired: {subs.Count(s => s.Status == SubscriptionStatus.Expired)}\n" +
               $"Cancelled: {subs.Count(s => s.Status == SubscriptionStatus.Cancelled)}\nExpiring within 7 days: {expiring}\nInvoices awaiting payment: {pending}";
    }
}

/// <summary>Bot status, delivery log and test messages for the Admin app.</summary>
public sealed class TelegramAdminService(DbContext db, TelegramOptions options, OutboxAlertPublisher publisher, IAuditWriter audit, IClock clock)
{
    public async Task<TelegramStatusDto> StatusAsync(CancellationToken ct)
    {
        var since = clock.UtcNow.AddHours(-24);
        var set = db.Set<TelegramOutboxMessage>().AsNoTracking();
        var lastSent = await set.Where(m => m.SentAt != null).MaxAsync(m => (DateTimeOffset?)m.SentAt, ct);
        var lastError = await set.Where(m => m.LastError != null).OrderByDescending(m => m.CreatedAt).Select(m => m.LastError).FirstOrDefaultAsync(ct);
        return new TelegramStatusDto(options.Enabled, !string.IsNullOrWhiteSpace(options.BotToken), !string.IsNullOrWhiteSpace(options.WebhookSecret),
            options.AllowedChatIds.Distinct().Count(), await set.CountAsync(m => m.Status == OutboxStatuses.Pending, ct),
            await set.CountAsync(m => m.Status == OutboxStatuses.Failed, ct), await set.CountAsync(m => m.Status == OutboxStatuses.Sent && m.SentAt >= since, ct),
            await set.CountAsync(m => m.Status == OutboxStatuses.Skipped && m.CreatedAt >= since, ct), lastSent, lastError);
    }

    public async Task<PagedResult<TelegramDeliveryDto>> DeliveriesAsync(PageQuery q, string? status, string? type, CancellationToken ct)
    {
        var query = db.Set<TelegramOutboxMessage>().AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(m => m.Status == status);
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(m => m.Type == type);
        var page = await query.OrderByDescending(m => m.CreatedAt).ToPagedAsync(q, ct);
        return new PagedResult<TelegramDeliveryDto>(page.Items.Select(m => new TelegramDeliveryDto(m.Id, m.Type, m.Severity, m.DedupKey, m.ChatId, m.Text, m.Status,
            m.Attempts, m.CreatedAt, m.NextAttemptAt, m.SentAt, m.LastError)).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<TelegramTestMessageResponse> SendTestAsync(TelegramTestMessageRequest req, CancellationToken ct)
    {
        new Validator().MaxLength(req.Text, "text", 500).ThrowIfInvalid();
        if (!options.CanSend) throw new DomainException("telegram.not_configured", "Telegram is disabled or has no bot token / allowed chat configured.", 409);
        var queued = await publisher.EnqueueAsync(db, new AlertMessage(AlertTypes.Test, "Test message",
            string.IsNullOrWhiteSpace(req.Text) ? $"VELTRIX Admin test message at {clock.UtcNow:yyyy-MM-dd HH:mm:ss}Z." : req.Text), dedup: false, ct);
        audit.Write("platform.telegram_test", "Telegram", null, new { queued });
        await db.SaveChangesAsync(ct);
        return new TelegramTestMessageResponse(queued);
    }

    /// <summary>Retention: delete delivered/skipped/failed rows older than the given number of days.</summary>
    public Task<int> PurgeAsync(int days, CancellationToken ct)
    {
        var cutoff = clock.UtcNow.AddDays(-Math.Clamp(days, 1, 3650));
        return db.Set<TelegramOutboxMessage>().Where(m => m.Status != OutboxStatuses.Pending && m.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
    }
}

public sealed class TelegramModule : IModule
{
    public const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";
    public string Name => "telegram";

    public void Register(IServiceCollection services)
    {
        services.AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection(TelegramOptions.Section).Get<TelegramOptions>() ?? new TelegramOptions());
        // No logging handlers: the default HttpClient logs would write the request URL, which contains the bot token.
        services.AddHttpClient(TelegramClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15)).RemoveAllLoggers();
        services.AddSingleton<TelegramClient>();
        services.AddSingleton<TelegramRateLimiter>();
        services.AddSingleton<OutboxAlertPublisher>();
        services.AddSingleton<IAlertPublisher>(sp => sp.GetRequiredService<OutboxAlertPublisher>());
        services.AddScoped<TelegramDispatcher>();
        services.AddScoped<TelegramCommandService>();
        services.AddScoped<TelegramAdminService>();
        services.AddHostedService<TelegramSenderService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/telegram/webhook", HandleWebhookAsync).WithTags("Telegram").AllowAnonymous().DisableAntiforgery();

        var p = app.MapGroup("/api/v1/platform/telegram").WithTags("Platform").RequirePermission(Permissions.PlatformConfig);
        p.MapGet("/status", (TelegramAdminService s, CancellationToken ct) => s.StatusAsync(ct));
        p.MapGet("/deliveries", ([AsParameters] PageQuery q, string? status, string? type, TelegramAdminService s, CancellationToken ct) => s.DeliveriesAsync(q, status, type, ct));
        p.MapPost("/test", (TelegramTestMessageRequest r, TelegramAdminService s, CancellationToken ct) => s.SendTestAsync(r, ct)).RequireStepUp();
    }

    public static bool SecretMatches(string? provided, string expected)
    {
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected)) return false;
        // Hash first so the comparison is constant-time regardless of length.
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>Telegram webhook. Rejects requests without the configured secret header; silently ignores chats that are not allowed;
    /// answers read-only commands inline (webhook reply with method=sendMessage).</summary>
    private static async Task<IResult> HandleWebhookAsync(HttpContext http, TelegramOptions options, TelegramCommandService commands, ILoggerFactory loggers,
        CancellationToken ct)
    {
        var logger = loggers.CreateLogger("Telegram.Webhook");
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.WebhookSecret)) return Results.NotFound();
        if (!SecretMatches(http.Request.Headers[SecretHeader].FirstOrDefault(), options.WebhookSecret))
        {
            logger.LogWarning("Telegram webhook rejected: invalid secret token from {Ip}", http.Connection.RemoteIpAddress);
            return Results.Unauthorized();
        }
        if (http.Request.ContentLength is > 65536) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        JsonElement update;
        try { update = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body, cancellationToken: ct); }
        catch (JsonException) { return Results.BadRequest(); }

        if (update.ValueKind != JsonValueKind.Object || !update.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("chat", out var chat) || !chat.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var chatId))
            return Results.Ok();
        if (!options.AllowedChatIds.Contains(chatId))
        {
            logger.LogWarning("Telegram webhook: ignored message from non-allowed chat {ChatId}", chatId);
            return Results.Ok();
        }
        if (!message.TryGetProperty("text", out var textEl) || textEl.GetString() is not { Length: > 0 } text || !text.StartsWith('/'))
            return Results.Ok();
        var reply = await commands.ExecuteAsync(text.Length > 200 ? text[..200] : text, ct);
        return Results.Json(new { method = "sendMessage", chat_id = chatId, text = reply, disable_web_page_preview = true });
    }
}
