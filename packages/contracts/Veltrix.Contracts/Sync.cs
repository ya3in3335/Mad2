using System.Text.Json;
using Veltrix.Contracts.Catalog;
using Veltrix.Contracts.Crm;
using Veltrix.Contracts.Inventory;
using Veltrix.Contracts.Pos;

namespace Veltrix.Contracts.Sync;

/// <summary>An offline operation from the device outbox. OpId is a stable idempotency key generated on the device.</summary>
public sealed record SyncOperation(string OpId, string Type, JsonElement Payload, DateTimeOffset ClientTime, DateTimeOffset? BaseVersion = null);
public sealed record SyncPushRequest(IReadOnlyList<SyncOperation> Operations);

/// <summary>Status: applied, duplicate (already applied earlier), conflict (server kept its version), rejected (business rule), retry (transient).</summary>
public sealed record SyncOperationResult(string OpId, string Status, string? Code, string? Message, string? EntityId, JsonElement? Result);
public sealed record SyncPushResponse(IReadOnlyList<SyncOperationResult> Results, DateTimeOffset ServerTime);

public sealed record SyncPullResponse(DateTimeOffset Cursor, bool HasMore, IReadOnlyList<ProductDto> Products, IReadOnlyList<CategoryDto> Categories,
    IReadOnlyList<TaxCategoryDto> TaxCategories, IReadOnlyList<PriceListDto> PriceLists, IReadOnlyList<CustomerDto> Customers,
    IReadOnlyList<StockBalanceDto> StockBalances, IReadOnlyList<RegisterDto> Registers, IReadOnlyDictionary<string, string> Settings);

public static class SyncOperationTypes
{
    public const string ShiftOpen = "pos.shift.open";
    public const string Sale = "pos.sale";
    public const string Return = "pos.return";
    public const string CashMovement = "pos.shift.cash";
    public const string ShiftClose = "pos.shift.close";
    public const string CustomerUpsert = "customer.upsert";
    public const string Expense = "expense.create";
    public const string CheckIn = "hr.checkin";
    public const string CheckOut = "hr.checkout";
    public const string InventoryAdjust = "inventory.adjust";

    public static readonly IReadOnlyList<string> All = [ShiftOpen, Sale, Return, CashMovement, ShiftClose, CustomerUpsert, Expense, CheckIn, CheckOut, InventoryAdjust];
}

public sealed record SyncReturnPayload(Guid SaleId, ReturnSaleRequest Request);
public sealed record SyncShiftPayload<T>(Guid ShiftId, T Request);
