using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;

namespace Veltrix.Desktop.Core.Erp;

public sealed record ManualLineInput(string AccountCode, decimal Debit, decimal Credit, string? Description = null);
public sealed record ManualEntryInput(DateOnly Date, string Description, IReadOnlyList<ManualLineInput> Lines);
public sealed record JournalLineView(LocalJournalLine Line, string AccountName);
public sealed record JournalEntryView(LocalJournalEntry Entry, IReadOnlyList<JournalLineView> Lines);
public sealed record LedgerRow(DateOnly Date, string Number, string Description, decimal Debit, decimal Credit, decimal Balance);
public sealed record LedgerReport(string Code, string Name, string Type, decimal Opening, IReadOnlyList<LedgerRow> Rows, decimal TotalDebit, decimal TotalCredit, decimal Closing);
public sealed record TrialBalanceRow(string Code, string Name, string Type, decimal Debit, decimal Credit, decimal Balance);
public sealed record TrialBalanceReport(DateOnly From, DateOnly To, IReadOnlyList<TrialBalanceRow> Rows, decimal TotalDebit, decimal TotalCredit)
{
    public bool IsBalanced => TotalDebit == TotalCredit;
}
public sealed record StatementLine(string Code, string Name, decimal Amount);
public sealed record IncomeStatementReport(DateOnly From, DateOnly To, IReadOnlyList<StatementLine> Revenue, IReadOnlyList<StatementLine> Expenses,
    decimal TotalRevenue, decimal TotalExpenses, decimal CostOfGoodsSold, decimal GrossMargin, decimal NetResult);
public sealed record BalanceSheetReport(DateOnly AsOf, IReadOnlyList<StatementLine> Assets, IReadOnlyList<StatementLine> Liabilities, IReadOnlyList<StatementLine> Equity,
    decimal TotalAssets, decimal TotalLiabilities, decimal TotalEquity, decimal CurrentResult)
{
    public bool IsBalanced => TotalAssets == TotalLiabilities + TotalEquity;
}

public sealed partial class ErpService
{
    /// <summary>Account label in the UI language (fr = SCF French name, en, ar).</summary>
    public static string AccountName(LocalAccount a, string language) => language switch
    {
        "ar" => a.NameAr ?? a.Name,
        "en" => a.NameEn,
        _ => a.Name,
    };

    public async Task<IReadOnlyList<LocalAccount>> AccountsAsync(bool postableOnly = false, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var all = await db.Accounts.AsNoTracking().ToListAsync(ct);
        return all.Where(a => !postableOnly || (a.IsPostable && a.IsActive)).OrderBy(a => a.Code, StringComparer.Ordinal).ToList();
    }

    /// <summary>Manual journal entry: every line on one side only, amounts ≥ 0, total debit = total credit, open period.</summary>
    public Task<LocalJournalEntry> CreateManualEntryAsync(ManualEntryInput input, CancellationToken ct = default) => InTransactionAsync(async (db, now) =>
    {
        Require(!string.IsNullOrWhiteSpace(input.Description), "erp.err.descriptionRequired");
        Require(input.Lines is { Count: >= 2 }, "erp.err.linesRequired");
        Require(input.Lines.All(l => l.Debit >= 0 && l.Credit >= 0 && (l.Debit == 0) != (l.Credit == 0)), "erp.err.lineBothSides");
        var b = new PostingBuilder();
        foreach (var l in input.Lines)
        {
            if (l.Debit > 0) b.DebitAccount(l.AccountCode.Trim(), l.Debit, O(l.Description));
            else b.CreditAccount(l.AccountCode.Trim(), l.Credit, O(l.Description));
        }
        return await Ledger.PostAsync(db, input.Date, input.Description.Trim(), JournalSources.Manual, null, b, now, ct)
               ?? throw new InvalidOperationException("erp.err.unbalanced");
    }, ct);

    public async Task<IReadOnlyList<JournalEntryView>> JournalAsync(DateOnly from, DateOnly to, string? source = null, string? search = null, string language = "fr",
        CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var q = db.JournalEntries.AsNoTracking().Where(e => e.Date >= from && e.Date <= to);
        if (!string.IsNullOrWhiteSpace(source)) q = q.Where(e => e.Source == source);
        var entries = await q.ToListAsync(ct);
        var s = search?.Trim() ?? "";
        if (s.Length > 0) entries = entries.Where(e => Matches(e.Number, s) || Matches(e.Description, s)).ToList();
        var ids = entries.Select(e => e.Id).ToList();
        var lines = (await db.JournalLines.AsNoTracking().Where(l => ids.Contains(l.EntryId)).ToListAsync(ct)).ToLookup(l => l.EntryId);
        var names = (await db.Accounts.AsNoTracking().ToListAsync(ct)).ToDictionary(a => a.Code, a => AccountName(a, language));
        return entries.OrderByDescending(e => e.Date).ThenByDescending(e => e.Number, StringComparer.Ordinal)
            .Select(e => new JournalEntryView(e, lines[e.Id].OrderBy(l => l.LineNo).Select(l => new JournalLineView(l, names.GetValueOrDefault(l.AccountCode, l.AccountCode))).ToList()))
            .ToList();
    }

    private static async Task<List<LocalJournalLine>> LinesAsync(LocalDb db, DateOnly? from, DateOnly to, CancellationToken ct)
    {
        var q = db.JournalLines.AsNoTracking().Where(l => l.Date <= to);
        if (from is { } f) q = q.Where(l => l.Date >= f);
        return await q.ToListAsync(ct);
    }

    /// <summary>General ledger of one account: opening balance, movements with running balance (in the account's normal direction), closing.</summary>
    public async Task<LedgerReport> LedgerAsync(string accountCode, DateOnly from, DateOnly to, string language = "fr", CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var acc = await db.Accounts.FindAsync([accountCode], ct) ?? throw new InvalidOperationException("erp.err.accountInvalid");
        var sign = AccountTypes.IsDebitNormal(acc.Type) ? 1 : -1;
        var lines = await db.JournalLines.AsNoTracking().Where(l => l.AccountCode == accountCode && l.Date <= to).ToListAsync(ct);
        var opening = lines.Where(l => l.Date < from).Sum(l => sign * (l.Debit - l.Credit));
        var period = lines.Where(l => l.Date >= from).ToList();
        var entries = (await db.JournalEntries.AsNoTracking().Where(e => e.Date >= from && e.Date <= to).ToListAsync(ct)).ToDictionary(e => e.Id);
        var bal = opening;
        var rows = period.Select(l => (l, e: entries.GetValueOrDefault(l.EntryId)))
            .OrderBy(x => x.l.Date).ThenBy(x => x.e?.Number, StringComparer.Ordinal).ThenBy(x => x.l.LineNo)
            .Select(x => new LedgerRow(x.l.Date, x.e?.Number ?? "", x.l.Description ?? x.e?.Description ?? "", x.l.Debit, x.l.Credit, bal += sign * (x.l.Debit - x.l.Credit)))
            .ToList();
        return new LedgerReport(acc.Code, AccountName(acc, language), acc.Type, opening, rows, rows.Sum(r => r.Debit), rows.Sum(r => r.Credit), bal);
    }

    public async Task<TrialBalanceReport> TrialBalanceAsync(DateOnly from, DateOnly to, string language = "fr", CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var accounts = (await db.Accounts.AsNoTracking().ToListAsync(ct)).ToDictionary(a => a.Code);
        var rows = (await LinesAsync(db, from, to, ct)).GroupBy(l => l.AccountCode).Select(g =>
        {
            var a = accounts[g.Key];
            decimal d = g.Sum(x => x.Debit), c = g.Sum(x => x.Credit);
            return new TrialBalanceRow(a.Code, AccountName(a, language), a.Type, d, c, AccountTypes.IsDebitNormal(a.Type) ? d - c : c - d);
        }).OrderBy(r => r.Code, StringComparer.Ordinal).ToList();
        return new TrialBalanceReport(from, to, rows, rows.Sum(r => r.Debit), rows.Sum(r => r.Credit));
    }

    /// <summary>Income statement (port of the server P&amp;L): revenue (class 7) − expenses (class 6), with cost of goods sold and gross margin.</summary>
    public async Task<IncomeStatementReport> IncomeStatementAsync(DateOnly from, DateOnly to, string language = "fr", CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var accounts = (await db.Accounts.AsNoTracking().ToListAsync(ct)).ToDictionary(a => a.Code);
        var sums = (await LinesAsync(db, from, to, ct)).GroupBy(l => l.AccountCode)
            .Select(g => (Acc: accounts[g.Key], D: g.Sum(x => x.Debit), C: g.Sum(x => x.Credit))).ToList();
        var revenue = sums.Where(s => s.Acc.Type == AccountTypes.Revenue).Select(s => new StatementLine(s.Acc.Code, AccountName(s.Acc, language), s.C - s.D))
            .OrderBy(x => x.Code, StringComparer.Ordinal).ToList();
        var expenses = sums.Where(s => s.Acc.Type == AccountTypes.Expense).Select(s => new StatementLine(s.Acc.Code, AccountName(s.Acc, language), s.D - s.C))
            .OrderBy(x => x.Code, StringComparer.Ordinal).ToList();
        var cogs = sums.Where(s => s.Acc.Role == AccountRoles.CostOfGoodsSold).Sum(s => s.D - s.C);
        var tr = revenue.Sum(r => r.Amount);
        var te = expenses.Sum(r => r.Amount);
        return new IncomeStatementReport(from, to, revenue, expenses, tr, te, cogs, tr - cogs, tr - te);
    }

    /// <summary>Simple balance sheet at a date: assets = liabilities + equity + result of the (unclosed) period.</summary>
    public async Task<BalanceSheetReport> BalanceSheetAsync(DateOnly asOf, string language = "fr", CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var accounts = (await db.Accounts.AsNoTracking().ToListAsync(ct)).ToDictionary(a => a.Code);
        var sums = (await LinesAsync(db, null, asOf, ct)).GroupBy(l => l.AccountCode)
            .Select(g => (Acc: accounts[g.Key], D: g.Sum(x => x.Debit), C: g.Sum(x => x.Credit))).ToList();
        List<StatementLine> Section(string type, bool debitNormal) => sums.Where(s => s.Acc.Type == type)
            .Select(s => new StatementLine(s.Acc.Code, AccountName(s.Acc, language), debitNormal ? s.D - s.C : s.C - s.D))
            .Where(l => l.Amount != 0).OrderBy(l => l.Code, StringComparer.Ordinal).ToList();
        var assets = Section(AccountTypes.Asset, true);
        var liabilities = Section(AccountTypes.Liability, false);
        var equity = Section(AccountTypes.Equity, false);
        var result = sums.Where(s => s.Acc.Type == AccountTypes.Revenue).Sum(s => s.C - s.D) - sums.Where(s => s.Acc.Type == AccountTypes.Expense).Sum(s => s.D - s.C);
        var ta = assets.Sum(a => a.Amount);
        var tl = liabilities.Sum(a => a.Amount);
        return new BalanceSheetReport(asOf, assets, liabilities, equity, ta, tl, equity.Sum(a => a.Amount) + result, result);
    }

    // ------------------------------------------------------------------ periods

    public async Task<IReadOnlyList<LocalClosedPeriod>> ClosedPeriodsAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory();
        return (await db.ClosedPeriods.AsNoTracking().ToListAsync(ct)).OrderByDescending(p => p.Period, StringComparer.Ordinal).ToList();
    }

    /// <summary>Locks a month: any posting dated in it is rejected until it is reopened.</summary>
    public Task<bool> ClosePeriodAsync(int year, int month, CancellationToken ct = default) => InTransactionAsync(async (db, now) =>
    {
        Require(year is >= 2000 and <= 2100 && month is >= 1 and <= 12, "erp.err.period");
        var key = Ledger.PeriodKey(new DateOnly(year, month, 1));
        if (await db.ClosedPeriods.FindAsync([key], ct) is null) db.ClosedPeriods.Add(new LocalClosedPeriod { Period = key, ClosedAt = now });
        return true;
    }, ct);

    public Task<bool> ReopenPeriodAsync(int year, int month, CancellationToken ct = default) => InTransactionAsync(async (db, _) =>
    {
        var key = Ledger.PeriodKey(new DateOnly(year, month, 1));
        if (await db.ClosedPeriods.FindAsync([key], ct) is { } p) db.ClosedPeriods.Remove(p);
        return true;
    }, ct);
}
