using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Veltrix.Contracts.Reporting;
using Veltrix.Modules.Identity;
using Veltrix.Modules.Licensing;
using Veltrix.Modules.Notifications;
using Veltrix.Modules.Platform;
using Veltrix.Modules.Reporting;
using Veltrix.Modules.Subscriptions;
using Veltrix.Modules.Tenants;
using Veltrix.SharedKernel;

namespace Veltrix.Workers;

public interface IJob
{
    string Name { get; }
    TimeSpan Interval { get; }
    Task RunAsync(IServiceProvider scope, CancellationToken ct);
}

/// <summary>Single-node scheduler. Each job runs in its own DI scope; outcomes are recorded as heartbeats visible in the Control Center.
/// For multi-node deployments run workers on exactly one instance (Workers__Enabled=true) — see DEPLOYMENT.md.</summary>
public sealed class JobScheduler(IServiceProvider root, IEnumerable<IJob> jobs, ILogger<JobScheduler> logger, IClock clock) : BackgroundService
{
    private readonly Dictionary<string, DateTimeOffset> _lastRun = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var job in jobs)
            {
                if (_lastRun.TryGetValue(job.Name, out var last) && clock.UtcNow - last < job.Interval) continue;
                _lastRun[job.Name] = clock.UtcNow;
                await RunJobAsync(job, stoppingToken);
            }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    public async Task RunJobAsync(IJob job, CancellationToken ct)
    {
        string status = "ok"; string? error = null;
        try
        {
            using var scope = root.CreateScope();
            await job.RunAsync(scope.ServiceProvider, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            status = "failed"; error = ex.Message.Length > 1900 ? ex.Message[..1900] : ex.Message;
            logger.LogError(ex, "Job {Job} failed", job.Name);
        }
        try
        {
            using var scope = root.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DbContext>();
            var hb = await db.Set<WorkerHeartbeat>().SingleOrDefaultAsync(h => h.Name == job.Name, ct) ?? db.Add(new WorkerHeartbeat { Name = job.Name }).Entity;
            hb.LastRunAt = clock.UtcNow; hb.LastStatus = status; hb.LastError = error;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Could not record heartbeat for {Job}", job.Name); }
    }
}

public sealed class SubscriptionStatusJob : IJob
{
    public string Name => "subscription-status";
    public TimeSpan Interval => TimeSpan.FromHours(1);
    public Task RunAsync(IServiceProvider sp, CancellationToken ct) => sp.GetRequiredService<SubscriptionService>().RefreshStatusesAsync(ct);
}

/// <summary>Renewal reminders 14, 7 and 1 day(s) before license expiry (deduplicated per license and threshold).</summary>
public sealed class LicenseExpiryReminderJob : IJob
{
    public string Name => "license-expiry-reminders";
    public TimeSpan Interval => TimeSpan.FromHours(6);

    public async Task RunAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<DbContext>();
        var clock = sp.GetRequiredService<IClock>();
        var now = clock.UtcNow;
        var licenses = await db.Set<License>().IgnoreQueryFilters().Where(l => l.Status == LicenseStatus.Active && l.ExpiresAt != null && l.ExpiresAt > now && l.ExpiresAt <= now.AddDays(14)).ToListAsync(ct);
        foreach (var l in licenses)
        {
            var days = (int)Math.Ceiling((l.ExpiresAt!.Value - now).TotalDays);
            var bucket = days <= 1 ? 1 : days <= 7 ? 7 : 14;
            var key = $"license-expiry:{l.Id}:{bucket}";
            if (await db.Set<Notification>().IgnoreQueryFilters().AnyAsync(n => n.TenantId == l.TenantId && n.DedupeKey == key, ct)) continue;
            db.Add(new Notification
            {
                TenantId = l.TenantId, Type = "license.expiring", Title = "License expiring soon",
                Body = $"Your VELTRIX OS license expires on {l.ExpiresAt:yyyy-MM-dd} ({days} day(s)). Renew to avoid interruption. Your data is never deleted.",
                Severity = bucket == 1 ? NotificationSeverity.Critical : NotificationSeverity.Warning, RequiredPermission = Permissions.CompanyManage, DedupeKey = key,
            });
        }
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Delivers notifications by email to users who opted in, when SMTP is configured.</summary>
public sealed class EmailNotificationJob : IJob
{
    public string Name => "email-notifications";
    public TimeSpan Interval => TimeSpan.FromMinutes(2);

    public async Task RunAsync(IServiceProvider sp, CancellationToken ct)
    {
        var email = sp.GetRequiredService<IEmailSender>();
        var db = sp.GetRequiredService<DbContext>();
        var clock = sp.GetRequiredService<IClock>();
        var pending = await db.Set<Notification>().IgnoreQueryFilters().Where(n => n.EmailProcessedAt == null && n.CreatedAt > clock.UtcNow.AddDays(-1)).OrderBy(n => n.CreatedAt).Take(200).ToListAsync(ct);
        if (!email.IsConfigured)
        {
            foreach (var n in pending) n.EmailProcessedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            return;
        }
        foreach (var n in pending)
        {
            var prefs = await db.Set<NotificationPreference>().IgnoreQueryFilters().Where(p => p.TenantId == n.TenantId && p.Type == n.Type && p.Email).Select(p => p.UserId).ToListAsync(ct);
            if (prefs.Count > 0)
            {
                var users = await db.Set<User>().Include(u => u.Roles).ThenInclude(r => r.Role).ThenInclude(r => r!.Permissions)
                    .Where(u => prefs.Contains(u.Id) && u.TenantId == n.TenantId && u.IsActive).ToListAsync(ct);
                foreach (var u in users)
                {
                    var perms = AuthService.Resolve(u).Permissions;
                    var allowed = n.UserId == u.Id || n.UserId == null && (n.RequiredPermission is null || perms.Contains(Permissions.All) || perms.Contains(n.RequiredPermission));
                    if (allowed) await email.SendAsync(u.Email, "VELTRIX OS — " + n.Title, n.Body, ct);
                }
            }
            n.EmailProcessedAt = clock.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }
}

public sealed class ScheduledReportJob : IJob
{
    public string Name => "scheduled-reports";
    public TimeSpan Interval => TimeSpan.FromMinutes(15);

    public async Task RunAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<DbContext>();
        var clock = sp.GetRequiredService<IClock>();
        var email = sp.GetRequiredService<IEmailSender>();
        var ctx = sp.GetRequiredService<TenantContext>();
        var now = clock.UtcNow;
        var due = (await db.Set<ScheduledReport>().IgnoreQueryFilters().Where(r => r.IsActive).ToListAsync(ct)).Where(r => IsDue(r, now)).ToList();
        foreach (var r in due)
        {
            try
            {
                ctx.RunAs(r.TenantId, r.OwnerUserId);
                var (from, to) = Range(r.Frequency, now);
                var parameters = r.ParametersJson is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(r.ParametersJson) ?? new();
                Guid? G(string k) => parameters.TryGetValue(k, out var v) && Guid.TryParse(v, out var g) ? g : null;
                var table = await sp.GetRequiredService<ReportingService>().RunAsync(r.ReportKey, new ReportFilter(from, to, G("branchId"), G("cashierId"), G("categoryId"), G("employeeId"),
                    parameters.GetValueOrDefault("groupBy")), ct);
                var (content, type, ext) = ReportExporters.Export(table, r.Format);
                if (email.IsConfigured)
                    foreach (var to2 in r.Recipients.Split(';', StringSplitOptions.RemoveEmptyEntries))
                        await email.SendAsync(to2, $"VELTRIX OS — {table.Title} {from:yyyy-MM-dd}…{to:yyyy-MM-dd}", "Attached is your scheduled report.", [($"{r.ReportKey}.{ext}", type, content)], ct);
                r.LastStatus = email.IsConfigured ? "sent" : "generated (SMTP not configured)";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                r.LastStatus = "failed: " + (ex.Message.Length > 250 ? ex.Message[..250] : ex.Message);
            }
            r.LastRunAt = now;
            ctx.TenantId = null;
            await db.SaveChangesAsync(ct);
        }
    }

    public static bool IsDue(ScheduledReport r, DateTimeOffset now) => r.Frequency switch
    {
        "weekly" => now.DayOfWeek == DayOfWeek.Sunday && (r.LastRunAt is null || now - r.LastRunAt > TimeSpan.FromDays(6)),
        "monthly" => now.Day == 1 && (r.LastRunAt is null || now - r.LastRunAt > TimeSpan.FromDays(25)),
        _ => r.LastRunAt is null || now - r.LastRunAt > TimeSpan.FromHours(23),
    };

    public static (DateOnly From, DateOnly To) Range(string frequency, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        return frequency switch
        {
            "weekly" => (today.AddDays(-7), today.AddDays(-1)),
            "monthly" => (new DateOnly(today.Year, today.Month, 1).AddMonths(-1), new DateOnly(today.Year, today.Month, 1).AddDays(-1)),
            _ => (today.AddDays(-1), today.AddDays(-1)),
        };
    }
}

public sealed class CleanupJob : IJob
{
    public string Name => "cleanup";
    public TimeSpan Interval => TimeSpan.FromHours(24);

    public async Task RunAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<DbContext>();
        var now = sp.GetRequiredService<IClock>().UtcNow;
        await db.Set<ActivationChallenge>().Where(c => c.ExpiresAt < now.AddDays(-1)).ExecuteDeleteAsync(ct);
        await db.Set<RefreshToken>().Where(t => t.ExpiresAt < now.AddDays(-30)).ExecuteDeleteAsync(ct);
        await db.Set<PasswordResetToken>().Where(t => t.ExpiresAt < now.AddDays(-7)).ExecuteDeleteAsync(ct);
        // Idempotency records are kept for a year so long-offline devices can safely replay.
        await db.Set<IdempotencyRecord>().IgnoreQueryFilters().Where(r => r.CreatedAt < now.AddDays(-365)).ExecuteDeleteAsync(ct);
    }
}

/// <summary>Retention for monitoring data: resolved incidents/events and finished diagnostic requests older than Diagnostics:RetentionDays
/// (default 90), and delivered Telegram outbox rows older than the same period.</summary>
public sealed class DiagnosticsRetentionJob : IJob
{
    public string Name => "diagnostics-retention";
    public TimeSpan Interval => TimeSpan.FromHours(24);

    public async Task RunAsync(IServiceProvider sp, CancellationToken ct)
    {
        var options = sp.GetRequiredService<Veltrix.Modules.Operations.OperationsOptions>();
        await sp.GetRequiredService<Veltrix.Modules.Operations.DiagnosticsService>().PurgeAsync(options.RetentionDays, ct);
        await sp.GetRequiredService<Veltrix.Modules.Telegram.TelegramAdminService>().PurgeAsync(options.RetentionDays, ct);
    }
}

/// <summary>Daily owner alerts for subscriptions expiring within 7 days and subscriptions that expired (deduplicated per period).</summary>
public sealed class SubscriptionAlertJob : IJob
{
    public string Name => "subscription-alerts";
    public TimeSpan Interval => TimeSpan.FromHours(24);

    public async Task RunAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<DbContext>();
        var alerts = sp.GetRequiredService<IAlertPublisher>();
        var now = sp.GetRequiredService<IClock>().UtcNow;
        var subs = await db.Set<Subscription>().IgnoreQueryFilters().AsNoTracking()
            .Where(s => ((s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing) && s.CurrentPeriodEnd > now && s.CurrentPeriodEnd <= now.AddDays(7))
                        || (s.Status == SubscriptionStatus.Expired && s.CurrentPeriodEnd > now.AddDays(-30)))
            .ToListAsync(ct);
        var ids = subs.Select(s => s.TenantId).Distinct().ToList();
        var names = await db.Set<Company>().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        foreach (var s in subs)
        {
            var name = names.GetValueOrDefault(s.TenantId) ?? "A tenant";
            if (s.Status == SubscriptionStatus.Expired)
                await alerts.PublishAsync(new AlertMessage(AlertTypes.SubscriptionExpired, "Subscription expired",
                    $"{name}: {s.PlanCode} subscription expired on {s.CurrentPeriodEnd:yyyy-MM-dd}. The tenant is read-only until renewal.",
                    AlertSeverity.Warning, $"sub-expired:{s.Id}:{s.CurrentPeriodEnd:yyyyMMdd}", s.TenantId, TimeSpan.FromDays(60)), ct);
            else
            {
                var days = (int)Math.Ceiling((s.CurrentPeriodEnd - now).TotalDays);
                await alerts.PublishAsync(new AlertMessage(AlertTypes.SubscriptionExpiring, "Subscription expiring soon",
                    $"{name}: {s.PlanCode} {(s.Status == SubscriptionStatus.Trialing ? "trial" : "subscription")} ends on {s.CurrentPeriodEnd:yyyy-MM-dd} ({days} day(s)).",
                    days <= 1 ? AlertSeverity.Warning : AlertSeverity.Info, $"sub-expiring:{s.Id}:{s.CurrentPeriodEnd:yyyyMMdd}", s.TenantId, TimeSpan.FromDays(30)), ct);
            }
        }
    }
}

/// <summary>Backend health check every few minutes. Degraded/down states raise an owner alert; if the database itself is unreachable the alert
/// bypasses the (database) outbox and is sent directly, at most once per hour.</summary>
public sealed class HealthMonitorJob : IJob
{
    private static DateTimeOffset _lastDirectAlert = DateTimeOffset.MinValue;
    public string Name => "health-monitor";
    public TimeSpan Interval => TimeSpan.FromMinutes(5);

    public async Task RunAsync(IServiceProvider sp, CancellationToken ct)
    {
        var clock = sp.GetRequiredService<IClock>();
        Veltrix.Contracts.Platform.SystemHealthDto health;
        try
        {
            health = await sp.GetRequiredService<PlatformService>().HealthAsync(Veltrix.Modules.Operations.OwnerDashboardService.ServerVersion, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await DirectAsync(sp, clock, "[CRITICAL] Backend outage\nHealth check failed: the database is unreachable.", ct);
            throw;
        }
        if (!health.DatabaseReachable)
        {
            await DirectAsync(sp, clock, "[CRITICAL] Backend outage\nHealth check failed: the database is unreachable.", ct);
            return;
        }
        if (health.Status == "healthy") return;
        var reasons = new List<string>();
        if (health.PendingMigrations.Count > 0) reasons.Add($"{health.PendingMigrations.Count} pending migration(s)");
        reasons.AddRange(health.Workers.Where(w => w.Stale || w.LastStatus == "failed").Select(w => $"job {w.Name} {(w.Stale ? "stale" : "failed")}"));
        if (health.LastBackup?.Status == "failed") reasons.Add("last backup failed");
        await sp.GetRequiredService<IAlertPublisher>().PublishAsync(new AlertMessage(AlertTypes.BackendOutage, $"Backend {health.Status}",
            "Health check: " + (reasons.Count == 0 ? health.Status : string.Join(", ", reasons)), AlertSeverity.Critical, "health:" + health.Status + ":" + string.Join('|', reasons)), ct);
    }

    private static async Task DirectAsync(IServiceProvider sp, IClock clock, string text, CancellationToken ct)
    {
        if (clock.UtcNow - _lastDirectAlert < TimeSpan.FromHours(1)) return;
        _lastDirectAlert = clock.UtcNow;
        await Veltrix.Modules.Telegram.TelegramDispatcher.SendDirectAsync(sp.GetRequiredService<Veltrix.Modules.Telegram.TelegramClient>(),
            sp.GetRequiredService<Veltrix.Modules.Telegram.TelegramRateLimiter>(), sp.GetRequiredService<Veltrix.Modules.Telegram.TelegramOptions>(), clock, text, ct);
    }
}

public static class WorkerRegistration
{
    public static void AddVeltrixWorkers(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<BackupOptions>(_ => config.GetSection("Backup").Get<BackupOptions>() ?? new BackupOptions());
        services.AddScoped<BackupService>();
        services.AddSingleton<IJob, SubscriptionStatusJob>();
        services.AddSingleton<IJob, LicenseExpiryReminderJob>();
        services.AddSingleton<IJob, EmailNotificationJob>();
        services.AddSingleton<IJob, ScheduledReportJob>();
        services.AddSingleton<IJob, BackupJob>();
        services.AddSingleton<IJob, CleanupJob>();
        services.AddSingleton<IJob, DiagnosticsRetentionJob>();
        services.AddSingleton<IJob, SubscriptionAlertJob>();
        services.AddSingleton<IJob, HealthMonitorJob>();
        services.AddSingleton<JobScheduler>();
        if (config.GetValue("Workers:Enabled", true)) services.AddHostedService(sp => sp.GetRequiredService<JobScheduler>());
    }
}
