using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Veltrix.Contracts.Licensing;
using Veltrix.Contracts.Operations;
using Veltrix.Modules.Licensing;
using Veltrix.Modules.Tenants;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Operations;

/// <summary>Device heartbeat (client), fleet view and device actions (vendor), and remote diagnostic requests.
/// Device lifecycle changes are delegated to <see cref="LicenseAdminService"/> so licensing rules live in one place.</summary>
public sealed class DeviceOpsService(DbContext db, ITenantContext ctx, IClock clock, IAuditWriter audit, OperationsOptions options,
    IDeviceAuthorization deviceAuth, LicenseAdminService licenses, IAlertPublisher alerts)
{
    // ---------- Client: heartbeat ----------
    public async Task<DeviceHeartbeatResponse> HeartbeatAsync(DeviceHeartbeatRequest req, CancellationToken ct)
    {
        var (tid, device) = await CurrentDeviceAsync(ct);
        new Validator().Require(req.InstallationId == device.InstallationId, "installationId", "Does not match the activated device.")
            .Require(req.Platform is "windows" or "android", "platform", "Unsupported platform.")
            .NotEmpty(req.AppVersion, "appVersion", 40).MaxLength(req.OsVersion, "osVersion", 60)
            .Require(req.PendingSyncOperations is null or (>= 0 and <= 1_000_000), "pendingSyncOperations", "Out of range.")
            .Require(req.FailedSyncOperations is null or (>= 0 and <= 1_000_000), "failedSyncOperations", "Out of range.").ThrowIfInvalid();
        var now = clock.UtcNow;
        var previousFailed = device.FailedSyncOperations ?? 0;
        device.LastSeenAt = now;
        device.LastHeartbeatAt = now;
        device.AppVersion = req.AppVersion.Trim();
        device.OsVersion = req.OsVersion is null ? null : Redaction.Sanitize(req.OsVersion, 60);
        if (req.LastSyncAt is { } ls && ls <= now.AddMinutes(5)) device.LastSyncAt = ls;
        device.PendingSyncOperations = req.PendingSyncOperations;
        device.FailedSyncOperations = req.FailedSyncOperations;

        var consent = await ConsentAsync(tid, ct);
        var pending = new List<PendingDiagnosticRequestDto>();
        if (consent)
        {
            var requests = await db.Set<DiagnosticRequest>().Where(r => r.DeviceId == device.Id && r.TenantId == tid
                && (r.Status == DiagnosticRequestStatuses.Pending || r.Status == DiagnosticRequestStatuses.Delivered) && r.ExpiresAt > now).ToListAsync(ct);
            foreach (var r in requests)
            {
                if (r.Status == DiagnosticRequestStatuses.Pending) { r.Status = DiagnosticRequestStatuses.Delivered; r.DeliveredAt = now; }
                pending.Add(new PendingDiagnosticRequestDto(r.Id, r.RequestedAt, r.ExpiresAt, r.Reason));
            }
        }
        await db.SaveChangesAsync(ct);

        if ((req.FailedSyncOperations ?? 0) > 0 && previousFailed == 0)
        {
            var tenant = await db.Set<Company>().Where(c => c.Id == tid).Select(c => c.Name).SingleOrDefaultAsync(ct);
            await alerts.PublishAsync(new AlertMessage(AlertTypes.SyncFailures, "Device sync failures",
                $"{tenant}: device \"{device.DeviceName}\" reports {req.FailedSyncOperations} failed sync operation(s).", AlertSeverity.Warning,
                $"device-sync:{device.Id}:{now:yyyyMMdd}", tid, TimeSpan.FromHours(24)), ct);
        }
        return new DeviceHeartbeatResponse(now, options.HeartbeatSeconds, consent, pending);
    }

    // ---------- Client: diagnostic report upload ----------
    public async Task UploadReportAsync(Guid requestId, DiagnosticReportUploadRequest req, CancellationToken ct)
    {
        var (tid, device) = await CurrentDeviceAsync(ct);
        var r = await db.Set<DiagnosticRequest>().SingleOrDefaultAsync(x => x.Id == requestId && x.TenantId == tid && x.DeviceId == device.Id, ct)
                ?? throw new NotFoundException("diagnostics.request_not_found", "Diagnostic request not found.");
        if (!await ConsentAsync(tid, ct))
            throw new ForbiddenException("diagnostics.consent_required", "Remote diagnostics are disabled for this company.");
        var now = clock.UtcNow;
        if (r.Status is not (DiagnosticRequestStatuses.Pending or DiagnosticRequestStatuses.Delivered) || r.ExpiresAt <= now)
            throw new ConflictException("diagnostics.request_closed", "This diagnostic request is no longer open.");
        new Validator().NotEmpty(req.AppVersion, "appVersion", 40).MaxLength(req.OsVersion, "osVersion", 60)
            .Require(req.Facts is null || req.Facts.Count <= 100, "facts", "At most 100 facts.")
            .Require(req.RecentLogLines is null || req.RecentLogLines.Count <= 500, "recentLogLines", "At most 500 lines.").ThrowIfInvalid();
        var facts = new Dictionary<string, string>();
        foreach (var (k, v) in req.Facts ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(k) || k.Length > 64 || !k.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-')) continue;
            if (new[] { "password", "token", "secret", "key", "pin", "card", "cvv" }.Any(s => k.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
            facts[k] = Redaction.Sanitize(v, 500);
        }
        var lines = (req.RecentLogLines ?? []).Select(l => Redaction.Sanitize(l, 500)).ToList();
        r.ReportJson = JsonSerializer.Serialize(new { facts, lines });
        r.AppVersion = req.AppVersion.Trim();
        r.OsVersion = req.OsVersion is null ? null : Redaction.Sanitize(req.OsVersion, 60);
        r.Status = DiagnosticRequestStatuses.Uploaded;
        r.UploadedAt = now;
        audit.Write("diagnostics.report_uploaded", nameof(DiagnosticRequest), r.Id.ToString());
        await db.SaveChangesAsync(ct);
    }

    private async Task<(Guid TenantId, Device Device)> CurrentDeviceAsync(CancellationToken ct)
    {
        var tid = ctx.RequireTenant();
        if (ctx.DeviceId is not { } did)
            throw new ForbiddenException("device.session_required", "This endpoint requires a session bound to an activated device.");
        if (!await deviceAuth.IsDeviceActiveAsync(tid, did, ct))
            throw new ForbiddenException("licensing.device_revoked", "This device's activation is no longer valid.");
        var device = await db.Set<Device>().SingleOrDefaultAsync(d => d.Id == did, ct) ?? throw new NotFoundException("device.not_found", "Device not found.");
        return (tid, device);
    }

    public async Task<bool> ConsentAsync(Guid tenantId, CancellationToken ct)
    {
        var value = await db.Set<TenantSetting>().IgnoreQueryFilters().Where(s => s.TenantId == tenantId && s.Key == SettingKeys.DiagnosticsConsent)
            .Select(s => s.Value).SingleOrDefaultAsync(ct);
        return value is not null && value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Vendor: fleet ----------
    public async Task<PagedResult<AdminDeviceDto>> ListAsync(PageQuery q, Guid? tenantId, string? platform, string? status, bool? online, int? onlineWithinMinutes,
        string? appVersion, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var window = Window(onlineWithinMinutes);
        var since = now.AddMinutes(-window);
        var query = db.Set<Device>().IgnoreQueryFilters().AsNoTracking().AsQueryable();
        if (tenantId is { } t) query = query.Where(d => d.TenantId == t);
        if (!string.IsNullOrWhiteSpace(platform)) query = query.Where(d => d.Platform == platform);
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<DeviceStatus>(status, true, out var st)) throw ValidationException.For("status", "Active, Revoked, Replaced or Released.");
            query = query.Where(d => d.Status == st);
        }
        if (online is true) query = query.Where(d => d.Status == DeviceStatus.Active && d.LastHeartbeatAt >= since);
        else if (online is false) query = query.Where(d => d.Status != DeviceStatus.Active || d.LastHeartbeatAt == null || d.LastHeartbeatAt < since);
        if (!string.IsNullOrWhiteSpace(appVersion)) query = query.Where(d => d.AppVersion == appVersion);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim().ToUpper();
            var tenants = db.Set<Company>().Where(c => c.Name.ToUpper().Contains(s)).Select(c => c.Id);
            query = query.Where(d => d.DeviceName.ToUpper().Contains(s) || tenants.Contains(d.TenantId));
        }
        var page = await query.OrderByDescending(d => d.Status == DeviceStatus.Active).ThenByDescending(d => d.LastSeenAt).ToPagedAsync(q, ct);
        var names = await NamesAsync(page.Items.Select(d => d.TenantId), ct);
        return new PagedResult<AdminDeviceDto>(page.Items.Select(d => ToDto(d, names.GetValueOrDefault(d.TenantId), since)).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<AdminDeviceDetailDto> GetAsync(Guid id, CancellationToken ct)
    {
        var d = await db.Set<Device>().IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("device.not_found", "Device not found.");
        var since = clock.UtcNow.AddMinutes(-Window(null));
        var license = await db.Set<License>().IgnoreQueryFilters().AsNoTracking().Where(l => l.Id == d.LicenseId)
            .Select(l => new { l.PlanCode, l.Type, l.Status }).SingleOrDefaultAsync(ct);
        var name = await db.Set<Company>().Where(c => c.Id == d.TenantId).Select(c => c.Name).SingleOrDefaultAsync(ct);
        var errors = await db.Set<ErrorEvent>().CountAsync(e => e.DeviceId == id, ct);
        var incidentIds = db.Set<ErrorEvent>().Where(e => e.DeviceId == id).Select(e => e.IncidentId).Distinct();
        var open = await db.Set<ErrorIncident>().CountAsync(i => incidentIds.Contains(i.Id) && i.Status != IncidentStatuses.Resolved, ct);
        var history = await db.Set<LicenseEvent>().IgnoreQueryFilters().AsNoTracking().Where(e => e.DeviceId == id).OrderByDescending(e => e.OccurredAt).Take(100)
            .Select(e => new LicenseEventDto(e.Id, e.LicenseId, e.DeviceId, e.Type, e.Details, e.OccurredAt)).ToListAsync(ct);
        var consent = await ConsentAsync(d.TenantId, ct);
        var requests = (await db.Set<DiagnosticRequest>().AsNoTracking().Where(r => r.DeviceId == id).OrderByDescending(r => r.RequestedAt).Take(20).ToListAsync(ct))
            .Select(r => ToDto(r, consent)).ToList();
        return new AdminDeviceDetailDto(ToDto(d, name, since), license?.PlanCode, license?.Type.ToString(), license?.Status.ToString(), d.RevokedAt, d.RevokedReason,
            d.ReplacedByDeviceId, errors, open, history, requests, consent);
    }

    public async Task<AdminDeviceDetailDto> DeactivateAsync(Guid id, string reason, bool asReplaced, CancellationToken ct)
    {
        await licenses.RevokeDeviceAsync(id, reason, ct, asReplaced);
        return await GetAsync(id, ct);
    }

    public async Task<AdminDeviceDetailDto> ReassignAsync(Guid id, ReassignDeviceRequest req, CancellationToken ct)
    {
        await licenses.ReassignDeviceAsync(id, req.TargetLicenseId, req.Reason, ct);
        return await GetAsync(id, ct);
    }

    // ---------- Vendor: remote diagnostic requests ----------
    public async Task<DiagnosticRequestDto> CreateRequestAsync(Guid deviceId, CreateDiagnosticRequestRequest req, CancellationToken ct)
    {
        new Validator().MaxLength(req.Reason, "reason", 500).Require(req.ExpiresInHours is null or (>= 1 and <= 168), "expiresInHours", "Between 1 and 168.").ThrowIfInvalid();
        var d = await db.Set<Device>().IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x => x.Id == deviceId, ct) ?? throw new NotFoundException("device.not_found", "Device not found.");
        if (d.Status != DeviceStatus.Active) throw new DomainException("device.inactive", "The device is not active.", 409);
        var now = clock.UtcNow;
        var r = new DiagnosticRequest
        {
            TenantId = d.TenantId, DeviceId = d.Id, Reason = req.Reason is null ? null : Redaction.Sanitize(req.Reason, 500), RequestedAt = now,
            ExpiresAt = now.AddHours(req.ExpiresInHours ?? 48),
        };
        db.Add(r);
        audit.Write("platform.diagnostic_requested", nameof(DiagnosticRequest), r.Id.ToString(), new { deviceId, d.TenantId });
        await db.SaveChangesAsync(ct);
        return ToDto(r, await ConsentAsync(d.TenantId, ct));
    }

    public async Task<DiagnosticReportDto> ReportAsync(Guid requestId, CancellationToken ct)
    {
        var r = await db.Set<DiagnosticRequest>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == requestId, ct) ?? throw new NotFoundException("diagnostics.request_not_found", "Not found.");
        var facts = new Dictionary<string, string>();
        var lines = new List<string>();
        if (r.ReportJson is not null)
        {
            using var doc = JsonDocument.Parse(r.ReportJson);
            if (doc.RootElement.TryGetProperty("facts", out var f))
                foreach (var p in f.EnumerateObject()) facts[p.Name] = p.Value.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("lines", out var l))
                lines.AddRange(l.EnumerateArray().Select(x => x.GetString() ?? ""));
        }
        return new DiagnosticReportDto(ToDto(r, await ConsentAsync(r.TenantId, ct)), r.AppVersion, r.OsVersion, facts, lines);
    }

    public async Task<DiagnosticRequestDto> CancelRequestAsync(Guid requestId, CancellationToken ct)
    {
        var r = await db.Set<DiagnosticRequest>().SingleOrDefaultAsync(x => x.Id == requestId, ct) ?? throw new NotFoundException("diagnostics.request_not_found", "Not found.");
        if (r.Status is DiagnosticRequestStatuses.Pending or DiagnosticRequestStatuses.Delivered) r.Status = DiagnosticRequestStatuses.Cancelled;
        audit.Write("platform.diagnostic_cancelled", nameof(DiagnosticRequest), r.Id.ToString());
        await db.SaveChangesAsync(ct);
        return ToDto(r, await ConsentAsync(r.TenantId, ct));
    }

    private int Window(int? minutes) => Math.Clamp(minutes ?? options.OnlineWindowMinutes, 1, 1440);

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Set<Company>().Where(c => list.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
    }

    public static AdminDeviceDto ToDto(Device d, string? tenantName, DateTimeOffset onlineSince) => new(d.Id, d.TenantId, tenantName, d.LicenseId, d.Platform, d.DeviceName,
        d.AppVersion, d.OsVersion, d.Status.ToString(), d.Status == DeviceStatus.Active && d.LastHeartbeatAt >= onlineSince, d.ActivatedAt,
        d.LastHeartbeatAt is { } h && h > d.LastSeenAt ? h : d.LastSeenAt, d.LastHeartbeatAt, d.LastSyncAt, d.PendingSyncOperations, d.FailedSyncOperations);

    private DiagnosticRequestDto ToDto(DiagnosticRequest r, bool consent)
    {
        var status = r.Status is DiagnosticRequestStatuses.Pending or DiagnosticRequestStatuses.Delivered && r.ExpiresAt <= clock.UtcNow ? DiagnosticRequestStatuses.Expired : r.Status;
        return new DiagnosticRequestDto(r.Id, r.DeviceId, r.TenantId, status, r.Reason, r.RequestedAt, r.ExpiresAt, r.DeliveredAt, r.UploadedAt, consent);
    }
}
