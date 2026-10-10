using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Veltrix.Contracts.Purchasing;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Purchasing;

public sealed class PurchasingModule : IModule
{
    public string Name => "purchasing";

    public void Register(IServiceCollection services)
    {
        services.AddScoped<PurchasingService>();
        services.AddScoped<IPartyDirectory, SupplierPartyDirectory>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var g = app.MapModuleGroup("purchasing", "Purchasing").RequireFeature(Features.Purchasing);
        g.MapGet("/suppliers", ([AsParameters] PageQuery q, PurchasingService s, CancellationToken ct) => s.SuppliersAsync(q, ct)).RequirePermission(Permissions.PurchasingView);
        g.MapPost("/suppliers", (UpsertSupplierRequest r, PurchasingService s, CancellationToken ct) => s.UpsertSupplierAsync(null, r, ct)).RequirePermission(Permissions.SuppliersManage);
        g.MapPut("/suppliers/{id:guid}", (Guid id, UpsertSupplierRequest r, PurchasingService s, CancellationToken ct) => s.UpsertSupplierAsync(id, r, ct)).RequirePermission(Permissions.SuppliersManage);

        g.MapGet("/requests", (string? status, PurchasingService s, CancellationToken ct) => s.RequestsAsync(status, ct)).RequirePermission(Permissions.PurchasingView);
        g.MapPost("/requests", (CreatePurchaseRequestRequest r, PurchasingService s, CancellationToken ct) => s.CreateRequestAsync(r, ct)).RequirePermission(Permissions.PurchasingView);
        g.MapPost("/requests/{id:guid}/approve", (Guid id, PurchasingService s, CancellationToken ct) => s.DecideRequestAsync(id, true, ct)).RequirePermission(Permissions.PurchasingApprove);
        g.MapPost("/requests/{id:guid}/reject", (Guid id, PurchasingService s, CancellationToken ct) => s.DecideRequestAsync(id, false, ct)).RequirePermission(Permissions.PurchasingApprove);

        g.MapGet("/orders", ([AsParameters] PageQuery q, string? status, Guid? supplierId, bool? outstanding, PurchasingService s, CancellationToken ct) =>
            s.OrdersAsync(q, status, supplierId, outstanding ?? false, ct)).RequirePermission(Permissions.PurchasingView);
        g.MapGet("/orders/{id:guid}", (Guid id, PurchasingService s, CancellationToken ct) => s.GetOrderAsync(id, ct)).RequirePermission(Permissions.PurchasingView);
        g.MapPost("/orders", (CreatePurchaseOrderRequest r, PurchasingService s, CancellationToken ct) => s.CreateOrderAsync(r, ct)).RequirePermission(Permissions.PurchasingManage);
        g.MapPost("/orders/{id:guid}/submit", (Guid id, PurchasingService s, CancellationToken ct) => s.SubmitOrderAsync(id, ct)).RequirePermission(Permissions.PurchasingManage);
        g.MapPost("/orders/{id:guid}/approve", (Guid id, PurchasingService s, CancellationToken ct) => s.ApproveOrderAsync(id, ct)).RequirePermission(Permissions.PurchasingApprove);
        g.MapPost("/orders/{id:guid}/cancel", (Guid id, PurchasingService s, CancellationToken ct) => s.CancelOrderAsync(id, ct)).RequirePermission(Permissions.PurchasingManage);
        g.MapPost("/orders/{id:guid}/close", (Guid id, PurchasingService s, CancellationToken ct) => s.CloseOrderAsync(id, ct)).RequirePermission(Permissions.PurchasingManage);
        g.MapPost("/orders/{id:guid}/receive", (Guid id, ReceiveGoodsRequest r, PurchasingService s, CancellationToken ct) => s.ReceiveAsync(id, r, ct)).RequirePermission(Permissions.PurchasingManage);

        g.MapGet("/invoices", ([AsParameters] PageQuery q, Guid? supplierId, bool? unpaid, PurchasingService s, CancellationToken ct) => s.InvoicesAsync(q, supplierId, unpaid ?? false, ct)).RequirePermission(Permissions.PurchasingView);
        g.MapGet("/invoices/{id:guid}", (Guid id, PurchasingService s, CancellationToken ct) => s.GetInvoiceAsync(id, ct)).RequirePermission(Permissions.PurchasingView);
        g.MapPost("/invoices", (CreateSupplierInvoiceRequest r, PurchasingService s, CancellationToken ct) => s.CreateInvoiceAsync(r, ct)).RequirePermission(Permissions.PurchasingManage);
        g.MapPost("/payments", (SupplierPaymentRequest r, PurchasingService s, CancellationToken ct) => s.PaySupplierAsync(r, ct)).RequirePermission(Permissions.AccountingPost);
        g.MapPost("/returns", (PurchaseReturnRequest r, PurchasingService s, CancellationToken ct) => s.ReturnToSupplierAsync(r, ct)).RequirePermission(Permissions.PurchasingManage);
        g.MapGet("/analytics/suppliers", (DateOnly from, DateOnly to, PurchasingService s, CancellationToken ct) => s.PerformanceAsync(from, to, ct)).RequirePermission(Permissions.PurchasingView);
    }
}
