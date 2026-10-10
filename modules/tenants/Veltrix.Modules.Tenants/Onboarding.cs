using Microsoft.EntityFrameworkCore;
using Veltrix.Contracts.Tenants;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Tenants;

public static class SetupTokens
{
    public static string New() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
}

/// <summary>Completes first-run company configuration. Runs every module's onboarding contributor in one transaction.</summary>
public sealed class OnboardingService(
    DbContext db, TenantContext ctx, IEnumerable<IOnboardingContributor> contributors, IClock clock, IAuditWriter audit,
    ITenantStatusProvider status)
{
    public async Task<OnboardingResult> CompleteAsync(CompleteOnboardingRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.SetupToken, "setupToken", 100).NotEmpty(req.CompanyName, "companyName")
            .NotEmpty(req.OwnerEmail, "ownerEmail", 256).Require(req.OwnerEmail?.Contains('@') == true, "ownerEmail", "Invalid email.")
            .NotEmpty(req.OwnerFullName, "ownerFullName").NotEmpty(req.HeadOfficeName, "headOfficeName", 150)
            .Require(req.Currency is { Length: 3 }, "currency", "Use an ISO 4217 code.")
            .Require(req.Locale is "ar-DZ" or "fr-DZ" or "en-US" or "en", "locale", "Unsupported locale.")
            .ThrowIfInvalid();

        var hash = SetupTokens.Hash(req.SetupToken);
        var company = await db.Set<Company>().SingleOrDefaultAsync(c => c.SetupTokenHash == hash, ct);
        if (company is null || company.SetupTokenExpiresAt < clock.UtcNow)
            throw new UnauthorizedException("onboarding.invalid_token", "The setup token is invalid or has expired. Re-activate the license to obtain a new one.");
        if (company.SetupCompleted) throw new ConflictException("onboarding.completed", "This company is already configured.");
        if (company.Status == CompanyStatus.Suspended) throw new ForbiddenException("tenant.suspended", "This company is suspended.");

        // Execute as the tenant so tenant-owned entities receive the correct TenantId.
        ctx.RunAs(company.Id);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        company.Name = req.CompanyName.Trim(); company.LegalName = req.LegalName; company.TaxId = req.TaxId;
        company.TradeRegisterNo = req.TradeRegisterNo; company.Address = req.Address; company.City = req.City; company.Wilaya = req.Wilaya;
        company.Phone = req.Phone; company.Currency = req.Currency.ToUpperInvariant(); company.Locale = req.Locale;

        var branch = new Branch { TenantId = company.Id, Code = "HQ", Name = req.HeadOfficeName.Trim(), IsHeadOffice = true, Address = req.Address, Phone = req.Phone };
        var wh = new Warehouse { TenantId = company.Id, BranchId = branch.Id, Code = "HQ-W1", Name = req.HeadOfficeName.Trim() + " — Stock", IsDefault = true };
        db.Set<Branch>().Add(branch);
        db.Set<Warehouse>().Add(wh);
        db.Set<TenantSetting>().Add(new TenantSetting { TenantId = company.Id, Key = SettingKeys.DefaultCurrency, Value = $"\"{company.Currency}\"" });
        db.Set<TenantSetting>().Add(new TenantSetting { TenantId = company.Id, Key = SettingKeys.Locale, Value = $"\"{company.Locale}\"" });
        await db.SaveChangesAsync(ct);

        var context = new OnboardingContext(company.Id, branch.Id, wh.Id, company.Name, company.Currency, company.Locale,
            req.OwnerEmail.Trim(), req.OwnerFullName.Trim(), req.OwnerPassword, req.OwnerLanguage, req.ApplyAlgeriaTaxTemplate);
        foreach (var c in contributors.OrderBy(c => c.Order))
            await c.ContributeAsync(context, ct);

        company.SetupCompleted = true;
        company.SetupTokenHash = null;
        company.SetupTokenExpiresAt = null;
        if (company.Status == CompanyStatus.Pending) company.Status = CompanyStatus.Active;
        audit.Write("onboarding.completed", nameof(Company), company.Id.ToString(), new { req.CompanyName, req.OwnerEmail });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        status.Invalidate(company.Id);
        return new OnboardingResult(company.Id, branch.Id, wh.Id);
    }
}

/// <summary>Vendor-side tenant administration (Control Center). Requires platform permissions.</summary>
public sealed class TenantAdminService(DbContext db, IClock clock, IAuditWriter audit, ITenantStatusProvider status)
{
    public async Task<CreateTenantResponse> CreateAsync(CreateTenantRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Name, "name").MaxLength(req.Email, "email", 256).ThrowIfInvalid();
        var token = SetupTokens.New();
        var c = new Company
        {
            Name = req.Name.Trim(), Email = req.Email, Phone = req.Phone, ContactName = req.ContactName, Wilaya = req.Wilaya, Notes = req.Notes,
            Status = CompanyStatus.Pending, SetupTokenHash = SetupTokens.Hash(token), SetupTokenExpiresAt = clock.UtcNow.AddDays(14),
        };
        db.Set<Company>().Add(c);
        audit.Write("platform.tenant_created", nameof(Company), c.Id.ToString(), new { c.Name });
        await db.SaveChangesAsync(ct);
        return new CreateTenantResponse(c.Id, token, c.SetupTokenExpiresAt!.Value);
    }

    /// <summary>Issue a fresh setup token for a tenant that has not completed onboarding (used by license activation).</summary>
    public async Task<(string Token, DateTimeOffset Expires)?> IssueSetupTokenIfPendingAsync(Guid tenantId, CancellationToken ct)
    {
        var c = await db.Set<Company>().SingleOrDefaultAsync(x => x.Id == tenantId, ct);
        if (c is null || c.SetupCompleted) return null;
        var token = SetupTokens.New();
        c.SetupTokenHash = SetupTokens.Hash(token);
        c.SetupTokenExpiresAt = clock.UtcNow.AddHours(24);
        await db.SaveChangesAsync(ct);
        return (token, c.SetupTokenExpiresAt.Value);
    }

    public async Task SetSuspendedAsync(Guid tenantId, bool suspended, string? reason, CancellationToken ct)
    {
        var c = await db.Set<Company>().SingleOrDefaultAsync(x => x.Id == tenantId, ct) ?? throw new NotFoundException("tenant.not_found", "Tenant not found.");
        if (suspended)
        {
            new Validator().NotEmpty(reason, "reason", 500).ThrowIfInvalid();
            c.Status = CompanyStatus.Suspended; c.SuspendedReason = reason;
        }
        else
        {
            c.Status = c.SetupCompleted ? CompanyStatus.Active : CompanyStatus.Pending; c.SuspendedReason = null;
        }
        audit.Write(suspended ? "platform.tenant_suspended" : "platform.tenant_reactivated", nameof(Company), c.Id.ToString(), new { reason });
        await db.SaveChangesAsync(ct);
        status.Invalidate(tenantId);
    }
}
