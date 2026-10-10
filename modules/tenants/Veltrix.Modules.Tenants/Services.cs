using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Veltrix.Contracts.Tenants;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Tenants;

public sealed class CompanyService(DbContext db, ITenantContext ctx, IAuditWriter audit)
{
    public async Task<CompanyDto> GetAsync(CancellationToken ct)
    {
        var tid = ctx.RequireTenant();
        var c = await db.Set<Company>().SingleOrDefaultAsync(x => x.Id == tid, ct) ?? throw new NotFoundException("company.not_found", "Company not found.");
        return ToDto(c);
    }

    public async Task<CompanyDto> UpdateAsync(UpdateCompanyRequest req, CancellationToken ct)
    {
        var tid = ctx.RequireTenant();
        new Validator().NotEmpty(req.Name, "name").MaxLength(req.LegalName, "legalName", 200).MaxLength(req.TaxId, "taxId", 50)
            .MaxLength(req.TradeRegisterNo, "tradeRegisterNo", 50).MaxLength(req.Address, "address", 300)
            .Require(req.FiscalYearStartMonth is null or (>= 1 and <= 12), "fiscalYearStartMonth", "Must be 1-12.")
            .Require(req.Locale is null or "ar-DZ" or "fr-DZ" or "en-US" or "en", "locale", "Unsupported locale.")
            .ThrowIfInvalid();
        var c = await db.Set<Company>().SingleAsync(x => x.Id == tid, ct);
        c.Name = req.Name.Trim(); c.LegalName = req.LegalName; c.TradeRegisterNo = req.TradeRegisterNo; c.TaxId = req.TaxId;
        c.StatisticalId = req.StatisticalId; c.ArticleNo = req.ArticleNo; c.Address = req.Address; c.City = req.City;
        c.Wilaya = req.Wilaya; c.Phone = req.Phone; c.Email = req.Email; c.BrandColor = req.BrandColor;
        if (req.Locale is not null) c.Locale = req.Locale;
        if (req.FiscalYearStartMonth is { } m) c.FiscalYearStartMonth = m;
        audit.Write("company.updated", nameof(Company), c.Id.ToString());
        await db.SaveChangesAsync(ct);
        return ToDto(c);
    }

    public static CompanyDto ToDto(Company c) => new(c.Id, c.Name, c.LegalName, c.TradeRegisterNo, c.TaxId, c.StatisticalId, c.ArticleNo,
        c.Address, c.City, c.Wilaya, c.Country, c.Phone, c.Email, c.Currency, c.Locale, c.TimeZone, c.BrandColor, c.Status.ToString(),
        c.SetupCompleted, c.FiscalYearStartMonth);
}

public sealed class BranchService(DbContext db, ITenantContext ctx, IAuditWriter audit)
{
    public async Task<IReadOnlyList<BranchDto>> ListAsync(CancellationToken ct)
    {
        var list = await db.Set<Branch>().OrderByDescending(b => b.IsHeadOffice).ThenBy(b => b.Name).ToListAsync(ct);
        return list.Where(b => ctx.CanAccessBranch(b.Id)).Select(ToDto).ToList();
    }

    public async Task<BranchDto> UpsertAsync(Guid? id, UpsertBranchRequest req, CancellationToken ct)
    {
        var tid = ctx.RequireTenant();
        new Validator().NotEmpty(req.Code, "code", 20).NotEmpty(req.Name, "name", 150).ThrowIfInvalid();
        var code = req.Code.Trim().ToUpperInvariant();
        Branch branch;
        if (id is { } bid)
        {
            branch = await db.Set<Branch>().SingleOrDefaultAsync(b => b.Id == bid, ct) ?? throw new NotFoundException("branch.not_found", "Branch not found.");
            ctx.EnsureBranch(bid);
            if (branch.IsHeadOffice && !req.IsActive) throw new DomainException("branch.head_office", "The head office cannot be deactivated.");
        }
        if (await db.Set<Branch>().AnyAsync(b => b.Code == code && b.Id != id, ct))
            throw new ConflictException("branch.code_taken", "Branch code already exists.");
        if (id is not null) branch = await db.Set<Branch>().SingleAsync(b => b.Id == id, ct);
        else
        {
            branch = new Branch { TenantId = tid };
            db.Set<Branch>().Add(branch);
            db.Set<Warehouse>().Add(new Warehouse { TenantId = tid, BranchId = branch.Id, Code = code + "-W1", Name = req.Name.Trim() + " — Stock", IsDefault = true });
        }
        branch.Code = code; branch.Name = req.Name.Trim(); branch.Address = req.Address; branch.Phone = req.Phone; branch.IsActive = req.IsActive;
        audit.Write(id is null ? "branch.created" : "branch.updated", nameof(Branch), branch.Id.ToString());
        await db.SaveChangesAsync(ct);
        return ToDto(branch);
    }

    public async Task<IReadOnlyList<WarehouseDto>> ListWarehousesAsync(CancellationToken ct)
    {
        var list = await db.Set<Warehouse>().OrderBy(w => w.Name).ToListAsync(ct);
        return list.Where(w => ctx.CanAccessBranch(w.BranchId)).Select(ToDto).ToList();
    }

    public async Task<WarehouseDto> UpsertWarehouseAsync(Guid? id, UpsertWarehouseRequest req, CancellationToken ct)
    {
        var tid = ctx.RequireTenant();
        new Validator().NotEmpty(req.Code, "code", 20).NotEmpty(req.Name, "name", 150).ThrowIfInvalid();
        if (!await db.Set<Branch>().AnyAsync(b => b.Id == req.BranchId, ct)) throw ValidationException.For("branchId", "Unknown branch.");
        ctx.EnsureBranch(req.BranchId);
        var code = req.Code.Trim().ToUpperInvariant();
        if (await db.Set<Warehouse>().AnyAsync(w => w.Code == code && w.Id != id, ct))
            throw new ConflictException("warehouse.code_taken", "Warehouse code already exists.");
        Warehouse wh;
        if (id is { } wid)
            wh = await db.Set<Warehouse>().SingleOrDefaultAsync(w => w.Id == wid, ct) ?? throw new NotFoundException("warehouse.not_found", "Warehouse not found.");
        else
        {
            wh = new Warehouse { TenantId = tid };
            db.Set<Warehouse>().Add(wh);
        }
        if (req.IsDefault)
        {
            var others = await db.Set<Warehouse>().Where(w => w.BranchId == req.BranchId && w.Id != wh.Id && w.IsDefault).ToListAsync(ct);
            others.ForEach(o => o.IsDefault = false);
        }
        wh.BranchId = req.BranchId; wh.Code = code; wh.Name = req.Name.Trim(); wh.IsDefault = req.IsDefault; wh.IsActive = req.IsActive;
        audit.Write(id is null ? "warehouse.created" : "warehouse.updated", nameof(Warehouse), wh.Id.ToString());
        await db.SaveChangesAsync(ct);
        return ToDto(wh);
    }

    public static BranchDto ToDto(Branch b) => new(b.Id, b.Code, b.Name, b.Address, b.Phone, b.IsHeadOffice, b.IsActive);
    public static WarehouseDto ToDto(Warehouse w) => new(w.Id, w.BranchId, w.Code, w.Name, w.IsDefault, w.IsActive);
}

public sealed class BranchDirectory(DbContext db) : IBranchDirectory
{
    public async Task<IReadOnlyList<Guid>> GetBranchIdsAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.Set<Branch>().IgnoreQueryFilters().Where(b => b.TenantId == tenantId).Select(b => b.Id).ToListAsync(ct);

    public Task<bool> WarehouseBelongsToTenantAsync(Guid tenantId, Guid warehouseId, CancellationToken ct = default) =>
        db.Set<Warehouse>().IgnoreQueryFilters().AnyAsync(w => w.Id == warehouseId && w.TenantId == tenantId, ct);

    public async Task<Guid?> GetWarehouseBranchAsync(Guid warehouseId, CancellationToken ct = default) =>
        await db.Set<Warehouse>().Where(w => w.Id == warehouseId).Select(w => (Guid?)w.BranchId).SingleOrDefaultAsync(ct);

    public async Task<Guid?> GetDefaultWarehouseAsync(Guid branchId, CancellationToken ct = default) =>
        await db.Set<Warehouse>().Where(w => w.BranchId == branchId && w.IsActive).OrderByDescending(w => w.IsDefault)
            .Select(w => (Guid?)w.Id).FirstOrDefaultAsync(ct);
}

public interface ISettingsService
{
    Task<T> GetAsync<T>(string key, T defaultValue, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, CancellationToken ct = default);
    Task<IReadOnlyList<SettingDto>> ListAsync(CancellationToken ct = default);
}

public sealed class SettingsService(DbContext db, ITenantContext ctx, IAuditWriter audit) : ISettingsService
{
    private static readonly HashSet<string> Allowed =
    [
        SettingKeys.AllowNegativeStock, SettingKeys.CashRoundingIncrement, SettingKeys.ReceiptHeader, SettingKeys.ReceiptFooter,
        SettingKeys.ReceiptWidthChars, SettingKeys.PricesIncludeTax, SettingKeys.DefaultCurrency, SettingKeys.Locale,
        SettingKeys.LargeDiscountThresholdPct, SettingKeys.MaxDiscountWithoutPermissionPct, SettingKeys.DiagnosticsConsent,
    ];

    public async Task<T> GetAsync<T>(string key, T defaultValue, CancellationToken ct = default)
    {
        var s = await db.Set<TenantSetting>().SingleOrDefaultAsync(x => x.Key == key, ct);
        if (s is null) return defaultValue;
        try { return JsonSerializer.Deserialize<T>(s.Value) ?? defaultValue; }
        catch (JsonException) { return defaultValue; }
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        if (!Allowed.Contains(key)) throw ValidationException.For("key", "Unknown setting.");
        var tid = ctx.RequireTenant();
        var s = await db.Set<TenantSetting>().SingleOrDefaultAsync(x => x.Key == key, ct);
        if (s is null) db.Set<TenantSetting>().Add(s = new TenantSetting { TenantId = tid, Key = key });
        s.Value = JsonSerializer.Serialize(value);
        audit.Write("setting.updated", nameof(TenantSetting), key, new { value });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SettingDto>> ListAsync(CancellationToken ct = default) =>
        (await db.Set<TenantSetting>().OrderBy(s => s.Key).ToListAsync(ct)).Select(s => new SettingDto(s.Key, s.Value)).ToList();

    public async Task SetRawAsync(string key, string json, CancellationToken ct)
    {
        try { JsonDocument.Parse(json); } catch (JsonException) { throw ValidationException.For("value", "Value must be valid JSON."); }
        if (!Allowed.Contains(key)) throw ValidationException.For("key", "Unknown setting.");
        var tid = ctx.RequireTenant();
        var s = await db.Set<TenantSetting>().SingleOrDefaultAsync(x => x.Key == key, ct);
        if (s is null) db.Set<TenantSetting>().Add(s = new TenantSetting { TenantId = tid, Key = key });
        s.Value = json;
        audit.Write("setting.updated", nameof(TenantSetting), key);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Allocates document numbers using a row-level lock (SELECT ... FOR UPDATE) in the current transaction.</summary>
public sealed class SequenceService(DbContext db, ITenantContext ctx, IClock clock) : ISequenceService
{
    public async Task<string> NextAsync(string documentType, Guid? branchId, CancellationToken ct = default)
    {
        var tid = ctx.RequireTenant();
        var year = clock.UtcNow.Year;
        string? branchCode = null;
        if (branchId is { } bid)
            branchCode = await db.Set<Branch>().Where(b => b.Id == bid).Select(b => b.Code).SingleOrDefaultAsync(ct);

        var scope = branchId ?? Guid.Empty;
        var isNpgsql = db.Database.ProviderName?.Contains("Npgsql") == true;
        DocumentSequence? seq;
        if (isNpgsql)
        {
            seq = await db.Set<DocumentSequence>()
                .FromSqlInterpolated($"SELECT *, xmin FROM tenants.document_sequences WHERE \"TenantId\" = {tid} AND \"DocumentType\" = {documentType} AND \"BranchId\" = {scope} FOR UPDATE")
                .SingleOrDefaultAsync(ct);
        }
        else
        {
            seq = await db.Set<DocumentSequence>().SingleOrDefaultAsync(s => s.DocumentType == documentType && s.BranchId == scope, ct);
        }

        if (seq is null)
        {
            seq = new DocumentSequence
            {
                TenantId = tid, DocumentType = documentType, BranchId = scope, Year = year,
                Prefix = DocumentTypes.DefaultPrefixes.GetValueOrDefault(documentType, documentType.ToUpperInvariant()),
            };
            db.Set<DocumentSequence>().Add(seq);
        }
        if (seq.ResetYearly && seq.Year != year) { seq.Year = year; seq.NextNumber = 1; }
        var n = seq.NextNumber++;
        await db.SaveChangesAsync(ct);
        var parts = new List<string> { seq.Prefix };
        if (branchCode is not null) parts.Add(branchCode);
        if (seq.ResetYearly) parts.Add(year.ToString());
        parts.Add(n.ToString().PadLeft(seq.Padding, '0'));
        return string.Join('-', parts);
    }
}

/// <summary>Company-status based access. Subscription state is layered on top by the subscriptions module.</summary>
public sealed class CompanyStatusReader(DbContext db)
{
    public async Task<CompanyStatus?> GetAsync(Guid tenantId, CancellationToken ct) =>
        await db.Set<Company>().Where(c => c.Id == tenantId).Select(c => (CompanyStatus?)c.Status).SingleOrDefaultAsync(ct);
}
