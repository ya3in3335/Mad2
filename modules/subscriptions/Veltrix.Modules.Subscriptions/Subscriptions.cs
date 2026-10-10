using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Veltrix.Contracts.Licensing;
using Veltrix.Contracts.Subscriptions;
using Veltrix.Modules.Licensing;
using Veltrix.Modules.Tenants;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Subscriptions;

public enum BillingCycle { Monthly, Annual, Custom }
public enum SubscriptionStatus { Trialing, Active, PastDue, Cancelled, Expired }
public enum BillingInvoiceStatus { Issued, Paid, Void }

public sealed class SubscriptionPlan : Entity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public decimal MonthlyPrice { get; set; }
    public decimal AnnualPrice { get; set; }
    public string Currency { get; set; } = "DZD";
    public string Features { get; set; } = "";
    public int MaxDevices { get; set; } = 1;
    public int MaxUsers { get; set; } = 5;
    public int MaxBranches { get; set; } = 1;
    public int TrialDays { get; set; } = 14;
    public int OfflineGraceDays { get; set; } = 7;
    public bool IsActive { get; set; } = true;
    public bool IsPublic { get; set; } = true;
}

public sealed class Subscription : TenantEntity
{
    public string PlanCode { get; set; } = "";
    public BillingCycle BillingCycle { get; set; }
    public SubscriptionStatus Status { get; set; }
    public DateTimeOffset CurrentPeriodStart { get; set; }
    public DateTimeOffset CurrentPeriodEnd { get; set; }
    public int Seats { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    public Guid? LicenseId { get; set; }
    public decimal PeriodPrice { get; set; }
    public string Currency { get; set; } = "DZD";
}

public sealed class BillingInvoice : TenantEntity, IImmutableWhenFinal
{
    public string Number { get; set; } = "";
    public Guid SubscriptionId { get; set; }
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public decimal Amount { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public string Currency { get; set; } = "DZD";
    public BillingInvoiceStatus Status { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateOnly DueDate { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public string? PaymentMethod { get; set; }
    public string? PaymentReference { get; set; }
    public Guid? ConfirmedBy { get; set; }
    public string? Description { get; set; }
    /// <summary>Renewal invoices extend the subscription when paid; adjustment invoices (proration) do not.</summary>
    public bool IsRenewal { get; set; }
    public bool IsFinal => true;
}

public sealed class EntitlementChange : TenantEntity
{
    public Guid SubscriptionId { get; set; }
    public string Change { get; set; } = "";
    public string? Details { get; set; }
    public DateTimeOffset At { get; set; }
}

internal sealed class PlanConfig : IEntityTypeConfiguration<SubscriptionPlan>
{
    public void Configure(EntityTypeBuilder<SubscriptionPlan> b)
    {
        b.ToTable("plans", "subscriptions");
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.Code).HasMaxLength(40);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.Features).HasMaxLength(1000);
    }
}

internal sealed class SubscriptionConfig : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> b)
    {
        b.ToTable("subscriptions", "subscriptions");
        b.Property(x => x.PlanCode).HasMaxLength(40);
        b.Property(x => x.BillingCycle).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.HasOne<Company>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BillingInvoiceConfig : IEntityTypeConfiguration<BillingInvoice>
{
    public void Configure(EntityTypeBuilder<BillingInvoice> b)
    {
        b.ToTable("invoices", "subscriptions");
        b.HasIndex(x => x.Number).IsUnique();
        b.Property(x => x.Number).HasMaxLength(40);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.PaymentMethod).HasMaxLength(30);
        b.Property(x => x.PaymentReference).HasMaxLength(100);
        b.Property(x => x.Description).HasMaxLength(300);
        b.HasOne<Subscription>().WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
        b.Ignore(x => x.IsFinal);
    }
}

internal sealed class EntitlementChangeConfig : IEntityTypeConfiguration<EntitlementChange>
{
    public void Configure(EntityTypeBuilder<EntitlementChange> b)
    {
        b.ToTable("entitlement_changes", "subscriptions");
        b.Property(x => x.Change).HasMaxLength(60);
        b.Property(x => x.Details).HasMaxLength(1000);
    }
}

/// <summary>Payment provider abstraction. Only manual confirmation (bank transfer, cash, cheque, CCP/BaridiMob receipts) is
/// implemented; no online gateway is integrated, so the platform never claims automated online payments.</summary>
public interface IPaymentProvider
{
    string Name { get; }
    bool SupportsOnlineCheckout { get; }
}

public sealed class ManualPaymentProvider : IPaymentProvider
{
    public string Name => "manual";
    public bool SupportsOnlineCheckout => false;
}

public sealed class SubscriptionService(DbContext db, ITenantContext ctx, IClock clock, IAuditWriter audit, LicenseAdminService licenses,
    ITenantStatusProvider status, IConfiguration config)
{
    // ---------- Plans ----------
    public async Task<IReadOnlyList<PlanDto>> PlansAsync(bool publicOnly, CancellationToken ct) =>
        (await db.Set<SubscriptionPlan>().Where(p => !publicOnly || p.IsPublic && p.IsActive).OrderBy(p => p.MonthlyPrice).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<PlanDto> UpsertPlanAsync(Guid? id, UpsertPlanRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Code, "code", 40).NotEmpty(req.Name, "name", 100).NonNegative(req.MonthlyPrice, "monthlyPrice").NonNegative(req.AnnualPrice, "annualPrice")
            .Require(req.Features.All(Features.All.Contains), "features", "Unknown feature.").Require(req.MaxDevices is >= 1 and <= 1000, "maxDevices", "1-1000.")
            .Require(req.TrialDays is >= 0 and <= 90, "trialDays", "0-90.").Require(req.OfflineGraceDays is >= 0 and <= 30, "offlineGraceDays", "0-30.").ThrowIfInvalid();
        var code = req.Code.Trim().ToLowerInvariant();
        if (await db.Set<SubscriptionPlan>().AnyAsync(p => p.Code == code && p.Id != id, ct)) throw new ConflictException("plan.code_taken", "Plan code exists.");
        var p = id is { } pid ? await db.Set<SubscriptionPlan>().SingleOrDefaultAsync(x => x.Id == pid, ct) ?? throw new NotFoundException("plan.not_found", "Not found.")
            : db.Add(new SubscriptionPlan()).Entity;
        p.Code = code; p.Name = req.Name; p.Description = req.Description; p.MonthlyPrice = req.MonthlyPrice; p.AnnualPrice = req.AnnualPrice; p.Currency = req.Currency;
        p.Features = string.Join(',', req.Features.Distinct()); p.MaxDevices = req.MaxDevices; p.MaxUsers = req.MaxUsers; p.MaxBranches = req.MaxBranches;
        p.TrialDays = req.TrialDays; p.OfflineGraceDays = req.OfflineGraceDays; p.IsActive = req.IsActive; p.IsPublic = req.IsPublic;
        audit.Write(id is null ? "platform.plan_created" : "platform.plan_updated", nameof(SubscriptionPlan), p.Id.ToString(), req);
        await db.SaveChangesAsync(ct);
        return ToDto(p);
    }

    // ---------- Subscriptions ----------
    public async Task<SubscriptionDto> CreateAsync(CreateSubscriptionRequest req, CancellationToken ct)
    {
        if (!Enum.TryParse<BillingCycle>(req.BillingCycle, true, out var cycle)) throw ValidationException.For("billingCycle", "Monthly, Annual or Custom.");
        var plan = await db.Set<SubscriptionPlan>().SingleOrDefaultAsync(p => p.Code == req.PlanCode && p.IsActive, ct) ?? throw ValidationException.For("planCode", "Unknown plan.");
        if (!await db.Set<Company>().AnyAsync(c => c.Id == req.TenantId, ct)) throw ValidationException.For("tenantId", "Unknown tenant.");
        if (await db.Set<Subscription>().IgnoreQueryFilters().AnyAsync(s => s.TenantId == req.TenantId && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing || s.Status == SubscriptionStatus.PastDue), ct))
            throw new ConflictException("subscription.exists", "The tenant already has an active subscription. Change its plan instead.");
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var now = clock.UtcNow;
        var seats = req.Seats ?? plan.MaxDevices;
        var trial = req.StartWithTrial && plan.TrialDays > 0;
        var end = trial ? now.AddDays(plan.TrialDays) : Advance(now, cycle);
        var price = req.CustomPrice ?? PriceFor(plan, cycle);
        var lic = await licenses.CreateAsync(new CreateLicenseRequest(req.TenantId, trial ? "Trial" : cycle == BillingCycle.Annual ? "Annual" : cycle == BillingCycle.Monthly ? "Monthly" : "Custom",
            PlanDefaults.Plans.ContainsKey(plan.Code) ? plan.Code : "custom", seats, now, end, plan.OfflineGraceDays, plan.Features.Split(',', StringSplitOptions.RemoveEmptyEntries), $"Subscription {plan.Code}"), ct);
        var sub = new Subscription
        {
            TenantId = req.TenantId, PlanCode = plan.Code, BillingCycle = cycle, Status = trial ? SubscriptionStatus.Trialing : SubscriptionStatus.PastDue,
            CurrentPeriodStart = now, CurrentPeriodEnd = end, Seats = seats, LicenseId = lic.License.Id, PeriodPrice = price, Currency = plan.Currency,
        };
        db.Add(sub);
        History(sub, "created", $"{plan.Code} {cycle} seats={seats} trial={trial}");
        if (!trial) db.Add(NewInvoice(sub, now, end, price, 0, $"{plan.Name} — {cycle}", renewal: false));
        audit.Write("platform.subscription_created", nameof(Subscription), sub.Id.ToString(), new { req.TenantId, plan.Code, cycle, trial });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        status.Invalidate(req.TenantId);
        return await GetAsync(sub.Id, ct);
    }

    /// <summary>Issue the next-period renewal invoice.</summary>
    public async Task<BillingInvoiceDto> IssueRenewalAsync(Guid subscriptionId, IssueInvoiceRequest req, CancellationToken ct)
    {
        var sub = await LoadAsync(subscriptionId, ct);
        if (sub.Status is SubscriptionStatus.Cancelled or SubscriptionStatus.Expired && sub.CancelAtPeriodEnd)
            throw new DomainException("subscription.cancelled", "Cancelled subscriptions cannot be renewed.");
        if (await db.Set<BillingInvoice>().IgnoreQueryFilters().AnyAsync(i => i.SubscriptionId == sub.Id && i.Status == BillingInvoiceStatus.Issued && i.IsRenewal, ct))
            throw new ConflictException("invoice.open", "An unpaid renewal invoice already exists.");
        var start = sub.CurrentPeriodEnd > clock.UtcNow ? sub.CurrentPeriodEnd : clock.UtcNow;
        var tax = Money.Round(sub.PeriodPrice * (req.TaxPercent ?? 0) / 100m);
        var inv = NewInvoice(sub, start, Advance(start, sub.BillingCycle), sub.PeriodPrice, tax, $"Renewal {sub.PlanCode} {sub.BillingCycle}", renewal: true);
        db.Add(inv);
        audit.Write("platform.invoice_issued", nameof(BillingInvoice), inv.Id.ToString(), new { inv.Total });
        await db.SaveChangesAsync(ct);
        return await InvoiceAsync(inv.Id, ct);
    }

    /// <summary>Manual payment confirmation by an authorized vendor administrator. A paid renewal extends the period and the license.</summary>
    public async Task<BillingInvoiceDto> ConfirmPaymentAsync(Guid invoiceId, ConfirmPaymentRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Method, "method", 30).MaxLength(req.Reference, "reference", 100).ThrowIfInvalid();
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var inv = await db.Set<BillingInvoice>().IgnoreQueryFilters().SingleOrDefaultAsync(i => i.Id == invoiceId, ct) ?? throw new NotFoundException("invoice.not_found", "Invoice not found.");
        if (inv.Status != BillingInvoiceStatus.Issued) throw new ConflictException("invoice.state", "Only issued invoices can be paid.");
        var sub = await LoadAsync(inv.SubscriptionId, ct);
        inv.Status = BillingInvoiceStatus.Paid; inv.PaidAt = req.PaidAt ?? clock.UtcNow; inv.PaymentMethod = req.Method; inv.PaymentReference = req.Reference; inv.ConfirmedBy = ctx.UserId;
        if (inv.IsRenewal)
        {
            sub.CurrentPeriodStart = inv.PeriodStart; sub.CurrentPeriodEnd = inv.PeriodEnd;
            History(sub, "renewed", $"until {inv.PeriodEnd:yyyy-MM-dd}");
        }
        if (sub.Status is SubscriptionStatus.PastDue or SubscriptionStatus.Trialing or SubscriptionStatus.Expired) sub.Status = SubscriptionStatus.Active;
        if (sub.LicenseId is { } lid)
        {
            var lic = await licenses.GetAsync(lid, ct);
            await licenses.UpdateAsync(lid, new UpdateLicenseRequest(sub.Seats, sub.CurrentPeriodEnd, lic.OfflineGraceDays, lic.Entitlements, lic.Notes), ct);
        }
        audit.Write("platform.payment_confirmed", nameof(BillingInvoice), inv.Id.ToString(), new { inv.Total, req.Method, req.Reference });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        status.Invalidate(sub.TenantId);
        return await InvoiceAsync(inv.Id, ct);
    }

    public async Task<BillingInvoiceDto> VoidInvoiceAsync(Guid invoiceId, CancellationToken ct)
    {
        var inv = await db.Set<BillingInvoice>().IgnoreQueryFilters().SingleOrDefaultAsync(i => i.Id == invoiceId, ct) ?? throw new NotFoundException("invoice.not_found", "Invoice not found.");
        if (inv.Status != BillingInvoiceStatus.Issued) throw new ConflictException("invoice.state", "Only unpaid invoices can be voided.");
        inv.Status = BillingInvoiceStatus.Void;
        audit.Write("platform.invoice_voided", nameof(BillingInvoice), inv.Id.ToString());
        await db.SaveChangesAsync(ct);
        return await InvoiceAsync(invoiceId, ct);
    }

    /// <summary>Upgrade/downgrade. Entitlements and seats apply immediately; optional prorated invoice for the remaining period.</summary>
    public async Task<SubscriptionDto> ChangePlanAsync(Guid id, ChangePlanRequest req, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var sub = await LoadAsync(id, ct);
        var plan = await db.Set<SubscriptionPlan>().SingleOrDefaultAsync(p => p.Code == req.PlanCode && p.IsActive, ct) ?? throw ValidationException.For("planCode", "Unknown plan.");
        var cycle = req.BillingCycle is null ? sub.BillingCycle : Enum.TryParse<BillingCycle>(req.BillingCycle, true, out var c) ? c : throw ValidationException.For("billingCycle", "Invalid.");
        var oldPrice = sub.PeriodPrice;
        var newPrice = PriceFor(plan, cycle);
        var now = clock.UtcNow;
        if (req.ProrateInvoice && newPrice > oldPrice && sub.CurrentPeriodEnd > now)
        {
            var remaining = (decimal)(sub.CurrentPeriodEnd - now).TotalDays / Math.Max(1m, (decimal)(sub.CurrentPeriodEnd - sub.CurrentPeriodStart).TotalDays);
            db.Add(NewInvoice(sub, now, sub.CurrentPeriodEnd, Money.Round((newPrice - oldPrice) * remaining), 0, $"Upgrade {sub.PlanCode} → {plan.Code} (prorated)", renewal: false));
        }
        History(sub, newPrice >= oldPrice ? "upgraded" : "downgraded", $"{sub.PlanCode} → {plan.Code}");
        sub.PlanCode = plan.Code; sub.BillingCycle = cycle; sub.PeriodPrice = newPrice; sub.Seats = Math.Max(sub.Seats, plan.MaxDevices);
        if (sub.LicenseId is { } lid)
        {
            var lic = await licenses.GetAsync(lid, ct);
            await licenses.UpdateAsync(lid, new UpdateLicenseRequest(sub.Seats, lic.ExpiresAt, plan.OfflineGraceDays, plan.Features.Split(',', StringSplitOptions.RemoveEmptyEntries), lic.Notes), ct);
        }
        audit.Write("platform.subscription_plan_changed", nameof(Subscription), sub.Id.ToString(), req);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        status.Invalidate(sub.TenantId);
        return await GetAsync(id, ct);
    }

    public async Task<SubscriptionDto> ChangeSeatsAsync(Guid id, int seats, CancellationToken ct)
    {
        if (seats is < 1 or > 1000) throw ValidationException.For("seats", "1-1000.");
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var sub = await LoadAsync(id, ct);
        History(sub, "seats_changed", $"{sub.Seats} → {seats}");
        sub.Seats = seats;
        if (sub.LicenseId is { } lid)
        {
            var lic = await licenses.GetAsync(lid, ct);
            if (seats < lic.ActiveDevices) throw new DomainException("subscription.seats_in_use", $"{lic.ActiveDevices} devices are active; revoke devices before reducing seats.");
            await licenses.UpdateAsync(lid, new UpdateLicenseRequest(seats, lic.ExpiresAt, lic.OfflineGraceDays, lic.Entitlements, lic.Notes), ct);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Cancellation never deletes business data. Immediate cancellation ends access to write operations; data stays readable.</summary>
    public async Task<SubscriptionDto> CancelAsync(Guid id, CancelSubscriptionRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Reason, "reason", 300).ThrowIfInvalid();
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var sub = await LoadAsync(id, ct);
        if (req.Immediately)
        {
            sub.Status = SubscriptionStatus.Cancelled;
            sub.CurrentPeriodEnd = clock.UtcNow;
            if (sub.LicenseId is { } lid)
            {
                var lic = await licenses.GetAsync(lid, ct);
                await licenses.UpdateAsync(lid, new UpdateLicenseRequest(lic.MaxDevices, clock.UtcNow, lic.OfflineGraceDays, lic.Entitlements, lic.Notes), ct);
            }
        }
        else sub.CancelAtPeriodEnd = true;
        History(sub, req.Immediately ? "cancelled" : "cancel_at_period_end", req.Reason);
        audit.Write("platform.subscription_cancelled", nameof(Subscription), sub.Id.ToString(), req);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        status.Invalidate(sub.TenantId);
        return await GetAsync(id, ct);
    }

    /// <summary>Periodic maintenance (worker): expire ended trials/periods and flag past-due subscriptions.</summary>
    public async Task<int> RefreshStatusesAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var subs = await db.Set<Subscription>().IgnoreQueryFilters().Where(s => s.Status != SubscriptionStatus.Cancelled && s.Status != SubscriptionStatus.Expired && s.CurrentPeriodEnd < now).ToListAsync(ct);
        foreach (var s in subs)
        {
            var paidNext = await db.Set<BillingInvoice>().IgnoreQueryFilters().AnyAsync(i => i.SubscriptionId == s.Id && i.Status == BillingInvoiceStatus.Paid && i.PeriodEnd > now, ct);
            if (paidNext) continue;
            var newStatus = s.CancelAtPeriodEnd || now > s.CurrentPeriodEnd.AddDays(LicenseTenantStatusProvider.RenewalGraceDays) ? SubscriptionStatus.Expired : SubscriptionStatus.PastDue;
            if (newStatus == s.Status) continue;
            s.Status = newStatus;
            History(s, newStatus == SubscriptionStatus.Expired ? "expired" : "past_due", null);
            status.Invalidate(s.TenantId);
        }
        await db.SaveChangesAsync(ct);
        return subs.Count;
    }

    // ---------- Queries ----------
    public async Task<PagedResult<SubscriptionDto>> ListAsync(PageQuery q, string? statusFilter, CancellationToken ct)
    {
        var query = db.Set<Subscription>().IgnoreQueryFilters().AsQueryable();
        if (statusFilter is not null && Enum.TryParse<SubscriptionStatus>(statusFilter, true, out var s)) query = query.Where(x => x.Status == s);
        var page = await query.OrderByDescending(x => x.CreatedAt).Select(x => x.Id).ToPagedAsync(q, ct);
        var items = new List<SubscriptionDto>();
        foreach (var id in page.Items) items.Add(await GetAsync(id, ct));
        return new PagedResult<SubscriptionDto>(items, page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<SubscriptionDto> GetAsync(Guid id, CancellationToken ct)
    {
        var s = await db.Set<Subscription>().IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("subscription.not_found", "Not found.");
        if (!ctx.IsPlatformAdmin && s.TenantId != ctx.TenantId) throw new NotFoundException("subscription.not_found", "Not found.");
        var name = await db.Set<Company>().Where(c => c.Id == s.TenantId).Select(c => c.Name).SingleOrDefaultAsync(ct);
        return new SubscriptionDto(s.Id, s.TenantId, name, s.PlanCode, s.BillingCycle.ToString(), s.Status.ToString(), s.CurrentPeriodStart, s.CurrentPeriodEnd, s.Seats,
            s.CancelAtPeriodEnd, s.LicenseId, s.PeriodPrice, s.Currency, s.CreatedAt);
    }

    public async Task<PagedResult<BillingInvoiceDto>> InvoicesAsync(PageQuery q, Guid? tenantId, string? statusFilter, CancellationToken ct)
    {
        var query = ctx.IsPlatformAdmin ? db.Set<BillingInvoice>().IgnoreQueryFilters() : db.Set<BillingInvoice>();
        if (tenantId is { } t) query = query.Where(i => i.TenantId == t);
        if (statusFilter is not null && Enum.TryParse<BillingInvoiceStatus>(statusFilter, true, out var s)) query = query.Where(i => i.Status == s);
        var page = await query.OrderByDescending(i => i.IssuedAt).ToPagedAsync(q, ct);
        var names = await db.Set<Company>().Where(c => page.Items.Select(i => i.TenantId).Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return new PagedResult<BillingInvoiceDto>(page.Items.Select(i => ToDto(i, names.GetValueOrDefault(i.TenantId))).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<BillingInvoiceDto> InvoiceAsync(Guid id, CancellationToken ct)
    {
        var query = ctx.IsPlatformAdmin ? db.Set<BillingInvoice>().IgnoreQueryFilters() : db.Set<BillingInvoice>();
        var i = await query.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("invoice.not_found", "Not found.");
        return ToDto(i, await db.Set<Company>().Where(c => c.Id == i.TenantId).Select(c => c.Name).SingleOrDefaultAsync(ct));
    }

    public async Task<IReadOnlyList<EntitlementChangeDto>> HistoryAsync(Guid subscriptionId, CancellationToken ct) =>
        await db.Set<EntitlementChange>().IgnoreQueryFilters().Where(h => h.SubscriptionId == subscriptionId).OrderByDescending(h => h.At)
            .Select(h => new EntitlementChangeDto(h.Id, h.SubscriptionId, h.Change, h.Details, h.At, h.CreatedBy)).ToListAsync(ct);

    public async Task<MySubscriptionDto> MineAsync(CancellationToken ct)
    {
        var tid = ctx.RequireTenant();
        var sub = await db.Set<Subscription>().OrderByDescending(s => s.CreatedAt).FirstOrDefaultAsync(ct);
        var plan = sub is null ? null : await db.Set<SubscriptionPlan>().SingleOrDefaultAsync(p => p.Code == sub.PlanCode, ct);
        var invoices = (await InvoicesAsync(new PageQuery(1, 50), tid, null, ct)).Items;
        return new MySubscriptionDto(sub is null ? null : await GetAsync(sub.Id, ct), plan is null ? null : ToDto(plan), invoices,
            config["Billing:PaymentInstructions"] ?? "Payments are confirmed manually by your vendor. Contact your vendor for bank transfer details.");
    }

    // ---------- helpers ----------
    private async Task<Subscription> LoadAsync(Guid id, CancellationToken ct) =>
        await db.Set<Subscription>().IgnoreQueryFilters().SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("subscription.not_found", "Not found.");

    private void History(Subscription s, string change, string? details) =>
        db.Add(new EntitlementChange { TenantId = s.TenantId, SubscriptionId = s.Id, Change = change, Details = details, At = clock.UtcNow });

    private BillingInvoice NewInvoice(Subscription s, DateTimeOffset start, DateTimeOffset end, decimal amount, decimal tax, string description, bool renewal) => new()
    {
        TenantId = s.TenantId, SubscriptionId = s.Id, Number = $"VLX-{clock.UtcNow:yyyyMM}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
        PeriodStart = start, PeriodEnd = end, Amount = amount, Tax = tax, Total = amount + tax, Currency = s.Currency, Status = BillingInvoiceStatus.Issued,
        IssuedAt = clock.UtcNow, DueDate = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime.AddDays(15)), Description = description, IsRenewal = renewal,
    };

    private static DateTimeOffset Advance(DateTimeOffset from, BillingCycle c) => c == BillingCycle.Annual ? from.AddYears(1) : from.AddMonths(1);
    private static decimal PriceFor(SubscriptionPlan p, BillingCycle c) => c == BillingCycle.Annual ? p.AnnualPrice : p.MonthlyPrice;

    private static PlanDto ToDto(SubscriptionPlan p) => new(p.Id, p.Code, p.Name, p.Description, p.MonthlyPrice, p.AnnualPrice, p.Currency,
        p.Features.Split(',', StringSplitOptions.RemoveEmptyEntries), p.MaxDevices, p.MaxUsers, p.MaxBranches, p.TrialDays, p.OfflineGraceDays, p.IsActive, p.IsPublic);

    private static BillingInvoiceDto ToDto(BillingInvoice i, string? tenant) => new(i.Id, i.Number, i.TenantId, tenant, i.SubscriptionId, i.PeriodStart, i.PeriodEnd, i.Amount,
        i.Tax, i.Total, i.Currency, i.Status.ToString(), i.IssuedAt, i.DueDate, i.PaidAt, i.PaymentMethod, i.PaymentReference, i.Description);
}

public sealed class SubscriptionsModule : IModule
{
    public string Name => "subscriptions";

    public void Register(IServiceCollection services)
    {
        services.AddScoped<SubscriptionService>();
        services.AddSingleton<IPaymentProvider, ManualPaymentProvider>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/plans", (SubscriptionService s, CancellationToken ct) => s.PlansAsync(true, ct)).WithTags("Subscriptions").AllowAnonymous();
        var my = app.MapModuleGroup("subscription", "Subscriptions");
        my.MapGet("/", (SubscriptionService s, CancellationToken ct) => s.MineAsync(ct));

        var p = app.MapGroup("/api/v1/platform").WithTags("Platform").RequirePermission(Permissions.PlatformSubscriptions);
        p.MapGet("/plans", (SubscriptionService s, CancellationToken ct) => s.PlansAsync(false, ct));
        p.MapPost("/plans", (UpsertPlanRequest r, SubscriptionService s, CancellationToken ct) => s.UpsertPlanAsync(null, r, ct));
        p.MapPut("/plans/{id:guid}", (Guid id, UpsertPlanRequest r, SubscriptionService s, CancellationToken ct) => s.UpsertPlanAsync(id, r, ct));
        p.MapGet("/subscriptions", ([AsParameters] PageQuery q, string? status, SubscriptionService s, CancellationToken ct) => s.ListAsync(q, status, ct));
        p.MapGet("/subscriptions/{id:guid}", (Guid id, SubscriptionService s, CancellationToken ct) => s.GetAsync(id, ct));
        p.MapPost("/subscriptions", (CreateSubscriptionRequest r, SubscriptionService s, CancellationToken ct) => s.CreateAsync(r, ct));
        p.MapPost("/subscriptions/{id:guid}/renewal-invoice", (Guid id, IssueInvoiceRequest r, SubscriptionService s, CancellationToken ct) => s.IssueRenewalAsync(id, r, ct));
        p.MapPost("/subscriptions/{id:guid}/plan", (Guid id, ChangePlanRequest r, SubscriptionService s, CancellationToken ct) => s.ChangePlanAsync(id, r, ct));
        p.MapPost("/subscriptions/{id:guid}/seats", (Guid id, ChangeSeatsRequest r, SubscriptionService s, CancellationToken ct) => s.ChangeSeatsAsync(id, r.Seats, ct));
        p.MapPost("/subscriptions/{id:guid}/cancel", (Guid id, CancelSubscriptionRequest r, SubscriptionService s, CancellationToken ct) => s.CancelAsync(id, r, ct)).RequireStepUp();
        p.MapGet("/subscriptions/{id:guid}/history", (Guid id, SubscriptionService s, CancellationToken ct) => s.HistoryAsync(id, ct));
        p.MapGet("/invoices", ([AsParameters] PageQuery q, Guid? tenantId, string? status, SubscriptionService s, CancellationToken ct) => s.InvoicesAsync(q, tenantId, status, ct));
        p.MapPost("/invoices/{id:guid}/confirm-payment", (Guid id, ConfirmPaymentRequest r, SubscriptionService s, CancellationToken ct) => s.ConfirmPaymentAsync(id, r, ct)).RequireStepUp();
        p.MapPost("/invoices/{id:guid}/void", (Guid id, SubscriptionService s, CancellationToken ct) => s.VoidInvoiceAsync(id, ct));
    }
}
