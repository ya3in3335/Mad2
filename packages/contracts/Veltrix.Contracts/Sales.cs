namespace Veltrix.Contracts.Sales;

public sealed record SalesLineInput(Guid ProductId, decimal Quantity, decimal? UnitPrice, decimal DiscountPercent, string? Description = null);
public sealed record CreateSalesDocumentRequest(string Type, Guid CustomerId, Guid WarehouseId, DateOnly? Date, DateOnly? DueDate, DateOnly? ValidUntil,
    Guid? SalespersonId, string? Notes, IReadOnlyList<SalesLineInput> Lines);

public sealed record SalesDocumentDto(Guid Id, string Type, string Number, string Status, Guid CustomerId, string CustomerName, Guid BranchId, Guid WarehouseId,
    DateOnly Date, DateOnly? DueDate, DateOnly? ValidUntil, Guid? SalespersonId, Guid? SourceDocumentId, decimal Subtotal, decimal DiscountTotal, decimal TaxTotal,
    decimal Total, decimal AmountPaid, decimal Outstanding, string? Notes, Guid? ApprovedBy, IReadOnlyList<SalesDocumentLineDto> Lines, DateTimeOffset CreatedAt);
public sealed record SalesDocumentLineDto(Guid Id, Guid ProductId, string Sku, string Description, decimal Quantity, decimal UnitPrice, decimal DiscountPercent,
    decimal TaxRatePercent, decimal NetAmount, decimal TaxAmount, decimal LineTotal, decimal DeliveredQuantity, decimal InvoicedQuantity, decimal CreditedQuantity);

public sealed record DeliveryLineInput(Guid OrderLineId, decimal Quantity, IReadOnlyList<string>? Serials = null, string? LotNumber = null);
public sealed record DeliverRequest(IReadOnlyList<DeliveryLineInput> Lines, string? Notes);
public sealed record DeliveryNoteDto(Guid Id, string Number, Guid OrderId, Guid WarehouseId, decimal CostValue, DateTimeOffset CreatedAt);

public sealed record CreditLineInput(Guid InvoiceLineId, decimal Quantity);
public sealed record CreateCreditNoteRequest(IReadOnlyList<CreditLineInput> Lines, string Reason, bool Restock);

public sealed record CustomerPaymentRequest(Guid CustomerId, DateOnly Date, decimal Amount, Guid PaymentAccountId, string Method, string? Reference,
    IReadOnlyList<Veltrix.Contracts.Purchasing.AllocationInput> Allocations, Guid? Id = null);

public sealed record CommissionRuleDto(Guid UserId, decimal RatePercent);
public sealed record CommissionRowDto(Guid SalespersonId, string Name, decimal InvoicedNet, decimal CreditedNet, decimal CommissionBase, decimal RatePercent, decimal Commission);
public sealed record PipelineRowDto(string Type, string Status, int Count, decimal Total);
