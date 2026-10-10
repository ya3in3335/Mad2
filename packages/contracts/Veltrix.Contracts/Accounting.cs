namespace Veltrix.Contracts.Accounting;

public sealed record AccountDto(Guid Id, string Code, string Name, string? NameAr, string Type, Guid? ParentId, bool IsPostable, bool IsActive, string? SystemRole);
public sealed record UpsertAccountRequest(string Code, string Name, string? NameAr, string Type, Guid? ParentId, bool IsPostable, bool IsActive = true);

public sealed record JournalLineInput(Guid AccountId, decimal Debit, decimal Credit, string? Description, string? PartyType = null, Guid? PartyId = null);
public sealed record CreateJournalEntryRequest(DateOnly Date, string Description, Guid? BranchId, IReadOnlyList<JournalLineInput> Lines);

public sealed record JournalEntryDto(Guid Id, string Number, DateOnly Date, string Description, string Source, Guid? SourceId, string Status,
    Guid? BranchId, Guid? ReversalOfId, Guid? ReversedById, decimal TotalDebit, IReadOnlyList<JournalLineDto> Lines, DateTimeOffset CreatedAt);
public sealed record JournalLineDto(Guid Id, Guid AccountId, string AccountCode, string AccountName, decimal Debit, decimal Credit, string? Description,
    string? PartyType, Guid? PartyId);

public sealed record ReverseRequest(DateOnly? Date, string Reason);

public sealed record FiscalPeriodDto(Guid Id, int Year, int Month, DateOnly StartDate, DateOnly EndDate, string Status, DateTimeOffset? ClosedAt);

public sealed record TrialBalanceRow(Guid AccountId, string Code, string Name, string Type, decimal Debit, decimal Credit, decimal Balance);
public sealed record TrialBalanceDto(DateOnly From, DateOnly To, IReadOnlyList<TrialBalanceRow> Rows, decimal TotalDebit, decimal TotalCredit, bool Balanced);

public sealed record StatementLine(string Code, string Name, decimal Amount);
public sealed record ProfitAndLossDto(DateOnly From, DateOnly To, IReadOnlyList<StatementLine> Revenue, IReadOnlyList<StatementLine> Expenses,
    decimal TotalRevenue, decimal TotalExpenses, decimal GrossProfit, decimal NetProfit, decimal CostOfSales);
public sealed record BalanceSheetDto(DateOnly AsOf, IReadOnlyList<StatementLine> Assets, IReadOnlyList<StatementLine> Liabilities,
    IReadOnlyList<StatementLine> Equity, decimal TotalAssets, decimal TotalLiabilities, decimal TotalEquity, decimal CurrentEarnings, bool Balanced);
public sealed record CashFlowDto(DateOnly From, DateOnly To, decimal OpeningCash, decimal ClosingCash, IReadOnlyList<StatementLine> InflowsBySource,
    IReadOnlyList<StatementLine> OutflowsBySource, decimal NetChange);
public sealed record TaxSummaryDto(DateOnly From, DateOnly To, decimal TaxCollected, decimal TaxDeductible, decimal NetTaxPayable);
public sealed record LedgerLineDto(Guid EntryId, string Number, DateOnly Date, string Description, decimal Debit, decimal Credit, decimal RunningBalance);
public sealed record LedgerDto(Guid AccountId, string Code, string Name, decimal OpeningBalance, IReadOnlyList<LedgerLineDto> Lines, decimal ClosingBalance);
public sealed record AgingRow(Guid PartyId, string PartyName, decimal Current, decimal Days30, decimal Days60, decimal Days90Plus, decimal Total);

public sealed record ExpenseDto(Guid Id, string Number, DateOnly Date, Guid ExpenseAccountId, string ExpenseAccountName, decimal Amount, decimal TaxAmount,
    Guid PaymentAccountId, string? Payee, string? Description, Guid? BranchId, Guid? JournalEntryId, string Status);
public sealed record CreateExpenseRequest(DateOnly Date, Guid ExpenseAccountId, decimal Amount, decimal TaxAmount, Guid PaymentAccountId, string? Payee,
    string? Description, Guid? BranchId, Guid? Id = null);

public sealed record BankAccountDto(Guid Id, string Name, string? BankName, string? AccountNumber, Guid LedgerAccountId, string Currency, bool IsActive);
public sealed record UpsertBankAccountRequest(string Name, string? BankName, string? AccountNumber, Guid? LedgerAccountId, string Currency, bool IsActive = true);
public sealed record BankStatementLineInput(DateOnly Date, string Description, decimal Amount, string? Reference);
public sealed record BankStatementLineDto(Guid Id, DateOnly Date, string Description, decimal Amount, string? Reference, Guid? MatchedJournalLineId, bool Reconciled);
public sealed record ReconcileRequest(Guid StatementLineId, Guid? JournalLineId);
public sealed record UnreconciledJournalLineDto(Guid JournalLineId, string Number, DateOnly Date, string Description, decimal Amount);
