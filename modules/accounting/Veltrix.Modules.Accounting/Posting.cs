using Microsoft.EntityFrameworkCore;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Accounting;

public sealed record PostingLine(string? Role, Guid? AccountId, decimal Debit, decimal Credit, string? Description = null,
    string? PartyType = null, Guid? PartyId = null);

/// <summary>Accumulates journal lines for a business document, then posts them as one balanced entry.</summary>
public sealed class PostingBuilder
{
    private readonly List<PostingLine> _lines = new();
    public IReadOnlyList<PostingLine> Lines => _lines;

    public PostingBuilder Debit(string role, decimal amount, string? description = null, string? partyType = null, Guid? partyId = null) =>
        Add(new PostingLine(role, null, amount, 0, description, partyType, partyId));
    public PostingBuilder Credit(string role, decimal amount, string? description = null, string? partyType = null, Guid? partyId = null) =>
        Add(new PostingLine(role, null, 0, amount, description, partyType, partyId));
    public PostingBuilder DebitAccount(Guid accountId, decimal amount, string? description = null, string? partyType = null, Guid? partyId = null) =>
        Add(new PostingLine(null, accountId, amount, 0, description, partyType, partyId));
    public PostingBuilder CreditAccount(Guid accountId, decimal amount, string? description = null, string? partyType = null, Guid? partyId = null) =>
        Add(new PostingLine(null, accountId, 0, amount, description, partyType, partyId));

    private PostingBuilder Add(PostingLine line)
    {
        // A negative debit is a credit (and vice versa); zero lines are dropped.
        var debit = Money.Round(line.Debit);
        var credit = Money.Round(line.Credit);
        if (debit < 0) { credit += -debit; debit = 0; }
        if (credit < 0) { debit += -credit; credit = 0; }
        var net = debit - credit;
        if (net == 0) return this;
        _lines.Add(line with { Debit = net > 0 ? net : 0, Credit = net < 0 ? -net : 0 });
        return this;
    }

    public decimal TotalDebit => _lines.Sum(l => l.Debit);
    public decimal TotalCredit => _lines.Sum(l => l.Credit);
}

public sealed class PostingService(DbContext db, ITenantContext ctx, ISequenceService sequences, IAuditWriter audit)
{
    private readonly Dictionary<string, Guid> _roleCache = new();

    public async Task<Guid> AccountForRoleAsync(string role, CancellationToken ct)
    {
        if (_roleCache.TryGetValue(role, out var id)) return id;
        id = await db.Set<Account>().Where(a => a.SystemRole == role).Select(a => a.Id).SingleOrDefaultAsync(ct);
        if (id == Guid.Empty)
            throw new DomainException("accounting.role_unmapped", $"No account is mapped to the '{role}' role. Configure the chart of accounts.", 409);
        _roleCache[role] = id;
        return id;
    }

    /// <summary>Validates and posts a balanced journal entry in the current unit of work. Caller saves/commits.</summary>
    public async Task<JournalEntry> PostAsync(DateOnly date, string description, JournalSource source, Guid? sourceId, Guid? branchId,
        PostingBuilder builder, CancellationToken ct)
    {
        var tid = ctx.RequireTenant();
        if (builder.Lines.Count < 2) throw new DomainException("accounting.unbalanced", "A journal entry needs at least two lines.");
        if (builder.TotalDebit != builder.TotalCredit)
            throw new DomainException("accounting.unbalanced", $"Journal entry is not balanced (debit {builder.TotalDebit} ≠ credit {builder.TotalCredit}).");
        await EnsurePeriodOpenAsync(date, ct);

        var entry = new JournalEntry
        {
            TenantId = tid, Date = date, Description = Truncate(description, 500), Source = source, SourceId = sourceId, BranchId = branchId,
            Status = JournalStatus.Posted, PostedAt = DateTimeOffset.UtcNow, PostedBy = ctx.UserId, TotalDebit = builder.TotalDebit,
        };
        var accountIds = new List<Guid>();
        foreach (var l in builder.Lines)
        {
            var accountId = l.AccountId ?? await AccountForRoleAsync(l.Role!, ct);
            accountIds.Add(accountId);
            entry.Lines.Add(new JournalLine
            {
                TenantId = tid, JournalEntryId = entry.Id, AccountId = accountId, Debit = l.Debit, Credit = l.Credit,
                Description = l.Description is null ? null : Truncate(l.Description, 300), PartyType = l.PartyType, PartyId = l.PartyId,
                BranchId = branchId, Date = date,
            });
        }
        var distinct = accountIds.Distinct().ToList();
        var valid = await db.Set<Account>().CountAsync(a => distinct.Contains(a.Id) && a.IsPostable && a.IsActive, ct);
        if (valid != distinct.Count) throw new DomainException("accounting.account_invalid", "One or more accounts are unknown, inactive or not postable.");

        entry.Number = await sequences.NextAsync(DocumentTypes.JournalEntry, null, ct);
        db.Set<JournalEntry>().Add(entry);
        return entry;
    }

    public async Task<JournalEntry> ReverseAsync(Guid entryId, DateOnly date, string reason, CancellationToken ct)
    {
        var original = await db.Set<JournalEntry>().Include(e => e.Lines).SingleOrDefaultAsync(e => e.Id == entryId, ct)
                       ?? throw new NotFoundException("journal.not_found", "Journal entry not found.");
        if (original.ReversedById is not null) throw new ConflictException("journal.already_reversed", "This entry has already been reversed.");
        if (original.ReversalOfId is not null) throw new DomainException("journal.is_reversal", "A reversal entry cannot itself be reversed.");
        var b = new PostingBuilder();
        foreach (var l in original.Lines)
        {
            if (l.Debit > 0) b.CreditAccount(l.AccountId, l.Debit, l.Description, l.PartyType, l.PartyId);
            else b.DebitAccount(l.AccountId, l.Credit, l.Description, l.PartyType, l.PartyId);
        }
        var reversal = await PostAsync(date, $"Reversal of {original.Number}: {reason}", JournalSource.Reversal, original.Id, original.BranchId, b, ct);
        reversal.ReversalOfId = original.Id;
        original.ReversedById = reversal.Id;
        audit.Write("journal.reversed", nameof(JournalEntry), original.Id.ToString(), new { reversal = reversal.Id, reason });
        return reversal;
    }

    public async Task EnsurePeriodOpenAsync(DateOnly date, CancellationToken ct)
    {
        var period = await db.Set<FiscalPeriod>().SingleOrDefaultAsync(p => p.Year == date.Year && p.Month == date.Month, ct);
        if (period is null)
        {
            var local = db.Set<FiscalPeriod>().Local.SingleOrDefault(p => p.Year == date.Year && p.Month == date.Month);
            if (local is null)
            {
                var start = new DateOnly(date.Year, date.Month, 1);
                db.Set<FiscalPeriod>().Add(new FiscalPeriod
                {
                    TenantId = ctx.RequireTenant(), Year = date.Year, Month = date.Month, StartDate = start, EndDate = start.AddMonths(1).AddDays(-1),
                });
            }
            return;
        }
        if (period.Status == PeriodStatus.Closed)
            throw new DomainException("accounting.period_closed", $"The fiscal period {date:yyyy-MM} is closed. Post in an open period or reopen it.", 409);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
