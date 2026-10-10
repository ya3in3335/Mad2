namespace Veltrix.SharedKernel;

public enum AlertSeverity { Info, Warning, Critical }

/// <summary>An owner/vendor operational alert (e.g. delivered to the Telegram bot). The text must never contain
/// credentials, keys, tokens, emails or payment data; publishers re-sanitize it anyway.</summary>
/// <param name="Type">Stable alert type, see <see cref="AlertTypes"/>.</param>
/// <param name="DedupKey">Alerts with the same key within <paramref name="DedupWindow"/> (or the configured default) are dropped.</param>
public sealed record AlertMessage(
    string Type, string Title, string Text, AlertSeverity Severity = AlertSeverity.Info, string? DedupKey = null, Guid? TenantId = null,
    TimeSpan? DedupWindow = null);

/// <summary>Publishes owner alerts to an outbox. Implementations persist independently of the caller's unit of work
/// (call it after the business change is saved) and never throw: alerting must not break business flows.</summary>
public interface IAlertPublisher
{
    Task PublishAsync(AlertMessage alert, CancellationToken ct = default);
}

public static class AlertTypes
{
    public const string TenantRegistered = "tenant.registered";
    public const string TenantOnboarded = "tenant.onboarded";
    public const string DeviceActivated = "device.activated";
    public const string LicenseActivationFailed = "license.activation_failed";
    public const string SubscriptionExpiring = "subscription.expiring";
    public const string SubscriptionExpired = "subscription.expired";
    public const string PaymentAwaiting = "payment.awaiting_confirmation";
    public const string BackendOutage = "backend.outage";
    public const string RepeatedCrashes = "diagnostics.repeated_crashes";
    public const string SyncFailures = "sync.failures";
    public const string BackupFailed = "backup.failed";
    public const string SupportTicket = "support.ticket";
    public const string SecurityAlert = "security.alert";
    public const string ReleasePublished = "release.published";
    public const string Test = "test";
}

/// <summary>Records server-side operational events (backup failures, repeated failed logins, sync failures...) into the
/// diagnostics incident store. Persists independently of the caller's unit of work and never throws.</summary>
public interface IDiagnosticsRecorder
{
    Task RecordAsync(ServerDiagnosticEvent e, CancellationToken ct = default);
}

/// <param name="Category">One of <see cref="DiagnosticCategories"/>.</param>
/// <param name="Severity">info, warning, error or critical.</param>
public sealed record ServerDiagnosticEvent(string Category, string Severity, string ErrorType, string Message, Guid? TenantId = null, Guid? DeviceId = null);

public static class DiagnosticCategories
{
    public const string Crash = "crash";
    public const string Api = "api";
    public const string Sync = "sync";
    public const string License = "license";
    public const string Migration = "migration";
    public const string Backup = "backup";
    public const string DeviceRegistration = "device_registration";
    public const string VersionIncompat = "version_incompat";
    public const string AuthFailures = "auth_failures";
    public const string Outage = "outage";

    public static readonly IReadOnlyList<string> All = [Crash, Api, Sync, License, Migration, Backup, DeviceRegistration, VersionIncompat, AuthFailures, Outage];
}

public static class DiagnosticSeverities
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";
    public const string Critical = "critical";

    public static readonly IReadOnlyList<string> All = [Info, Warning, Error, Critical];
    public static int Rank(string s) => s switch { Critical => 3, Error => 2, Warning => 1, _ => 0 };
}
