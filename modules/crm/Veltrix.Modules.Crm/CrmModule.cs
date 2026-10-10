using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Veltrix.Contracts.Crm;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Crm;

public sealed class CrmModule : IModule
{
    public string Name => "crm";

    public void Register(IServiceCollection services)
    {
        services.AddScoped<CrmService>();
        services.AddScoped<IPartyDirectory, CustomerPartyDirectory>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var g = app.MapModuleGroup("customers", "CRM");
        g.MapGet("/", ([AsParameters] PageQuery q, Guid? groupId, bool? active, DateTimeOffset? changedSince, CrmService s, CancellationToken ct) =>
            s.ListAsync(q, groupId, active, changedSince, ct)).RequirePermission(Permissions.CustomersView);
        g.MapGet("/{id:guid}", (Guid id, CrmService s, CancellationToken ct) => s.GetAsync(id, ct)).RequirePermission(Permissions.CustomersView);
        g.MapPost("/", (UpsertCustomerRequest r, CrmService s, CancellationToken ct) => s.UpsertAsync(null, r, ct)).RequirePermission(Permissions.CustomersManage);
        g.MapPut("/{id:guid}", (Guid id, UpsertCustomerRequest r, CrmService s, CancellationToken ct) => s.UpsertAsync(id, r, ct)).RequirePermission(Permissions.CustomersManage);
        g.MapPost("/{id:guid}/deactivate", async (Guid id, CrmService s, CancellationToken ct) => { await s.SetActiveAsync(id, false, ct); return Results.NoContent(); }).RequirePermission(Permissions.CustomersManage);
        g.MapPost("/{id:guid}/reactivate", async (Guid id, CrmService s, CancellationToken ct) => { await s.SetActiveAsync(id, true, ct); return Results.NoContent(); }).RequirePermission(Permissions.CustomersManage);
        g.MapPost("/{id:guid}/anonymize", async (Guid id, CrmService s, CancellationToken ct) => { await s.AnonymizeAsync(id, ct); return Results.NoContent(); }).RequirePermission(Permissions.CompanyManage);
        g.MapGet("/{id:guid}/export", async (Guid id, CrmService s, CancellationToken ct) =>
            Results.File(await s.ExportAsync(id, ct), "application/json", $"customer-{id}.json")).RequirePermission(Permissions.CustomersExport);
        g.MapGet("/{id:guid}/purchases", (Guid id, CrmService s, CancellationToken ct) => s.PurchasesAsync(id, ct)).RequirePermission(Permissions.CustomersView);
        g.MapGet("/{id:guid}/statement", (Guid id, DateOnly from, DateOnly to, CrmService s, CancellationToken ct) => s.StatementAsync(id, from, to, ct)).RequirePermission(Permissions.CustomersView);
        g.MapGet("/{id:guid}/notes", (Guid id, CrmService s, CancellationToken ct) => s.NotesAsync(id, ct)).RequirePermission(Permissions.CustomersView);
        g.MapPost("/{id:guid}/notes", (Guid id, AddNoteRequest r, CrmService s, CancellationToken ct) => s.AddNoteAsync(id, r, ct)).RequirePermission(Permissions.CustomersManage);
        g.MapGet("/{id:guid}/loyalty", (Guid id, CrmService s, CancellationToken ct) => s.LoyaltyAsync(id, ct)).RequirePermission(Permissions.CustomersView);
        g.MapPost("/{id:guid}/loyalty", (Guid id, AdjustLoyaltyRequest r, CrmService s, CancellationToken ct) => s.AdjustLoyaltyAsync(id, r, ct)).RequirePermission(Permissions.CustomersManage);

        var crm = app.MapModuleGroup("crm", "CRM").RequireFeature(Features.Crm);
        crm.MapGet("/groups", (CrmService s, CancellationToken ct) => s.GroupsAsync(ct)).RequirePermission(Permissions.CustomersView);
        crm.MapPost("/groups", (UpsertCustomerGroupRequest r, CrmService s, CancellationToken ct) => s.UpsertGroupAsync(null, r, ct)).RequirePermission(Permissions.CustomersManage);
        crm.MapPut("/groups/{id:guid}", (Guid id, UpsertCustomerGroupRequest r, CrmService s, CancellationToken ct) => s.UpsertGroupAsync(id, r, ct)).RequirePermission(Permissions.CustomersManage);
        crm.MapGet("/tasks", (Guid? customerId, bool? openOnly, CrmService s, CancellationToken ct) => s.TasksAsync(customerId, openOnly ?? true, ct)).RequirePermission(Permissions.CustomersView);
        crm.MapPost("/tasks", (UpsertTaskRequest r, CrmService s, CancellationToken ct) => s.UpsertTaskAsync(null, r, ct)).RequirePermission(Permissions.CustomersManage);
        crm.MapPut("/tasks/{id:guid}", (Guid id, UpsertTaskRequest r, CrmService s, CancellationToken ct) => s.UpsertTaskAsync(id, r, ct)).RequirePermission(Permissions.CustomersManage);
        crm.MapGet("/opportunities", (string? stage, CrmService s, CancellationToken ct) => s.OpportunitiesAsync(stage, ct)).RequirePermission(Permissions.CustomersView);
        crm.MapPost("/opportunities", (UpsertOpportunityRequest r, CrmService s, CancellationToken ct) => s.UpsertOpportunityAsync(null, r, ct)).RequirePermission(Permissions.CustomersManage);
        crm.MapPut("/opportunities/{id:guid}", (Guid id, UpsertOpportunityRequest r, CrmService s, CancellationToken ct) => s.UpsertOpportunityAsync(id, r, ct)).RequirePermission(Permissions.CustomersManage);
        crm.MapGet("/segments", (CrmService s, CancellationToken ct) => s.SegmentsAsync(ct)).RequirePermission(Permissions.ReportsSales);
    }
}
