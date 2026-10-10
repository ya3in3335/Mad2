using Microsoft.EntityFrameworkCore;
using Veltrix.Contracts.Licensing;
using Veltrix.Contracts.Operations;
using Veltrix.Contracts.Platform;
using Veltrix.Contracts.Subscriptions;
using Veltrix.Modules.Crm;
using Veltrix.Modules.Identity;
using Veltrix.Modules.Licensing;
using Veltrix.Modules.Platform;
using Veltrix.Modules.Subscriptions;
using Veltrix.Modules.Tenants;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Operations;

/// <summary>Aggregate, cross-tenant metrics for the owner (VELTRIX Admin app, Telegram /stats). Counts only — no customer records leave their tenant.</summary>
public sealed class OwnerDashboardService(DbContext db, IClock clock, OperationsOptions options, PlatformService platform, LicenseAdminService licenses,
    SubscriptionService subscriptions, DeviceOpsService devices)
{
    public static string ServerVersion => typeof(OwnerDashboardService).Assembly.GetName().Version?.ToString() ?? "0";

    public async Task<OwnerDashboardDto> GetAsync(int? onlineWithinMinutes, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var window = Math.Clamp(onlineWithinMinutes ?? options.OnlineWindowMinutes, 1, 1440);
        var onlineSince = now.AddMinutes(-window);

        var companies = await db.Set<Company>().AsNoTracking().GroupBy(c => c.Status).Select(g => new { g.Key, C = g.Count() }).ToListAsync(ct);
        var customers = await db.Set<Customer>().IgnoreQueryFilters().CountAsync(c => c.AnonymizedAt == null, ct);
        var subs = await db.Set<Subscription>().IgnoreQueryFilters().AsNoTracking().Select(s => new { s.Status, s.BillingCycle, s.PeriodPrice }).ToListAsync(ct);
        var lic = await db.Set<License>().IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
        var dev = await db.Set<Device>().IgnoreQueryFilters().AsNoTracking().Where(d => d.Status == DeviceStatus.Active)
            .Select(d => new { d.Platform, d.LastHeartbeatAt, d.FailedSyncOperations }).ToListAsync(ct);
        var pendingInvoices = await db.Set<BillingInvoice>().IgnoreQueryFilters().AsNoTracking().Where(i => i.Status == BillingInvoiceStatus.Issued)
            .Select(i => i.Total).ToListAsync(ct);
        var since7 = now.AddDays(-7);
        var newTickets = await db.Set<SupportCase>().CountAsync(c => c.Status == SupportStatus.Open && c.CreatedAt >= since7, ct);
        var openTickets = await db.Set<SupportCase>().CountAsync(c => c.Status == SupportStatus.Open || c.Status == SupportStatus.Pending, ct);
        var unresolved = await db.Set<ErrorIncident>().Where(i => i.Status != IncidentStatuses.Resolved).Select(i => i.Severity).ToListAsync(ct);
        var since24 = now.AddHours(-24);
        var failedSync = await db.Set<IdempotencyRecord>().IgnoreQueryFilters().CountAsync(r => r.StatusCode != 200 && r.CreatedAt >= since24, ct);
        string health;
        try { health = (await platform.HealthAsync(ServerVersion, ct)).Status; }
        catch (Exception e) when (e is not OperationCanceledException) { health = "unknown"; }

        var mrr = subs.Where(s => s.Status == SubscriptionStatus.Active).Sum(s => s.BillingCycle == BillingCycle.Annual ? s.PeriodPrice / 12m : s.PeriodPrice);
        int C(CompanyStatus s) => companies.SingleOrDefault(x => x.Key == s)?.C ?? 0;
        return new OwnerDashboardDto(
            companies.Sum(x => x.C), C(CompanyStatus.Active), C(CompanyStatus.Suspended), customers,
            subs.Count(s => s.Status == SubscriptionStatus.Active), subs.Count(s => s.Status == SubscriptionStatus.Trialing),
            subs.Count(s => s.Status == SubscriptionStatus.PastDue), subs.Count(s => s.Status == SubscriptionStatus.Expired),
            lic.Count(l => l.IsUsable(now)), lic.Count(l => l.Status == LicenseStatus.Suspended),
            lic.Count(l => l.IsUsable(now) && l.ExpiresAt is { } e && e <= now.AddDays(14)),
            dev.Count, dev.Count(d => d.LastHeartbeatAt >= onlineSince), window, dev.Count(d => d.Platform == "android"), dev.Count(d => d.Platform == "windows"),
            Money.Round(mrr), pendingInvoices.Count, Money.Round(pendingInvoices.Sum()),
            newTickets, openTickets, unresolved.Count, unresolved.Count(s => s == DiagnosticSeverities.Critical),
            failedSync, dev.Sum(d => d.FailedSyncOperations ?? 0), health, await LatestReleasesAsync(ct), now);
    }

    /// <summary>Latest active release per platform and channel.</summary>
    public async Task<IReadOnlyList<AppReleaseDto>> LatestReleasesAsync(CancellationToken ct)
    {
        var all = await platform.ReleasesAsync(ct);
        return all.Where(r => r.IsActive).GroupBy(r => (r.Platform, r.Channel))
            .Select(g => g.OrderByDescending(r => System.Version.TryParse(r.Version, out var v) ? v : new System.Version(0, 0)).First())
            .OrderBy(r => r.Platform).ThenBy(r => r.Channel).ToList();
    }

    /// <summary>Tenant 360: aggregates existing services (no duplicated business logic).</summary>
    public async Task<TenantOverviewDto> TenantAsync(Guid tenantId, CancellationToken ct)
    {
        var company = await platform.TenantAsync(tenantId, ct);
        var suspendedReason = await db.Set<Company>().Where(c => c.Id == tenantId).Select(c => c.SuspendedReason).SingleOrDefaultAsync(ct);
        var subId = await db.Set<Subscription>().IgnoreQueryFilters().Where(s => s.TenantId == tenantId).OrderByDescending(s => s.CreatedAt).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
        SubscriptionDto? sub = subId is { } sid ? await subscriptions.GetAsync(sid, ct) : null;
        var lics = (await licenses.ListAsync(new PageQuery(1, 200), tenantId, ct)).Items;
        var devs = (await devices.ListAsync(new PageQuery(1, 500), tenantId, null, null, null, null, null, ct)).Items;
        var history = await db.Set<LicenseEvent>().IgnoreQueryFilters().AsNoTracking().Where(e => e.TenantId == tenantId).OrderByDescending(e => e.OccurredAt).Take(200)
            .Select(e => new LicenseEventDto(e.Id, e.LicenseId, e.DeviceId, e.Type, e.Details, e.OccurredAt)).ToListAsync(ct);
        var invoices = (await subscriptions.InvoicesAsync(new PageQuery(1, 100), tenantId, null, ct)).Items;
        var cases = await db.Set<SupportCase>().AsNoTracking().Where(c => c.TenantId == tenantId).OrderByDescending(c => c.UpdatedAt).Take(100)
            .Select(c => new TenantSupportCaseSummaryDto(c.Id, c.Subject, c.Status.ToString(), c.Priority.ToString(), c.CreatedAt, c.UpdatedAt)).ToListAsync(ct);
        var users = await db.Set<User>().CountAsync(u => u.TenantId == tenantId, ct);
        var customers = await db.Set<Customer>().IgnoreQueryFilters().CountAsync(c => c.TenantId == tenantId && c.AnonymizedAt == null, ct);
        var incidents = await db.Set<ErrorIncident>().CountAsync(i => i.TenantId == tenantId && i.Status != IncidentStatuses.Resolved, ct);
        return new TenantOverviewDto(company, suspendedReason, sub, lics, devs, history, invoices, cases, users, customers, incidents,
            await devices.ConsentAsync(tenantId, ct));
    }
}
