namespace Veltrix.Contracts.Inventory;

public sealed record StockBalanceDto(Guid WarehouseId, string WarehouseName, Guid ProductId, string Sku, string ProductName, decimal Quantity,
    decimal ReservedQuantity, decimal Available, decimal AverageCost, decimal Value, decimal LowStockThreshold, DateTimeOffset? LastReceivedAt, DateTimeOffset UpdatedAt);

public sealed record StockMovementDto(Guid Id, DateTimeOffset OccurredAt, Guid WarehouseId, Guid ProductId, string Sku, string ProductName, string Type,
    decimal Quantity, decimal UnitCost, decimal BalanceAfter, string? ReferenceType, string? ReferenceNumber, string? Reason, string? LotNumber, string? SerialNumber, Guid? UserId);

public sealed record AdjustmentLineInput(Guid ProductId, decimal QuantityChange, decimal? UnitCost, string? LotNumber, DateOnly? ExpiryDate, IReadOnlyList<string>? Serials);
/// <summary>Reason: Correction, Damage, Expired, Found, Theft, Opening, Other.</summary>
public sealed record CreateAdjustmentRequest(Guid WarehouseId, string Reason, string? Notes, IReadOnlyList<AdjustmentLineInput> Lines, Guid? Id = null);
public sealed record AdjustmentDto(Guid Id, string Number, Guid WarehouseId, string Reason, string? Notes, decimal ValueChange, DateTimeOffset CreatedAt, Guid? JournalEntryId);

public sealed record TransferLineInput(Guid ProductId, decimal Quantity, string? LotNumber, IReadOnlyList<string>? Serials);
public sealed record CreateTransferRequest(Guid FromWarehouseId, Guid ToWarehouseId, string? Notes, IReadOnlyList<TransferLineInput> Lines);
public sealed record TransferDto(Guid Id, string Number, Guid FromWarehouseId, Guid ToWarehouseId, string Status, string? Notes,
    IReadOnlyList<TransferLineDto> Lines, DateTimeOffset CreatedAt, DateTimeOffset? ShippedAt, DateTimeOffset? ReceivedAt);
public sealed record TransferLineDto(Guid ProductId, string Sku, string Name, decimal Quantity, decimal UnitCost, string? LotNumber);

public sealed record CreateCountRequest(Guid WarehouseId, IReadOnlyList<Guid>? ProductIds, string? Notes);
public sealed record CountLineInput(Guid ProductId, decimal CountedQuantity);
public sealed record CountDto(Guid Id, string Number, Guid WarehouseId, string Status, string? Notes, IReadOnlyList<CountLineDto> Lines, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);
public sealed record CountLineDto(Guid ProductId, string Sku, string Name, decimal ExpectedQuantity, decimal? CountedQuantity, decimal? Variance);

public sealed record CreateReservationRequest(Guid WarehouseId, Guid ProductId, decimal Quantity, string ReferenceType, Guid ReferenceId, DateTimeOffset? ExpiresAt);
public sealed record ReservationDto(Guid Id, Guid WarehouseId, Guid ProductId, decimal Quantity, string ReferenceType, Guid ReferenceId, DateTimeOffset? ExpiresAt, bool Released);

public sealed record LowStockDto(Guid ProductId, string Sku, string Name, Guid WarehouseId, string WarehouseName, decimal Quantity, decimal Threshold, decimal SuggestedReorder);
public sealed record ValuationDto(decimal TotalValue, IReadOnlyList<ValuationRow> Rows);
public sealed record ValuationRow(Guid WarehouseId, string WarehouseName, decimal Quantity, decimal Value);
public sealed record AgingStockDto(Guid ProductId, string Sku, string Name, Guid WarehouseId, decimal Quantity, decimal Value, int DaysSinceLastReceipt, string Bucket);
public sealed record LotDto(Guid Id, Guid ProductId, string Sku, Guid WarehouseId, string LotNumber, DateOnly? ExpiryDate, decimal Quantity);
public sealed record SerialDto(Guid Id, Guid ProductId, string Serial, Guid? WarehouseId, string Status);
