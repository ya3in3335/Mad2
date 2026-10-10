namespace Veltrix.Contracts.Pos;

public sealed record RegisterDto(Guid Id, Guid BranchId, Guid WarehouseId, string Code, string Name, bool IsActive, Guid? OpenShiftId);
public sealed record UpsertRegisterRequest(Guid BranchId, Guid WarehouseId, string Code, string Name, bool IsActive = true);

public sealed record OpenShiftRequest(Guid RegisterId, decimal OpeningFloat, Guid? Id = null, DateTimeOffset? OpenedAt = null);
public sealed record CloseShiftRequest(decimal CountedCash, string? Notes, DateTimeOffset? ClosedAt = null);
public sealed record ShiftDto(Guid Id, Guid RegisterId, Guid BranchId, Guid OpenedBy, DateTimeOffset OpenedAt, decimal OpeningFloat, string Status,
    DateTimeOffset? ClosedAt, Guid? ClosedBy, decimal? CountedCash, decimal? ExpectedCash, decimal? Variance, string? Notes);
public sealed record ShiftReportDto(ShiftDto Shift, int SalesCount, int ReturnsCount, decimal GrossSales, decimal Returns, decimal Discounts, decimal Tax,
    decimal NetSales, IReadOnlyList<PaymentTotalDto> Payments, decimal CashIn, decimal CashOut, decimal ExpectedCash);
public sealed record PaymentTotalDto(string Method, decimal Amount, int Count);

/// <summary>PayIn, PayOut or Drop (cash removed from the drawer to the safe).</summary>
public sealed record CashMovementRequest(string Type, decimal Amount, string Reason, Guid? CounterAccountId, Guid? Id = null);
public sealed record CashMovementDto(Guid Id, Guid ShiftId, string Type, decimal Amount, string Reason, DateTimeOffset CreatedAt);

public sealed record SaleLineInput(Guid ProductId, decimal Quantity, decimal? UnitPrice, decimal DiscountPercent = 0, decimal DiscountAmount = 0,
    IReadOnlyList<string>? Serials = null, string? LotNumber = null, string? Note = null);
/// <summary>Cash, Card, BankTransfer, Cheque, OnAccount, Voucher, Loyalty.</summary>
public sealed record PaymentInput(string Method, decimal Amount, string? Reference = null);

public sealed record CompleteSaleRequest(
    string IdempotencyKey, Guid RegisterId, Guid ShiftId, Guid? CustomerId, IReadOnlyList<SaleLineInput> Lines, IReadOnlyList<PaymentInput> Payments,
    decimal CartDiscountAmount = 0, string? Notes = null, Guid? Id = null, DateTimeOffset? ClientCreatedAt = null, bool Offline = false,
    string? ClientNumber = null);

public sealed record SaleDto(Guid Id, string Number, string Status, bool IsReturn, Guid? ReturnOfSaleId, Guid BranchId, Guid RegisterId, Guid ShiftId,
    Guid? CustomerId, Guid CashierId, decimal Subtotal, decimal DiscountTotal, decimal TaxTotal, decimal Total, decimal Paid, decimal Change, decimal Rounding,
    decimal OnAccount, IReadOnlyList<SaleLineDto> Lines, IReadOnlyList<PaymentDto> Payments, DateTimeOffset CreatedAt, DateTimeOffset? ClientCreatedAt,
    bool Offline, string? Notes);
public sealed record SaleLineDto(Guid Id, Guid ProductId, string Sku, string Description, decimal Quantity, decimal UnitPrice, decimal DiscountAmount,
    decimal TaxRatePercent, decimal TaxAmount, decimal NetAmount, decimal LineTotal, decimal ReturnedQuantity);
public sealed record PaymentDto(string Method, decimal Amount, string? Reference);

public sealed record ReturnLineInput(Guid SaleLineId, decimal Quantity);
public sealed record ReturnSaleRequest(string IdempotencyKey, Guid RegisterId, Guid ShiftId, IReadOnlyList<ReturnLineInput> Lines, string RefundMethod,
    string Reason, bool Restock = true, Guid? Id = null);
public sealed record VoidSaleRequest(string Reason);
public sealed record ExchangeRequest(ReturnSaleRequest Return, CompleteSaleRequest NewSale);
public sealed record ExchangeResult(SaleDto Return, SaleDto Sale);

public sealed record ParkedCartDto(Guid Id, Guid RegisterId, string Name, string PayloadJson, Guid CreatedBy, DateTimeOffset CreatedAt);
public sealed record ParkCartRequest(Guid RegisterId, string Name, string PayloadJson);

public sealed record ReceiptDto(string Number, string Format, string Content);
