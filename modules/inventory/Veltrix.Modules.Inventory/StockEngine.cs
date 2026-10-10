using Microsoft.EntityFrameworkCore;
using Veltrix.Modules.Catalog;
using Veltrix.Modules.Tenants;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Inventory;

public sealed record MovementRequest(
    Guid WarehouseId, Guid ProductId, MovementType Type, decimal Quantity, decimal? UnitCost = null,
    string? ReferenceType = null, Guid? ReferenceId = null, string? ReferenceNumber = null, string? Reason = null,
    string? LotNumber = null, DateOnly? ExpiryDate = null, IReadOnlyList<string>? Serials = null, bool AllowNegativeOverride = false);

public sealed record MovementResult(decimal Quantity, decimal UnitCost, decimal TotalCost, decimal BalanceAfter);

/// <summary>The only component allowed to change stock. Inbound movements update the moving-average cost;
/// outbound movements are valued at the current average cost. Balances are locked row-by-row (SELECT ... FOR UPDATE)
/// so concurrent sales cannot corrupt quantities. Callers own the transaction.</summary>
public sealed class StockEngine(DbContext db, ITenantContext ctx, ISettingsService settings, IClock clock, INotificationPublisher notifications)
{
    private bool? _allowNegative;

    public async Task<MovementResult> MoveAsync(MovementRequest r, CancellationToken ct)
    {
        var tid = ctx.RequireTenant();
        if (r.Quantity == 0) throw ValidationException.For("quantity", "Quantity cannot be zero.");
        var product = await db.Set<Product>().SingleOrDefaultAsync(p => p.Id == r.ProductId, ct)
                      ?? throw ValidationException.For("productId", $"Unknown product {r.ProductId}.");
        if (!product.TrackStock) return new MovementResult(r.Quantity, product.CostPrice, Money.Round(product.CostPrice * Math.Abs(r.Quantity)), 0);
        if (!await db.Set<Warehouse>().AnyAsync(w => w.Id == r.WarehouseId, ct)) throw ValidationException.For("warehouseId", "Unknown warehouse.");

        var balance = await LockBalanceAsync(tid, r.WarehouseId, r.ProductId, ct);
        var now = clock.UtcNow;
        var inbound = r.Quantity > 0;
        decimal unitCost;
        if (inbound)
        {
            unitCost = r.UnitCost ?? (balance.AverageCost > 0 ? balance.AverageCost : product.CostPrice);
            if (unitCost < 0) throw ValidationException.For("unitCost", "Unit cost cannot be negative.");
            // Moving average: only positive on-hand quantity carries cost weight.
            var onHand = Math.Max(balance.Quantity, 0);
            var newQty = onHand + r.Quantity;
            balance.AverageCost = newQty > 0 ? Math.Round((onHand * balance.AverageCost + r.Quantity * unitCost) / newQty, 4) : unitCost;
            if (r.Type is MovementType.Receipt or MovementType.OpeningBalance or MovementType.TransferIn) balance.LastReceivedAt = now;
            // Keep a company-wide reference cost on the product.
            product.CostPrice = balance.AverageCost;
            if (r.Type == MovementType.Receipt && r.UnitCost is { } pc) product.PurchasePrice = pc;
        }
        else
        {
            unitCost = balance.AverageCost > 0 ? balance.AverageCost : product.CostPrice;
            var available = balance.Quantity;
            if (available + r.Quantity < 0 && !r.AllowNegativeOverride && !await AllowNegativeAsync(ct))
                throw new DomainException("inventory.insufficient_stock",
                    $"Insufficient stock for {product.Sku} ({product.Name}): available {available:0.###}, requested {-r.Quantity:0.###}.", 409);
            balance.LastIssuedAt = now;
        }

        var before = balance.Quantity;
        balance.Quantity += r.Quantity;
        Guid? lotId = await ApplyLotAsync(tid, product, r, ct);
        await ApplySerialsAsync(tid, product, r, ct);

        var totalCost = Money.Round(unitCost * Math.Abs(r.Quantity));
        db.Set<StockMovement>().Add(new StockMovement
        {
            TenantId = tid, WarehouseId = r.WarehouseId, ProductId = r.ProductId, Type = r.Type, Quantity = r.Quantity, UnitCost = unitCost,
            TotalCost = totalCost, BalanceAfter = balance.Quantity, LotId = lotId, ReferenceType = r.ReferenceType, ReferenceId = r.ReferenceId,
            ReferenceNumber = r.ReferenceNumber, Reason = r.Reason, OccurredAt = now,
            SerialNumber = r.Serials is { Count: > 0 } s ? string.Join(',', s).Length > 100 ? string.Join(',', s)[..100] : string.Join(',', s) : null,
        });

        // Notify once when crossing the low-stock threshold downwards.
        if (product.LowStockThreshold > 0 && before > product.LowStockThreshold && balance.Quantity <= product.LowStockThreshold)
            notifications.Publish(tid, "inventory.low_stock", $"Low stock: {product.Name}",
                $"{product.Sku} is at {balance.Quantity:0.###} (threshold {product.LowStockThreshold:0.###}).",
                NotificationSeverity.Warning, Permissions.InventoryView, link: $"/inventory/products/{product.Id}",
                dedupeKey: $"lowstock:{r.WarehouseId}:{product.Id}");

        return new MovementResult(r.Quantity, unitCost, totalCost, balance.Quantity);
    }

    private async Task<StockBalance> LockBalanceAsync(Guid tid, Guid warehouseId, Guid productId, CancellationToken ct)
    {
        var tracked = db.Set<StockBalance>().Local.SingleOrDefault(b => b.WarehouseId == warehouseId && b.ProductId == productId);
        if (tracked is not null) return tracked;
        if (db.Database.ProviderName?.Contains("Npgsql") == true)
        {
            var now = clock.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.stock_balances ("Id","TenantId","WarehouseId","ProductId","Quantity","ReservedQuantity","AverageCost","CreatedAt","UpdatedAt")
                VALUES ({Guid.CreateVersion7()}, {tid}, {warehouseId}, {productId}, 0, 0, 0, {now}, {now})
                ON CONFLICT ("TenantId","WarehouseId","ProductId") DO NOTHING
                """, ct);
            return await db.Set<StockBalance>().FromSqlInterpolated($"""
                SELECT *, xmin FROM inventory.stock_balances
                WHERE "TenantId" = {tid} AND "WarehouseId" = {warehouseId} AND "ProductId" = {productId} FOR UPDATE
                """).SingleAsync(ct);
        }
        var existing = await db.Set<StockBalance>().SingleOrDefaultAsync(b => b.WarehouseId == warehouseId && b.ProductId == productId, ct);
        if (existing is not null) return existing;
        var created = new StockBalance { TenantId = tid, WarehouseId = warehouseId, ProductId = productId };
        db.Add(created);
        return created;
    }

    private async Task<bool> AllowNegativeAsync(CancellationToken ct) =>
        _allowNegative ??= await settings.GetAsync(SettingKeys.AllowNegativeStock, false, ct);

    private async Task<Guid?> ApplyLotAsync(Guid tid, Product product, MovementRequest r, CancellationToken ct)
    {
        if (!product.TrackLot && !product.TrackExpiry) return null;
        if (r.Quantity > 0)
        {
            if (string.IsNullOrWhiteSpace(r.LotNumber)) throw ValidationException.For("lotNumber", $"Lot number is required for {product.Sku}.");
            if (product.TrackExpiry && r.ExpiryDate is null && r.Type is MovementType.Receipt or MovementType.OpeningBalance)
                throw ValidationException.For("expiryDate", $"Expiry date is required for {product.Sku}.");
            var lot = db.Set<StockLot>().Local.SingleOrDefault(l => l.WarehouseId == r.WarehouseId && l.ProductId == product.Id && l.LotNumber == r.LotNumber)
                      ?? await db.Set<StockLot>().SingleOrDefaultAsync(l => l.WarehouseId == r.WarehouseId && l.ProductId == product.Id && l.LotNumber == r.LotNumber, ct);
            if (lot is null)
            {
                lot = new StockLot { TenantId = tid, WarehouseId = r.WarehouseId, ProductId = product.Id, LotNumber = r.LotNumber.Trim(), ExpiryDate = r.ExpiryDate, ReceivedAt = clock.UtcNow };
                db.Add(lot);
            }
            lot.Quantity += r.Quantity;
            return lot.Id;
        }
        // Outbound: a specific lot, or FEFO (first-expiry-first-out) allocation.
        var remaining = -r.Quantity;
        var lots = await db.Set<StockLot>().Where(l => l.WarehouseId == r.WarehouseId && l.ProductId == product.Id && l.Quantity > 0
                                                       && (r.LotNumber == null || l.LotNumber == r.LotNumber))
            .OrderBy(l => l.ExpiryDate == null).ThenBy(l => l.ExpiryDate).ThenBy(l => l.ReceivedAt).ToListAsync(ct);
        Guid? first = null;
        foreach (var lot in lots)
        {
            if (remaining <= 0) break;
            var take = Math.Min(lot.Quantity, remaining);
            lot.Quantity -= take;
            remaining -= take;
            first ??= lot.Id;
        }
        return first;
    }

    private async Task ApplySerialsAsync(Guid tid, Product product, MovementRequest r, CancellationToken ct)
    {
        if (!product.TrackSerial) return;
        var serials = (r.Serials ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();
        if (serials.Count != Math.Abs(r.Quantity) || r.Quantity != Math.Truncate(r.Quantity))
            throw ValidationException.For("serials", $"{product.Sku} requires exactly one serial number per unit.");
        var existing = await db.Set<SerialNumber>().Where(s => s.ProductId == product.Id && serials.Contains(s.Serial)).ToListAsync(ct);
        if (r.Quantity > 0)
        {
            foreach (var s in serials)
            {
                var row = existing.SingleOrDefault(e => e.Serial == s);
                if (row is null) db.Add(new SerialNumber { TenantId = tid, ProductId = product.Id, Serial = s, WarehouseId = r.WarehouseId, Status = SerialStatus.InStock });
                else if (row.Status == SerialStatus.InStock) throw new ConflictException("inventory.serial_in_stock", $"Serial {s} is already in stock.");
                else { row.Status = SerialStatus.InStock; row.WarehouseId = r.WarehouseId; }
            }
        }
        else
        {
            foreach (var s in serials)
            {
                var row = existing.SingleOrDefault(e => e.Serial == s && e.WarehouseId == r.WarehouseId && e.Status == SerialStatus.InStock)
                          ?? throw new DomainException("inventory.serial_unavailable", $"Serial {s} is not in stock in this warehouse.", 409);
                row.Status = r.Type switch
                {
                    MovementType.Damage => SerialStatus.Damaged,
                    MovementType.TransferOut => SerialStatus.InTransit,
                    MovementType.PurchaseReturn => SerialStatus.ReturnedToSupplier,
                    _ => SerialStatus.Sold,
                };
            }
        }
    }

    /// <summary>Reserve stock for an order. Reservations reduce availability but not on-hand quantity.</summary>
    public async Task<StockReservation> ReserveAsync(Guid warehouseId, Guid productId, decimal qty, string refType, Guid refId, DateTimeOffset? expires, CancellationToken ct)
    {
        var tid = ctx.RequireTenant();
        if (qty <= 0) throw ValidationException.For("quantity", "Must be positive.");
        var balance = await LockBalanceAsync(tid, warehouseId, productId, ct);
        if (balance.Quantity - balance.ReservedQuantity < qty && !await AllowNegativeAsync(ct))
            throw new DomainException("inventory.insufficient_stock", $"Only {balance.Quantity - balance.ReservedQuantity:0.###} available to reserve.", 409);
        balance.ReservedQuantity += qty;
        var res = new StockReservation { TenantId = tid, WarehouseId = warehouseId, ProductId = productId, Quantity = qty, ReferenceType = refType, ReferenceId = refId, ExpiresAt = expires };
        db.Add(res);
        return res;
    }

    public async Task ReleaseReservationsAsync(string refType, Guid refId, CancellationToken ct)
    {
        var tid = ctx.RequireTenant();
        var list = await db.Set<StockReservation>().Where(r => r.ReferenceType == refType && r.ReferenceId == refId && !r.Released).ToListAsync(ct);
        foreach (var r in list)
        {
            var balance = await LockBalanceAsync(tid, r.WarehouseId, r.ProductId, ct);
            balance.ReservedQuantity = Math.Max(0, balance.ReservedQuantity - r.Quantity);
            r.Released = true;
        }
    }
}
