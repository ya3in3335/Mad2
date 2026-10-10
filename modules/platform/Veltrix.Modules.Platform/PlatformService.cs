using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Veltrix.Contracts.Platform;
using Veltrix.Contracts.Tenants;
using Veltrix.Modules.Identity;
using Veltrix.Modules.Licensing;
using Veltrix.Modules.Subscriptions;
using Veltrix.Modules.Tenants;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Platform;

public sealed class PlatformService(DbContext db, ITenantContext ctx, IClock clock, IAuditWriter audit, AuthService auth)
{
    public async Task<PlatformDashboardDto> DashboardAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var companies = await db.Set<Company>().Select(c => new { c.Status, c.SetupCompleted }).ToListAsync(ct);
        var licenses = await db.Set<License>().IgnoreQueryFilters().ToListAsync(ct);
        var activeLicenses = licenses.Count(l => l.IsUsable(now));
        var expiring = licenses.Count(l => l.IsUsable(now) && l.ExpiresAt is { } e && e <= now.AddDays(14));
        var devices = await db.Set<Device>().IgnoreQueryFilters().CountAsync(d => d.Status == DeviceStatus.Active, ct);
        var since30 = now.AddDays(-30);
        var activations30 = await db.Set<LicenseEvent>().IgnoreQueryFilters().CountAsync(e => e.Type == "device.activated" && e.OccurredAt >= since30, ct);
        var subs = await db.Set<Subscription>().IgnoreQueryFilters().Where(s => s.Status == SubscriptionStatus.Active).ToListAsync(ct);
        var mrr = subs.Sum(s => s.BillingCycle == BillingCycle.Annual ? s.PeriodPrice / 12m : s.PeriodPrice);
        var yearAgo = now.AddMonths(-11);
        var startOfWindow = new DateTimeOffset(yearAgo.Year, yearAgo.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var paid = await db.Set<BillingInvoice>().IgnoreQueryFilters().Where(i => i.Status == BillingInvoiceStatus.Paid && i.PaidAt >= startOfWindow)
            .Select(i => new { i.PaidAt, i.Total }).ToListAsync(ct);
        var activations = await db.Set<LicenseEvent>().IgnoreQueryFilters().Where(e => e.Type == "device.activated" && e.OccurredAt >= startOfWindow)
            .Select(e => e.OccurredAt).ToListAsync(ct);
        var months = Enumerable.Range(0, 12).Select(i => startOfWindow.AddMonths(i).ToString("yyyy-MM")).ToList();
        var revenueByMonth = months.Select(m => new MonthValue(m, paid.Where(p => p.PaidAt!.Value.ToString("yyyy-MM") == m).Sum(p => p.Total))).ToList();
        var activationsByMonth = months.Select(m => new MonthValue(m, activations.Count(a => a.ToString("yyyy-MM") == m))).ToList();
        var openCases = await db.Set<SupportCase>().CountAsync(c => c.Status == SupportStatus.Open || c.Status == SupportStatus.Pending, ct);
        var alerts = (await SecurityAlertsAsync(7, ct)).Count;
        return new PlatformDashboardDto(companies.Count, companies.Count(c => c.Status == CompanyStatus.Active), companies.Count(c => c.Status == CompanyStatus.Suspended),
            companies.Count(c => !c.SetupCompleted), activeLicenses, expiring, devices, activations30, Money.Round(mrr), revenueByMonth[^1].Value,
            revenueByMonth, activationsByMonth, openCases, alerts);
    }

    public async Task<PagedResult<TenantSummaryDto>> TenantsAsync(PageQuery q, string? status, CancellationToken ct)
    {
        var query = db.Set<Company>().AsNoTracking().AsQueryable();
        if (status is not null && Enum.TryParse<CompanyStatus>(status, true, out var s)) query = query.Where(c => c.Status == s);
        if (!string.IsNullOrWhiteSpace(q.Search)) query = query.Where(c => c.Name.ToUpper().Contains(q.Search.ToUpper()) || (c.Email != null && c.Email.Contains(q.Search)));
        var page = await query.OrderBy(c => c.Name).ToPagedAsync(q, ct);
        var ids = page.Items.Select(c => c.Id).ToList();
        var subs = await db.Set<Subscription>().IgnoreQueryFilters().Where(x => ids.Contains(x.TenantId)).ToListAsync(ct);
        var devices = await db.Set<Device>().IgnoreQueryFilters().Where(d => ids.Contains(d.TenantId) && d.Status == DeviceStatus.Active)
            .GroupBy(d => d.TenantId).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C, ct);
        var users = await db.Set<User>().Where(u => u.TenantId != null && ids.Contains(u.TenantId.Value))
            .GroupBy(u => u.TenantId!.Value).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C, ct);
        return new PagedResult<TenantSummaryDto>(page.Items.Select(c =>
        {
            var sub = subs.Where(x => x.TenantId == c.Id).OrderByDescending(x => x.CreatedAt).FirstOrDefault();
            return new TenantSummaryDto(c.Id, c.Name, c.Status.ToString(), c.SetupCompleted, c.Email, c.Phone, c.Wilaya, c.CreatedAt, sub?.PlanCode, sub?.Status.ToString(),
                sub?.CurrentPeriodEnd, devices.GetValueOrDefault(c.Id), users.GetValueOrDefault(c.Id));
        }).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<CompanyDto> TenantAsync(Guid id, CancellationToken ct) =>
        CompanyService.ToDto(await db.Set<Company>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("tenant.not_found", "Tenant not found."));

    // ---------- Platform administrators ----------
    public async Task<IReadOnlyList<PlatformAdminDto>> AdminsAsync(CancellationToken ct) =>
        await db.Set<User>().Where(u => u.IsPlatformAdmin).OrderBy(u => u.Email)
            .Select(u => new PlatformAdminDto(u.Id, u.Email, u.FullName, u.IsActive, u.MfaEnabled, u.LastLoginAt)).ToListAsync(ct);

    public async Task<PlatformAdminDto> CreateAdminAsync(CreatePlatformAdminRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Email, "email", 256).Require(req.Email.Contains('@'), "email", "Invalid email.").NotEmpty(req.FullName, "fullName").ThrowIfInvalid();
        var normalized = AuthService.Normalize(req.Email);
        if (await db.Set<User>().AnyAsync(u => u.NormalizedEmail == normalized, ct)) throw new ConflictException("user.email_taken", "Email already used.");
        var role = await db.Set<Role>().SingleAsync(r => r.TenantId == null && r.Code == SystemRoles.PlatformSuperAdmin, ct);
        var u = new User { Email = req.Email.Trim(), NormalizedEmail = normalized, FullName = req.FullName.Trim(), IsPlatformAdmin = true };
        auth.SetPassword(u, req.Password);
        u.Roles.Add(new UserRole { UserId = u.Id, RoleId = role.Id });
        db.Add(u);
        audit.Write("platform.admin_created", nameof(User), u.Id.ToString(), new { u.Email });
        await db.SaveChangesAsync(ct);
        return new PlatformAdminDto(u.Id, u.Email, u.FullName, u.IsActive, u.MfaEnabled, u.LastLoginAt);
    }

    public async Task SetAdminActiveAsync(Guid id, bool active, CancellationToken ct)
    {
        if (id == ctx.UserId && !active) throw new DomainException("admin.self_disable", "You cannot disable your own account.");
        var u = await db.Set<User>().SingleOrDefaultAsync(x => x.Id == id && x.IsPlatformAdmin, ct) ?? throw new NotFoundException("admin.not_found", "Not found.");
        if (!active && await db.Set<User>().CountAsync(x => x.IsPlatformAdmin && x.IsActive, ct) <= 1) throw new DomainException("admin.last", "At least one active platform administrator is required.");
        u.IsActive = active;
        u.SecurityStamp++;
        if (!active) await auth.RevokeAllAsync(u.Id, "admin_disabled", ct);
        audit.Write(active ? "platform.admin_enabled" : "platform.admin_disabled", nameof(User), u.Id.ToString());
        await db.SaveChangesAsync(ct);
    }

    // ---------- Support ----------
    public async Task<IReadOnlyList<SupportCaseDto>> CasesAsync(string? status, CancellationToken ct)
    {
        var q = db.Set<SupportCase>().Include(c => c.Messages).AsNoTracking().AsQueryable();
        if (!ctx.IsPlatformAdmin) { var tid = ctx.RequireTenant(); q = q.Where(c => c.TenantId == tid); }
        if (status is not null && Enum.TryParse<SupportStatus>(status, true, out var s)) q = q.Where(c => c.Status == s);
        var list = await q.OrderByDescending(c => c.UpdatedAt).Take(500).ToListAsync(ct);
        var names = await db.Set<Company>().Where(c => list.Select(x => x.TenantId).Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return list.Select(c => ToDto(c, c.TenantId is { } t ? names.GetValueOrDefault(t) : null)).ToList();
    }

    public async Task<SupportCaseDto> OpenCaseAsync(CreateSupportCaseRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Subject, "subject").NotEmpty(req.Body, "body", 8000)
            .Require(Enum.TryParse<SupportPriority>(req.Priority, true, out _), "priority", "Low, Normal, High or Urgent.").ThrowIfInvalid();
        var c = new SupportCase { TenantId = ctx.TenantId, Subject = req.Subject.Trim(), Priority = Enum.Parse<SupportPriority>(req.Priority, true), Status = SupportStatus.Open };
        c.Messages.Add(new SupportMessage { CaseId = c.Id, AuthorId = ctx.RequireUser(), FromVendor = ctx.IsPlatformAdmin, Body = req.Body });
        db.Add(c);
        await db.SaveChangesAsync(ct);
        return ToDto(c, null);
    }

    public async Task<SupportCaseDto> ReplyAsync(Guid id, ReplySupportCaseRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Body, "body", 8000).Require(req.Status is null || Enum.TryParse<SupportStatus>(req.Status, true, out _), "status", "Invalid status.").ThrowIfInvalid();
        var c = await db.Set<SupportCase>().Include(x => x.Messages).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("case.not_found", "Not found.");
        if (!ctx.IsPlatformAdmin && c.TenantId != ctx.TenantId) throw new NotFoundException("case.not_found", "Not found.");
        db.Add(new SupportMessage { CaseId = c.Id, AuthorId = ctx.RequireUser(), FromVendor = ctx.IsPlatformAdmin, Body = req.Body });
        if (req.Status is not null && ctx.IsPlatformAdmin) c.Status = Enum.Parse<SupportStatus>(req.Status, true);
        else c.Status = ctx.IsPlatformAdmin ? SupportStatus.Pending : SupportStatus.Open;
        c.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return (await CasesAsync(null, ct)).Single(x => x.Id == id);
    }

    private static SupportCaseDto ToDto(SupportCase c, string? tenant) => new(c.Id, c.TenantId, tenant, c.Subject, c.Status.ToString(), c.Priority.ToString(),
        c.Messages.OrderBy(m => m.CreatedAt).FirstOrDefault()?.AuthorId ?? Guid.Empty, c.CreatedAt, c.UpdatedAt,
        c.Messages.OrderBy(m => m.CreatedAt).Select(m => new SupportMessageDto(m.Id, m.AuthorId, m.FromVendor, m.Body, m.CreatedAt)).ToList());

    // ---------- Audit & security ----------
    public async Task<PagedResult<AuditLogDto>> AuditAsync(PageQuery q, Guid? tenantId, string? action, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var query = db.Set<AuditLog>().AsNoTracking().AsQueryable();
        if (!ctx.IsPlatformAdmin) { var tid = ctx.RequireTenant(); query = query.Where(a => a.TenantId == tid); }
        else if (tenantId is { } t) query = query.Where(a => a.TenantId == t);
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(a => a.Action.StartsWith(action));
        if (from is { } f) query = query.Where(a => a.OccurredAt >= f);
        if (to is { } tt) query = query.Where(a => a.OccurredAt <= tt);
        var page = await query.OrderByDescending(a => a.OccurredAt).ToPagedAsync(q, ct);
        return new PagedResult<AuditLogDto>(page.Items.Select(a => new AuditLogDto(a.Id, a.TenantId, a.UserId, a.DeviceId, a.Action, a.EntityType, a.EntityId, a.Details,
            a.IpAddress, a.CorrelationId, a.OccurredAt)).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    private static readonly Dictionary<string, (string Severity, string Description)> AlertActions = new()
    {
        ["auth.lockout"] = ("high", "Account locked after repeated failed sign-ins"),
        ["auth.refresh_reuse_detected"] = ("critical", "Refresh token reuse detected (possible token theft)"),
        ["auth.step_up_failed"] = ("medium", "Failed password confirmation for a privileged operation"),
        ["licensing.device_revoked"] = ("medium", "Device activation revoked"),
        ["platform.license_transferred"] = ("medium", "License transferred between tenants"),
        ["platform.tenant_suspended"] = ("medium", "Tenant suspended"),
        ["platform.admin_created"] = ("high", "Platform administrator created"),
        ["platform.admin_disabled"] = ("medium", "Platform administrator disabled"),
        ["pos.sale_voided"] = ("low", "POS sale voided"),
    };

    public async Task<IReadOnlyList<SecurityAlertDto>> SecurityAlertsAsync(int days, CancellationToken ct)
    {
        var since = clock.UtcNow.AddDays(-Math.Clamp(days, 1, 90));
        var keys = AlertActions.Keys.ToList();
        var rows = await db.Set<AuditLog>().AsNoTracking().Where(a => a.OccurredAt >= since && keys.Contains(a.Action)).OrderByDescending(a => a.OccurredAt).Take(1000).ToListAsync(ct);
        var alerts = rows.Select(a => new SecurityAlertDto(a.Action, AlertActions[a.Action].Severity, a.TenantId, a.UserId, AlertActions[a.Action].Description, a.OccurredAt)).ToList();
        // Burst detection: many failed logins against one account within an hour.
        var failures = await db.Set<AuditLog>().AsNoTracking().Where(a => a.OccurredAt >= since && a.Action == "auth.login_failed")
            .Select(a => new { a.EntityId, a.TenantId, a.OccurredAt }).ToListAsync(ct);
        foreach (var g in failures.GroupBy(f => (f.EntityId, f.OccurredAt.ToString("yyyyMMddHH"))).Where(g => g.Count() >= 10))
            alerts.Add(new SecurityAlertDto("auth.failed_login_burst", "high", g.First().TenantId, Guid.TryParse(g.Key.EntityId, out var u) ? u : null,
                $"{g.Count()} failed sign-ins within one hour", g.Max(x => x.OccurredAt)));
        return alerts.OrderByDescending(a => a.OccurredAt).ToList();
    }

    // ---------- Health & backups ----------
    public async Task<SystemHealthDto> HealthAsync(string version, CancellationToken ct)
    {
        var dbOk = await db.Database.CanConnectAsync(ct);
        var pending = dbOk ? (await db.Database.GetPendingMigrationsAsync(ct)).ToList() : [];
        var workers = await db.Set<WorkerHeartbeat>().AsNoTracking().ToListAsync(ct);
        var now = clock.UtcNow;
        var ws = workers.Select(w => new WorkerStatusDto(w.Name, w.LastRunAt, w.LastStatus, w.LastError, now - w.LastRunAt > TimeSpan.FromHours(26))).ToList();
        var last = await db.Set<BackupRecord>().AsNoTracking().OrderByDescending(b => b.StartedAt).FirstOrDefaultAsync(ct);
        var status = !dbOk ? "down" : pending.Count > 0 || ws.Any(w => w.Stale || w.LastStatus == "failed") || last?.Status == "failed" ? "degraded" : "healthy";
        return new SystemHealthDto(status, dbOk, pending, ws, last is null ? null : ToDto(last), version, now);
    }

    public async Task<IReadOnlyList<BackupRecordDto>> BackupsAsync(CancellationToken ct) =>
        (await db.Set<BackupRecord>().AsNoTracking().OrderByDescending(b => b.StartedAt).Take(100).ToListAsync(ct)).Select(ToDto).ToList();

    public static BackupRecordDto ToDto(BackupRecord b) => new(b.Id, b.FileName, b.SizeBytes, b.Sha256, b.StartedAt, b.CompletedAt, b.Status, b.Error, b.Verified, b.VerifiedAt, b.VerificationDetails);

    // ---------- Releases ----------
    public async Task<IReadOnlyList<AppReleaseDto>> ReleasesAsync(CancellationToken ct) =>
        (await db.Set<AppRelease>().AsNoTracking().OrderByDescending(r => r.PublishedAt).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<AppReleaseDto> UpsertReleaseAsync(Guid? id, UpsertReleaseRequest req, CancellationToken ct)
    {
        new Validator().Require(req.Platform is "windows" or "android", "platform", "windows or android.")
            .Require(System.Version.TryParse(req.Version, out _), "version", "Use semantic version x.y.z.").Require(System.Version.TryParse(req.MinSupportedVersion, out _), "minSupportedVersion", "x.y.z.")
            .Require(req.Channel is "stable" or "beta", "channel", "stable or beta.").Require(req.Sha256 is null || req.Sha256.Length == 64, "sha256", "64 hex chars.")
            .Require(req.DownloadUrl is null || req.DownloadUrl.StartsWith("https://"), "downloadUrl", "HTTPS required.").ThrowIfInvalid();
        var r = id is { } rid ? await db.Set<AppRelease>().SingleOrDefaultAsync(x => x.Id == rid, ct) ?? throw new NotFoundException("release.not_found", "Not found.")
            : db.Add(new AppRelease { PublishedAt = clock.UtcNow }).Entity;
        r.Platform = req.Platform; r.Version = req.Version; r.Channel = req.Channel; r.MinSupportedVersion = req.MinSupportedVersion; r.ReleaseNotes = req.ReleaseNotes;
        r.DownloadUrl = req.DownloadUrl; r.Sha256 = req.Sha256?.ToLowerInvariant(); r.IsActive = req.IsActive;
        audit.Write("platform.release_saved", nameof(AppRelease), r.Id.ToString(), new { r.Platform, r.Version });
        await db.SaveChangesAsync(ct);
        return ToDto(r);
    }

    public async Task<UpdateCheckDto> CheckUpdateAsync(string platform, string currentVersion, string channel, CancellationToken ct)
    {
        if (!System.Version.TryParse(currentVersion, out var current)) throw ValidationException.For("currentVersion", "Invalid version.");
        var releases = await db.Set<AppRelease>().AsNoTracking().Where(r => r.Platform == platform && r.IsActive && (r.Channel == channel || r.Channel == "stable")).ToListAsync(ct);
        var latest = releases.OrderByDescending(r => System.Version.Parse(r.Version)).FirstOrDefault();
        if (latest is null) return new UpdateCheckDto(false, false, null, true);
        var supported = current >= System.Version.Parse(latest.MinSupportedVersion);
        return new UpdateCheckDto(System.Version.Parse(latest.Version) > current, !supported, ToDto(latest), supported);
    }

    private static AppReleaseDto ToDto(AppRelease r) => new(r.Id, r.Platform, r.Version, r.Channel, r.MinSupportedVersion, r.ReleaseNotes, r.DownloadUrl, r.Sha256, r.IsActive, r.PublishedAt);

    // ---------- Announcements ----------
    public async Task<IReadOnlyList<AnnouncementDto>> AnnouncementsAsync(bool activeOnly, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var q = db.Set<Announcement>().AsNoTracking().AsQueryable();
        if (activeOnly)
        {
            var tid = ctx.TenantId;
            q = q.Where(a => a.Published && a.StartsAt <= now && (a.EndsAt == null || a.EndsAt > now) && (a.TenantId == null || a.TenantId == tid));
        }
        return await q.OrderByDescending(a => a.StartsAt).Select(a => new AnnouncementDto(a.Id, a.Title, a.Body, a.Severity.ToString(), a.TenantId, a.StartsAt, a.EndsAt, a.Published)).ToListAsync(ct);
    }

    public async Task<AnnouncementDto> UpsertAnnouncementAsync(Guid? id, UpsertAnnouncementRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Title, "title").NotEmpty(req.Body, "body", 4000).Require(Enum.TryParse<NotificationSeverity>(req.Severity, true, out _), "severity", "Invalid.").ThrowIfInvalid();
        var a = id is { } aid ? await db.Set<Announcement>().SingleOrDefaultAsync(x => x.Id == aid, ct) ?? throw new NotFoundException("announcement.not_found", "Not found.")
            : db.Add(new Announcement()).Entity;
        a.Title = req.Title; a.Body = req.Body; a.Severity = Enum.Parse<NotificationSeverity>(req.Severity, true); a.TenantId = req.TenantId; a.StartsAt = req.StartsAt; a.EndsAt = req.EndsAt; a.Published = req.Published;
        audit.Write("platform.announcement_saved", nameof(Announcement), a.Id.ToString());
        await db.SaveChangesAsync(ct);
        return new AnnouncementDto(a.Id, a.Title, a.Body, a.Severity.ToString(), a.TenantId, a.StartsAt, a.EndsAt, a.Published);
    }

    // ---------- Global configuration ----------
    public async Task<IReadOnlyList<PlatformSettingDto>> SettingsAsync(CancellationToken ct) =>
        await db.Set<PlatformSetting>().OrderBy(s => s.Key).Select(s => new PlatformSettingDto(s.Key, s.Value)).ToListAsync(ct);

    public async Task SetSettingAsync(string key, string value, CancellationToken ct)
    {
        new Validator().NotEmpty(key, "key", 100).Require(key.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-'), "key", "Invalid key.").MaxLength(value, "value", 4000).ThrowIfInvalid();
        var s = await db.Set<PlatformSetting>().SingleOrDefaultAsync(x => x.Key == key, ct) ?? db.Add(new PlatformSetting { Key = key }).Entity;
        s.Value = value;
        audit.Write("platform.setting_changed", nameof(PlatformSetting), key);
        await db.SaveChangesAsync(ct);
    }

    // ---------- Tenant data export ----------
    /// <summary>Exports every tenant-owned table of the caller's company as JSON lines in a zip archive (no credentials included).</summary>
    public async Task<byte[]> ExportTenantAsync(Guid tenantId, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var tables = db.Model.GetEntityTypes().Where(e => typeof(ITenantOwned).IsAssignableFrom(e.ClrType) && e.GetTableName() is not null)
                .Select(e => (Schema: e.GetSchema() ?? "public", Table: e.GetTableName()!)).Distinct().OrderBy(t => t.Schema).ThenBy(t => t.Table).ToList();
            foreach (var (schema, table) in tables)
            {
                var sql = $"SELECT row_to_json(t)::text AS \"Value\" FROM \"{schema}\".\"{table}\" t WHERE t.\"TenantId\" = {{0}}";
                var rows = await db.Database.SqlQueryRaw<string>(sql, tenantId).ToListAsync(ct);
                var entry = zip.CreateEntry($"{schema}/{table}.jsonl", CompressionLevel.Optimal);
                await using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                foreach (var r in rows) await w.WriteLineAsync(r);
            }
            var company = await db.Set<Company>().AsNoTracking().SingleAsync(c => c.Id == tenantId, ct);
            var info = zip.CreateEntry("company.json");
            await using (var w = new StreamWriter(info.Open(), new UTF8Encoding(false)))
                await w.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { company.Id, company.Name, company.LegalName, company.TaxId, exportedAt = clock.UtcNow, format = "jsonl-per-table/v1" }));
            var users = await db.Set<User>().Where(u => u.TenantId == tenantId).Select(u => new { u.Id, u.Email, u.FullName, u.IsActive, u.LastLoginAt }).ToListAsync(ct);
            var ue = zip.CreateEntry("identity/users.json");
            await using (var w = new StreamWriter(ue.Open(), new UTF8Encoding(false))) await w.WriteAsync(System.Text.Json.JsonSerializer.Serialize(users));
        }
        audit.Write("company.data_exported", nameof(Company), tenantId.ToString());
        await db.SaveChangesAsync(ct);
        return ms.ToArray();
    }
}
