using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Veltrix.Contracts.Crm;
using Veltrix.Modules.Accounting;
using Veltrix.SharedKernel;
using TaskStatus = Veltrix.Modules.Crm.TaskStatus;

namespace Veltrix.Modules.Crm;

/// <summary>Implemented by POS and sales so CRM can show purchase history without depending on them.</summary>
public interface ICustomerHistoryProvider
{
    Task<IReadOnlyList<CustomerPurchaseDto>> PurchasesAsync(Guid customerId, int take, CancellationToken ct);
}

public sealed class CustomerPartyDirectory(DbContext db) : IPartyDirectory
{
    public string PartyType => PartyTypes.Customer;
    public async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
        await db.Set<Customer>().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
}

public sealed class CrmService(DbContext db, ITenantContext ctx, ISequenceService sequences, IAuditWriter audit, IClock clock,
    AccountingService accounting, PostingService posting, IEnumerable<ICustomerHistoryProvider> history)
{
    public async Task<PagedResult<CustomerDto>> ListAsync(PageQuery q, Guid? groupId, bool? active, DateTimeOffset? changedSince, CancellationToken ct)
    {
        var query = db.Set<Customer>().AsNoTracking().AsQueryable();
        if (groupId is { } g) query = query.Where(c => c.GroupId == g);
        if (active is { } a) query = query.Where(c => c.IsActive == a);
        if (changedSince is { } since) query = query.Where(c => c.UpdatedAt > since);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim().ToUpper();
            query = query.Where(c => c.Name.ToUpper().Contains(s) || c.Code.ToUpper().Contains(s) || (c.Phone != null && c.Phone.Contains(q.Search.Trim()))
                                     || (c.Email != null && c.Email.ToUpper().Contains(s)));
        }
        query = q.SortField switch
        {
            "code" => q.Descending ? query.OrderByDescending(c => c.Code) : query.OrderBy(c => c.Code),
            "updated" => q.Descending ? query.OrderByDescending(c => c.UpdatedAt) : query.OrderBy(c => c.UpdatedAt),
            _ => q.Descending ? query.OrderByDescending(c => c.Name) : query.OrderBy(c => c.Name),
        };
        var page = await query.ToPagedAsync(q, ct);
        var balances = await BalancesAsync(page.Items.Select(c => c.Id).ToList(), ct);
        return new PagedResult<CustomerDto>(page.Items.Select(c => ToDto(c, balances.GetValueOrDefault(c.Id))).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<CustomerDto> GetAsync(Guid id, CancellationToken ct)
    {
        var c = await db.Set<Customer>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("customer.not_found", "Customer not found.");
        return ToDto(c, await accounting.PartyBalanceAsync(PartyTypes.Customer, id, ct));
    }

    public async Task<CustomerDto> UpsertAsync(Guid? id, UpsertCustomerRequest req, CancellationToken ct)
    {
        var tid = ctx.RequireTenant();
        new Validator().NotEmpty(req.Name, "name").Require(Enum.TryParse<CustomerType>(req.Type, true, out _), "type", "Unknown type.")
            .MaxLength(req.Phone, "phone", 40).MaxLength(req.Email, "email", 256).Require(req.Email is null || req.Email.Contains('@'), "email", "Invalid email.")
            .MaxLength(req.Address, "address", 300).MaxLength(req.Notes, "notes", 2000).NonNegative(req.CreditLimit, "creditLimit")
            .MaxLength(req.Code, "code", 30).ThrowIfInvalid();
        if (req.GroupId is { } gid && !await db.Set<CustomerGroup>().AnyAsync(g => g.Id == gid, ct)) throw ValidationException.For("groupId", "Unknown group.");
        Customer c;
        if (id is { } cid)
        {
            c = await db.Set<Customer>().SingleOrDefaultAsync(x => x.Id == cid, ct) ?? throw new NotFoundException("customer.not_found", "Customer not found.");
            if (c.AnonymizedAt is not null) throw new DomainException("customer.anonymized", "This customer record was anonymized.");
        }
        else
        {
            // Offline clients create customers with their own stable id; replays are idempotent.
            if (req.Id is { } clientId && clientId != Guid.Empty)
            {
                var existing = await db.Set<Customer>().SingleOrDefaultAsync(x => x.Id == clientId, ct);
                if (existing is not null) return await GetAsync(existing.Id, ct);
            }
            c = new Customer { TenantId = tid };
            if (req.Id is { } newId && newId != Guid.Empty) c.Id = newId;
            c.Code = string.IsNullOrWhiteSpace(req.Code) ? await sequences.NextAsync(DocumentTypes.Customer, null, ct) : req.Code.Trim();
            db.Add(c);
        }
        if (!string.IsNullOrWhiteSpace(req.Code) && req.Code.Trim() != c.Code)
        {
            if (await db.Set<Customer>().AnyAsync(x => x.Code == req.Code.Trim() && x.Id != c.Id, ct)) throw new ConflictException("customer.code_taken", "Customer code already exists.");
            c.Code = req.Code.Trim();
        }
        if (req.CreditLimit != c.CreditLimit && id is not null) ctx.EnsurePermission(Permissions.SalesApprove);
        c.Type = Enum.Parse<CustomerType>(req.Type, true); c.Name = req.Name.Trim(); c.Phone = req.Phone?.Trim(); c.Email = req.Email?.Trim();
        c.Address = req.Address; c.City = req.City; c.Wilaya = req.Wilaya; c.TaxId = req.TaxId; c.TradeRegisterNo = req.TradeRegisterNo;
        c.GroupId = req.GroupId; c.PriceListId = req.PriceListId; c.CreditLimit = req.CreditLimit; c.Notes = req.Notes;
        if (req.MarketingConsent != c.MarketingConsent)
        {
            c.MarketingConsent = req.MarketingConsent;
            c.ConsentAt = clock.UtcNow;
            audit.Write(req.MarketingConsent ? "customer.consent_given" : "customer.consent_withdrawn", nameof(Customer), c.Id.ToString());
        }
        audit.Write(id is null ? "customer.created" : "customer.updated", nameof(Customer), c.Id.ToString());
        await db.SaveChangesAsync(ct);
        return await GetAsync(c.Id, ct);
    }

    public async Task SetActiveAsync(Guid id, bool active, CancellationToken ct)
    {
        var c = await db.Set<Customer>().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("customer.not_found", "Customer not found.");
        c.IsActive = active;
        audit.Write(active ? "customer.reactivated" : "customer.deactivated", nameof(Customer), id.ToString());
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Privacy erasure: personal fields are removed while financial history (required for accounting) is kept.</summary>
    public async Task AnonymizeAsync(Guid id, CancellationToken ct)
    {
        var c = await db.Set<Customer>().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("customer.not_found", "Customer not found.");
        if (await accounting.PartyBalanceAsync(PartyTypes.Customer, id, ct) != 0)
            throw new DomainException("customer.balance_open", "Settle the customer's balance before anonymizing.");
        c.Name = "Anonymized customer " + c.Code; c.Phone = null; c.Email = null; c.Address = null; c.City = null; c.TaxId = null;
        c.TradeRegisterNo = null; c.Notes = null; c.MarketingConsent = false; c.IsActive = false; c.AnonymizedAt = clock.UtcNow;
        await db.Set<CustomerNote>().Where(n => n.CustomerId == id).ExecuteDeleteAsync(ct);
        audit.Write("customer.anonymized", nameof(Customer), id.ToString());
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Data-subject export of everything stored about a customer.</summary>
    public async Task<byte[]> ExportAsync(Guid id, CancellationToken ct)
    {
        var customer = await GetAsync(id, ct);
        var notes = await NotesAsync(id, ct);
        var loyalty = await LoyaltyAsync(id, ct);
        var purchases = await PurchasesAsync(id, ct);
        audit.Write("customer.exported", nameof(Customer), id.ToString());
        await db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToUtf8Bytes(new { exportedAt = clock.UtcNow, customer, notes, loyalty, purchases },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    }

    public async Task<IReadOnlyList<CustomerPurchaseDto>> PurchasesAsync(Guid id, CancellationToken ct)
    {
        var all = new List<CustomerPurchaseDto>();
        foreach (var h in history) all.AddRange(await h.PurchasesAsync(id, 200, ct));
        return all.OrderByDescending(p => p.Date).ToList();
    }

    public async Task<CustomerStatementDto> StatementAsync(Guid id, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var c = await db.Set<Customer>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("customer.not_found", "Customer not found.");
        var ar = await posting.AccountForRoleAsync(AccountRoles.AccountsReceivable, ct);
        var opening = await db.Set<JournalLine>().Where(l => l.AccountId == ar && l.PartyType == PartyTypes.Customer && l.PartyId == id && l.Date < from)
            .SumAsync(l => (decimal?)(l.Debit - l.Credit), ct) ?? 0;
        var lines = await (from l in db.Set<JournalLine>()
                           join e in db.Set<JournalEntry>() on l.JournalEntryId equals e.Id
                           where l.AccountId == ar && l.PartyType == PartyTypes.Customer && l.PartyId == id && l.Date >= @from && l.Date <= to
                           orderby l.Date, e.Number
                           select new { l.Date, e.Number, Description = l.Description ?? e.Description, l.Debit, l.Credit }).ToListAsync(ct);
        var running = opening;
        var result = lines.Select(l => { running += l.Debit - l.Credit; return new StatementLineDto(l.Date, l.Number, l.Description, l.Debit, l.Credit, running); }).ToList();
        return new CustomerStatementDto(id, c.Name, from, to, opening, result, running);
    }

    /// <summary>Throws when a credit sale would exceed the customer's credit limit (0 = credit not allowed).</summary>
    public async Task EnsureCreditAvailableAsync(Guid customerId, decimal additionalCredit, CancellationToken ct)
    {
        if (additionalCredit <= 0) return;
        var c = await db.Set<Customer>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == customerId, ct) ?? throw ValidationException.For("customerId", "Unknown customer.");
        if (!c.IsActive) throw new DomainException("customer.inactive", "This customer is deactivated.");
        var balance = await accounting.PartyBalanceAsync(PartyTypes.Customer, customerId, ct);
        if (balance + additionalCredit > c.CreditLimit)
            throw new DomainException("customer.credit_limit", $"Credit limit exceeded: balance {balance:0.00} + {additionalCredit:0.00} > limit {c.CreditLimit:0.00}.", 409);
    }

    // ---------- Loyalty ----------
    public const decimal PointsPerCurrencyUnit = 0.01m; // 1 point per 100 DZD by default

    public void EarnPoints(Customer customer, decimal amount, string reference, Guid referenceId)
    {
        var pts = Math.Floor(amount * PointsPerCurrencyUnit);
        if (pts == 0) return;
        customer.LoyaltyPoints += pts;
        db.Add(new LoyaltyTransaction { TenantId = customer.TenantId, CustomerId = customer.Id, Points = pts, Reason = "purchase", Reference = reference, ReferenceId = referenceId });
    }

    public async Task<IReadOnlyList<LoyaltyTransactionDto>> LoyaltyAsync(Guid customerId, CancellationToken ct) =>
        await db.Set<LoyaltyTransaction>().Where(l => l.CustomerId == customerId).OrderByDescending(l => l.CreatedAt).Take(500)
            .Select(l => new LoyaltyTransactionDto(l.Id, l.Points, l.Reason, l.Reference, l.CreatedAt)).ToListAsync(ct);

    public async Task<CustomerDto> AdjustLoyaltyAsync(Guid customerId, AdjustLoyaltyRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Reason, "reason").Require(req.Points != 0, "points", "Cannot be zero.").ThrowIfInvalid();
        var c = await db.Set<Customer>().SingleOrDefaultAsync(x => x.Id == customerId, ct) ?? throw new NotFoundException("customer.not_found", "Customer not found.");
        if (c.LoyaltyPoints + req.Points < 0) throw ValidationException.For("points", "Insufficient points.");
        c.LoyaltyPoints += req.Points;
        db.Add(new LoyaltyTransaction { TenantId = c.TenantId, CustomerId = c.Id, Points = req.Points, Reason = req.Reason });
        audit.Write("customer.loyalty_adjusted", nameof(Customer), c.Id.ToString(), req);
        await db.SaveChangesAsync(ct);
        return await GetAsync(customerId, ct);
    }

    // ---------- Groups, notes, tasks, opportunities ----------
    public async Task<IReadOnlyList<CustomerGroupDto>> GroupsAsync(CancellationToken ct) =>
        await db.Set<CustomerGroup>().OrderBy(g => g.Name).Select(g => new CustomerGroupDto(g.Id, g.Name, g.DiscountPercent, g.PriceListId)).ToListAsync(ct);

    public async Task<CustomerGroupDto> UpsertGroupAsync(Guid? id, UpsertCustomerGroupRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Name, "name", 100).Require(req.DiscountPercent is >= 0 and <= 100, "discountPercent", "0-100.").ThrowIfInvalid();
        if (await db.Set<CustomerGroup>().AnyAsync(g => g.Name == req.Name.Trim() && g.Id != id, ct)) throw new ConflictException("group.exists", "Group already exists.");
        var g = id is { } gid ? await db.Set<CustomerGroup>().SingleOrDefaultAsync(x => x.Id == gid, ct) ?? throw new NotFoundException("group.not_found", "Group not found.")
            : db.Add(new CustomerGroup { TenantId = ctx.RequireTenant() }).Entity;
        g.Name = req.Name.Trim(); g.DiscountPercent = req.DiscountPercent; g.PriceListId = req.PriceListId;
        await db.SaveChangesAsync(ct);
        return new CustomerGroupDto(g.Id, g.Name, g.DiscountPercent, g.PriceListId);
    }

    public async Task<IReadOnlyList<CustomerNoteDto>> NotesAsync(Guid customerId, CancellationToken ct) =>
        await db.Set<CustomerNote>().Where(n => n.CustomerId == customerId).OrderByDescending(n => n.CreatedAt)
            .Select(n => new CustomerNoteDto(n.Id, n.Text, n.CreatedBy, n.CreatedAt)).ToListAsync(ct);

    public async Task<CustomerNoteDto> AddNoteAsync(Guid customerId, AddNoteRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Text, "text", 4000).ThrowIfInvalid();
        if (!await db.Set<Customer>().AnyAsync(c => c.Id == customerId, ct)) throw new NotFoundException("customer.not_found", "Customer not found.");
        var n = db.Add(new CustomerNote { TenantId = ctx.RequireTenant(), CustomerId = customerId, Text = req.Text.Trim() }).Entity;
        await db.SaveChangesAsync(ct);
        return new CustomerNoteDto(n.Id, n.Text, n.CreatedBy, n.CreatedAt);
    }

    public async Task<IReadOnlyList<FollowUpTaskDto>> TasksAsync(Guid? customerId, bool openOnly, CancellationToken ct) =>
        await db.Set<FollowUpTask>().Where(t => (customerId == null || t.CustomerId == customerId) && (!openOnly || t.Status == TaskStatus.Open))
            .OrderBy(t => t.DueDate).Select(t => new FollowUpTaskDto(t.Id, t.CustomerId, t.Title, t.DueDate, t.AssignedToUserId, t.Status.ToString(), t.Notes)).ToListAsync(ct);

    public async Task<FollowUpTaskDto> UpsertTaskAsync(Guid? id, UpsertTaskRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Title, "title").Require(Enum.TryParse<TaskStatus>(req.Status, true, out _), "status", "Unknown status.").ThrowIfInvalid();
        if (!await db.Set<Customer>().AnyAsync(c => c.Id == req.CustomerId, ct)) throw ValidationException.For("customerId", "Unknown customer.");
        var t = id is { } tid ? await db.Set<FollowUpTask>().SingleOrDefaultAsync(x => x.Id == tid, ct) ?? throw new NotFoundException("task.not_found", "Task not found.")
            : db.Add(new FollowUpTask { TenantId = ctx.RequireTenant() }).Entity;
        t.CustomerId = req.CustomerId; t.Title = req.Title.Trim(); t.DueDate = req.DueDate; t.AssignedToUserId = req.AssignedToUserId;
        t.Status = Enum.Parse<TaskStatus>(req.Status, true); t.Notes = req.Notes;
        await db.SaveChangesAsync(ct);
        return new FollowUpTaskDto(t.Id, t.CustomerId, t.Title, t.DueDate, t.AssignedToUserId, t.Status.ToString(), t.Notes);
    }

    public async Task<IReadOnlyList<OpportunityDto>> OpportunitiesAsync(string? stage, CancellationToken ct)
    {
        var q = db.Set<Opportunity>().AsQueryable();
        if (stage is not null && Enum.TryParse<OpportunityStage>(stage, true, out var s)) q = q.Where(o => o.Stage == s);
        return await q.OrderByDescending(o => o.UpdatedAt).Select(o => new OpportunityDto(o.Id, o.CustomerId, o.Title, o.Stage.ToString(), o.EstimatedValue,
            o.Probability, o.ExpectedCloseDate, o.OwnerUserId, o.Notes, o.UpdatedAt)).ToListAsync(ct);
    }

    public async Task<OpportunityDto> UpsertOpportunityAsync(Guid? id, UpsertOpportunityRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Title, "title").Require(Enum.TryParse<OpportunityStage>(req.Stage, true, out _), "stage", "Unknown stage.")
            .NonNegative(req.EstimatedValue, "estimatedValue").Require(req.Probability is >= 0 and <= 100, "probability", "0-100.").ThrowIfInvalid();
        if (!await db.Set<Customer>().AnyAsync(c => c.Id == req.CustomerId, ct)) throw ValidationException.For("customerId", "Unknown customer.");
        var o = id is { } oid ? await db.Set<Opportunity>().SingleOrDefaultAsync(x => x.Id == oid, ct) ?? throw new NotFoundException("opportunity.not_found", "Opportunity not found.")
            : db.Add(new Opportunity { TenantId = ctx.RequireTenant() }).Entity;
        o.CustomerId = req.CustomerId; o.Title = req.Title.Trim(); o.Stage = Enum.Parse<OpportunityStage>(req.Stage, true); o.EstimatedValue = req.EstimatedValue;
        o.Probability = req.Probability; o.ExpectedCloseDate = req.ExpectedCloseDate; o.OwnerUserId = req.OwnerUserId; o.Notes = req.Notes;
        await db.SaveChangesAsync(ct);
        return new OpportunityDto(o.Id, o.CustomerId, o.Title, o.Stage.ToString(), o.EstimatedValue, o.Probability, o.ExpectedCloseDate, o.OwnerUserId, o.Notes, o.UpdatedAt);
    }

    /// <summary>RFM-style segmentation over the last 365 days using AR + POS history providers.</summary>
    public async Task<IReadOnlyList<SegmentDto>> SegmentsAsync(CancellationToken ct)
    {
        var customers = await db.Set<Customer>().Where(c => c.IsActive).Select(c => c.Id).ToListAsync(ct);
        var since = clock.UtcNow.AddDays(-365);
        var stats = new Dictionary<Guid, (decimal Revenue, int Count, DateTimeOffset Last)>();
        foreach (var id in customers)
        {
            var purchases = new List<CustomerPurchaseDto>();
            foreach (var h in history) purchases.AddRange((await h.PurchasesAsync(id, 500, ct)).Where(p => p.Date >= since));
            if (purchases.Count > 0) stats[id] = (purchases.Sum(p => p.Total), purchases.Count, purchases.Max(p => p.Date));
        }
        string Segment(Guid id)
        {
            if (!stats.TryGetValue(id, out var s)) return "inactive";
            var recentDays = (clock.UtcNow - s.Last).TotalDays;
            if (s.Count >= 10 && recentDays <= 30) return "champions";
            if (s.Count >= 4 && recentDays <= 90) return "loyal";
            if (recentDays > 120) return "at_risk";
            return s.Count == 1 ? "new" : "regular";
        }
        return customers.GroupBy(Segment).Select(g => new SegmentDto(g.Key, g.Count(), g.Sum(id => stats.TryGetValue(id, out var s) ? s.Revenue : 0)))
            .OrderByDescending(s => s.Revenue).ToList();
    }

    private async Task<Dictionary<Guid, decimal>> BalancesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var ar = await db.Set<Account>().Where(a => a.SystemRole == AccountRoles.AccountsReceivable).Select(a => (Guid?)a.Id).SingleOrDefaultAsync(ct);
        if (ar is null) return new();
        return await db.Set<JournalLine>().Where(l => l.AccountId == ar && l.PartyType == PartyTypes.Customer && l.PartyId != null && ids.Contains(l.PartyId.Value))
            .GroupBy(l => l.PartyId!.Value).Select(g => new { g.Key, B = g.Sum(x => x.Debit - x.Credit) }).ToDictionaryAsync(x => x.Key, x => x.B, ct);
    }

    public static CustomerDto ToDto(Customer c, decimal balance) => new(c.Id, c.Code, c.Type.ToString(), c.Name, c.Phone, c.Email, c.Address, c.City, c.Wilaya,
        c.TaxId, c.TradeRegisterNo, c.GroupId, c.PriceListId, c.CreditLimit, c.LoyaltyPoints, c.IsActive, c.MarketingConsent, c.ConsentAt, c.Notes, balance, c.UpdatedAt);
}
