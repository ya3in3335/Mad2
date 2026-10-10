using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Veltrix.Contracts;
using Veltrix.Contracts.Catalog;
using Veltrix.Contracts.Pos;
using Veltrix.Contracts.Sync;
using Veltrix.Contracts.Tenants;
using Veltrix.Desktop.Core.Api;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Sync;

namespace Veltrix.Desktop.Core.Pos;

public sealed record CheckoutResult(string Number, bool Offline, ReceiptModel Receipt, SaleDto? Sale);

/// <summary>Where a register's open shift came from: the server, or a shift opened offline and queued for sync.</summary>
public sealed record ShiftInfo(Guid Id, Guid RegisterId, DateTimeOffset OpenedAt, decimal OpeningFloat, bool Offline);

/// <summary>
/// Online-first POS operations with an offline fallback: when the server cannot be reached, the operation is stored in the
/// local database and queued in the outbox (the op id doubles as the server idempotency key, so a sale that reached the
/// server before the connection dropped can never be booked twice).
/// </summary>
public sealed class PosService(ApiClient api, Func<LocalDb> dbFactory, TimeProvider time)
{
    public async Task<PosConfig> LoadConfigAsync(CancellationToken ct)
    {
        try
        {
            var list = await api.GetAsync<List<SettingDto>>("/api/v1/settings", ct);
            return PosConfig.FromSettings(list.ToDictionary(s => s.Key, s => s.Value));
        }
        catch (Exception e) when (e is ApiOfflineException or ApiException)
        {
            await using var db = dbFactory();
            var p = LocalDb.Keys.ServerSettingPrefix;
            var values = await db.Values.Where(v => v.Key.StartsWith(p)).ToListAsync(ct);
            return values.Count == 0 ? PosConfig.Default : PosConfig.FromSettings(values.ToDictionary(v => v.Key[p.Length..], v => v.Value));
        }
    }

    public async Task<(IReadOnlyList<RegisterDto> Registers, bool Offline)> RegistersAsync(CancellationToken ct)
    {
        try
        {
            return (await api.GetAsync<List<RegisterDto>>("/api/v1/pos/registers", ct), false);
        }
        catch (ApiOfflineException)
        {
            await using var db = dbFactory();
            var local = await db.Registers.Where(r => r.IsActive).OrderBy(r => r.Code).ToListAsync(ct);
            var shifts = await db.Shifts.Where(s => s.Status == "Open").ToListAsync(ct);
            return (local.Select(r => new RegisterDto(r.Id, r.BranchId, r.WarehouseId, r.Code, r.Name, r.IsActive,
                shifts.FirstOrDefault(s => s.RegisterId == r.Id)?.Id ?? r.OpenShiftId)).ToList(), true);
        }
    }

    /// <summary>Finds a product by exact barcode or SKU.</summary>
    public async Task<SellableProduct?> FindByCodeAsync(string code, CancellationToken ct)
    {
        code = code.Trim();
        if (code.Length == 0) return null;
        try
        {
            return SellableProduct.From(await api.GetAsync<ProductDto>("/api/v1/catalog/products/by-code/" + Uri.EscapeDataString(code), ct));
        }
        catch (ApiException e) when (e.Status == 404)
        {
            return null;
        }
        catch (ApiOfflineException)
        {
            await using var db = dbFactory();
            var p = await db.Products.FirstOrDefaultAsync(x => x.Sku == code, ct)
                    ?? (await db.Products.Where(x => x.Barcodes.Contains(code)).ToListAsync(ct))
                        .FirstOrDefault(x => x.Barcodes.Split('\n').Contains(code, StringComparer.Ordinal));
            return p is null ? null : SellableProduct.From(p);
        }
    }

    public async Task<(IReadOnlyList<SellableProduct> Items, bool Offline)> SearchAsync(string text, CancellationToken ct)
    {
        try
        {
            var page = await api.GetAsync<PagedResponse<ProductDto>>("/api/v1/catalog/products" + ApiClient.Query(("search", text), ("status", "Active"), ("pageSize", 30)), ct);
            return (page.Items.Select(SellableProduct.From).ToList(), false);
        }
        catch (ApiOfflineException)
        {
            await using var db = dbFactory();
            var q = db.Products.Where(p => p.Status == "Active");
            if (!string.IsNullOrWhiteSpace(text)) q = q.Where(p => p.Name.Contains(text) || p.Sku.Contains(text) || (p.NameAr != null && p.NameAr.Contains(text)));
            return ((await q.OrderBy(p => p.Name).Take(30).ToListAsync(ct)).Select(SellableProduct.From).ToList(), true);
        }
    }

    public async Task<ShiftInfo?> CurrentShiftAsync(RegisterDto register, CancellationToken ct)
    {
        await using var db = dbFactory();
        var local = await db.Shifts.Where(s => s.RegisterId == register.Id && s.Status == "Open").OrderByDescending(s => s.OpenedAt).FirstOrDefaultAsync(ct);
        if (register.OpenShiftId is { } id)
            return new ShiftInfo(id, register.Id, local?.Id == id ? local.OpenedAt : time.GetUtcNow(), local?.Id == id ? local.OpeningFloat : 0, local?.Id == id);
        return local is null ? null : new ShiftInfo(local.Id, local.RegisterId, local.OpenedAt, local.OpeningFloat, true);
    }

    public async Task<ShiftInfo> OpenShiftAsync(Guid registerId, decimal openingFloat, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var now = time.GetUtcNow();
        var request = new OpenShiftRequest(registerId, openingFloat, id, now);
        try
        {
            var s = await api.PostAsync<ShiftDto>("/api/v1/pos/shifts", request, ct);
            return new ShiftInfo(s.Id, s.RegisterId, s.OpenedAt, s.OpeningFloat, false);
        }
        catch (ApiOfflineException)
        {
            await using var db = dbFactory();
            db.Shifts.Add(new LocalShift { Id = id, RegisterId = registerId, OpenedAt = now, OpeningFloat = openingFloat });
            Outbox.Enqueue(db, SyncOperationTypes.ShiftOpen, request, now);
            await db.SaveChangesAsync(ct);
            return new ShiftInfo(id, registerId, now, openingFloat, true);
        }
    }

    /// <summary>Closes the shift. Returns the server report when online, null when the close was queued offline.</summary>
    public async Task<ShiftDto?> CloseShiftAsync(Guid shiftId, decimal countedCash, string? notes, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var request = new CloseShiftRequest(countedCash, notes, now);
        await using var db = dbFactory();
        var local = await db.Shifts.FindAsync([shiftId], ct);
        // A shift opened offline must reach the server before its close, so the close is queued behind it.
        var mustQueue = await db.Outbox.AnyAsync(e => e.Status == OutboxStatus.Pending, ct);
        ShiftDto? result = null;
        if (!mustQueue)
        {
            try { result = await api.PostAsync<ShiftDto>($"/api/v1/pos/shifts/{shiftId}/close", request, ct); }
            catch (ApiOfflineException) { mustQueue = true; }
        }
        if (mustQueue) Outbox.Enqueue(db, SyncOperationTypes.ShiftClose, new SyncShiftPayload<CloseShiftRequest>(shiftId, request), now);
        if (local is not null) { local.Status = "Closed"; local.ClosedAt = now; local.CountedCash = countedCash; }
        await db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<CheckoutResult> CheckoutAsync(Cart cart, PosConfig config, Guid registerId, string registerCode, ShiftInfo shift, Guid? customerId,
        string? customerName, IReadOnlyList<PaymentLine> payments, string companyName, string cashier, string language, CancellationToken ct)
    {
        if (cart.IsEmpty) throw new InvalidOperationException("The cart is empty.");
        var totals = cart.Totals(config);
        var settlement = CartCalculator.Settle(totals.Total, payments, config.CashRoundingIncrement);
        if (!settlement.IsComplete) throw new InvalidOperationException("The payment does not cover the amount due.");

        var now = time.GetUtcNow();
        var opId = Outbox.NewOpId();
        var saleId = Guid.NewGuid();
        // The tendered amounts are sent as-is: the server computes the change exactly like CartCalculator.Settle.
        var recorded = payments.Where(p => p.Amount > 0).ToList();

        await using var db = dbFactory();
        var mustQueue = shift.Offline || await db.Outbox.AnyAsync(e => e.Status == OutboxStatus.Pending && e.Type == SyncOperationTypes.ShiftOpen, ct);
        if (!mustQueue)
        {
            try
            {
                var sale = await api.PostAsync<SaleDto>("/api/v1/pos/sales", cart.ToSaleRequest(opId, registerId, shift.Id, customerId, recorded, saleId, now, false, null), ct);
                var receipt = cart.ToReceipt(config, companyName, sale.Number, sale.CreatedAt, cashier, customerName, payments, settlement, false, language);
                return new CheckoutResult(sale.Number, false, receipt, sale);
            }
            catch (ApiOfflineException) { }
        }

        var number = await NextClientNumberAsync(db, registerCode, now, ct);
        var offlineReceipt = cart.ToReceipt(config, companyName, number, now, cashier, customerName, payments, settlement, true, language);
        var request = cart.ToSaleRequest(opId, registerId, shift.Id, customerId, recorded, saleId, now, true, number);
        Outbox.Enqueue(db, SyncOperationTypes.Sale, request, now, opId: opId);
        db.Sales.Add(new LocalSale
        {
            Id = saleId, ClientNumber = number, ShiftId = shift.Id, RegisterId = registerId, CustomerId = customerId, OpId = opId, CreatedAt = now,
            Subtotal = totals.Gross, Discount = totals.Discount, Tax = totals.Tax, Total = totals.Total + settlement.Rounding,
            CashReceived = payments.Where(p => p.Method == "Cash").Sum(p => p.Amount), Change = settlement.Change,
            ReceiptJson = JsonSerializer.Serialize(offlineReceipt, ApiClient.Json),
        });
        await db.SaveChangesAsync(ct);
        return new CheckoutResult(number, true, offlineReceipt, null);
    }

    /// <summary>Device-side receipt number: <c>{register}-{yyMMdd}-{counter}</c>, unique per register and monotonic.</summary>
    public static async Task<string> NextClientNumberAsync(LocalDb db, string registerCode, DateTimeOffset now, CancellationToken ct)
    {
        var current = await db.GetValueAsync(LocalDb.Keys.ReceiptCounter, ct);
        var next = (long.TryParse(current, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0) + 1;
        await db.SetValueAsync(LocalDb.Keys.ReceiptCounter, next.ToString(CultureInfo.InvariantCulture), ct);
        var code = string.IsNullOrWhiteSpace(registerCode) ? "POS" : registerCode.Trim().ToUpperInvariant();
        return $"{code}-OFF-{now.ToLocalTime():yyMMdd}-{next.ToString("00000", CultureInfo.InvariantCulture)}";
    }
}
