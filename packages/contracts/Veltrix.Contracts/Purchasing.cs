namespace Veltrix.Contracts.Purchasing;

public sealed record SupplierDto(Guid Id, string Code, string Name, string? Phone, string? Email, string? Address, string? TaxId, string? TradeRegisterNo,
    int PaymentTermsDays, bool IsActive, string? Notes, decimal Balance);
public sealed record UpsertSupplierRequest(string Name, string? Phone, string? Email, string? Address, string? TaxId, string? TradeRegisterNo,
    int PaymentTermsDays, bool IsActive, string? Notes, string? Code = null);

public sealed record DocLineInput(Guid? ProductId, string? Description, decimal Quantity, decimal UnitCost, decimal TaxRatePercent);
public sealed record PurchaseRequestDto(Guid Id, string Number, Guid BranchId, string Status, string? Notes, IReadOnlyList<PurchaseRequestLineDto> Lines, DateTimeOffset CreatedAt);
public sealed record PurchaseRequestLineDto(Guid ProductId, decimal Quantity, string? Notes);
public sealed record CreatePurchaseRequestRequest(Guid BranchId, string? Notes, IReadOnlyList<PurchaseRequestLineDto> Lines);

public sealed record PurchaseOrderDto(Guid Id, string Number, Guid SupplierId, string SupplierName, Guid BranchId, Guid WarehouseId, string Status, DateOnly OrderDate,
    DateOnly? ExpectedDate, decimal Subtotal, decimal TaxTotal, decimal Total, string? Notes, Guid? ApprovedBy, DateTimeOffset? ApprovedAt,
    IReadOnlyList<PurchaseOrderLineDto> Lines, Guid? PurchaseRequestId);
public sealed record PurchaseOrderLineDto(Guid Id, Guid ProductId, string Sku, string Name, decimal Quantity, decimal UnitCost, decimal TaxRatePercent,
    decimal ReceivedQuantity, decimal InvoicedQuantity, decimal LineTotal);
public sealed record CreatePurchaseOrderRequest(Guid SupplierId, Guid WarehouseId, DateOnly? ExpectedDate, string? Notes,
    IReadOnlyList<DocLineInput> Lines, Guid? PurchaseRequestId = null);

public sealed record ReceiptLineInput(Guid PurchaseOrderLineId, decimal Quantity, string? LotNumber, DateOnly? ExpiryDate, IReadOnlyList<string>? Serials);
public sealed record ReceiveGoodsRequest(IReadOnlyList<ReceiptLineInput> Lines, string? Notes);
public sealed record GoodsReceiptDto(Guid Id, string Number, Guid PurchaseOrderId, Guid WarehouseId, decimal Value, DateTimeOffset CreatedAt);

public sealed record SupplierInvoiceDto(Guid Id, string Number, string SupplierInvoiceNo, Guid SupplierId, string SupplierName, Guid? PurchaseOrderId,
    DateOnly Date, DateOnly DueDate, decimal Subtotal, decimal TaxTotal, decimal Total, decimal AmountPaid, decimal Outstanding, string Status);
/// <summary>For PO invoices, lines reference PO lines via ProductId; for direct invoices set ReceiveStock to also receive goods.</summary>
public sealed record CreateSupplierInvoiceRequest(Guid SupplierId, string SupplierInvoiceNo, DateOnly Date, DateOnly? DueDate, Guid? PurchaseOrderId,
    IReadOnlyList<DocLineInput> Lines, bool ReceiveStock = false, Guid? WarehouseId = null, Guid? ExpenseAccountId = null);

public sealed record AllocationInput(Guid DocumentId, decimal Amount);
public sealed record SupplierPaymentRequest(Guid SupplierId, DateOnly Date, decimal Amount, Guid PaymentAccountId, string Method, string? Reference,
    IReadOnlyList<AllocationInput> Allocations);
public sealed record PaymentResultDto(Guid Id, string Number, decimal Amount, decimal Allocated, decimal Unallocated);

public sealed record PurchaseReturnRequest(Guid SupplierId, Guid WarehouseId, DateOnly Date, string Reason, IReadOnlyList<DocLineInput> Lines);
public sealed record PurchaseReturnDto(Guid Id, string Number, Guid SupplierId, decimal Total, string Reason, DateTimeOffset CreatedAt);

public sealed record SupplierPerformanceDto(Guid SupplierId, string Name, int Orders, decimal Spend, int Receipts, int OnTimeReceipts, double OnTimeRate,
    decimal OutstandingPayable);
