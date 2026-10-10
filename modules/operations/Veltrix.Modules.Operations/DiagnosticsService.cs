using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Veltrix.Contracts.Operations;
using Veltrix.Modules.Tenants;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Operations;

public sealed class OperationsOptions
{
    public const string Section = "Diagnostics";
    /// <summary>Resolved incidents (and their events) older than this are purged by the retention job.</summary>
    public int RetentionDays { get; set; } = 90;
    /// <summary>A device is "online" when its last heartbeat is within this window.</summary>
    public int OnlineWindowMinutes { get; set; } = 5;
    public int HeartbeatSeconds { get; set; } = 120;
    public int MaxReportsPerBatch { get; set; } = 50;
    /// <summary>Occurrence counts at which a repeated-crash alert is raised.</summary>
    public int[] CrashAlertThresholds { get; set; } = [5, 25, 100, 500];
}

/// <summary>Error report ingestion (client and server), incident grouping and incident administration.</summary>
public sealed class DiagnosticsService(DbContext db, ITenantContext ctx, IClock clock, IAuditWriter audit, IAlertPublisher alerts, OperationsOptions options,
    ILogger<DiagnosticsService> logger)
{
    public static readonly IReadOnlyList<string> Platforms = ["windows", "android", "web", "server"];

    // ---------- Ingestion ----------
    public async Task<ErrorReportBatchResponse> IngestAsync(ErrorReportBatchRequest req, CancellationToken ct)
    {
        if (req.Items is not { Count: > 0 } || req.Items.Count > options.MaxReportsPerBatch)
            throw ValidationException.For("items", $"Send between 1 and {options.MaxReportsPerBatch} reports per batch.");
        var ids = new List<Guid>();
        var rejected = 0;
        foreach (var item in req.Items)
        {
            if (!IsValid(item)) { rejected++; continue; }
            var id = await RecordAsync("client", ctx.TenantId, ctx.DeviceId, ctx.UserId, item, ct);
            if (id is { } g) ids.Add(g); else rejected++;
        }
        return new ErrorReportBatchResponse(ids.Count, rejected, ids.Distinct().ToList());
    }

    private static bool IsValid(ErrorReportItem i) =>
        i is not null && DiagnosticCategories.All.Contains(i.Category) && DiagnosticSeverities.All.Contains(i.Severity)
        && !string.IsNullOrWhiteSpace(i.App) && i.App.Length <= 40 && !string.IsNullOrWhiteSpace(i.AppVersion) && i.AppVersion.Length <= 40
        && Platforms.Contains(i.Platform) && !string.IsNullOrWhiteSpace(i.ErrorType) && !string.IsNullOrWhiteSpace(i.Message)
        && (i.OsVersion is null || i.OsVersion.Length <= 60) && (i.Context is null || i.Context.Count <= 30);

    /// <summary>Server-side operational event (backup failure, repeated failed logins, sync failure...).</summary>
    public Task<Guid?> RecordServerEventAsync(ServerDiagnosticEvent e, CancellationToken ct) =>
        RecordAsync("server", e.TenantId, e.DeviceId, null, new ErrorReportItem("server", "server", "server", null,
            DiagnosticCategories.All.Contains(e.Category) ? e.Category : DiagnosticCategories.Api,
            DiagnosticSeverities.All.Contains(e.Severity) ? e.Severity : DiagnosticSeverities.Error, e.ErrorType, e.Message, null, null, null), ct);

    public static string Fingerprint(string app, string appVersion, string category, string errorType, string sanitizedMessage) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{app.Trim().ToLowerInvariant()}|{appVersion.Trim()}|{category}|{errorType.Trim().ToLowerInvariant()}|{Redaction.NormalizeForFingerprint(sanitizedMessage)}"))).ToLowerInvariant();

    private async Task<Guid?> RecordAsync(string source, Guid? tenantId, Guid? deviceId, Guid? userId, ErrorReportItem item, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var message = Redaction.Sanitize(item.Message, 2000);
        var errorType = Redaction.Sanitize(item.ErrorType, 120);
        var stack = item.StackTrace is null ? null : Redaction.Sanitize(item.StackTrace, 16000);
        var context = SanitizeContext(item.Context);
        var occurred = item.OccurredAt is { } o && o <= now && o > now.AddDays(-30) ? o : now;
        var fp = Fingerprint(item.App, item.AppVersion, item.Category, errorType, message);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            db.ChangeTracker.Clear();
            var incident = await db.Set<ErrorIncident>().SingleOrDefaultAsync(i => i.Fingerprint == fp && i.TenantId == tenantId, ct);
            var isNew = incident is null;
            if (incident is null)
            {
                incident = new ErrorIncident
                {
                    Fingerprint = fp, TenantId = tenantId, Source = source, App = item.App.Trim(), AppVersion = item.AppVersion.Trim(), Platform = item.Platform,
                    Category = item.Category, ErrorType = errorType, Severity = item.Severity, Title = message.Length > 500 ? message[..500] : message,
                    FirstSeenAt = occurred, LastSeenAt = occurred,
                };
                db.Add(incident);
            }
            var reopened = incident.Status == IncidentStatuses.Resolved;
            if (reopened) { incident.Status = IncidentStatuses.Open; incident.ResolvedAt = null; incident.StatusNote = "Reopened: occurred again after resolution."; }
            incident.OccurrenceCount++;
            if (occurred > incident.LastSeenAt) incident.LastSeenAt = occurred;
            if (occurred < incident.FirstSeenAt) incident.FirstSeenAt = occurred;
            if (DiagnosticSeverities.Rank(item.Severity) > DiagnosticSeverities.Rank(incident.Severity)) incident.Severity = item.Severity;
            if (stack is not null) incident.SampleStackTrace = stack;
            if (deviceId is not null) incident.LastDeviceId = deviceId;
            db.Add(new ErrorEvent
            {
                IncidentId = incident.Id, TenantId = tenantId, DeviceId = deviceId, UserId = userId, OsVersion = item.OsVersion, Message = message, Context = context,
                OccurredAt = occurred, ReceivedAt = now,
            });
            var threshold = CrashThresholdCrossed(incident);
            if (threshold > 0) incident.AlertedThreshold = threshold;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (attempt < 2)
            {
                // Concurrent insert of the same fingerprint or concurrent increment: reload and retry.
                logger.LogDebug(ex, "Retrying incident upsert for {Fingerprint}", fp);
                continue;
            }
            db.ChangeTracker.Clear();
            await RaiseAlertsAsync(incident, isNew || reopened, threshold, ct);
            return incident.Id;
        }
        return null;
    }

    private int CrashThresholdCrossed(ErrorIncident i)
    {
        if (i.Category != DiagnosticCategories.Crash) return 0;
        var t = options.CrashAlertThresholds.Where(x => x > i.AlertedThreshold && i.OccurrenceCount >= x).DefaultIfEmpty(0).Max();
        return t;
    }

    private async Task RaiseAlertsAsync(ErrorIncident i, bool newOrReopened, int crashThreshold, CancellationToken ct)
    {
        var tenant = i.TenantId is { } tid ? await db.Set<Company>().Where(c => c.Id == tid).Select(c => c.Name).SingleOrDefaultAsync(ct) : null;
        var where = tenant is null ? "platform" : tenant;
        if (crashThreshold > 0)
            await alerts.PublishAsync(new AlertMessage(AlertTypes.RepeatedCrashes, "Repeated crashes",
                $"{i.App} {i.AppVersion} ({i.Platform}) crashed {i.OccurrenceCount} times at {where}: {i.ErrorType} — {Short(i.Title)}",
                AlertSeverity.Critical, $"crash:{i.Id}:{crashThreshold}", i.TenantId, TimeSpan.FromDays(30)), ct);
        if (!newOrReopened) return;
        switch (i.Category)
        {
            case DiagnosticCategories.Sync:
                await alerts.PublishAsync(new AlertMessage(AlertTypes.SyncFailures, "Sync failures", $"Sync failure at {where}: {i.ErrorType} — {Short(i.Title)}",
                    AlertSeverity.Warning, $"sync-incident:{i.Id}", i.TenantId, TimeSpan.FromHours(24)), ct);
                break;
            case DiagnosticCategories.Outage when i.Severity == DiagnosticSeverities.Critical:
                await alerts.PublishAsync(new AlertMessage(AlertTypes.BackendOutage, "Outage reported", $"Outage reported at {where}: {Short(i.Title)}",
                    AlertSeverity.Critical, $"outage-incident:{i.Id}", i.TenantId), ct);
                break;
        }
    }

    private static string Short(string s) => s.Length > 160 ? s[..160] + "…" : s;

    private static string? SanitizeContext(IReadOnlyDictionary<string, string>? context)
    {
        if (context is null || context.Count == 0) return null;
        var clean = new Dictionary<string, string>();
        foreach (var (k, v) in context.Take(30))
        {
            if (string.IsNullOrWhiteSpace(k) || k.Length > 64 || !k.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-')) continue;
            // Keys that name credentials are dropped entirely, whatever their value.
            if (new[] { "password", "token", "secret", "key", "pin", "card", "cvv", "authorization", "cookie" }.Any(s => k.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
            clean[k] = Redaction.Sanitize(v, 500);
        }
        return clean.Count == 0 ? null : JsonSerializer.Serialize(clean);
    }

    // ---------- Incident administration (platform) ----------
    public async Task<PagedResult<IncidentDto>> ListAsync(PageQuery q, string? status, string? category, string? severity, Guid? tenantId, string? platform,
        string? app, string? source, DateTimeOffset? since, CancellationToken ct)
    {
        var query = db.Set<ErrorIncident>().AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
            query = status == "unresolved" ? query.Where(i => i.Status != IncidentStatuses.Resolved) : query.Where(i => i.Status == status);
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(i => i.Category == category);
        if (!string.IsNullOrWhiteSpace(severity)) query = query.Where(i => i.Severity == severity);
        if (tenantId is { } t) query = query.Where(i => i.TenantId == t);
        if (!string.IsNullOrWhiteSpace(platform)) query = query.Where(i => i.Platform == platform);
        if (!string.IsNullOrWhiteSpace(app)) query = query.Where(i => i.App == app);
        if (!string.IsNullOrWhiteSpace(source)) query = query.Where(i => i.Source == source);
        if (since is { } s) query = query.Where(i => i.LastSeenAt >= s);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim().ToUpper();
            query = query.Where(i => i.Title.ToUpper().Contains(term) || i.ErrorType.ToUpper().Contains(term) || i.Fingerprint.StartsWith(q.Search.Trim().ToLower()));
        }
        query = q.SortField switch
        {
            "occurrences" => q.Descending ? query.OrderByDescending(i => i.OccurrenceCount) : query.OrderBy(i => i.OccurrenceCount),
            "firstSeen" => q.Descending ? query.OrderByDescending(i => i.FirstSeenAt) : query.OrderBy(i => i.FirstSeenAt),
            _ => query.OrderByDescending(i => i.LastSeenAt),
        };
        var page = await query.ToPagedAsync(q, ct);
        var names = await TenantNamesAsync(page.Items.Select(i => i.TenantId), ct);
        return new PagedResult<IncidentDto>(page.Items.Select(i => ToDto(i, names)).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<IReadOnlyList<IncidentGroupDto>> GroupsAsync(string? status, string? category, int take, CancellationToken ct)
    {
        var query = db.Set<ErrorIncident>().AsNoTracking().AsQueryable();
        if (status == "unresolved") query = query.Where(i => i.Status != IncidentStatuses.Resolved);
        else if (!string.IsNullOrWhiteSpace(status)) query = query.Where(i => i.Status == status);
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(i => i.Category == category);
        var rows = await query.OrderByDescending(i => i.LastSeenAt).Take(5000).ToListAsync(ct);
        return rows.GroupBy(i => i.Fingerprint).Select(g =>
        {
            var latest = g.MaxBy(i => i.LastSeenAt)!;
            return new IncidentGroupDto(g.Key, latest.Category, latest.ErrorType, latest.Title, latest.App,
                g.MaxBy(i => DiagnosticSeverities.Rank(i.Severity))!.Severity, g.Count(), g.Select(i => i.TenantId).Distinct().Count(),
                g.Sum(i => i.OccurrenceCount), g.Count(i => i.Status != IncidentStatuses.Resolved), g.Min(i => i.FirstSeenAt), g.Max(i => i.LastSeenAt));
        }).OrderByDescending(g => g.Occurrences).Take(Math.Clamp(take, 1, 500)).ToList();
    }

    public async Task<IncidentDetailDto> GetAsync(Guid id, CancellationToken ct)
    {
        var i = await db.Set<ErrorIncident>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("incident.not_found", "Incident not found.");
        var events = await db.Set<ErrorEvent>().AsNoTracking().Where(e => e.IncidentId == id).OrderByDescending(e => e.OccurredAt).Take(50)
            .Select(e => new ErrorEventDto(e.Id, e.DeviceId, e.OsVersion, e.Message, e.Context, e.OccurredAt, e.ReceivedAt)).ToListAsync(ct);
        var devices = await db.Set<ErrorEvent>().Where(e => e.IncidentId == id && e.DeviceId != null).Select(e => e.DeviceId).Distinct().CountAsync(ct);
        var names = await TenantNamesAsync([i.TenantId], ct);
        return new IncidentDetailDto(ToDto(i, names), i.SampleStackTrace, i.StatusNote, i.AcknowledgedAt, i.ResolvedAt, events, devices);
    }

    public async Task<IncidentDetailDto> UpdateStatusAsync(Guid id, UpdateIncidentStatusRequest req, CancellationToken ct)
    {
        new Validator().Require(IncidentStatuses.All.Contains(req.Status), "status", "open, acknowledged or resolved.").MaxLength(req.Note, "note", 1000).ThrowIfInvalid();
        var i = await db.Set<ErrorIncident>().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("incident.not_found", "Incident not found.");
        var now = clock.UtcNow;
        var old = i.Status;
        i.Status = req.Status;
        i.StatusNote = req.Note is null ? i.StatusNote : Redaction.Sanitize(req.Note, 1000);
        if (req.Status == IncidentStatuses.Acknowledged) i.AcknowledgedAt ??= now;
        i.ResolvedAt = req.Status == IncidentStatuses.Resolved ? now : null;
        audit.Write("platform.incident_status", nameof(ErrorIncident), i.Id.ToString(), new { from = old, to = req.Status });
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // ---------- Retention ----------
    /// <summary>Deletes resolved incidents (with their events) and events of resolved incidents older than the retention period,
    /// plus finished diagnostic requests. Open/acknowledged incidents are never purged.</summary>
    public async Task<int> PurgeAsync(int? retentionDays, CancellationToken ct)
    {
        var days = Math.Clamp(retentionDays ?? options.RetentionDays, 1, 3650);
        var cutoff = clock.UtcNow.AddDays(-days);
        var resolved = db.Set<ErrorIncident>().Where(i => i.Status == IncidentStatuses.Resolved).Select(i => i.Id);
        var events = await db.Set<ErrorEvent>().Where(e => e.OccurredAt < cutoff && resolved.Contains(e.IncidentId)).ExecuteDeleteAsync(ct);
        var incidents = await db.Set<ErrorIncident>().Where(i => i.Status == IncidentStatuses.Resolved && i.ResolvedAt < cutoff && i.LastSeenAt < cutoff).ExecuteDeleteAsync(ct);
        var requests = await db.Set<DiagnosticRequest>().Where(r => r.RequestedAt < cutoff).ExecuteDeleteAsync(ct);
        return events + incidents + requests;
    }

    private async Task<Dictionary<Guid, string>> TenantNamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
        return list.Count == 0 ? new() : await db.Set<Company>().Where(c => list.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
    }

    public static IncidentDto ToDto(ErrorIncident i, IReadOnlyDictionary<Guid, string> names) => new(i.Id, i.Fingerprint, i.TenantId,
        i.TenantId is { } t ? names.GetValueOrDefault(t) : null, i.Source, i.App, i.AppVersion, i.Platform, i.Category, i.ErrorType, i.Severity, i.Status, i.Title,
        i.OccurrenceCount, i.FirstSeenAt, i.LastSeenAt, i.LastDeviceId);
}

/// <summary>Writes server events in a dedicated scope so they survive a rollback of the caller's unit of work. Never throws.</summary>
public sealed class DiagnosticsRecorder(IServiceScopeFactory scopes, ILogger<DiagnosticsRecorder> logger) : IDiagnosticsRecorder
{
    public async Task RecordAsync(ServerDiagnosticEvent e, CancellationToken ct = default)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<DiagnosticsService>().RecordServerEventAsync(e, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not record diagnostic event {Category}/{ErrorType}", e.Category, e.ErrorType);
        }
    }
}
