using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Veltrix.Contracts.Inventory;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Inventory;

public sealed class InventoryModule : IModule
{
    public string Name => "inventory";

    public void Register(IServiceCollection services)
    {
        services.AddScoped<StockEngine>();
        services.AddScoped<InventoryService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var g = app.MapModuleGroup("inventory", "Inventory").RequireFeature(Features.Inventory);
        g.MapGet("/balances", ([AsParameters] PageQuery q, Guid? warehouseId, Guid? productId, bool? inStock, DateTimeOffset? changedSince, InventoryService s, CancellationToken ct) =>
            s.BalancesAsync(q, warehouseId, productId, inStock, changedSince, ct)).RequirePermission(Permissions.InventoryView);
        g.MapGet("/movements", ([AsParameters] PageQuery q, Guid? productId, Guid? warehouseId, DateTimeOffset? from, DateTimeOffset? to, InventoryService s, CancellationToken ct) =>
            s.MovementsAsync(q, productId, warehouseId, from, to, ct)).RequirePermission(Permissions.InventoryView);
        g.MapGet("/low-stock", (Guid? warehouseId, InventoryService s, CancellationToken ct) => s.LowStockAsync(warehouseId, ct)).RequirePermission(Permissions.InventoryView);
        g.MapGet("/valuation", (InventoryService s, CancellationToken ct) => s.ValuationAsync(ct)).RequirePermission(Permissions.ReportsInventory);
        g.MapGet("/aging", (InventoryService s, CancellationToken ct) => s.AgingAsync(ct)).RequirePermission(Permissions.ReportsInventory);
        g.MapGet("/lots", (Guid? productId, int? expiringWithinDays, InventoryService s, CancellationToken ct) => s.LotsAsync(productId, expiringWithinDays, ct)).RequirePermission(Permissions.InventoryView);
        g.MapGet("/serials/{productId:guid}", (Guid productId, InventoryService s, CancellationToken ct) => s.SerialsAsync(productId, ct)).RequirePermission(Permissions.InventoryView);

        g.MapPost("/adjustments", (CreateAdjustmentRequest r, InventoryService s, CancellationToken ct) => s.AdjustAsync(r, ct)).RequirePermission(Permissions.InventoryAdjust);
        g.MapGet("/adjustments/{id:guid}", (Guid id, InventoryService s, CancellationToken ct) => s.GetAdjustmentAsync(id, ct)).RequirePermission(Permissions.InventoryView);

        g.MapGet("/transfers", ([AsParameters] PageQuery q, string? status, InventoryService s, CancellationToken ct) => s.TransfersAsync(q, status, ct)).RequirePermission(Permissions.InventoryView);
        g.MapGet("/transfers/{id:guid}", (Guid id, InventoryService s, CancellationToken ct) => s.GetTransferAsync(id, ct)).RequirePermission(Permissions.InventoryView);
        g.MapPost("/transfers", (CreateTransferRequest r, InventoryService s, CancellationToken ct) => s.CreateTransferAsync(r, ct)).RequirePermission(Permissions.InventoryTransfer);
        g.MapPost("/transfers/{id:guid}/ship", (Guid id, InventoryService s, CancellationToken ct) => s.ShipTransferAsync(id, ct)).RequirePermission(Permissions.InventoryTransfer);
        g.MapPost("/transfers/{id:guid}/receive", (Guid id, InventoryService s, CancellationToken ct) => s.ReceiveTransferAsync(id, ct)).RequirePermission(Permissions.InventoryTransfer);
        g.MapPost("/transfers/{id:guid}/cancel", (Guid id, InventoryService s, CancellationToken ct) => s.CancelTransferAsync(id, ct)).RequirePermission(Permissions.InventoryTransfer);

        g.MapPost("/counts", (CreateCountRequest r, InventoryService s, CancellationToken ct) => s.StartCountAsync(r, ct)).RequirePermission(Permissions.InventoryCount);
        g.MapGet("/counts/{id:guid}", (Guid id, InventoryService s, CancellationToken ct) => s.GetCountAsync(id, ct)).RequirePermission(Permissions.InventoryView);
        g.MapPut("/counts/{id:guid}/lines", (Guid id, List<CountLineInput> lines, InventoryService s, CancellationToken ct) => s.RecordCountAsync(id, lines, ct)).RequirePermission(Permissions.InventoryCount);
        g.MapPost("/counts/{id:guid}/complete", (Guid id, InventoryService s, CancellationToken ct) => s.CompleteCountAsync(id, ct)).RequirePermission(Permissions.InventoryAdjust);

        g.MapPost("/reservations", (CreateReservationRequest r, InventoryService s, CancellationToken ct) => s.ReserveAsync(r, ct)).RequirePermission(Permissions.SalesManage);
    }
}
