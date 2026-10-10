using Microsoft.EntityFrameworkCore;
using Veltrix.Contracts.Inventory;
using Veltrix.Modules.Accounting;
using Veltrix.Modules.Catalog;
using Veltrix.Modules.Tenants;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Inventory;

public sealed class InventoryService(DbContext db, ITenantContext ctx, StockEngine engine, PostingService posting, ISequenceService sequences,
    IAuditWriter audit, IClock clock, IBranchDirectory branches)
{
    public static readonly string[] AdjustmentReasons = ["Correction", "Damage", "Expired", "Found", "Theft", "Opening", "Other"];

    private async Task<Guid> EnsureWarehouseAccessAsync(Guid warehouseId, CancellationToken ct)
    {
        var branchId = await branches.GetWarehouseBranchAsync(warehouseId, ct) ?? throw ValidationException.For("warehouseId", "Unknown warehouse.");
        ctx.EnsureBranch(branchId);
        return branchId;
    }

    // ---------- Adjustments (damaged, expired, found, opening balances...) ----------
    public async Task<AdjustmentDto> AdjustAsync(CreateAdjustmentRequest req, CancellationToken ct)
    {
        new Validator().Require(AdjustmentReasons.Contains(req.Reason), "reason", "Unknown reason.")
            .Require(req.Lines is { Count: > 0 and <= 500 }, "lines", "Between 1 and 500 lines.")
            .Require(req.Lines.All(l => l.QuantityChange != 0), "lines", "Quantity change cannot be zero.")
            .Require(req.Reason is not ("Damage" or "Expired" or "Theft") || req.Lines.All(l => l.QuantityChange < 0), "lines", "This reason only removes stock.")
            .Require(req.Reason != "Opening" || req.Lines.All(l => l.QuantityChange > 0 && l.UnitCost is >= 0), "lines", "Opening balances need positive quantities and unit costs.")
            .MaxLength(req.Notes, "notes", 500).ThrowIfInvalid();
        if (req.Id is { } existing && await db.Set<StockAdjustment>().AnyAsync(a => a.Id == existing, ct))
            return await GetAdjustmentAsync(existing, ct);
        var branchId = await EnsureWarehouseAccessAsync(req.WarehouseId, ct);
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var number = await sequences.NextAsync(DocumentTypes.StockAdjustment, branchId, ct);
        var adj = new StockAdjustment { TenantId = ctx.RequireTenant(), Number = number, WarehouseId = req.WarehouseId, Reason = req.Reason, Notes = req.Notes };
        if (req.Id is { } id && id != Guid.Empty) adj.Id = id;
        decimal valueChange = 0;
        foreach (var l in req.Lines)
        {
            var type = req.Reason switch
            {
                "Damage" => MovementType.Damage,
                "Expired" => MovementType.Expired,
                "Opening" => MovementType.OpeningBalance,
                _ => l.QuantityChange > 0 ? MovementType.AdjustmentIn : MovementType.AdjustmentOut,
            };
            var res = await engine.MoveAsync(new MovementRequest(req.WarehouseId, l.ProductId, type, l.QuantityChange, l.UnitCost, "adjustment", adj.Id, adj.Number,
                req.Reason, l.LotNumber, l.ExpiryDate, l.Serials, AllowNegativeOverride: false), ct);
            valueChange += Math.Sign(l.QuantityChange) * res.TotalCost;
        }
        adj.ValueChange = valueChange;
        db.Add(adj);
        if (valueChange != 0)
        {
            // Opening balances are booked against equity; other adjustments against the inventory adjustment account.
            var counterRole = req.Reason == "Opening" ? AccountRoles.Equity : AccountRoles.InventoryAdjustment;
            var b = new PostingBuilder();
            if (valueChange > 0) b.Debit(AccountRoles.Inventory, valueChange).Credit(counterRole, valueChange);
            else b.Debit(counterRole, -valueChange).Credit(AccountRoles.Inventory, -valueChange);
            var je = await posting.PostAsync(DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), $"Stock adjustment {adj.Number} ({req.Reason})", JournalSource.Inventory, adj.Id, branchId, b, ct);
            adj.JournalEntryId = je.Id;
        }
        audit.Write("inventory.adjusted", nameof(StockAdjustment), adj.Id.ToString(), new { req.Reason, valueChange, lines = req.Lines.Count });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAdjustmentAsync(adj.Id, ct);
    }

    public async Task<AdjustmentDto> GetAdjustmentAsync(Guid id, CancellationToken ct)
    {
        var a = await db.Set<StockAdjustment>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("adjustment.not_found", "Adjustment not found.");
        return new AdjustmentDto(a.Id, a.Number, a.WarehouseId, a.Reason, a.Notes, a.ValueChange, a.CreatedAt, a.JournalEntryId);
    }

    // ---------- Transfers ----------
    public async Task<TransferDto> CreateTransferAsync(CreateTransferRequest req, CancellationToken ct)
    {
        new Validator().Require(req.FromWarehouseId != req.ToWarehouseId, "toWarehouseId", "Source and destination must differ.")
            .Require(req.Lines is { Count: > 0 and <= 500 }, "lines", "Between 1 and 500 lines.")
            .Require(req.Lines.All(l => l.Quantity > 0), "lines", "Quantities must be positive.").ThrowIfInvalid();
        var fromBranch = await EnsureWarehouseAccessAsync(req.FromWarehouseId, ct);
        _ = await branches.GetWarehouseBranchAsync(req.ToWarehouseId, ct) ?? throw ValidationException.For("toWarehouseId", "Unknown warehouse.");
        var tid = ctx.RequireTenant();
        var t = new StockTransfer
        {
            TenantId = tid, FromWarehouseId = req.FromWarehouseId, ToWarehouseId = req.ToWarehouseId, Status = TransferStatus.Draft, Notes = req.Notes,
            Number = await sequences.NextAsync(DocumentTypes.StockTransfer, fromBranch, ct),
        };
        foreach (var l in req.Lines)
            t.Lines.Add(new StockTransferLine { TenantId = tid, TransferId = t.Id, ProductId = l.ProductId, Quantity = l.Quantity, LotNumber = l.LotNumber,
                Serials = l.Serials is { Count: > 0 } ? string.Join('\n', l.Serials) : null });
        db.Add(t);
        audit.Write("transfer.created", nameof(StockTransfer), t.Id.ToString());
        await db.SaveChangesAsync(ct);
        return await GetTransferAsync(t.Id, ct);
    }

    /// <summary>Ship: stock leaves the source warehouse at average cost (goods are in transit).</summary>
    public async Task<TransferDto> ShipTransferAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var t = await db.Set<StockTransfer>().Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("transfer.not_found", "Transfer not found.");
        await EnsureWarehouseAccessAsync(t.FromWarehouseId, ct);
        if (t.Status != TransferStatus.Draft) throw new ConflictException("transfer.state", "Only draft transfers can be shipped.");
        foreach (var l in t.Lines)
        {
            var res = await engine.MoveAsync(new MovementRequest(t.FromWarehouseId, l.ProductId, MovementType.TransferOut, -l.Quantity, null, "transfer", t.Id, t.Number,
                null, l.LotNumber, null, l.Serials?.Split('\n')), ct);
            l.UnitCost = res.UnitCost;
        }
        t.Status = TransferStatus.InTransit;
        t.ShippedAt = clock.UtcNow;
        audit.Write("transfer.shipped", nameof(StockTransfer), t.Id.ToString());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetTransferAsync(id, ct);
    }

    /// <summary>Receive: stock enters the destination at the shipped cost. Inventory value is unchanged company-wide, so no journal is needed.</summary>
    public async Task<TransferDto> ReceiveTransferAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var t = await db.Set<StockTransfer>().Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("transfer.not_found", "Transfer not found.");
        await EnsureWarehouseAccessAsync(t.ToWarehouseId, ct);
        if (t.Status != TransferStatus.InTransit) throw new ConflictException("transfer.state", "Only in-transit transfers can be received.");
        foreach (var l in t.Lines)
        {
            var lotExpiry = l.LotNumber is null ? null : await db.Set<StockLot>().Where(x => x.WarehouseId == t.FromWarehouseId && x.ProductId == l.ProductId && x.LotNumber == l.LotNumber)
                .Select(x => x.ExpiryDate).FirstOrDefaultAsync(ct);
            await engine.MoveAsync(new MovementRequest(t.ToWarehouseId, l.ProductId, MovementType.TransferIn, l.Quantity, l.UnitCost, "transfer", t.Id, t.Number,
                null, l.LotNumber, lotExpiry, l.Serials?.Split('\n')), ct);
        }
        t.Status = TransferStatus.Received;
        t.ReceivedAt = clock.UtcNow;
        audit.Write("transfer.received", nameof(StockTransfer), t.Id.ToString());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetTransferAsync(id, ct);
    }

    public async Task<TransferDto> CancelTransferAsync(Guid id, CancellationToken ct)
    {
        var t = await db.Set<StockTransfer>().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("transfer.not_found", "Transfer not found.");
        if (t.Status != TransferStatus.Draft) throw new ConflictException("transfer.state", "Only draft transfers can be cancelled.");
        t.Status = TransferStatus.Cancelled;
        await db.SaveChangesAsync(ct);
        return await GetTransferAsync(id, ct);
    }

    public async Task<TransferDto> GetTransferAsync(Guid id, CancellationToken ct)
    {
        var t = await db.Set<StockTransfer>().Include(x => x.Lines).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("transfer.not_found", "Transfer not found.");
        var names = await ProductNamesAsync(t.Lines.Select(l => l.ProductId), ct);
        return new TransferDto(t.Id, t.Number, t.FromWarehouseId, t.ToWarehouseId, t.Status.ToString(), t.Notes,
            t.Lines.Select(l => new TransferLineDto(l.ProductId, names[l.ProductId].Sku, names[l.ProductId].Name, l.Quantity, l.UnitCost, l.LotNumber)).ToList(),
            t.CreatedAt, t.ShippedAt, t.ReceivedAt);
    }

    public async Task<PagedResult<TransferDto>> TransfersAsync(PageQuery q, string? status, CancellationToken ct)
    {
        var query = db.Set<StockTransfer>().AsNoTracking().AsQueryable();
        if (status is not null && Enum.TryParse<TransferStatus>(status, true, out var s)) query = query.Where(t => t.Status == s);
        var page = await query.OrderByDescending(t => t.CreatedAt).Select(t => t.Id).ToPagedAsync(q, ct);
        var items = new List<TransferDto>();
        foreach (var id in page.Items) items.Add(await GetTransferAsync(id, ct));
        return new PagedResult<TransferDto>(items, page.Page, page.PageSize, page.TotalCount);
    }

    // ---------- Physical counts ----------
    public async Task<CountDto> StartCountAsync(CreateCountRequest req, CancellationToken ct)
    {
        var branchId = await EnsureWarehouseAccessAsync(req.WarehouseId, ct);
        var tid = ctx.RequireTenant();
        if (await db.Set<StockCount>().AnyAsync(c => c.WarehouseId == req.WarehouseId && c.Status == CountStatus.Open, ct))
            throw new ConflictException("count.open_exists", "A count is already open for this warehouse.");
        var productIds = req.ProductIds is { Count: > 0 } ids ? ids.ToList()
            : await db.Set<Product>().Where(p => p.TrackStock && p.Status != ProductStatus.Discontinued).Select(p => p.Id).ToListAsync(ct);
        var balances = await db.Set<StockBalance>().Where(b => b.WarehouseId == req.WarehouseId && productIds.Contains(b.ProductId))
            .ToDictionaryAsync(b => b.ProductId, b => b.Quantity, ct);
        var count = new StockCount { TenantId = tid, WarehouseId = req.WarehouseId, Status = CountStatus.Open, Notes = req.Notes,
            Number = await sequences.NextAsync(DocumentTypes.StockCount, branchId, ct) };
        foreach (var pid in productIds.Distinct())
            count.Lines.Add(new StockCountLine { TenantId = tid, CountId = count.Id, ProductId = pid, ExpectedQuantity = balances.GetValueOrDefault(pid) });
        db.Add(count);
        await db.SaveChangesAsync(ct);
        return await GetCountAsync(count.Id, ct);
    }

    public async Task<CountDto> RecordCountAsync(Guid id, IReadOnlyList<CountLineInput> lines, CancellationToken ct)
    {
        var c = await db.Set<StockCount>().Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("count.not_found", "Count not found.");
        if (c.Status != CountStatus.Open) throw new ConflictException("count.closed", "This count is closed.");
        foreach (var l in lines)
        {
            if (l.CountedQuantity < 0) throw ValidationException.For("countedQuantity", "Cannot be negative.");
            var line = c.Lines.SingleOrDefault(x => x.ProductId == l.ProductId);
            if (line is null) c.Lines.Add(line = new StockCountLine { TenantId = c.TenantId, CountId = c.Id, ProductId = l.ProductId });
            line.CountedQuantity = l.CountedQuantity;
        }
        await db.SaveChangesAsync(ct);
        return await GetCountAsync(id, ct);
    }

    /// <summary>Complete: variances between counted and current quantities become count adjustments (with journal).</summary>
    public async Task<CountDto> CompleteCountAsync(Guid id, CancellationToken ct)
    {
        var c = await db.Set<StockCount>().Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("count.not_found", "Count not found.");
        if (c.Status != CountStatus.Open) throw new ConflictException("count.closed", "This count is closed.");
        var branchId = await EnsureWarehouseAccessAsync(c.WarehouseId, ct);
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        // Compare with the live balance (sales may have happened during the count window).
        var current = await db.Set<StockBalance>().Where(b => b.WarehouseId == c.WarehouseId).ToDictionaryAsync(b => b.ProductId, b => b.Quantity, ct);
        decimal value = 0;
        foreach (var l in c.Lines.Where(l => l.CountedQuantity is not null))
        {
            var diff = l.CountedQuantity!.Value - current.GetValueOrDefault(l.ProductId);
            if (diff == 0) continue;
            var res = await engine.MoveAsync(new MovementRequest(c.WarehouseId, l.ProductId, MovementType.CountAdjustment, diff, null, "count", c.Id, c.Number,
                "Physical count", AllowNegativeOverride: true), ct);
            value += Math.Sign(diff) * res.TotalCost;
        }
        if (value != 0)
        {
            var b = new PostingBuilder();
            if (value > 0) b.Debit(AccountRoles.Inventory, value).Credit(AccountRoles.InventoryAdjustment, value);
            else b.Debit(AccountRoles.InventoryAdjustment, -value).Credit(AccountRoles.Inventory, -value);
            await posting.PostAsync(DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), $"Physical count {c.Number}", JournalSource.Inventory, c.Id, branchId, b, ct);
        }
        c.Status = CountStatus.Completed;
        c.CompletedAt = clock.UtcNow;
        audit.Write("count.completed", nameof(StockCount), c.Id.ToString(), new { value });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetCountAsync(id, ct);
    }

    public async Task<CountDto> GetCountAsync(Guid id, CancellationToken ct)
    {
        var c = await db.Set<StockCount>().Include(x => x.Lines).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("count.not_found", "Count not found.");
        var names = await ProductNamesAsync(c.Lines.Select(l => l.ProductId), ct);
        return new CountDto(c.Id, c.Number, c.WarehouseId, c.Status.ToString(), c.Notes,
            c.Lines.OrderBy(l => names[l.ProductId].Sku).Select(l => new CountLineDto(l.ProductId, names[l.ProductId].Sku, names[l.ProductId].Name, l.ExpectedQuantity,
                l.CountedQuantity, l.CountedQuantity - l.ExpectedQuantity)).ToList(), c.CreatedAt, c.CompletedAt);
    }

    // ---------- Reservations ----------
    public async Task<ReservationDto> ReserveAsync(CreateReservationRequest req, CancellationToken ct)
    {
        await EnsureWarehouseAccessAsync(req.WarehouseId, ct);
        var r = await engine.ReserveAsync(req.WarehouseId, req.ProductId, req.Quantity, req.ReferenceType, req.ReferenceId, req.ExpiresAt, ct);
        await db.SaveChangesAsync(ct);
        return new ReservationDto(r.Id, r.WarehouseId, r.ProductId, r.Quantity, r.ReferenceType, r.ReferenceId, r.ExpiresAt, r.Released);
    }

    // ---------- Queries ----------
    public async Task<PagedResult<StockBalanceDto>> BalancesAsync(PageQuery q, Guid? warehouseId, Guid? productId, bool? onlyInStock, DateTimeOffset? changedSince, CancellationToken ct)
    {
        var query = from b in db.Set<StockBalance>().AsNoTracking()
                    join p in db.Set<Product>() on b.ProductId equals p.Id
                    join w in db.Set<Warehouse>() on b.WarehouseId equals w.Id
                    select new { b, p.Sku, p.Name, p.LowStockThreshold, WName = w.Name, w.BranchId };
        if (warehouseId is { } wid) query = query.Where(x => x.b.WarehouseId == wid);
        if (productId is { } pid) query = query.Where(x => x.b.ProductId == pid);
        if (onlyInStock == true) query = query.Where(x => x.b.Quantity > 0);
        if (changedSince is { } since) query = query.Where(x => x.b.UpdatedAt > since);
        if (ctx.AllowedBranches is { } allowed) query = query.Where(x => allowed.Contains(x.BranchId));
        if (!string.IsNullOrWhiteSpace(q.Search)) query = query.Where(x => x.Sku.ToUpper().Contains(q.Search.ToUpper()) || x.Name.ToUpper().Contains(q.Search.ToUpper()));
        var page = await query.OrderBy(x => x.Name).ToPagedAsync(q, ct);
        return new PagedResult<StockBalanceDto>(page.Items.Select(x => new StockBalanceDto(x.b.WarehouseId, x.WName, x.b.ProductId, x.Sku, x.Name, x.b.Quantity,
            x.b.ReservedQuantity, x.b.Quantity - x.b.ReservedQuantity, x.b.AverageCost, Money.Round(Math.Max(x.b.Quantity, 0) * x.b.AverageCost), x.LowStockThreshold,
            x.b.LastReceivedAt, x.b.UpdatedAt)).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<PagedResult<StockMovementDto>> MovementsAsync(PageQuery q, Guid? productId, Guid? warehouseId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var query = from m in db.Set<StockMovement>().AsNoTracking()
                    join p in db.Set<Product>() on m.ProductId equals p.Id
                    join w in db.Set<Warehouse>() on m.WarehouseId equals w.Id
                    join l in db.Set<StockLot>() on m.LotId equals l.Id into lots
                    from lot in lots.DefaultIfEmpty()
                    select new { m, p.Sku, p.Name, w.BranchId, Lot = lot == null ? null : lot.LotNumber };
        if (productId is { } pid) query = query.Where(x => x.m.ProductId == pid);
        if (warehouseId is { } wid) query = query.Where(x => x.m.WarehouseId == wid);
        if (from is { } f) query = query.Where(x => x.m.OccurredAt >= f);
        if (to is { } t) query = query.Where(x => x.m.OccurredAt <= t);
        if (ctx.AllowedBranches is { } allowed) query = query.Where(x => allowed.Contains(x.BranchId));
        var page = await query.OrderByDescending(x => x.m.OccurredAt).ToPagedAsync(q, ct);
        return new PagedResult<StockMovementDto>(page.Items.Select(x => new StockMovementDto(x.m.Id, x.m.OccurredAt, x.m.WarehouseId, x.m.ProductId, x.Sku, x.Name,
            x.m.Type.ToString(), x.m.Quantity, x.m.UnitCost, x.m.BalanceAfter, x.m.ReferenceType, x.m.ReferenceNumber, x.m.Reason, x.Lot, x.m.SerialNumber, x.m.CreatedBy)).ToList(),
            page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<IReadOnlyList<LowStockDto>> LowStockAsync(Guid? warehouseId, CancellationToken ct)
    {
        var rows = await (from p in db.Set<Product>().AsNoTracking()
                          from w in db.Set<Warehouse>()
                          join b in db.Set<StockBalance>() on new { P = p.Id, W = w.Id } equals new { P = b.ProductId, W = b.WarehouseId } into bs
                          from b in bs.DefaultIfEmpty()
                          where p.TrackStock && p.LowStockThreshold > 0 && p.Status == ProductStatus.Active && w.IsActive
                                && (warehouseId == null || w.Id == warehouseId)
                                && (b == null ? 0 : b.Quantity) <= p.LowStockThreshold
                          select new { p.Id, p.Sku, p.Name, WId = w.Id, WName = w.Name, w.BranchId, Qty = b == null ? 0 : b.Quantity, p.LowStockThreshold, p.ReorderQuantity })
            .ToListAsync(ct);
        return rows.Where(r => ctx.CanAccessBranch(r.BranchId)).Select(r => new LowStockDto(r.Id, r.Sku, r.Name, r.WId, r.WName, r.Qty, r.LowStockThreshold,
            r.ReorderQuantity > 0 ? r.ReorderQuantity : Math.Max(r.LowStockThreshold * 2 - r.Qty, 1))).OrderBy(r => r.Quantity - r.Threshold).ToList();
    }

    public async Task<ValuationDto> ValuationAsync(CancellationToken ct)
    {
        var rows = await (from b in db.Set<StockBalance>().AsNoTracking()
                          join w in db.Set<Warehouse>() on b.WarehouseId equals w.Id
                          where b.Quantity > 0
                          group b by new { w.Id, w.Name } into g
                          select new ValuationRow(g.Key.Id, g.Key.Name, g.Sum(x => x.Quantity), g.Sum(x => x.Quantity * x.AverageCost))).ToListAsync(ct);
        rows = rows.Select(r => r with { Value = Money.Round(r.Value) }).ToList();
        return new ValuationDto(rows.Sum(r => r.Value), rows);
    }

    public async Task<IReadOnlyList<AgingStockDto>> AgingAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var rows = await (from b in db.Set<StockBalance>().AsNoTracking()
                          join p in db.Set<Product>() on b.ProductId equals p.Id
                          where b.Quantity > 0
                          select new { b.ProductId, p.Sku, p.Name, b.WarehouseId, b.Quantity, b.AverageCost, Last = b.LastReceivedAt ?? b.CreatedAt }).ToListAsync(ct);
        return rows.Select(r =>
        {
            var days = (int)(now - r.Last).TotalDays;
            var bucket = days <= 30 ? "0-30" : days <= 90 ? "31-90" : days <= 180 ? "91-180" : "180+";
            return new AgingStockDto(r.ProductId, r.Sku, r.Name, r.WarehouseId, r.Quantity, Money.Round(r.Quantity * r.AverageCost), days, bucket);
        }).OrderByDescending(r => r.DaysSinceLastReceipt).ToList();
    }

    public async Task<IReadOnlyList<LotDto>> LotsAsync(Guid? productId, int? expiringWithinDays, CancellationToken ct)
    {
        var query = from l in db.Set<StockLot>().AsNoTracking() join p in db.Set<Product>() on l.ProductId equals p.Id where l.Quantity > 0 select new { l, p.Sku };
        if (productId is { } pid) query = query.Where(x => x.l.ProductId == pid);
        if (expiringWithinDays is { } d)
        {
            var limit = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime.AddDays(d));
            query = query.Where(x => x.l.ExpiryDate != null && x.l.ExpiryDate <= limit);
        }
        return await query.OrderBy(x => x.l.ExpiryDate).Select(x => new LotDto(x.l.Id, x.l.ProductId, x.Sku, x.l.WarehouseId, x.l.LotNumber, x.l.ExpiryDate, x.l.Quantity)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SerialDto>> SerialsAsync(Guid productId, CancellationToken ct) =>
        await db.Set<SerialNumber>().Where(s => s.ProductId == productId).OrderBy(s => s.Serial)
            .Select(s => new SerialDto(s.Id, s.ProductId, s.Serial, s.WarehouseId, s.Status.ToString())).ToListAsync(ct);

    private async Task<Dictionary<Guid, (string Sku, string Name)>> ProductNamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Set<Product>().Where(p => list.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => (p.Sku, p.Name), ct);
    }
}
