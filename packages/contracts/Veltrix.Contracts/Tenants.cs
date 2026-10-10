namespace Veltrix.Contracts.Tenants;

public sealed record CompanyDto(
    Guid Id, string Name, string? LegalName, string? TradeRegisterNo, string? TaxId, string? StatisticalId, string? ArticleNo,
    string? Address, string? City, string? Wilaya, string Country, string? Phone, string? Email, string Currency, string Locale,
    string TimeZone, string? BrandColor, string Status, bool SetupCompleted, int FiscalYearStartMonth);

public sealed record UpdateCompanyRequest(
    string Name, string? LegalName, string? TradeRegisterNo, string? TaxId, string? StatisticalId, string? ArticleNo,
    string? Address, string? City, string? Wilaya, string? Phone, string? Email, string? Locale, string? BrandColor, int? FiscalYearStartMonth);

public sealed record BranchDto(Guid Id, string Code, string Name, string? Address, string? Phone, bool IsHeadOffice, bool IsActive);
public sealed record UpsertBranchRequest(string Code, string Name, string? Address, string? Phone, bool IsActive = true);

public sealed record WarehouseDto(Guid Id, Guid BranchId, string Code, string Name, bool IsDefault, bool IsActive);
public sealed record UpsertWarehouseRequest(Guid BranchId, string Code, string Name, bool IsDefault, bool IsActive = true);

public sealed record SettingDto(string Key, string Value);

/// <summary>First-run company configuration, authorized by a one-time setup token issued at license activation.</summary>
public sealed record CompleteOnboardingRequest(
    string SetupToken, string CompanyName, string? LegalName, string? TaxId, string? TradeRegisterNo, string? Address, string? City,
    string? Wilaya, string? Phone, string Currency, string Locale, string OwnerEmail, string OwnerFullName, string OwnerPassword,
    string? OwnerLanguage, string HeadOfficeName, bool ApplyAlgeriaTaxTemplate);

public sealed record OnboardingResult(Guid TenantId, Guid BranchId, Guid WarehouseId);

// ---- Platform (vendor) administration ----
public sealed record TenantSummaryDto(Guid Id, string Name, string Status, bool SetupCompleted, string? Email, string? Phone,
    string? Wilaya, DateTimeOffset CreatedAt, string? PlanCode, string? SubscriptionStatus, DateTimeOffset? SubscriptionEndsAt,
    int ActiveDevices, int Users);
public sealed record CreateTenantRequest(string Name, string? Email, string? Phone, string? ContactName, string? Wilaya, string? Notes);
public sealed record CreateTenantResponse(Guid TenantId, string SetupToken, DateTimeOffset SetupTokenExpiresAt);
public sealed record SuspendTenantRequest(string Reason);
