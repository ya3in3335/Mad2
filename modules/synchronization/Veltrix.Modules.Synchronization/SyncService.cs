using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Veltrix.Contracts.Accounting;
using Veltrix.Contracts.Catalog;
using Veltrix.Contracts.Crm;
using Veltrix.Contracts.Hr;
using Veltrix.Contracts.Inventory;
using Veltrix.Contracts.Pos;
using Veltrix.Contracts.Sync;
using Veltrix.Modules.Accounting;
using Veltrix.Modules.Catalog;
using Veltrix.Modules.Crm;
using Veltrix.Modules.Hr;
using Veltrix.Modules.Inventory;
using Veltrix.Modules.Pos;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Synchronization;

/// <summary>
/// Offline synchronization. Conflict rules per entity (see docs/ARCHITECTURE.md#synchronization):
///  - POS sales, returns, shifts, cash movements, expenses, attendance: append-only facts; never overwritten. Replays are de-duplicated by OpId and entity id.
///  - Offline sales that break online rules (stock, credit, discount) are accepted and flagged for review, never silently dropped.
///  - Customers: last-writer-wins guarded by BaseVersion; when the server copy changed after the device's base version the server wins and a conflict is reported.
///  - Catalog, prices, tax, settings: server-authoritative, pulled only.
/// </summary>
public sealed class SyncService(DbContext db, TenantContext ctx, IServiceProvider services, IClock clock, IDeviceAuthorization devices, ILogger<SyncService> logger,
    IDiagnosticsRecorder diagnostics)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public async Task<SyncPushResponse> PushAsync(SyncPushRequest req, CancellationToken ct)
    {
        var tid = ctx.RequireTenant();
        await EnsureDeviceAsync(tid, ct);
        if (req.Operations is not { Count: > 0 and <= 500 }) throw ValidationException.For("operations", "Send between 1 and 500 operations per batch.");
        var results = new List<SyncOperationResult>();
        foreach (var op in req.Operations) results.Add(await ApplyAsync(tid, op, ct));
        return new SyncPushResponse(results, clock.UtcNow);
    }

    private async Task<SyncOperationResult> ApplyAsync(Guid tid, SyncOperation op, CancellationToken ct)
    {
        if (op.OpId is not { Length: >= 8 and <= 100 }) return new(op.OpId ?? "", "rejected", "sync.invalid_op_id", "OpId must be 8-100 characters.", null, null);
        db.ChangeTracker.Clear();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(op.Type + op.Payload.GetRawText())))[..64];
        var existing = await db.Set<IdempotencyRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.Key == op.OpId && r.TenantId == tid, ct);
        if (existing is not null)
        {
            if (existing.RequestHash != hash) return new(op.OpId, "rejected", "sync.op_id_reused", "This OpId was already used for a different payload.", null, null);
            return new(op.OpId, existing.StatusCode == 200 ? "duplicate" : "rejected", existing.StatusCode == 200 ? null : "sync.previously_rejected",
                null, existing.ResultEntityId, existing.ResponseJson is null ? null : JsonDocument.Parse(existing.ResponseJson).RootElement.Clone());
        }

        string status; string? code = null, message = null, entityId = null; object? result = null; int statusCode;
        try
        {
            (result, entityId, status) = await DispatchAsync(op, ct);
            statusCode = 200;
        }
        catch (DomainException e) when (e.StatusCode is >= 400 and < 500 and not 402)
        {
            db.ChangeTracker.Clear();
            status = "rejected"; code = e.Code; message = e.Message; statusCode = e.StatusCode;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(e, "Sync operation {OpId} ({Type}) failed transiently", op.OpId, op.Type);
            await diagnostics.RecordAsync(new ServerDiagnosticEvent(DiagnosticCategories.Sync, DiagnosticSeverities.Error, "sync_transient:" + op.Type,
                $"{e.GetType().Name}: {e.Message}", tid, ctx.DeviceId), ct);
            return new(op.OpId, "retry", "sync.transient", "Temporary failure; the device will retry.", null, null);
        }

        // Record the outcome so replays return the same answer. Business rejections are final; the device surfaces them for review.
        db.ChangeTracker.Clear();
        var json = result is null ? null : JsonSerializer.Serialize(result, Json);
        db.Add(new IdempotencyRecord { TenantId = tid, Key = op.OpId, Operation = op.Type, RequestHash = hash, StatusCode = statusCode, ResponseJson = json,
            ResultEntityId = entityId, CreatedAt = clock.UtcNow });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); /* concurrent replay recorded it first */ }
        return new(op.OpId, status, code, message, entityId, json is null ? null : JsonDocument.Parse(json).RootElement.Clone());
    }

    private T Payload<T>(SyncOperation op) =>
        op.Payload.Deserialize<T>(Json) ?? throw ValidationException.For("payload", "Empty payload.");

    private async Task<(object? Result, string? EntityId, string Status)> DispatchAsync(SyncOperation op, CancellationToken ct)
    {
        var pos = services.GetRequiredService<PosService>();
        switch (op.Type)
        {
            case SyncOperationTypes.ShiftOpen:
            {
                ctx.EnsurePermission(Permissions.PosShiftManage);
                var r = await pos.OpenShiftAsync(Payload<OpenShiftRequest>(op), ct);
                return (r, r.Id.ToString(), "applied");
            }
            case SyncOperationTypes.Sale:
            {
                ctx.EnsurePermission(Permissions.PosSell);
                var req = Payload<CompleteSaleRequest>(op) with { Offline = true, ClientCreatedAt = op.ClientTime };
                if (req.IdempotencyKey != op.OpId) req = req with { IdempotencyKey = op.OpId };
                var r = await pos.CompleteSaleAsync(req, ct);
                return (r, r.Id.ToString(), "applied");
            }
            case SyncOperationTypes.Return:
            {
                ctx.EnsurePermission(Permissions.PosRefund);
                var p = Payload<SyncReturnPayload>(op);
                var r = await pos.ReturnAsync(p.SaleId, p.Request with { IdempotencyKey = op.OpId }, ct);
                return (r, r.Id.ToString(), "applied");
            }
            case SyncOperationTypes.CashMovement:
            {
                ctx.EnsurePermission(Permissions.PosCashDrawer);
                var p = Payload<SyncShiftPayload<CashMovementRequest>>(op);
                var r = await pos.CashMovementAsync(p.ShiftId, p.Request, ct);
                return (r, r.Id.ToString(), "applied");
            }
            case SyncOperationTypes.ShiftClose:
            {
                ctx.EnsurePermission(Permissions.PosShiftManage);
                var p = Payload<SyncShiftPayload<CloseShiftRequest>>(op);
                var r = await pos.CloseShiftAsync(p.ShiftId, p.Request with { ClosedAt = p.Request.ClosedAt ?? op.ClientTime }, ct);
                return (r, p.ShiftId.ToString(), "applied");
            }
            case SyncOperationTypes.CustomerUpsert:
            {
                ctx.EnsurePermission(Permissions.CustomersManage);
                var req = Payload<UpsertCustomerRequest>(op);
                var crm = services.GetRequiredService<CrmService>();
                if (req.Id is { } cid && await db.Set<Customer>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == cid, ct) is { } server)
                {
                    if (op.BaseVersion is null || server.UpdatedAt > op.BaseVersion.Value.AddSeconds(1))
                        return (await crm.GetAsync(cid, ct), cid.ToString(), "conflict");
                    var updated = await crm.UpsertAsync(cid, req, ct);
                    return (updated, cid.ToString(), "applied");
                }
                var created = await crm.UpsertAsync(null, req, ct);
                return (created, created.Id.ToString(), "applied");
            }
            case SyncOperationTypes.Expense:
            {
                ctx.EnsurePermission(Permissions.ExpensesManage);
                var r = await services.GetRequiredService<AccountingService>().CreateExpenseAsync(Payload<CreateExpenseRequest>(op), ct);
                return (r, r.Id.ToString(), "applied");
            }
            case SyncOperationTypes.CheckIn or SyncOperationTypes.CheckOut:
            {
                var req = Payload<CheckRequest>(op);
                var r = await services.GetRequiredService<HrService>().CheckAsync(req with { At = req.At ?? op.ClientTime }, op.Type == SyncOperationTypes.CheckIn, ct);
                return (r, r.Id.ToString(), "applied");
            }
            case SyncOperationTypes.InventoryAdjust:
            {
                ctx.EnsurePermission(Permissions.InventoryAdjust);
                var r = await services.GetRequiredService<InventoryService>().AdjustAsync(Payload<CreateAdjustmentRequest>(op), ct);
                return (r, r.Id.ToString(), "applied");
            }
            default:
                throw new DomainException("sync.unknown_type", $"Unknown operation type '{op.Type}'.");
        }
    }

    public async Task<SyncPullResponse> PullAsync(DateTimeOffset? since, Guid? warehouseId, int limit, CancellationToken ct)
    {
        var tid = ctx.RequireTenant();
        await EnsureDeviceAsync(tid, ct);
        limit = Math.Clamp(limit, 50, 5000);
        // The new cursor is taken before reading so concurrent changes are picked up next time (at-least-once delivery).
        var cursor = clock.UtcNow.AddSeconds(-2);
        var s = since ?? DateTimeOffset.MinValue;
        var rates = await db.Set<TaxCategory>().ToDictionaryAsync(t => t.Id, t => t.RatePercent, ct);
        var products = await db.Set<Product>().Include(p => p.Barcodes).Include(p => p.Components).AsNoTracking()
            .Where(p => p.UpdatedAt > s).OrderBy(p => p.UpdatedAt).Take(limit + 1).ToListAsync(ct);
        var hasMore = products.Count > limit;
        if (hasMore) { products = products.Take(limit).ToList(); cursor = products[^1].UpdatedAt.AddTicks(-1); }
        var catalog = services.GetRequiredService<CatalogService>();
        var categories = since is null ? await catalog.CategoriesAsync(ct) : (await catalog.CategoriesAsync(ct)).ToList();
        var priceLists = await db.Set<PriceList>().AnyAsync(p => p.UpdatedAt > s, ct) || await db.Set<PriceListItem>().AnyAsync(i => i.UpdatedAt > s, ct)
            ? await catalog.PriceListsAsync(ct) : [];
        var customers = ctx.HasPermission(Permissions.CustomersView)
            ? (await db.Set<Customer>().AsNoTracking().Where(c => c.UpdatedAt > s).OrderBy(c => c.UpdatedAt).Take(limit).ToListAsync(ct)).Select(c => CrmService.ToDto(c, 0)).ToList()
            : [];
        var balances = new List<StockBalanceDto>();
        if (warehouseId is { } wid && ctx.HasPermission(Permissions.CatalogView))
            balances = (await services.GetRequiredService<InventoryService>().BalancesAsync(new PageQuery(1, 500), wid, null, null, since, ct)).Items.ToList();
        var registers = ctx.HasPermission(Permissions.PosSell) ? await services.GetRequiredService<PosService>().RegistersAsync(ct) : [];
        var settings = await db.Set<TenantSetting>().AsNoTracking().ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        return new SyncPullResponse(cursor, hasMore, products.Select(p => CatalogService.ToDto(p, rates)).ToList(), categories,
            await catalog.TaxCategoriesAsync(ct), priceLists, customers, balances, registers, settings);
    }

    private async Task EnsureDeviceAsync(Guid tid, CancellationToken ct)
    {
        if (ctx.DeviceId is not { } did)
            throw new ForbiddenException("sync.device_required", "Synchronization requires a session bound to an activated device.");
        if (!await devices.IsDeviceActiveAsync(tid, did, ct))
            throw new ForbiddenException("licensing.device_revoked", "This device's activation is no longer valid.");
    }
}

public sealed class SynchronizationModule : IModule
{
    public string Name => "synchronization";

    public void Register(IServiceCollection services) => services.AddScoped<SyncService>();

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var g = app.MapModuleGroup("sync", "Synchronization").RequirePermission(Permissions.SyncUse);
        g.MapPost("/push", (SyncPushRequest r, SyncService s, CancellationToken ct) => s.PushAsync(r, ct));
        g.MapGet("/pull", (DateTimeOffset? since, Guid? warehouseId, int? limit, SyncService s, CancellationToken ct) => s.PullAsync(since, warehouseId, limit ?? 1000, ct));
    }
}
