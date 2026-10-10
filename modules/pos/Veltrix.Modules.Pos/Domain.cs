using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Veltrix.Modules.Catalog;
using Veltrix.Modules.Tenants;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Pos;

public enum ShiftStatus { Open, Closed }
public enum SaleStatus { Completed, Voided }
public enum PaymentMethod { Cash, Card, BankTransfer, Cheque, OnAccount, Voucher, Loyalty }
public enum CashMovementType { PayIn, PayOut, Drop }

public sealed class CashRegister : TenantEntity, IBranchScoped
{
    public Guid BranchId { get; set; }
    public Guid WarehouseId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public sealed class Shift : TenantEntity, IBranchScoped
{
    public Guid RegisterId { get; set; }
    public Guid BranchId { get; set; }
    public Guid OpenedBy { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public decimal OpeningFloat { get; set; }
    public ShiftStatus Status { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public Guid? ClosedBy { get; set; }
    public decimal? CountedCash { get; set; }
    public decimal? ExpectedCash { get; set; }
    public decimal? Variance { get; set; }
    public string? Notes { get; set; }
}

public sealed class CashMovement : TenantEntity
{
    public Guid ShiftId { get; set; }
    public CashMovementType Type { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = "";
    public Guid? JournalEntryId { get; set; }
}

/// <summary>A completed POS sale or return. Finalized sales are immutable; corrections use returns/voids.</summary>
public sealed class PosSale : TenantEntity, IBranchScoped, IImmutableWhenFinal
{
    public string Number { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public SaleStatus Status { get; set; }
    public bool IsReturn { get; set; }
    public bool IsVoidReversal { get; set; }
    public Guid? ReturnOfSaleId { get; set; }
    public Guid BranchId { get; set; }
    public Guid RegisterId { get; set; }
    public Guid ShiftId { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid CashierId { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public decimal Paid { get; set; }
    public decimal Change { get; set; }
    public decimal Rounding { get; set; }
    public decimal OnAccount { get; set; }
    public decimal CostTotal { get; set; }
    public bool Offline { get; set; }
    public DateTimeOffset? ClientCreatedAt { get; set; }
    public string? ClientNumber { get; set; }
    public string? Notes { get; set; }
    public Guid? JournalEntryId { get; set; }
    public int ReprintCount { get; set; }
    public List<PosSaleLine> Lines { get; set; } = new();
    public List<PosPayment> Payments { get; set; } = new();
    public bool IsFinal => true;
}

public sealed class PosSaleLine : TenantEntity
{
    public Guid SaleId { get; set; }
    public int LineNo { get; set; }
    public Guid ProductId { get; set; }
    public string Sku { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TaxRatePercent { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal NetAmount { get; set; }
    public decimal LineTotal { get; set; }
    public decimal UnitCost { get; set; }
    public decimal ReturnedQuantity { get; set; }
    public Guid? ReturnOfLineId { get; set; }
    public bool IsService { get; set; }
    public string? Serials { get; set; }
    public string? LotNumber { get; set; }
}

public sealed class PosPayment : TenantEntity
{
    public Guid SaleId { get; set; }
    public PaymentMethod Method { get; set; }
    public decimal Amount { get; set; }
    public string? Reference { get; set; }
}

public sealed class ParkedCart : TenantEntity
{
    public Guid RegisterId { get; set; }
    public string Name { get; set; } = "";
    public string PayloadJson { get; set; } = "";
}

internal sealed class CashRegisterConfig : IEntityTypeConfiguration<CashRegister>
{
    public void Configure(EntityTypeBuilder<CashRegister> b)
    {
        b.ToTable("registers", "pos");
        b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        b.Property(x => x.Code).HasMaxLength(20);
        b.Property(x => x.Name).HasMaxLength(100);
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ShiftConfig : IEntityTypeConfiguration<Shift>
{
    public void Configure(EntityTypeBuilder<Shift> b)
    {
        b.ToTable("shifts", "pos");
        b.HasIndex(x => new { x.RegisterId, x.Status });
        // At most one open shift per register.
        b.HasIndex(x => x.RegisterId).IsUnique().HasFilter("\"Status\" = 'Open'").HasDatabaseName("ux_shifts_one_open_per_register");
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasOne<CashRegister>().WithMany().HasForeignKey(x => x.RegisterId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CashMovementConfig : IEntityTypeConfiguration<CashMovement>
{
    public void Configure(EntityTypeBuilder<CashMovement> b)
    {
        b.ToTable("cash_movements", "pos");
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.Reason).HasMaxLength(300);
        b.HasOne<Shift>().WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PosSaleConfig : IEntityTypeConfiguration<PosSale>
{
    public void Configure(EntityTypeBuilder<PosSale> b)
    {
        b.ToTable("sales", "pos");
        b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
        b.HasIndex(x => new { x.TenantId, x.CustomerId });
        b.HasIndex(x => x.ShiftId);
        b.Property(x => x.Number).HasMaxLength(40);
        b.Property(x => x.IdempotencyKey).HasMaxLength(100);
        b.Property(x => x.ClientNumber).HasMaxLength(40);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Payments).WithOne().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Shift>().WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PosSale>().WithMany().HasForeignKey(x => x.ReturnOfSaleId).OnDelete(DeleteBehavior.Restrict);
        b.Ignore(x => x.IsFinal);
    }
}

internal sealed class PosSaleLineConfig : IEntityTypeConfiguration<PosSaleLine>
{
    public void Configure(EntityTypeBuilder<PosSaleLine> b)
    {
        b.ToTable("sale_lines", "pos");
        b.Property(x => x.Sku).HasMaxLength(64);
        b.Property(x => x.Description).HasMaxLength(200);
        b.Property(x => x.TaxRatePercent).HasPrecision(7, 4);
        b.Property(x => x.Serials).HasMaxLength(2000);
        b.Property(x => x.LotNumber).HasMaxLength(60);
        b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PosPaymentConfig : IEntityTypeConfiguration<PosPayment>
{
    public void Configure(EntityTypeBuilder<PosPayment> b)
    {
        b.ToTable("payments", "pos");
        b.Property(x => x.Method).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Reference).HasMaxLength(100);
    }
}

internal sealed class ParkedCartConfig : IEntityTypeConfiguration<ParkedCart>
{
    public void Configure(EntityTypeBuilder<ParkedCart> b)
    {
        b.ToTable("parked_carts", "pos");
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.PayloadJson).HasColumnType("jsonb");
    }
}
