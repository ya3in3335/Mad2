using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Veltrix.Contracts.Pos;
using Veltrix.Modules.Crm;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Pos;

public sealed class PosModule : IModule
{
    public string Name => "pos";

    public void Register(IServiceCollection services)
    {
        services.AddScoped<PosService>();
        services.AddScoped<ReceiptRenderer>();
        services.AddScoped<ICustomerHistoryProvider, PosCustomerHistory>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var g = app.MapModuleGroup("pos", "POS").RequireFeature(Features.Pos);
        g.MapGet("/registers", (PosService s, CancellationToken ct) => s.RegistersAsync(ct)).RequirePermission(Permissions.PosSell);
        g.MapPost("/registers", (UpsertRegisterRequest r, PosService s, CancellationToken ct) => s.UpsertRegisterAsync(null, r, ct)).RequirePermission(Permissions.BranchesManage);
        g.MapPut("/registers/{id:guid}", (Guid id, UpsertRegisterRequest r, PosService s, CancellationToken ct) => s.UpsertRegisterAsync(id, r, ct)).RequirePermission(Permissions.BranchesManage);

        g.MapGet("/shifts", (Guid? registerId, bool? openOnly, PosService s, CancellationToken ct) => s.ShiftsAsync(registerId, openOnly ?? false, ct)).RequirePermission(Permissions.PosShiftManage);
        g.MapPost("/shifts", (OpenShiftRequest r, PosService s, CancellationToken ct) => s.OpenShiftAsync(r, ct)).RequirePermission(Permissions.PosShiftManage);
        g.MapPost("/shifts/{id:guid}/close", (Guid id, CloseShiftRequest r, PosService s, CancellationToken ct) => s.CloseShiftAsync(id, r, ct)).RequirePermission(Permissions.PosShiftManage);
        g.MapGet("/shifts/{id:guid}/report", (Guid id, PosService s, CancellationToken ct) => s.ShiftReportAsync(id, ct)).RequirePermission(Permissions.PosShiftManage);
        g.MapPost("/shifts/{id:guid}/cash", (Guid id, CashMovementRequest r, PosService s, CancellationToken ct) => s.CashMovementAsync(id, r, ct)).RequirePermission(Permissions.PosCashDrawer);

        g.MapGet("/sales", ([AsParameters] PageQuery q, Guid? shiftId, Guid? customerId, DateTimeOffset? from, DateTimeOffset? to, PosService s, CancellationToken ct) =>
            s.SalesAsync(q, shiftId, customerId, from, to, ct)).RequirePermission(Permissions.SalesView);
        g.MapGet("/sales/{id:guid}", (Guid id, PosService s, CancellationToken ct) => s.GetSaleAsync(id, ct)).RequirePermission(Permissions.PosSell);
        g.MapPost("/sales", (CompleteSaleRequest r, PosService s, CancellationToken ct) => s.CompleteSaleAsync(r, ct)).RequirePermission(Permissions.PosSell);
        g.MapPost("/sales/{id:guid}/return", (Guid id, ReturnSaleRequest r, PosService s, CancellationToken ct) => s.ReturnAsync(id, r, ct)).RequirePermission(Permissions.PosRefund);
        g.MapPost("/sales/{id:guid}/exchange", (Guid id, ExchangeRequest r, PosService s, CancellationToken ct) => s.ExchangeAsync(id, r, ct)).RequirePermission(Permissions.PosRefund);
        g.MapPost("/sales/{id:guid}/void", (Guid id, VoidSaleRequest r, PosService s, CancellationToken ct) => s.VoidAsync(id, r, ct)).RequirePermission(Permissions.PosVoid);
        g.MapGet("/sales/{id:guid}/receipt", async (Guid id, string? format, int? width, bool? reprint, PosService s, ReceiptRenderer renderer, CancellationToken ct) =>
        {
            var sale = await s.LoadForReceiptAsync(id, reprint ?? false, ct);
            return format == "html"
                ? Results.Content(await renderer.RenderHtmlAsync(sale, ct), "text/html; charset=utf-8")
                : Results.Ok(new ReceiptDto(sale.Number, "text", await renderer.RenderTextAsync(sale, width ?? 0, ct)));
        }).RequirePermission(Permissions.PosSell);

        g.MapGet("/parked", (Guid registerId, PosService s, CancellationToken ct) => s.ParkedAsync(registerId, ct)).RequirePermission(Permissions.PosSell);
        g.MapPost("/parked", (ParkCartRequest r, PosService s, CancellationToken ct) => s.ParkAsync(r, ct)).RequirePermission(Permissions.PosSell);
        g.MapPost("/parked/{id:guid}/resume", (Guid id, PosService s, CancellationToken ct) => s.ResumeAsync(id, ct)).RequirePermission(Permissions.PosSell);
    }
}
