using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Tenants;

public enum CompanyStatus { Pending, Active, Suspended, Closed }

/// <summary>A tenant (customer company). The company Id is the TenantId used everywhere.</summary>
public sealed class Company : Entity
{
    public string Name { get; set; } = "";
    public string? LegalName { get; set; }
    /// <summary>Registre du commerce (RC).</summary>
    public string? TradeRegisterNo { get; set; }
    /// <summary>Numéro d'identification fiscale (NIF).</summary>
    public string? TaxId { get; set; }
    /// <summary>Numéro d'identification statistique (NIS).</summary>
    public string? StatisticalId { get; set; }
    /// <summary>Article d'imposition (AI).</summary>
    public string? ArticleNo { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Wilaya { get; set; }
    public string Country { get; set; } = "DZ";
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string Currency { get; set; } = "DZD";
    public string Locale { get; set; } = "fr-DZ";
    public string TimeZone { get; set; } = "Africa/Algiers";
    public string? BrandColor { get; set; }
    public int FiscalYearStartMonth { get; set; } = 1;
    public CompanyStatus Status { get; set; } = CompanyStatus.Pending;
    public string? SuspendedReason { get; set; }
    public bool SetupCompleted { get; set; }
    public string? SetupTokenHash { get; set; }
    public DateTimeOffset? SetupTokenExpiresAt { get; set; }
    public string? ContactName { get; set; }
    public string? Notes { get; set; }
}

public sealed class Branch : TenantEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public bool IsHeadOffice { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class Warehouse : TenantEntity, IBranchScoped
{
    public Guid BranchId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

internal sealed class CompanyConfig : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> b)
    {
        b.ToTable("companies", "tenants");
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.LegalName).HasMaxLength(200);
        b.Property(x => x.TradeRegisterNo).HasMaxLength(50);
        b.Property(x => x.TaxId).HasMaxLength(50);
        b.Property(x => x.StatisticalId).HasMaxLength(50);
        b.Property(x => x.ArticleNo).HasMaxLength(50);
        b.Property(x => x.Address).HasMaxLength(300);
        b.Property(x => x.City).HasMaxLength(100);
        b.Property(x => x.Wilaya).HasMaxLength(100);
        b.Property(x => x.Country).HasMaxLength(2);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.Locale).HasMaxLength(10);
        b.Property(x => x.TimeZone).HasMaxLength(64);
        b.Property(x => x.BrandColor).HasMaxLength(9);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.SetupTokenHash).HasMaxLength(128);
        b.HasIndex(x => x.Name);
    }
}

internal sealed class BranchConfig : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> b)
    {
        b.ToTable("branches", "tenants");
        b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        b.Property(x => x.Code).HasMaxLength(20);
        b.Property(x => x.Name).HasMaxLength(150);
        b.Property(x => x.Address).HasMaxLength(300);
        b.Property(x => x.Phone).HasMaxLength(40);
        b.HasOne<Company>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WarehouseConfig : IEntityTypeConfiguration<Warehouse>
{
    public void Configure(EntityTypeBuilder<Warehouse> b)
    {
        b.ToTable("warehouses", "tenants");
        b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        b.Property(x => x.Code).HasMaxLength(20);
        b.Property(x => x.Name).HasMaxLength(150);
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TenantSettingConfig : IEntityTypeConfiguration<TenantSetting>
{
    public void Configure(EntityTypeBuilder<TenantSetting> b)
    {
        b.ToTable("settings", "tenants");
        b.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
        b.Property(x => x.Key).HasMaxLength(100);
        b.Property(x => x.Value).HasMaxLength(4000);
    }
}

internal sealed class DocumentSequenceConfig : IEntityTypeConfiguration<DocumentSequence>
{
    public void Configure(EntityTypeBuilder<DocumentSequence> b)
    {
        b.ToTable("document_sequences", "tenants");
        b.HasIndex(x => new { x.TenantId, x.DocumentType, x.BranchId }).IsUnique();
        b.Property(x => x.DocumentType).HasMaxLength(40);
        b.Property(x => x.Prefix).HasMaxLength(20);
    }
}
