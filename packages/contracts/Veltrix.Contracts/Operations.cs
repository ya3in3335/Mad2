using Veltrix.Contracts.Licensing;
using Veltrix.Contracts.Platform;
using Veltrix.Contracts.Subscriptions;
using Veltrix.Contracts.Tenants;

namespace Veltrix.Contracts.Operations;

// ---------- Device telemetry (client side) ----------

/// <summary>Periodic client heartbeat. Sent with a session bound to an activated device; InstallationId must match that device.</summary>
public sealed record DeviceHeartbeatRequest(Guid InstallationId, string Platform, string AppVersion, string? OsVersion, DateTimeOffset? LastSyncAt,
    int? PendingSyncOperations, int? FailedSyncOperations);

/// <summary>DiagnosticRequests is only populated when the tenant granted remote-diagnostics consent.</summary>
public sealed record DeviceHeartbeatResponse(DateTimeOffset ServerTime, int NextHeartbeatSeconds, bool DiagnosticsConsent,
    IReadOnlyList<PendingDiagnosticRequestDto> DiagnosticRequests);

public sealed record PendingDiagnosticRequestDto(Guid Id, DateTimeOffset RequestedAt, DateTimeOffset ExpiresAt, string? Reason);

/// <summary>Sanitized diagnostic report uploaded by a device in answer to a request. Facts are key/value pairs (e.g. "db.size", "printer.status");
/// no remote command is ever executed — the client only reports pre-defined, non-sensitive state.</summary>
public sealed record DiagnosticReportUploadRequest(string AppVersion, string? OsVersion, IReadOnlyDictionary<string, string>? Facts, IReadOnlyList<string>? RecentLogLines);

// ---------- Device fleet (platform admin) ----------

/// <summary>Device as seen by the vendor. Never contains license keys, key hashes or device key material.</summary>
public sealed record AdminDeviceDto(Guid Id, Guid TenantId, string? TenantName, Guid LicenseId, string Platform, string DeviceName, string AppVersion, string? OsVersion,
    string Status, bool Online, DateTimeOffset ActivatedAt, DateTimeOffset LastContactAt, DateTimeOffset? LastHeartbeatAt, DateTimeOffset? LastSyncAt,
    int? PendingSyncOperations, int? FailedSyncOperations);

public sealed record AdminDeviceDetailDto(AdminDeviceDto Device, string? LicensePlan, string? LicenseType, string? LicenseStatus, DateTimeOffset? RevokedAt, string? RevokedReason,
    Guid? ReplacedByDeviceId, int ReportedErrors, int OpenIncidents, IReadOnlyList<LicenseEventDto> History, IReadOnlyList<DiagnosticRequestDto> DiagnosticRequests,
    bool TenantDiagnosticsConsent);

public sealed record DeviceActionRequest(string Reason);
public sealed record ReassignDeviceRequest(Guid TargetLicenseId, string Reason);

// ---------- Error reporting / incidents ----------

/// <summary>Client error report. Category: crash, api, sync, license, migration, backup, device_registration, version_incompat, auth_failures, outage.
/// Severity: info, warning, error, critical. Text fields are re-sanitized and truncated by the server.</summary>
public sealed record ErrorReportItem(string App, string AppVersion, string Platform, string? OsVersion, string Category, string Severity, string ErrorType,
    string Message, string? StackTrace, IReadOnlyDictionary<string, string>? Context, DateTimeOffset? OccurredAt);

public sealed record ErrorReportBatchRequest(IReadOnlyList<ErrorReportItem> Items);
public sealed record ErrorReportBatchResponse(int Accepted, int Rejected, IReadOnlyList<Guid> IncidentIds);

public sealed record IncidentDto(Guid Id, string Fingerprint, Guid? TenantId, string? TenantName, string Source, string App, string AppVersion, string Platform,
    string Category, string ErrorType, string Severity, string Status, string Title, int OccurrenceCount, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt,
    Guid? LastDeviceId);

public sealed record IncidentDetailDto(IncidentDto Incident, string? SampleStackTrace, string? StatusNote, DateTimeOffset? AcknowledgedAt, DateTimeOffset? ResolvedAt,
    IReadOnlyList<ErrorEventDto> RecentEvents, int AffectedDevices);

public sealed record ErrorEventDto(Guid Id, Guid? DeviceId, string? OsVersion, string Message, string? Context, DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt);

/// <summary>Cross-tenant grouping of incidents sharing a fingerprint.</summary>
public sealed record IncidentGroupDto(string Fingerprint, string Category, string ErrorType, string Title, string App, string MaxSeverity, int Incidents, int Tenants,
    int Occurrences, int OpenIncidents, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt);

/// <summary>Status: open, acknowledged, resolved.</summary>
public sealed record UpdateIncidentStatusRequest(string Status, string? Note);

// ---------- Remote diagnostics ----------

public sealed record CreateDiagnosticRequestRequest(string? Reason, int? ExpiresInHours);

/// <summary>Status: pending, delivered, uploaded, expired, cancelled.</summary>
public sealed record DiagnosticRequestDto(Guid Id, Guid DeviceId, Guid TenantId, string Status, string? Reason, DateTimeOffset RequestedAt, DateTimeOffset ExpiresAt,
    DateTimeOffset? DeliveredAt, DateTimeOffset? UploadedAt, bool ConsentGranted);

public sealed record DiagnosticReportDto(DiagnosticRequestDto Request, string? AppVersion, string? OsVersion, IReadOnlyDictionary<string, string> Facts,
    IReadOnlyList<string> RecentLogLines);

// ---------- Owner dashboard ----------

public sealed record OwnerDashboardDto(
    int TotalTenants, int ActiveTenants, int SuspendedTenants, int TotalCustomers,
    int ActiveSubscriptions, int TrialingSubscriptions, int PastDueSubscriptions, int ExpiredSubscriptions,
    int ActiveLicenses, int SuspendedLicenses, int ExpiringLicenses14d,
    int RegisteredDevices, int OnlineDevices, int OnlineWindowMinutes, int AndroidInstallations, int WindowsInstallations,
    decimal MonthlyRecurringRevenue, int PendingManualPayments, decimal PendingManualPaymentsAmount,
    int NewSupportTickets, int OpenSupportTickets, int UnresolvedErrors, int CriticalUnresolvedErrors,
    int FailedSyncOperations24h, int DeviceReportedFailedSyncOperations, string BackendHealth,
    IReadOnlyList<AppReleaseDto> LatestReleases, DateTimeOffset GeneratedAt);

// ---------- Tenant 360 ----------

public sealed record TenantSupportCaseSummaryDto(Guid Id, string Subject, string Status, string Priority, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record TenantOverviewDto(CompanyDto Company, string? SuspendedReason, SubscriptionDto? Subscription, IReadOnlyList<LicenseDto> Licenses,
    IReadOnlyList<AdminDeviceDto> Devices, IReadOnlyList<LicenseEventDto> ActivationHistory, IReadOnlyList<BillingInvoiceDto> Invoices,
    IReadOnlyList<TenantSupportCaseSummaryDto> SupportCases, int Users, int Customers, int OpenIncidents, bool DiagnosticsConsent);

// ---------- Licensing admin additions ----------

/// <summary>Issue a time-limited demo (trial plan) license.</summary>
public sealed record IssueDemoLicenseRequest(Guid TenantId, int Days, int MaxDevices, string? Notes);
public sealed record ChangeLicenseSeatsRequest(int MaxDevices, string Reason);

/// <summary>Failed activation attempt (no key material is ever stored: only the last 4 characters of a recognized license).</summary>
public sealed record ActivationFailureDto(Guid Id, Guid? TenantId, Guid? LicenseId, string Code, string? Platform, Guid? InstallationId, string? IpAddress,
    DateTimeOffset OccurredAt);
