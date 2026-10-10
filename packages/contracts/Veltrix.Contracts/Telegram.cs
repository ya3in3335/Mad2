namespace Veltrix.Contracts.Telegram;

/// <summary>Bot configuration state. The bot token itself is never returned; only whether one is configured.</summary>
public sealed record TelegramStatusDto(bool Enabled, bool BotTokenConfigured, bool WebhookSecretConfigured, int AllowedChats, int Pending, int Failed,
    int Sent24h, int Skipped24h, DateTimeOffset? LastSentAt, string? LastError);

/// <summary>Status: pending, sent, failed, skipped.</summary>
public sealed record TelegramDeliveryDto(Guid Id, string Type, string Severity, string? DedupKey, long? ChatId, string Text, string Status, int Attempts,
    DateTimeOffset CreatedAt, DateTimeOffset? NextAttemptAt, DateTimeOffset? SentAt, string? LastError);

public sealed record TelegramTestMessageRequest(string? Text);
public sealed record TelegramTestMessageResponse(int Queued);
