using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Veltrix.Modules.Catalog;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Purchasing;

public enum PurchaseRequestStatus { Draft, Submitted, Approved, Rejected, Converted }
public enum PurchaseOrderStatus { Draft, PendingApproval, Approved, PartiallyReceived, Received, Closed, Cancelled }
public enum InvoiceStatus { Posted, PartiallyPaid, Paid }

public sealed class Supplier : TenantEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? TaxId { get; set; }
    public string? TradeRegisterNo { get; set; }
    public int PaymentTermsDays { get; set; } = 30;
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
}

public sealed class PurchaseRequest : TenantEntity
{
    public string Number { get; set; } = "";
    public Guid BranchId { get; set; }
    public PurchaseRequestStatus Status { get; set; }
    public string? Notes { get; set; }
    public Guid? DecidedBy { get; set; }
    public List<PurchaseRequestLine> Lines { get; set; } = new();
}

public sealed class PurchaseRequestLine : TenantEntity
{
    public Guid RequestId { get; set; }
    public Guid ProductId { get; set; }
    public decimal Quantity { get; set; }
    public string? Notes { get; set; }
}

public sealed class PurchaseOrder : TenantEntity
{
    public string Number { get; set; } = "";
    public Guid SupplierId { get; set; }
    public Guid BranchId { get; set; }
    public Guid WarehouseId { get; set; }
    public PurchaseOrderStatus Status { get; set; }
    public DateOnly OrderDate { get; set; }
    public DateOnly? ExpectedDate { get; set; }
    public decimal Subtotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public string? Notes { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public Guid? PurchaseRequestId { get; set; }
    public List<PurchaseOrderLine> Lines { get; set; } = new();
}

public sealed class PurchaseOrderLine : TenantEntity
{
    public Guid OrderId { get; set; }
    public Guid ProductId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal TaxRatePercent { get; set; }
    public decimal ReceivedQuantity { get; set; }
    public decimal InvoicedQuantity { get; set; }
}

public sealed class GoodsReceipt : TenantEntity
{
    public string Number { get; set; } = "";
    public Guid PurchaseOrderId { get; set; }
    public Guid WarehouseId { get; set; }
    public decimal Value { get; set; }
    public string? Notes { get; set; }
    public Guid? JournalEntryId { get; set; }
}

public sealed class SupplierInvoice : TenantEntity, IImmutableWhenFinal
{
    public string Number { get; set; } = "";
    public string SupplierInvoiceNo { get; set; } = "";
    public Guid SupplierId { get; set; }
    public Guid? PurchaseOrderId { get; set; }
    public DateOnly Date { get; set; }
    public DateOnly DueDate { get; set; }
    public decimal Subtotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public decimal AmountPaid { get; set; }
    public InvoiceStatus Status { get; set; }
    public Guid? JournalEntryId { get; set; }
    public bool IsFinal => true;
}

public sealed class SupplierPayment : TenantEntity, IImmutableWhenFinal
{
    public string Number { get; set; } = "";
    public Guid SupplierId { get; set; }
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = "";
    public Guid PaymentAccountId { get; set; }
    public string? Reference { get; set; }
    public Guid? JournalEntryId { get; set; }
    public List<SupplierPaymentAllocation> Allocations { get; set; } = new();
    public bool IsFinal => true;
}

public sealed class SupplierPaymentAllocation : TenantEntity
{
    public Guid PaymentId { get; set; }
    public Guid InvoiceId { get; set; }
    public decimal Amount { get; set; }
}

public sealed class PurchaseReturn : TenantEntity, IImmutableWhenFinal
{
    public string Number { get; set; } = "";
    public Guid SupplierId { get; set; }
    public Guid WarehouseId { get; set; }
    public DateOnly Date { get; set; }
    public string Reason { get; set; } = "";
    public decimal Total { get; set; }
    public Guid? JournalEntryId { get; set; }
    public bool IsFinal => true;
}

internal static class P
{
    public static void Num<T>(EntityTypeBuilder<T> b, string table) where T : TenantEntity
    {
        b.ToTable(table, "purchasing");
        b.HasIndex("TenantId", "Number").IsUnique();
        b.Property<string>("Number").HasMaxLength(40);
    }
}

internal sealed class SupplierConfig : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> b)
    {
        b.ToTable("suppliers", "purchasing");
        b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        b.Property(x => x.Code).HasMaxLength(30);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Phone).HasMaxLength(40);
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.Address).HasMaxLength(300);
        b.Property(x => x.TaxId).HasMaxLength(50);
        b.Property(x => x.TradeRegisterNo).HasMaxLength(50);
        b.Property(x => x.Notes).HasMaxLength(2000);
    }
}

internal sealed class PurchaseRequestConfig : IEntityTypeConfiguration<PurchaseRequest>
{
    public void Configure(EntityTypeBuilder<PurchaseRequest> b)
    {
        P.Num(b, "purchase_requests");
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PurchaseRequestLineConfig : IEntityTypeConfiguration<PurchaseRequestLine>
{
    public void Configure(EntityTypeBuilder<PurchaseRequestLine> b)
    {
        b.ToTable("purchase_request_lines", "purchasing");
        b.Property(x => x.Notes).HasMaxLength(300);
        b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PurchaseOrderConfig : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> b)
    {
        P.Num(b, "purchase_orders");
        b.HasIndex(x => new { x.TenantId, x.Status });
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PurchaseOrderLineConfig : IEntityTypeConfiguration<PurchaseOrderLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLine> b)
    {
        b.ToTable("purchase_order_lines", "purchasing");
        b.Property(x => x.TaxRatePercent).HasPrecision(7, 4);
        b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class GoodsReceiptConfig : IEntityTypeConfiguration<GoodsReceipt>
{
    public void Configure(EntityTypeBuilder<GoodsReceipt> b)
    {
        P.Num(b, "goods_receipts");
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SupplierInvoiceConfig : IEntityTypeConfiguration<SupplierInvoice>
{
    public void Configure(EntityTypeBuilder<SupplierInvoice> b)
    {
        P.Num(b, "supplier_invoices");
        b.HasIndex(x => new { x.TenantId, x.SupplierId, x.SupplierInvoiceNo }).IsUnique();
        b.Property(x => x.SupplierInvoiceNo).HasMaxLength(60);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
        b.Ignore(x => x.IsFinal);
    }
}

internal sealed class SupplierPaymentConfig : IEntityTypeConfiguration<SupplierPayment>
{
    public void Configure(EntityTypeBuilder<SupplierPayment> b)
    {
        P.Num(b, "supplier_payments");
        b.Property(x => x.Method).HasMaxLength(20);
        b.Property(x => x.Reference).HasMaxLength(100);
        b.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Allocations).WithOne().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        b.Ignore(x => x.IsFinal);
    }
}

internal sealed class SupplierPaymentAllocationConfig : IEntityTypeConfiguration<SupplierPaymentAllocation>
{
    public void Configure(EntityTypeBuilder<SupplierPaymentAllocation> b)
    {
        b.ToTable("supplier_payment_allocations", "purchasing");
        b.HasOne<SupplierInvoice>().WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PurchaseReturnConfig : IEntityTypeConfiguration<PurchaseReturn>
{
    public void Configure(EntityTypeBuilder<PurchaseReturn> b)
    {
        P.Num(b, "purchase_returns");
        b.Property(x => x.Reason).HasMaxLength(300);
        b.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
        b.Ignore(x => x.IsFinal);
    }
}
