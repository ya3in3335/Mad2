using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Veltrix.Modules.Catalog;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Crm;

public enum CustomerType { Individual, Business }
public enum TaskStatus { Open, Done, Cancelled }
public enum OpportunityStage { Lead, Qualified, Proposal, Negotiation, Won, Lost }

public sealed class CustomerGroup : TenantEntity
{
    public string Name { get; set; } = "";
    public decimal DiscountPercent { get; set; }
    public Guid? PriceListId { get; set; }
}

public sealed class Customer : TenantEntity
{
    public string Code { get; set; } = "";
    public CustomerType Type { get; set; }
    public string Name { get; set; } = "";
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Wilaya { get; set; }
    public string? TaxId { get; set; }
    public string? TradeRegisterNo { get; set; }
    public Guid? GroupId { get; set; }
    public Guid? PriceListId { get; set; }
    public decimal CreditLimit { get; set; }
    public decimal LoyaltyPoints { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Explicit opt-in for marketing communications (separate from transactional messages).</summary>
    public bool MarketingConsent { get; set; }
    public DateTimeOffset? ConsentAt { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset? AnonymizedAt { get; set; }
}

public sealed class CustomerNote : TenantEntity
{
    public Guid CustomerId { get; set; }
    public string Text { get; set; } = "";
}

public sealed class FollowUpTask : TenantEntity
{
    public Guid CustomerId { get; set; }
    public string Title { get; set; } = "";
    public DateOnly DueDate { get; set; }
    public Guid? AssignedToUserId { get; set; }
    public TaskStatus Status { get; set; }
    public string? Notes { get; set; }
}

public sealed class Opportunity : TenantEntity
{
    public Guid CustomerId { get; set; }
    public string Title { get; set; } = "";
    public OpportunityStage Stage { get; set; }
    public decimal EstimatedValue { get; set; }
    public int Probability { get; set; }
    public DateOnly? ExpectedCloseDate { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string? Notes { get; set; }
}

public sealed class LoyaltyTransaction : TenantEntity
{
    public Guid CustomerId { get; set; }
    public decimal Points { get; set; }
    public string Reason { get; set; } = "";
    public string? Reference { get; set; }
    public Guid? ReferenceId { get; set; }
}

internal sealed class CustomerGroupConfig : IEntityTypeConfiguration<CustomerGroup>
{
    public void Configure(EntityTypeBuilder<CustomerGroup> b)
    {
        b.ToTable("customer_groups", "crm");
        b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.DiscountPercent).HasPrecision(7, 4);
        b.HasOne<PriceList>().WithMany().HasForeignKey(x => x.PriceListId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class CustomerConfig : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> b)
    {
        b.ToTable("customers", "crm");
        b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.Phone });
        b.HasIndex(x => new { x.TenantId, x.Name });
        b.HasIndex(x => new { x.TenantId, x.UpdatedAt });
        b.Property(x => x.Code).HasMaxLength(30);
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Phone).HasMaxLength(40);
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.Address).HasMaxLength(300);
        b.Property(x => x.City).HasMaxLength(100);
        b.Property(x => x.Wilaya).HasMaxLength(100);
        b.Property(x => x.TaxId).HasMaxLength(50);
        b.Property(x => x.TradeRegisterNo).HasMaxLength(50);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.HasOne<CustomerGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<PriceList>().WithMany().HasForeignKey(x => x.PriceListId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class CustomerNoteConfig : IEntityTypeConfiguration<CustomerNote>
{
    public void Configure(EntityTypeBuilder<CustomerNote> b)
    {
        b.ToTable("customer_notes", "crm");
        b.Property(x => x.Text).HasMaxLength(4000);
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class FollowUpTaskConfig : IEntityTypeConfiguration<FollowUpTask>
{
    public void Configure(EntityTypeBuilder<FollowUpTask> b)
    {
        b.ToTable("follow_up_tasks", "crm");
        b.HasIndex(x => new { x.TenantId, x.Status, x.DueDate });
        b.Property(x => x.Title).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OpportunityConfig : IEntityTypeConfiguration<Opportunity>
{
    public void Configure(EntityTypeBuilder<Opportunity> b)
    {
        b.ToTable("opportunities", "crm");
        b.Property(x => x.Title).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.Stage).HasConversion<string>().HasMaxLength(20);
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class LoyaltyTransactionConfig : IEntityTypeConfiguration<LoyaltyTransaction>
{
    public void Configure(EntityTypeBuilder<LoyaltyTransaction> b)
    {
        b.ToTable("loyalty_transactions", "crm");
        b.HasIndex(x => new { x.CustomerId, x.CreatedAt });
        b.Property(x => x.Reason).HasMaxLength(200);
        b.Property(x => x.Reference).HasMaxLength(60);
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
    }
}
