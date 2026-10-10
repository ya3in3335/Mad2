using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Veltrix.Modules.Catalog;
using Veltrix.Modules.Crm;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Sales;

public enum SalesDocumentType { Quotation, Order, Invoice, CreditNote }

public static class SalesStatus
{
    public const string Draft = "Draft", Sent = "Sent", Accepted = "Accepted", Rejected = "Rejected", Converted = "Converted",
        PendingApproval = "PendingApproval", Approved = "Approved", PartiallyDelivered = "PartiallyDelivered", Delivered = "Delivered",
        Invoiced = "Invoiced", Cancelled = "Cancelled", Issued = "Issued", PartiallyPaid = "PartiallyPaid", Paid = "Paid";
}

public sealed class SalesDocument : TenantEntity, IBranchScoped, IImmutableWhenFinal
{
    public SalesDocumentType Type { get; set; }
    public string Number { get; set; } = "";
    public string Status { get; set; } = SalesStatus.Draft;
    public Guid CustomerId { get; set; }
    public Guid BranchId { get; set; }
    public Guid WarehouseId { get; set; }
    public DateOnly Date { get; set; }
    public DateOnly? DueDate { get; set; }
    public DateOnly? ValidUntil { get; set; }
    public Guid? SalespersonId { get; set; }
    public Guid? SourceDocumentId { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public decimal AmountPaid { get; set; }
    public string? Notes { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public Guid? JournalEntryId { get; set; }
    public List<SalesDocumentLine> Lines { get; set; } = new();
    public bool IsFinal => Type is SalesDocumentType.Invoice or SalesDocumentType.CreditNote;
}

public sealed class SalesDocumentLine : TenantEntity
{
    public Guid DocumentId { get; set; }
    public int LineNo { get; set; }
    public Guid ProductId { get; set; }
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal TaxRatePercent { get; set; }
    public decimal NetAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal LineTotal { get; set; }
    public decimal DeliveredQuantity { get; set; }
    public decimal InvoicedQuantity { get; set; }
    public decimal CreditedQuantity { get; set; }
    public decimal UnitCost { get; set; }
    public Guid? SourceLineId { get; set; }
}

public sealed class DeliveryNote : TenantEntity
{
    public string Number { get; set; } = "";
    public Guid OrderId { get; set; }
    public Guid WarehouseId { get; set; }
    public decimal CostValue { get; set; }
    public string? Notes { get; set; }
    public Guid? JournalEntryId { get; set; }
}

public sealed class CustomerPayment : TenantEntity, IImmutableWhenFinal
{
    public string Number { get; set; } = "";
    public Guid CustomerId { get; set; }
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = "";
    public Guid PaymentAccountId { get; set; }
    public string? Reference { get; set; }
    public Guid? JournalEntryId { get; set; }
    public List<CustomerPaymentAllocation> Allocations { get; set; } = new();
    public bool IsFinal => true;
}

public sealed class CustomerPaymentAllocation : TenantEntity
{
    public Guid PaymentId { get; set; }
    public Guid InvoiceId { get; set; }
    public decimal Amount { get; set; }
}

public sealed class CommissionRule : TenantEntity
{
    public Guid UserId { get; set; }
    public decimal RatePercent { get; set; }
}

internal sealed class SalesDocumentConfig : IEntityTypeConfiguration<SalesDocument>
{
    public void Configure(EntityTypeBuilder<SalesDocument> b)
    {
        b.ToTable("documents", "sales");
        b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.Type, x.Status });
        b.HasIndex(x => new { x.TenantId, x.CustomerId });
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Number).HasMaxLength(40);
        b.Property(x => x.Status).HasMaxLength(30);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        b.Ignore(x => x.IsFinal);
    }
}

internal sealed class SalesDocumentLineConfig : IEntityTypeConfiguration<SalesDocumentLine>
{
    public void Configure(EntityTypeBuilder<SalesDocumentLine> b)
    {
        b.ToTable("document_lines", "sales");
        b.Property(x => x.Description).HasMaxLength(300);
        b.Property(x => x.DiscountPercent).HasPrecision(7, 4);
        b.Property(x => x.TaxRatePercent).HasPrecision(7, 4);
        b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DeliveryNoteConfig : IEntityTypeConfiguration<DeliveryNote>
{
    public void Configure(EntityTypeBuilder<DeliveryNote> b)
    {
        b.ToTable("delivery_notes", "sales");
        b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
        b.Property(x => x.Number).HasMaxLength(40);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasOne<SalesDocument>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CustomerPaymentConfig : IEntityTypeConfiguration<CustomerPayment>
{
    public void Configure(EntityTypeBuilder<CustomerPayment> b)
    {
        b.ToTable("customer_payments", "sales");
        b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
        b.Property(x => x.Number).HasMaxLength(40);
        b.Property(x => x.Method).HasMaxLength(20);
        b.Property(x => x.Reference).HasMaxLength(100);
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Allocations).WithOne().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        b.Ignore(x => x.IsFinal);
    }
}

internal sealed class CustomerPaymentAllocationConfig : IEntityTypeConfiguration<CustomerPaymentAllocation>
{
    public void Configure(EntityTypeBuilder<CustomerPaymentAllocation> b)
    {
        b.ToTable("customer_payment_allocations", "sales");
        b.HasOne<SalesDocument>().WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CommissionRuleConfig : IEntityTypeConfiguration<CommissionRule>
{
    public void Configure(EntityTypeBuilder<CommissionRule> b)
    {
        b.ToTable("commission_rules", "sales");
        b.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
        b.Property(x => x.RatePercent).HasPrecision(7, 4);
    }
}
