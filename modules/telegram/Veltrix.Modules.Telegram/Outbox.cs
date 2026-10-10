using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Telegram;

public sealed class TelegramOptions
{
    public const string Section = "Telegram";
    public bool Enabled { get; set; }
    /// <summary>Bot token. Supply ONLY via environment/secret store (Telegram__BotToken); never commit it to appsettings.</summary>
    public string BotToken { get; set; } = "";
    /// <summary>Owner chats allowed to receive alerts and to use bot commands.</summary>
    public long[] AllowedChatIds { get; set; } = [];
    /// <summary>Value Telegram echoes in X-Telegram-Bot-Api-Secret-Token (set via setWebhook secret_token). Webhook is disabled when empty.</summary>
    public string WebhookSecret { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "https://api.telegram.org";
    public int DedupWindowMinutes { get; set; } = 60;
    public int MaxMessagesPerMinute { get; set; } = 20;
    public int PerChatIntervalMilliseconds { get; set; } = 1000;
    public int MaxAttempts { get; set; } = 8;
    public int DispatchIntervalSeconds { get; set; } = 2;

    public bool CanSend => Enabled && !string.IsNullOrWhiteSpace(BotToken) && AllowedChatIds.Length > 0;
}

public static class OutboxStatuses
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Failed = "failed";
    /// <summary>Recorded but not deliverable (bot disabled or no chat configured). Kept as alert history.</summary>
    public const string Skipped = "skipped";
}

/// <summary>One alert delivery to one chat. Rows of the same alert share <see cref="AlertId"/>. Text is sanitized, never contains secrets.</summary>
public sealed class TelegramOutboxMessage
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AlertId { get; set; }
    public string Type { get; set; } = "";
    public string Severity { get; set; } = "";
    public string? DedupKey { get; set; }
    public Guid? TenantId { get; set; }
    public long? ChatId { get; set; }
    public string Text { get; set; } = "";
    public string Status { get; set; } = OutboxStatuses.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class TelegramOutboxConfig : IEntityTypeConfiguration<TelegramOutboxMessage>
{
    public void Configure(EntityTypeBuilder<TelegramOutboxMessage> b)
    {
        b.ToTable("outbox", "telegram");
        b.HasIndex(x => new { x.Status, x.NextAttemptAt });
        b.HasIndex(x => new { x.DedupKey, x.CreatedAt });
        b.HasIndex(x => x.CreatedAt);
        b.Property(x => x.Type).HasMaxLength(60);
        b.Property(x => x.Severity).HasMaxLength(10);
        b.Property(x => x.DedupKey).HasMaxLength(200);
        b.Property(x => x.Text).HasMaxLength(4000);
        b.Property(x => x.Status).HasMaxLength(10);
        b.Property(x => x.LastError).HasMaxLength(1000);
    }
}

/// <summary>Writes alerts to the outbox in a dedicated scope (independent of the caller's transaction), with deduplication.
/// Never throws: alerting must not break business flows.</summary>
public sealed class OutboxAlertPublisher(IServiceScopeFactory scopes, TelegramOptions options, IClock clock, ILogger<OutboxAlertPublisher> logger) : IAlertPublisher
{
    public const int MaxTextLength = 3500;

    public async Task PublishAsync(AlertMessage alert, CancellationToken ct = default)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DbContext>();
            await EnqueueAsync(db, alert, dedup: true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not enqueue alert {Type}", alert.Type);
        }
    }

    /// <summary>Stage rows (one per allowed chat) and save. Returns the number of deliverable rows queued (0 when deduplicated/skipped).</summary>
    public async Task<int> EnqueueAsync(DbContext db, AlertMessage alert, bool dedup, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var key = alert.DedupKey is null ? null : Truncate(alert.DedupKey, 200);
        if (dedup && key is not null)
        {
            var since = now - (alert.DedupWindow ?? TimeSpan.FromMinutes(Math.Max(1, options.DedupWindowMinutes)));
            if (await db.Set<TelegramOutboxMessage>().AnyAsync(m => m.DedupKey == key && m.CreatedAt >= since, ct)) return 0;
        }
        var text = Format(alert);
        var alertId = Guid.CreateVersion7();
        var chats = options.CanSend ? options.AllowedChatIds.Distinct().ToArray() : [];
        if (chats.Length == 0)
            db.Add(Row(alertId, alert, key, null, text, OutboxStatuses.Skipped, now));
        foreach (var chat in chats) db.Add(Row(alertId, alert, key, chat, text, OutboxStatuses.Pending, now));
        await db.SaveChangesAsync(ct);
        return chats.Length;
    }

    private static TelegramOutboxMessage Row(Guid alertId, AlertMessage a, string? key, long? chat, string text, string status, DateTimeOffset now) => new()
    {
        AlertId = alertId, Type = Truncate(a.Type, 60), Severity = a.Severity.ToString().ToLowerInvariant(), DedupKey = key, TenantId = a.TenantId, ChatId = chat,
        Text = text, Status = status, CreatedAt = now, NextAttemptAt = status == OutboxStatuses.Pending ? now : null,
    };

    public static string Format(AlertMessage a)
    {
        var prefix = a.Severity switch { AlertSeverity.Critical => "[CRITICAL] ", AlertSeverity.Warning => "[WARNING] ", _ => "" };
        var title = Redaction.Sanitize(a.Title, 200);
        var body = Redaction.Sanitize(a.Text, MaxTextLength);
        var text = $"{prefix}{title}\n{body}";
        return text.Length > MaxTextLength ? text[..MaxTextLength] : text;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
