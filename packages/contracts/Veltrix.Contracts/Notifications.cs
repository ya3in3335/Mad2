namespace Veltrix.Contracts.Notifications;

public sealed record NotificationDto(Guid Id, string Type, string Title, string Body, string Severity, string? Link, DateTimeOffset CreatedAt, bool Read);
public sealed record UnreadCountDto(int Unread);
public sealed record NotificationPreferenceDto(string Type, bool InApp, bool Email, bool Push);
public sealed record RegisterPushTokenRequest(string Platform, string Token, Guid? DeviceId);
