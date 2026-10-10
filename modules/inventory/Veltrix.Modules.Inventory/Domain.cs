using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Veltrix.Modules.Catalog;
using Veltrix.Modules.Tenants;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Inventory;

public enum MovementType
{
    OpeningBalance, Receipt, Sale, SaleReturn, PurchaseReturn, AdjustmentIn, AdjustmentOut, TransferOut, TransferIn,
    CountAdjustment, Damage, Expired, Delivery, BundleComponent,
}

public enum SerialStatus { InStock, Sold, Returned, Damaged, InTransit, ReturnedToSupplier }

/// <summary>Current quantity and moving-average cost of a product in a warehouse. Only changed through <see cref="StockMovement"/>s.</summary>
public sealed class StockBalance : TenantEntity
{
    public Guid WarehouseId { get; set; }
    public Guid ProductId { get; set; }
    public decimal Quantity { get; set; }
    public decimal ReservedQuantity { get; set; }
    public decimal AverageCost { get; set; }
    public DateTimeOffset? LastReceivedAt { get; set; }
    public DateTimeOffset? LastIssuedAt { get; set; }
}

/// <summary>Append-only inventory ledger. Every stock change is traceable to a document and a user.</summary>
public sealed class StockMovement : TenantEntity
{
    public Guid WarehouseId { get; set; }
    public Guid ProductId { get; set; }
    public MovementType Type { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal TotalCost { get; set; }
    public decimal BalanceAfter { get; set; }
    public Guid? LotId { get; set; }
    public string? SerialNumber { get; set; }
    public string? ReferenceType { get; set; }
    public Guid? ReferenceId { get; set; }
    public string? ReferenceNumber { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class StockLot : TenantEntity
{
    public Guid WarehouseId { get; set; }
    public Guid ProductId { get; set; }
    public string LotNumber { get; set; } = "";
    public DateOnly? ExpiryDate { get; set; }
    public decimal Quantity { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class SerialNumber : TenantEntity
{
    public Guid ProductId { get; set; }
    public string Serial { get; set; } = "";
    public Guid? WarehouseId { get; set; }
    public SerialStatus Status { get; set; }
}

public sealed class StockReservation : TenantEntity
{
    public Guid WarehouseId { get; set; }
    public Guid ProductId { get; set; }
    public decimal Quantity { get; set; }
    public string ReferenceType { get; set; } = "";
    public Guid ReferenceId { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool Released { get; set; }
}

public sealed class StockAdjustment : TenantEntity
{
    public string Number { get; set; } = "";
    public Guid WarehouseId { get; set; }
    public string Reason { get; set; } = "";
    public string? Notes { get; set; }
    public decimal ValueChange { get; set; }
    public Guid? JournalEntryId { get; set; }
}

public enum TransferStatus { Draft, InTransit, Received, Cancelled }

public sealed class StockTransfer : TenantEntity
{
    public string Number { get; set; } = "";
    public Guid FromWarehouseId { get; set; }
    public Guid ToWarehouseId { get; set; }
    public TransferStatus Status { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset? ShippedAt { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }
    public List<StockTransferLine> Lines { get; set; } = new();
}

public sealed class StockTransferLine : TenantEntity
{
    public Guid TransferId { get; set; }
    public Guid ProductId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public string? LotNumber { get; set; }
    public string? Serials { get; set; }
}

public enum CountStatus { Open, Completed, Cancelled }

public sealed class StockCount : TenantEntity
{
    public string Number { get; set; } = "";
    public Guid WarehouseId { get; set; }
    public CountStatus Status { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? AdjustmentId { get; set; }
    public List<StockCountLine> Lines { get; set; } = new();
}

public sealed class StockCountLine : TenantEntity
{
    public Guid CountId { get; set; }
    public Guid ProductId { get; set; }
    public decimal ExpectedQuantity { get; set; }
    public decimal? CountedQuantity { get; set; }
}

internal static class Cfg
{
    public static void Wh<T>(EntityTypeBuilder<T> b, System.Linq.Expressions.Expression<Func<T, object?>> fk) where T : class =>
        b.HasOne<Warehouse>().WithMany().HasForeignKey(fk!).OnDelete(DeleteBehavior.Restrict);
    public static void Prod<T>(EntityTypeBuilder<T> b, System.Linq.Expressions.Expression<Func<T, object?>> fk) where T : class =>
        b.HasOne<Product>().WithMany().HasForeignKey(fk!).OnDelete(DeleteBehavior.Restrict);
}

internal sealed class StockBalanceConfig : IEntityTypeConfiguration<StockBalance>
{
    public void Configure(EntityTypeBuilder<StockBalance> b)
    {
        b.ToTable("stock_balances", "inventory");
        b.HasIndex(x => new { x.TenantId, x.WarehouseId, x.ProductId }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.UpdatedAt });
        Cfg.Wh(b, x => x.WarehouseId); Cfg.Prod(b, x => x.ProductId);
    }
}

internal sealed class StockMovementConfig : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> b)
    {
        b.ToTable("stock_movements", "inventory");
        b.HasIndex(x => new { x.TenantId, x.ProductId, x.OccurredAt });
        b.HasIndex(x => new { x.TenantId, x.WarehouseId, x.OccurredAt });
        b.HasIndex(x => new { x.ReferenceType, x.ReferenceId });
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.SerialNumber).HasMaxLength(100);
        b.Property(x => x.ReferenceType).HasMaxLength(40);
        b.Property(x => x.ReferenceNumber).HasMaxLength(60);
        b.Property(x => x.Reason).HasMaxLength(300);
        Cfg.Wh(b, x => x.WarehouseId); Cfg.Prod(b, x => x.ProductId);
    }
}

internal sealed class StockLotConfig : IEntityTypeConfiguration<StockLot>
{
    public void Configure(EntityTypeBuilder<StockLot> b)
    {
        b.ToTable("stock_lots", "inventory");
        b.HasIndex(x => new { x.TenantId, x.WarehouseId, x.ProductId, x.LotNumber }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.ExpiryDate });
        b.Property(x => x.LotNumber).HasMaxLength(60);
        Cfg.Wh(b, x => x.WarehouseId); Cfg.Prod(b, x => x.ProductId);
    }
}

internal sealed class SerialNumberConfig : IEntityTypeConfiguration<SerialNumber>
{
    public void Configure(EntityTypeBuilder<SerialNumber> b)
    {
        b.ToTable("serial_numbers", "inventory");
        b.HasIndex(x => new { x.TenantId, x.ProductId, x.Serial }).IsUnique();
        b.Property(x => x.Serial).HasMaxLength(100);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
        Cfg.Prod(b, x => x.ProductId);
    }
}

internal sealed class StockReservationConfig : IEntityTypeConfiguration<StockReservation>
{
    public void Configure(EntityTypeBuilder<StockReservation> b)
    {
        b.ToTable("stock_reservations", "inventory");
        b.HasIndex(x => new { x.ReferenceType, x.ReferenceId });
        b.Property(x => x.ReferenceType).HasMaxLength(40);
        Cfg.Wh(b, x => x.WarehouseId); Cfg.Prod(b, x => x.ProductId);
    }
}

internal sealed class StockAdjustmentConfig : IEntityTypeConfiguration<StockAdjustment>
{
    public void Configure(EntityTypeBuilder<StockAdjustment> b)
    {
        b.ToTable("stock_adjustments", "inventory");
        b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
        b.Property(x => x.Number).HasMaxLength(40);
        b.Property(x => x.Reason).HasMaxLength(30);
        b.Property(x => x.Notes).HasMaxLength(500);
        Cfg.Wh(b, x => x.WarehouseId);
    }
}

internal sealed class StockTransferConfig : IEntityTypeConfiguration<StockTransfer>
{
    public void Configure(EntityTypeBuilder<StockTransfer> b)
    {
        b.ToTable("stock_transfers", "inventory");
        b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
        b.Property(x => x.Number).HasMaxLength(40);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Notes).HasMaxLength(500);
        Cfg.Wh(b, x => x.FromWarehouseId); Cfg.Wh(b, x => x.ToWarehouseId);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.TransferId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class StockTransferLineConfig : IEntityTypeConfiguration<StockTransferLine>
{
    public void Configure(EntityTypeBuilder<StockTransferLine> b)
    {
        b.ToTable("stock_transfer_lines", "inventory");
        b.Property(x => x.LotNumber).HasMaxLength(60);
        b.Property(x => x.Serials).HasMaxLength(4000);
        Cfg.Prod(b, x => x.ProductId);
    }
}

internal sealed class StockCountConfig : IEntityTypeConfiguration<StockCount>
{
    public void Configure(EntityTypeBuilder<StockCount> b)
    {
        b.ToTable("stock_counts", "inventory");
        b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
        b.Property(x => x.Number).HasMaxLength(40);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Notes).HasMaxLength(500);
        Cfg.Wh(b, x => x.WarehouseId);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.CountId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class StockCountLineConfig : IEntityTypeConfiguration<StockCountLine>
{
    public void Configure(EntityTypeBuilder<StockCountLine> b)
    {
        b.ToTable("stock_count_lines", "inventory");
        b.HasIndex(x => new { x.CountId, x.ProductId }).IsUnique();
        Cfg.Prod(b, x => x.ProductId);
    }
}
