using Microsoft.EntityFrameworkCore;
using Veltrix.Contracts.Accounting;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Accounting;

public sealed class AccountingService(DbContext db, ITenantContext ctx, PostingService posting, ISequenceService sequences,
    IAuditWriter audit, IClock clock, IEnumerable<IPartyDirectory> parties)
{
    // ---------- Chart of accounts ----------
    public async Task<IReadOnlyList<AccountDto>> AccountsAsync(CancellationToken ct) =>
        await db.Set<Account>().OrderBy(a => a.Code)
            .Select(a => new AccountDto(a.Id, a.Code, a.Name, a.NameAr, a.Type.ToString(), a.ParentId, a.IsPostable, a.IsActive, a.SystemRole)).ToListAsync(ct);

    public async Task<AccountDto> UpsertAccountAsync(Guid? id, UpsertAccountRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Code, "code", 20).NotEmpty(req.Name, "name", 150)
            .Require(Enum.TryParse<AccountType>(req.Type, true, out _), "type", "Unknown account type.").ThrowIfInvalid();
        var code = req.Code.Trim();
        if (await db.Set<Account>().AnyAsync(a => a.Code == code && a.Id != id, ct)) throw new ConflictException("account.code_taken", "Account code already exists.");
        if (req.ParentId is { } p && !await db.Set<Account>().AnyAsync(a => a.Id == p, ct)) throw ValidationException.For("parentId", "Unknown parent account.");
        Account a;
        if (id is { } aid)
        {
            a = await db.Set<Account>().SingleOrDefaultAsync(x => x.Id == aid, ct) ?? throw new NotFoundException("account.not_found", "Account not found.");
            var hasLines = await db.Set<JournalLine>().AnyAsync(l => l.AccountId == aid, ct);
            if (hasLines && a.Type.ToString() != req.Type) throw new DomainException("account.type_locked", "The type of an account with postings cannot change.");
            if (a.SystemRole is not null && (!req.IsActive || !req.IsPostable)) throw new DomainException("account.system_role", "System-mapped accounts must stay active and postable.");
        }
        else a = db.Set<Account>().Add(new Account { TenantId = ctx.RequireTenant() }).Entity;
        a.Code = code; a.Name = req.Name.Trim(); a.NameAr = req.NameAr; a.Type = Enum.Parse<AccountType>(req.Type, true);
        a.ParentId = req.ParentId; a.IsPostable = req.IsPostable; a.IsActive = req.IsActive;
        audit.Write(id is null ? "account.created" : "account.updated", nameof(Account), a.Id.ToString());
        await db.SaveChangesAsync(ct);
        return new AccountDto(a.Id, a.Code, a.Name, a.NameAr, a.Type.ToString(), a.ParentId, a.IsPostable, a.IsActive, a.SystemRole);
    }

    // ---------- Journal ----------
    public async Task<JournalEntryDto> CreateManualEntryAsync(CreateJournalEntryRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Description, "description", 500).Require(req.Lines is { Count: >= 2 }, "lines", "At least two lines.")
            .Require(req.Lines.All(l => l.Debit >= 0 && l.Credit >= 0 && (l.Debit == 0) != (l.Credit == 0)), "lines", "Each line must have either a debit or a credit.")
            .ThrowIfInvalid();
        if (req.BranchId is { } br) ctx.EnsureBranch(br);
        var b = new PostingBuilder();
        foreach (var l in req.Lines)
        {
            if (l.Debit > 0) b.DebitAccount(l.AccountId, l.Debit, l.Description, l.PartyType, l.PartyId);
            else b.CreditAccount(l.AccountId, l.Credit, l.Description, l.PartyType, l.PartyId);
        }
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var e = await posting.PostAsync(req.Date, req.Description, JournalSource.Manual, null, req.BranchId, b, ct);
        audit.Write("journal.posted", nameof(JournalEntry), e.Id.ToString(), new { e.TotalDebit });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetEntryAsync(e.Id, ct);
    }

    public async Task<JournalEntryDto> ReverseAsync(Guid id, ReverseRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Reason, "reason", 300).ThrowIfInvalid();
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var r = await posting.ReverseAsync(id, req.Date ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), req.Reason, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetEntryAsync(r.Id, ct);
    }

    public async Task<PagedResult<JournalEntryDto>> EntriesAsync(PageQuery q, DateOnly? from, DateOnly? to, string? source, CancellationToken ct)
    {
        var query = db.Set<JournalEntry>().Include(e => e.Lines).AsNoTracking().AsQueryable();
        if (from is { } f) query = query.Where(e => e.Date >= f);
        if (to is { } t) query = query.Where(e => e.Date <= t);
        if (source is not null && Enum.TryParse<JournalSource>(source, true, out var s)) query = query.Where(e => e.Source == s);
        if (!string.IsNullOrWhiteSpace(q.Search)) query = query.Where(e => e.Number.Contains(q.Search) || e.Description.Contains(q.Search));
        if (ctx.AllowedBranches is { } allowed) query = query.Where(e => e.BranchId == null || allowed.Contains(e.BranchId.Value));
        var page = await query.OrderByDescending(e => e.Date).ThenByDescending(e => e.Number).ToPagedAsync(q, ct);
        var accounts = await AccountMapAsync(ct);
        return new PagedResult<JournalEntryDto>(page.Items.Select(e => ToDto(e, accounts)).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<JournalEntryDto> GetEntryAsync(Guid id, CancellationToken ct)
    {
        var e = await db.Set<JournalEntry>().Include(x => x.Lines).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("journal.not_found", "Journal entry not found.");
        return ToDto(e, await AccountMapAsync(ct));
    }

    // ---------- Periods ----------
    public async Task<IReadOnlyList<FiscalPeriodDto>> PeriodsAsync(CancellationToken ct) =>
        await db.Set<FiscalPeriod>().OrderByDescending(p => p.Year).ThenByDescending(p => p.Month)
            .Select(p => new FiscalPeriodDto(p.Id, p.Year, p.Month, p.StartDate, p.EndDate, p.Status.ToString(), p.ClosedAt)).ToListAsync(ct);

    public async Task<FiscalPeriodDto> SetPeriodStatusAsync(int year, int month, bool close, CancellationToken ct)
    {
        if (month is < 1 or > 12) throw ValidationException.For("month", "Invalid month.");
        var p = await db.Set<FiscalPeriod>().SingleOrDefaultAsync(x => x.Year == year && x.Month == month, ct);
        if (p is null)
        {
            var start = new DateOnly(year, month, 1);
            p = db.Set<FiscalPeriod>().Add(new FiscalPeriod { TenantId = ctx.RequireTenant(), Year = year, Month = month, StartDate = start, EndDate = start.AddMonths(1).AddDays(-1) }).Entity;
        }
        if (close)
        {
            // Periods close in order: an earlier open period blocks closing a later one.
            var earlierOpen = await db.Set<FiscalPeriod>().AnyAsync(x => (x.Year < year || x.Year == year && x.Month < month) && x.Status == PeriodStatus.Open, ct);
            if (earlierOpen) throw new DomainException("accounting.close_order", "Close earlier periods first.");
            p.Status = PeriodStatus.Closed; p.ClosedAt = clock.UtcNow; p.ClosedBy = ctx.UserId;
        }
        else
        {
            var laterClosed = await db.Set<FiscalPeriod>().AnyAsync(x => (x.Year > year || x.Year == year && x.Month > month) && x.Status == PeriodStatus.Closed, ct);
            if (laterClosed) throw new DomainException("accounting.reopen_order", "Reopen later periods first.");
            p.Status = PeriodStatus.Open; p.ClosedAt = null; p.ClosedBy = null;
        }
        audit.Write(close ? "period.closed" : "period.reopened", nameof(FiscalPeriod), p.Id.ToString(), new { year, month });
        await db.SaveChangesAsync(ct);
        return new FiscalPeriodDto(p.Id, p.Year, p.Month, p.StartDate, p.EndDate, p.Status.ToString(), p.ClosedAt);
    }

    // ---------- Reports ----------
    private IQueryable<JournalLine> Lines(DateOnly? from, DateOnly to, Guid? branchId)
    {
        var q = db.Set<JournalLine>().AsNoTracking().Where(l => l.Date <= to);
        if (from is { } f) q = q.Where(l => l.Date >= f);
        if (branchId is { } b) q = q.Where(l => l.BranchId == b);
        return q;
    }

    public async Task<TrialBalanceDto> TrialBalanceAsync(DateOnly from, DateOnly to, Guid? branchId, CancellationToken ct)
    {
        var sums = await Lines(from, to, branchId).GroupBy(l => l.AccountId)
            .Select(g => new { g.Key, D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit) }).ToListAsync(ct);
        var accounts = await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Id, ct);
        var rows = sums.Select(s =>
        {
            var a = accounts[s.Key];
            return new TrialBalanceRow(a.Id, a.Code, a.Name, a.Type.ToString(), s.D, s.C, a.IsDebitNormal ? s.D - s.C : s.C - s.D);
        }).OrderBy(r => r.Code).ToList();
        var td = rows.Sum(r => r.Debit); var tc = rows.Sum(r => r.Credit);
        return new TrialBalanceDto(from, to, rows, td, tc, td == tc);
    }

    public async Task<ProfitAndLossDto> ProfitAndLossAsync(DateOnly from, DateOnly to, Guid? branchId, CancellationToken ct)
    {
        var accounts = await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Id, ct);
        var sums = await Lines(from, to, branchId).GroupBy(l => l.AccountId)
            .Select(g => new { g.Key, D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit) }).ToListAsync(ct);
        var revenue = sums.Where(s => accounts[s.Key].Type == AccountType.Revenue)
            .Select(s => new StatementLine(accounts[s.Key].Code, accounts[s.Key].Name, s.C - s.D)).OrderBy(x => x.Code).ToList();
        var expenses = sums.Where(s => accounts[s.Key].Type == AccountType.Expense)
            .Select(s => new StatementLine(accounts[s.Key].Code, accounts[s.Key].Name, s.D - s.C)).OrderBy(x => x.Code).ToList();
        var cogs = sums.Where(s => accounts[s.Key].SystemRole == AccountRoles.CostOfGoodsSold).Sum(s => s.D - s.C);
        var totalRevenue = revenue.Sum(r => r.Amount);
        var totalExpenses = expenses.Sum(r => r.Amount);
        return new ProfitAndLossDto(from, to, revenue, expenses, totalRevenue, totalExpenses, totalRevenue - cogs, totalRevenue - totalExpenses, cogs);
    }

    public async Task<BalanceSheetDto> BalanceSheetAsync(DateOnly asOf, CancellationToken ct)
    {
        var accounts = await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Id, ct);
        var sums = await Lines(null, asOf, null).GroupBy(l => l.AccountId)
            .Select(g => new { g.Key, D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit) }).ToListAsync(ct);
        List<StatementLine> Section(AccountType t, bool debitNormal) => sums.Where(s => accounts[s.Key].Type == t)
            .Select(s => new StatementLine(accounts[s.Key].Code, accounts[s.Key].Name, debitNormal ? s.D - s.C : s.C - s.D))
            .Where(l => l.Amount != 0).OrderBy(l => l.Code).ToList();
        var assets = Section(AccountType.Asset, true);
        var liabilities = Section(AccountType.Liability, false);
        var equity = Section(AccountType.Equity, false);
        var earnings = sums.Where(s => accounts[s.Key].Type == AccountType.Revenue).Sum(s => s.C - s.D)
                       - sums.Where(s => accounts[s.Key].Type == AccountType.Expense).Sum(s => s.D - s.C);
        var ta = assets.Sum(a => a.Amount); var tl = liabilities.Sum(a => a.Amount); var te = equity.Sum(a => a.Amount) + earnings;
        return new BalanceSheetDto(asOf, assets, liabilities, equity, ta, tl, te, earnings, ta == tl + te);
    }

    public async Task<CashFlowDto> CashFlowAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var cashRoles = new[] { AccountRoles.Cash, AccountRoles.Bank, AccountRoles.CardClearing };
        var cashIds = await db.Set<Account>().Where(a => cashRoles.Contains(a.SystemRole!) ||
            db.Set<BankAccount>().Select(b => b.LedgerAccountId).Contains(a.Id)).Select(a => a.Id).ToListAsync(ct);
        var opening = await db.Set<JournalLine>().Where(l => cashIds.Contains(l.AccountId) && l.Date < from).SumAsync(l => (decimal?)(l.Debit - l.Credit), ct) ?? 0;
        var flows = await (from l in db.Set<JournalLine>()
                           join e in db.Set<JournalEntry>() on l.JournalEntryId equals e.Id
                           where cashIds.Contains(l.AccountId) && l.Date >= @from && l.Date <= to
                           group l by e.Source into g
                           select new { Source = g.Key, In = g.Sum(x => x.Debit), Out = g.Sum(x => x.Credit) }).ToListAsync(ct);
        var inflows = flows.Where(f => f.In > 0).Select(f => new StatementLine(f.Source.ToString(), f.Source.ToString(), f.In)).ToList();
        var outflows = flows.Where(f => f.Out > 0).Select(f => new StatementLine(f.Source.ToString(), f.Source.ToString(), f.Out)).ToList();
        var net = inflows.Sum(i => i.Amount) - outflows.Sum(o => o.Amount);
        return new CashFlowDto(from, to, opening, opening + net, inflows, outflows, net);
    }

    public async Task<TaxSummaryDto> TaxSummaryAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var collectedId = await posting.AccountForRoleAsync(AccountRoles.VatCollected, ct);
        var deductibleId = await posting.AccountForRoleAsync(AccountRoles.VatDeductible, ct);
        var collected = await Lines(from, to, null).Where(l => l.AccountId == collectedId).SumAsync(l => (decimal?)(l.Credit - l.Debit), ct) ?? 0;
        var deductible = await Lines(from, to, null).Where(l => l.AccountId == deductibleId).SumAsync(l => (decimal?)(l.Debit - l.Credit), ct) ?? 0;
        return new TaxSummaryDto(from, to, collected, deductible, collected - deductible);
    }

    public async Task<LedgerDto> LedgerAsync(Guid accountId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var a = await db.Set<Account>().SingleOrDefaultAsync(x => x.Id == accountId, ct) ?? throw new NotFoundException("account.not_found", "Account not found.");
        var sign = a.IsDebitNormal ? 1 : -1;
        var opening = sign * (await db.Set<JournalLine>().Where(l => l.AccountId == accountId && l.Date < from).SumAsync(l => (decimal?)(l.Debit - l.Credit), ct) ?? 0);
        var lines = await (from l in db.Set<JournalLine>()
                           join e in db.Set<JournalEntry>() on l.JournalEntryId equals e.Id
                           where l.AccountId == accountId && l.Date >= @from && l.Date <= to
                           orderby l.Date, e.Number
                           select new { e.Id, e.Number, l.Date, Description = l.Description ?? e.Description, l.Debit, l.Credit }).Take(5000).ToListAsync(ct);
        var running = opening;
        var result = lines.Select(l => { running += sign * (l.Debit - l.Credit); return new LedgerLineDto(l.Id, l.Number, l.Date, l.Description, l.Debit, l.Credit, running); }).ToList();
        return new LedgerDto(a.Id, a.Code, a.Name, opening, result, running);
    }

    /// <summary>Outstanding balance per party, bucketed by age of the open debit (FIFO allocation of credits).</summary>
    public async Task<IReadOnlyList<AgingRow>> AgingAsync(string partyType, DateOnly asOf, CancellationToken ct)
    {
        var role = partyType == PartyTypes.Customer ? AccountRoles.AccountsReceivable : AccountRoles.AccountsPayable;
        var accountId = await posting.AccountForRoleAsync(role, ct);
        var lines = await db.Set<JournalLine>().AsNoTracking()
            .Where(l => l.AccountId == accountId && l.PartyType == partyType && l.PartyId != null && l.Date <= asOf)
            .Select(l => new { l.PartyId, l.Date, l.Debit, l.Credit }).ToListAsync(ct);
        var rows = new List<(Guid Party, decimal[] Buckets)>();
        foreach (var g in lines.GroupBy(l => l.PartyId!.Value))
        {
            // Receivable increases on debit; payable increases on credit.
            var increases = g.Select(l => (l.Date, Amount: partyType == PartyTypes.Customer ? l.Debit - l.Credit : l.Credit - l.Debit)).ToList();
            var open = new List<(DateOnly Date, decimal Amount)>();
            decimal credit = -increases.Where(i => i.Amount < 0).Sum(i => i.Amount);
            foreach (var inc in increases.Where(i => i.Amount > 0).OrderBy(i => i.Date))
            {
                var applied = Math.Min(credit, inc.Amount);
                credit -= applied;
                if (inc.Amount - applied > 0) open.Add((inc.Date, inc.Amount - applied));
            }
            var buckets = new decimal[4];
            foreach (var o in open)
            {
                var days = asOf.DayNumber - o.Date.DayNumber;
                buckets[days <= 30 ? 0 : days <= 60 ? 1 : days <= 90 ? 2 : 3] += o.Amount;
            }
            if (credit > 0) buckets[0] -= credit; // unapplied credit (advance)
            if (buckets.Any(b => b != 0)) rows.Add((g.Key, buckets));
        }
        var dir = parties.FirstOrDefault(p => p.PartyType == partyType);
        var names = dir is null ? new Dictionary<Guid, string>() : await dir.NamesAsync(rows.Select(r => r.Party).ToList(), ct);
        return rows.Select(r => new AgingRow(r.Party, names.GetValueOrDefault(r.Party, r.Party.ToString()), r.Buckets[0], r.Buckets[1], r.Buckets[2], r.Buckets[3], r.Buckets.Sum()))
            .OrderByDescending(r => r.Total).ToList();
    }

    public async Task<decimal> PartyBalanceAsync(string partyType, Guid partyId, CancellationToken ct)
    {
        var role = partyType == PartyTypes.Customer ? AccountRoles.AccountsReceivable : AccountRoles.AccountsPayable;
        var accountId = await posting.AccountForRoleAsync(role, ct);
        var net = await db.Set<JournalLine>().Where(l => l.AccountId == accountId && l.PartyType == partyType && l.PartyId == partyId)
            .SumAsync(l => (decimal?)(l.Debit - l.Credit), ct) ?? 0;
        return partyType == PartyTypes.Customer ? net : -net;
    }

    // ---------- Expenses ----------
    public async Task<ExpenseDto> CreateExpenseAsync(CreateExpenseRequest req, CancellationToken ct)
    {
        new Validator().Positive(req.Amount, "amount").NonNegative(req.TaxAmount, "taxAmount").Require(req.TaxAmount < req.Amount, "taxAmount", "Tax must be less than the amount.")
            .MaxLength(req.Description, "description", 500).MaxLength(req.Payee, "payee", 150).ThrowIfInvalid();
        if (req.Id is { } existingId && await db.Set<Expense>().AnyAsync(e => e.Id == existingId, ct))
            return (await ExpensesAsync(new PageQuery(1, 1), null, null, existingId, ct)).Items.Single();
        if (req.BranchId is { } br) ctx.EnsureBranch(br);
        var expenseAccount = await db.Set<Account>().SingleOrDefaultAsync(a => a.Id == req.ExpenseAccountId && a.Type == AccountType.Expense && a.IsPostable, ct)
                             ?? throw ValidationException.For("expenseAccountId", "Choose a postable expense account.");
        var payAccount = await db.Set<Account>().SingleOrDefaultAsync(a => a.Id == req.PaymentAccountId && a.Type == AccountType.Asset && a.IsPostable, ct)
                         ?? throw ValidationException.For("paymentAccountId", "Choose a cash or bank account.");
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var number = await sequences.NextAsync(DocumentTypes.Expense, null, ct);
        var expense = new Expense
        {
            TenantId = ctx.RequireTenant(), Number = number, Date = req.Date, ExpenseAccountId = expenseAccount.Id, Amount = Money.Round(req.Amount),
            TaxAmount = Money.Round(req.TaxAmount), PaymentAccountId = payAccount.Id, Payee = req.Payee, Description = req.Description, BranchId = req.BranchId,
            Status = ExpenseStatus.Posted,
        };
        if (req.Id is { } cid && cid != Guid.Empty) expense.Id = cid;
        var b = new PostingBuilder()
            .DebitAccount(expenseAccount.Id, expense.Amount - expense.TaxAmount, req.Description)
            .Debit(AccountRoles.VatDeductible, expense.TaxAmount)
            .CreditAccount(payAccount.Id, expense.Amount, req.Payee);
        var je = await posting.PostAsync(req.Date, $"Expense {number} {req.Payee}".Trim(), JournalSource.Expense, expense.Id, req.BranchId, b, ct);
        expense.JournalEntryId = je.Id;
        db.Add(expense);
        audit.Write("expense.created", nameof(Expense), expense.Id.ToString(), new { expense.Amount });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await ExpensesAsync(new PageQuery(1, 1), null, null, expense.Id, ct)).Items.Single();
    }

    public async Task<PagedResult<ExpenseDto>> ExpensesAsync(PageQuery q, DateOnly? from, DateOnly? to, Guid? id, CancellationToken ct)
    {
        var query = from e in db.Set<Expense>().AsNoTracking()
                    join a in db.Set<Account>() on e.ExpenseAccountId equals a.Id
                    select new { e, a.Name };
        if (id is { } x) query = query.Where(r => r.e.Id == x);
        if (from is { } f) query = query.Where(r => r.e.Date >= f);
        if (to is { } t) query = query.Where(r => r.e.Date <= t);
        if (ctx.AllowedBranches is { } allowed) query = query.Where(r => r.e.BranchId == null || allowed.Contains(r.e.BranchId.Value));
        var page = await query.OrderByDescending(r => r.e.Date).ThenByDescending(r => r.e.Number).ToPagedAsync(q, ct);
        return new PagedResult<ExpenseDto>(page.Items.Select(r => new ExpenseDto(r.e.Id, r.e.Number, r.e.Date, r.e.ExpenseAccountId, r.Name, r.e.Amount,
            r.e.TaxAmount, r.e.PaymentAccountId, r.e.Payee, r.e.Description, r.e.BranchId, r.e.JournalEntryId, r.e.Status.ToString())).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    // ---------- Banks & reconciliation ----------
    public async Task<IReadOnlyList<BankAccountDto>> BankAccountsAsync(CancellationToken ct) =>
        await db.Set<BankAccount>().OrderBy(b => b.Name)
            .Select(b => new BankAccountDto(b.Id, b.Name, b.BankName, b.AccountNumber, b.LedgerAccountId, b.Currency, b.IsActive)).ToListAsync(ct);

    public async Task<BankAccountDto> UpsertBankAccountAsync(Guid? id, UpsertBankAccountRequest req, CancellationToken ct)
    {
        var tid = ctx.RequireTenant();
        new Validator().NotEmpty(req.Name, "name", 120).Require(req.Currency is { Length: 3 }, "currency", "ISO code required.").ThrowIfInvalid();
        var b = id is { } bid ? await db.Set<BankAccount>().SingleOrDefaultAsync(x => x.Id == bid, ct) ?? throw new NotFoundException("bank.not_found", "Bank account not found.")
            : db.Set<BankAccount>().Add(new BankAccount { TenantId = tid }).Entity;
        Guid ledger;
        if (req.LedgerAccountId is { } l)
        {
            if (!await db.Set<Account>().AnyAsync(a => a.Id == l && a.Type == AccountType.Asset && a.IsPostable, ct)) throw ValidationException.For("ledgerAccountId", "Choose a postable asset account.");
            ledger = l;
        }
        else if (b.LedgerAccountId != Guid.Empty) ledger = b.LedgerAccountId;
        else
        {
            // Create a dedicated 512x sub-account for the new bank account.
            var parent = await db.Set<Account>().SingleAsync(a => a.SystemRole == AccountRoles.Bank, ct);
            var n = await db.Set<Account>().CountAsync(a => a.Code.StartsWith("512"), ct);
            var acc = new Account { TenantId = tid, Code = "512" + (n + 1), Name = req.Name.Trim(), Type = AccountType.Asset, ParentId = parent.ParentId };
            db.Add(acc);
            ledger = acc.Id;
        }
        b.Name = req.Name.Trim(); b.BankName = req.BankName; b.AccountNumber = req.AccountNumber; b.LedgerAccountId = ledger;
        b.Currency = req.Currency.ToUpperInvariant(); b.IsActive = req.IsActive;
        await db.SaveChangesAsync(ct);
        return new BankAccountDto(b.Id, b.Name, b.BankName, b.AccountNumber, b.LedgerAccountId, b.Currency, b.IsActive);
    }

    public async Task<int> ImportStatementAsync(Guid bankAccountId, IReadOnlyList<BankStatementLineInput> lines, CancellationToken ct)
    {
        var tid = ctx.RequireTenant();
        if (!await db.Set<BankAccount>().AnyAsync(b => b.Id == bankAccountId, ct)) throw new NotFoundException("bank.not_found", "Bank account not found.");
        if (lines.Count is 0 or > 5000) throw ValidationException.For("lines", "Provide 1 to 5000 lines.");
        foreach (var l in lines)
            db.Add(new BankStatementLine { TenantId = tid, BankAccountId = bankAccountId, Date = l.Date, Description = l.Description.Length > 300 ? l.Description[..300] : l.Description, Amount = l.Amount, Reference = l.Reference });
        await db.SaveChangesAsync(ct);
        return lines.Count;
    }

    public async Task<IReadOnlyList<BankStatementLineDto>> StatementLinesAsync(Guid bankAccountId, bool unreconciledOnly, CancellationToken ct) =>
        await db.Set<BankStatementLine>().Where(l => l.BankAccountId == bankAccountId && (!unreconciledOnly || !l.Reconciled))
            .OrderBy(l => l.Date).Select(l => new BankStatementLineDto(l.Id, l.Date, l.Description, l.Amount, l.Reference, l.MatchedJournalLineId, l.Reconciled)).ToListAsync(ct);

    public async Task<IReadOnlyList<UnreconciledJournalLineDto>> UnreconciledLedgerLinesAsync(Guid bankAccountId, CancellationToken ct)
    {
        var bank = await db.Set<BankAccount>().SingleOrDefaultAsync(b => b.Id == bankAccountId, ct) ?? throw new NotFoundException("bank.not_found", "Bank account not found.");
        var matched = db.Set<BankStatementLine>().Where(s => s.MatchedJournalLineId != null).Select(s => s.MatchedJournalLineId!.Value);
        return await (from l in db.Set<JournalLine>()
                      join e in db.Set<JournalEntry>() on l.JournalEntryId equals e.Id
                      where l.AccountId == bank.LedgerAccountId && !matched.Contains(l.Id)
                      orderby l.Date
                      select new UnreconciledJournalLineDto(l.Id, e.Number, l.Date, l.Description ?? e.Description, l.Debit - l.Credit)).Take(2000).ToListAsync(ct);
    }

    /// <summary>Match a statement line to a ledger line of equal amount (or mark reconciled with no match, e.g. bank fees already booked).</summary>
    public async Task ReconcileAsync(Guid bankAccountId, ReconcileRequest req, CancellationToken ct)
    {
        var s = await db.Set<BankStatementLine>().SingleOrDefaultAsync(x => x.Id == req.StatementLineId && x.BankAccountId == bankAccountId, ct)
                ?? throw new NotFoundException("statement.not_found", "Statement line not found.");
        if (s.Reconciled) throw new ConflictException("statement.reconciled", "Already reconciled.");
        if (req.JournalLineId is { } jl)
        {
            var bank = await db.Set<BankAccount>().SingleAsync(b => b.Id == bankAccountId, ct);
            var line = await db.Set<JournalLine>().SingleOrDefaultAsync(l => l.Id == jl && l.AccountId == bank.LedgerAccountId, ct)
                       ?? throw ValidationException.For("journalLineId", "Ledger line not found on this bank account.");
            if (line.Debit - line.Credit != s.Amount) throw ValidationException.For("journalLineId", "Amounts do not match.");
            if (await db.Set<BankStatementLine>().AnyAsync(x => x.MatchedJournalLineId == jl, ct)) throw new ConflictException("statement.line_matched", "Ledger line already matched.");
            s.MatchedJournalLineId = jl;
        }
        s.Reconciled = true;
        s.ReconciledAt = clock.UtcNow;
        audit.Write("bank.reconciled", nameof(BankStatementLine), s.Id.ToString(), new { req.JournalLineId });
        await db.SaveChangesAsync(ct);
    }

    private async Task<Dictionary<Guid, Account>> AccountMapAsync(CancellationToken ct) => await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Id, ct);

    private static JournalEntryDto ToDto(JournalEntry e, IReadOnlyDictionary<Guid, Account> accounts) => new(
        e.Id, e.Number, e.Date, e.Description, e.Source.ToString(), e.SourceId, e.Status.ToString(), e.BranchId, e.ReversalOfId, e.ReversedById, e.TotalDebit,
        e.Lines.OrderByDescending(l => l.Debit).Select(l => new JournalLineDto(l.Id, l.AccountId, accounts.GetValueOrDefault(l.AccountId)?.Code ?? "",
            accounts.GetValueOrDefault(l.AccountId)?.Name ?? "", l.Debit, l.Credit, l.Description, l.PartyType, l.PartyId)).ToList(), e.CreatedAt);
}
