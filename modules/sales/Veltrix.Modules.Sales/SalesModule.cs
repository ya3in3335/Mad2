using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Veltrix.Contracts.Sales;
using Veltrix.Modules.Crm;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Sales;

public sealed class SalesModule : IModule
{
    public string Name => "sales";

    public void Register(IServiceCollection services)
    {
        services.AddScoped<SalesService>();
        services.AddScoped<ICustomerHistoryProvider, SalesCustomerHistory>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var g = app.MapModuleGroup("sales", "Sales").RequireFeature(Features.Sales);
        g.MapGet("/documents", ([AsParameters] PageQuery q, string? type, string? status, Guid? customerId, bool? outstanding, SalesService s, CancellationToken ct) =>
            s.ListAsync(q, type, status, customerId, outstanding ?? false, ct)).RequirePermission(Permissions.SalesView);
        g.MapGet("/documents/{id:guid}", (Guid id, SalesService s, CancellationToken ct) => s.GetAsync(id, ct)).RequirePermission(Permissions.SalesView);
        g.MapPost("/documents", (CreateSalesDocumentRequest r, SalesService s, CancellationToken ct) => s.CreateAsync(r, ct)).RequirePermission(Permissions.SalesManage);
        g.MapPost("/quotations/{id:guid}/status/{status}", (Guid id, string status, SalesService s, CancellationToken ct) => s.SetQuotationStatusAsync(id, status, ct)).RequirePermission(Permissions.SalesManage);
        g.MapPost("/quotations/{id:guid}/convert", (Guid id, SalesService s, CancellationToken ct) => s.ConvertQuotationAsync(id, ct)).RequirePermission(Permissions.SalesManage);
        g.MapPost("/orders/{id:guid}/submit", (Guid id, SalesService s, CancellationToken ct) => s.SubmitOrderAsync(id, ct)).RequirePermission(Permissions.SalesManage);
        g.MapPost("/orders/{id:guid}/approve", (Guid id, SalesService s, CancellationToken ct) => s.ApproveOrderAsync(id, ct)).RequirePermission(Permissions.SalesApprove);
        g.MapPost("/orders/{id:guid}/cancel", (Guid id, SalesService s, CancellationToken ct) => s.CancelOrderAsync(id, ct)).RequirePermission(Permissions.SalesManage);
        g.MapPost("/orders/{id:guid}/deliver", (Guid id, DeliverRequest r, SalesService s, CancellationToken ct) => s.DeliverAsync(id, r, ct)).RequirePermission(Permissions.SalesManage);
        g.MapPost("/orders/{id:guid}/invoice", (Guid id, SalesService s, CancellationToken ct) => s.InvoiceOrderAsync(id, ct)).RequirePermission(Permissions.SalesManage);
        g.MapPost("/invoices/{id:guid}/credit-note", (Guid id, CreateCreditNoteRequest r, SalesService s, CancellationToken ct) => s.CreditInvoiceAsync(id, r, ct)).RequirePermission(Permissions.SalesApprove);
        g.MapPost("/payments", (CustomerPaymentRequest r, SalesService s, CancellationToken ct) => s.ReceivePaymentAsync(r, ct)).RequirePermission(Permissions.SalesManage);
        g.MapGet("/pipeline", (SalesService s, CancellationToken ct) => s.PipelineAsync(ct)).RequirePermission(Permissions.ReportsSales);
        g.MapGet("/commission-rules", (SalesService s, CancellationToken ct) => s.CommissionRulesAsync(ct)).RequirePermission(Permissions.ReportsSales);
        g.MapPut("/commission-rules", async (CommissionRuleDto r, SalesService s, CancellationToken ct) => { await s.SetCommissionRuleAsync(r, ct); return Results.NoContent(); }).RequirePermission(Permissions.SalesApprove);
        g.MapGet("/commissions", (DateOnly from, DateOnly to, SalesService s, CancellationToken ct) => s.CommissionsAsync(from, to, ct)).RequirePermission(Permissions.ReportsSales);
    }
}
