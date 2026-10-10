using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Veltrix.Contracts.Accounting;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Accounting;

public sealed class AccountingModule : IModule
{
    public string Name => "accounting";

    public void Register(IServiceCollection services)
    {
        services.AddScoped<PostingService>();
        services.AddScoped<AccountingService>();
        services.AddScoped<IOnboardingContributor, AccountingOnboardingContributor>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var g = app.MapModuleGroup("accounting", "Accounting").RequireFeature(Features.Accounting);
        g.MapGet("/accounts", (AccountingService s, CancellationToken ct) => s.AccountsAsync(ct)).RequirePermission(Permissions.AccountingView);
        g.MapPost("/accounts", (UpsertAccountRequest r, AccountingService s, CancellationToken ct) => s.UpsertAccountAsync(null, r, ct)).RequirePermission(Permissions.AccountingPost);
        g.MapPut("/accounts/{id:guid}", (Guid id, UpsertAccountRequest r, AccountingService s, CancellationToken ct) => s.UpsertAccountAsync(id, r, ct)).RequirePermission(Permissions.AccountingPost);
        g.MapGet("/journal", ([AsParameters] PageQuery q, DateOnly? from, DateOnly? to, string? source, AccountingService s, CancellationToken ct) =>
            s.EntriesAsync(q, from, to, source, ct)).RequirePermission(Permissions.AccountingView);
        g.MapGet("/journal/{id:guid}", (Guid id, AccountingService s, CancellationToken ct) => s.GetEntryAsync(id, ct)).RequirePermission(Permissions.AccountingView);
        g.MapPost("/journal", (CreateJournalEntryRequest r, AccountingService s, CancellationToken ct) => s.CreateManualEntryAsync(r, ct)).RequirePermission(Permissions.AccountingPost);
        g.MapPost("/journal/{id:guid}/reverse", (Guid id, ReverseRequest r, AccountingService s, CancellationToken ct) => s.ReverseAsync(id, r, ct)).RequirePermission(Permissions.AccountingPost);
        g.MapGet("/periods", (AccountingService s, CancellationToken ct) => s.PeriodsAsync(ct)).RequirePermission(Permissions.AccountingView);
        g.MapPost("/periods/{year:int}/{month:int}/close", (int year, int month, AccountingService s, CancellationToken ct) => s.SetPeriodStatusAsync(year, month, true, ct)).RequirePermission(Permissions.AccountingClose);
        g.MapPost("/periods/{year:int}/{month:int}/reopen", (int year, int month, AccountingService s, CancellationToken ct) => s.SetPeriodStatusAsync(year, month, false, ct)).RequirePermission(Permissions.AccountingClose);

        var r = g.MapGroup("/reports").RequirePermission(Permissions.ReportsFinancial);
        r.MapGet("/trial-balance", (DateOnly from, DateOnly to, Guid? branchId, AccountingService s, CancellationToken ct) => s.TrialBalanceAsync(from, to, branchId, ct));
        r.MapGet("/profit-and-loss", (DateOnly from, DateOnly to, Guid? branchId, AccountingService s, CancellationToken ct) => s.ProfitAndLossAsync(from, to, branchId, ct));
        r.MapGet("/balance-sheet", (DateOnly asOf, AccountingService s, CancellationToken ct) => s.BalanceSheetAsync(asOf, ct));
        r.MapGet("/cash-flow", (DateOnly from, DateOnly to, AccountingService s, CancellationToken ct) => s.CashFlowAsync(from, to, ct));
        r.MapGet("/tax-summary", (DateOnly from, DateOnly to, AccountingService s, CancellationToken ct) => s.TaxSummaryAsync(from, to, ct));
        r.MapGet("/ledger/{accountId:guid}", (Guid accountId, DateOnly from, DateOnly to, AccountingService s, CancellationToken ct) => s.LedgerAsync(accountId, from, to, ct));
        r.MapGet("/aging/{partyType}", (string partyType, DateOnly? asOf, AccountingService s, IClock clock, CancellationToken ct) =>
            partyType is PartyTypes.Customer or PartyTypes.Supplier
                ? s.AgingAsync(partyType, asOf ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), ct)
                : throw ValidationException.For("partyType", "Use 'customer' or 'supplier'."));

        var e = app.MapModuleGroup("expenses", "Accounting");
        e.MapGet("/", ([AsParameters] PageQuery q, DateOnly? from, DateOnly? to, AccountingService s, CancellationToken ct) => s.ExpensesAsync(q, from, to, null, ct))
            .RequirePermission(Permissions.ExpensesManage);
        e.MapPost("/", (CreateExpenseRequest r, AccountingService s, CancellationToken ct) => s.CreateExpenseAsync(r, ct)).RequirePermission(Permissions.ExpensesManage);

        var b = g.MapGroup("/bank-accounts");
        b.MapGet("/", (AccountingService s, CancellationToken ct) => s.BankAccountsAsync(ct)).RequirePermission(Permissions.AccountingView);
        b.MapPost("/", (UpsertBankAccountRequest r, AccountingService s, CancellationToken ct) => s.UpsertBankAccountAsync(null, r, ct)).RequirePermission(Permissions.AccountingPost);
        b.MapPut("/{id:guid}", (Guid id, UpsertBankAccountRequest r, AccountingService s, CancellationToken ct) => s.UpsertBankAccountAsync(id, r, ct)).RequirePermission(Permissions.AccountingPost);
        b.MapPost("/{id:guid}/statement", async (Guid id, List<BankStatementLineInput> lines, AccountingService s, CancellationToken ct) =>
            Results.Ok(new { imported = await s.ImportStatementAsync(id, lines, ct) })).RequirePermission(Permissions.AccountingPost);
        b.MapGet("/{id:guid}/statement", (Guid id, bool? unreconciled, AccountingService s, CancellationToken ct) => s.StatementLinesAsync(id, unreconciled ?? false, ct)).RequirePermission(Permissions.AccountingView);
        b.MapGet("/{id:guid}/unreconciled-ledger", (Guid id, AccountingService s, CancellationToken ct) => s.UnreconciledLedgerLinesAsync(id, ct)).RequirePermission(Permissions.AccountingView);
        b.MapPost("/{id:guid}/reconcile", async (Guid id, ReconcileRequest r, AccountingService s, CancellationToken ct) =>
        {
            await s.ReconcileAsync(id, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.AccountingPost);
    }
}
