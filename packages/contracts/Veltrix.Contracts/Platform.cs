namespace Veltrix.Contracts.Platform;

public sealed record PlatformDashboardDto(int Tenants, int ActiveTenants, int SuspendedTenants, int PendingSetup, int ActiveLicenses, int ExpiringLicenses14d,
    int ActiveDevices, int Activations30d, decimal MonthlyRecurringRevenue, decimal RevenueThisMonth, IReadOnlyList<MonthValue> RevenueByMonth,
    IReadOnlyList<MonthValue> ActivationsByMonth, int OpenSupportCases, int SecurityAlerts7d);
public sealed record MonthValue(string Month, decimal Value);

public sealed record PlatformAdminDto(Guid Id, string Email, string FullName, bool IsActive, bool MfaEnabled, DateTimeOffset? LastLoginAt);
public sealed record CreatePlatformAdminRequest(string Email, string FullName, string Password);

public sealed record SupportCaseDto(Guid Id, Guid? TenantId, string? TenantName, string Subject, string Status, string Priority, Guid CreatedBy,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<SupportMessageDto> Messages);
public sealed record SupportMessageDto(Guid Id, Guid AuthorId, bool FromVendor, string Body, DateTimeOffset CreatedAt);
public sealed record CreateSupportCaseRequest(string Subject, string Priority, string Body);
public sealed record ReplySupportCaseRequest(string Body, string? Status);

public sealed record AuditLogDto(Guid Id, Guid? TenantId, Guid? UserId, Guid? DeviceId, string Action, string EntityType, string? EntityId, string? Details,
    string? IpAddress, string? CorrelationId, DateTimeOffset OccurredAt);
public sealed record SecurityAlertDto(string Kind, string Severity, Guid? TenantId, Guid? UserId, string Description, DateTimeOffset OccurredAt);
public sealed record SystemHealthDto(string Status, bool DatabaseReachable, IReadOnlyList<string> PendingMigrations, IReadOnlyList<WorkerStatusDto> Workers,
    BackupRecordDto? LastBackup, string Version, DateTimeOffset ServerTime);
public sealed record WorkerStatusDto(string Name, DateTimeOffset LastRunAt, string LastStatus, string? LastError, bool Stale);
public sealed record BackupRecordDto(Guid Id, string FileName, long SizeBytes, string Sha256, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string Status,
    string? Error, bool Verified, DateTimeOffset? VerifiedAt, string? VerificationDetails);

public sealed record AppReleaseDto(Guid Id, string Platform, string Version, string Channel, string MinSupportedVersion, string? ReleaseNotes, string? DownloadUrl,
    string? Sha256, bool IsActive, DateTimeOffset PublishedAt);
public sealed record UpsertReleaseRequest(string Platform, string Version, string Channel, string MinSupportedVersion, string? ReleaseNotes, string? DownloadUrl,
    string? Sha256, bool IsActive);
public sealed record UpdateCheckDto(bool UpdateAvailable, bool Mandatory, AppReleaseDto? Latest, bool Supported);

public sealed record AnnouncementDto(Guid Id, string Title, string Body, string Severity, Guid? TenantId, DateTimeOffset StartsAt, DateTimeOffset? EndsAt, bool Published);
public sealed record UpsertAnnouncementRequest(string Title, string Body, string Severity, Guid? TenantId, DateTimeOffset StartsAt, DateTimeOffset? EndsAt, bool Published);

public sealed record PlatformSettingDto(string Key, string Value);
