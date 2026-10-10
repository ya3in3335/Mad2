using Microsoft.EntityFrameworkCore;
using Veltrix.Contracts.Purchasing;
using Veltrix.Modules.Accounting;
using Veltrix.Modules.Catalog;
using Veltrix.Modules.Inventory;
using Veltrix.Modules.Tenants;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Purchasing;

public sealed class SupplierPartyDirectory(DbContext db) : IPartyDirectory
{
    public string PartyType => PartyTypes.Supplier;
    public async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
        await db.Set<Supplier>().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
}

public sealed class PurchasingService(DbContext db, ITenantContext ctx, ISequenceService sequences, StockEngine stock, PostingService posting,
    AccountingService accounting, IAuditWriter audit, IClock clock, IBranchDirectory branches, INotificationPublisher notifications)
{
    private DateOnly Today => DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

    // ---------- Suppliers ----------
    public async Task<PagedResult<SupplierDto>> SuppliersAsync(PageQuery q, CancellationToken ct)
    {
        var query = db.Set<Supplier>().AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q.Search)) query = query.Where(s => s.Name.ToUpper().Contains(q.Search.ToUpper()) || s.Code.Contains(q.Search));
        var page = await query.OrderBy(s => s.Name).ToPagedAsync(q, ct);
        var items = new List<SupplierDto>();
        foreach (var s in page.Items) items.Add(ToDto(s, await accounting.PartyBalanceAsync(PartyTypes.Supplier, s.Id, ct)));
        return new PagedResult<SupplierDto>(items, page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<SupplierDto> UpsertSupplierAsync(Guid? id, UpsertSupplierRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Name, "name").MaxLength(req.Phone, "phone", 40).MaxLength(req.Email, "email", 256)
            .Require(req.PaymentTermsDays is >= 0 and <= 365, "paymentTermsDays", "0-365.").MaxLength(req.Notes, "notes", 2000).ThrowIfInvalid();
        var s = id is { } sid ? await db.Set<Supplier>().SingleOrDefaultAsync(x => x.Id == sid, ct) ?? throw new NotFoundException("supplier.not_found", "Supplier not found.")
            : db.Add(new Supplier { TenantId = ctx.RequireTenant(), Code = string.IsNullOrWhiteSpace(req.Code) ? await sequences.NextAsync(DocumentTypes.Supplier, null, ct) : req.Code.Trim() }).Entity;
        s.Name = req.Name.Trim(); s.Phone = req.Phone; s.Email = req.Email; s.Address = req.Address; s.TaxId = req.TaxId; s.TradeRegisterNo = req.TradeRegisterNo;
        s.PaymentTermsDays = req.PaymentTermsDays; s.IsActive = req.IsActive; s.Notes = req.Notes;
        audit.Write(id is null ? "supplier.created" : "supplier.updated", nameof(Supplier), s.Id.ToString());
        await db.SaveChangesAsync(ct);
        return ToDto(s, await accounting.PartyBalanceAsync(PartyTypes.Supplier, s.Id, ct));
    }

    // ---------- Purchase requests ----------
    public async Task<PurchaseRequestDto> CreateRequestAsync(CreatePurchaseRequestRequest req, CancellationToken ct)
    {
        ctx.EnsureBranch(req.BranchId);
        new Validator().Require(req.Lines is { Count: > 0 }, "lines", "Add at least one line.").Require(req.Lines.All(l => l.Quantity > 0), "lines", "Quantities must be positive.").ThrowIfInvalid();
        var tid = ctx.RequireTenant();
        var r = new PurchaseRequest { TenantId = tid, BranchId = req.BranchId, Status = PurchaseRequestStatus.Submitted, Notes = req.Notes,
            Number = await sequences.NextAsync(DocumentTypes.PurchaseRequest, req.BranchId, ct) };
        foreach (var l in req.Lines) r.Lines.Add(new PurchaseRequestLine { TenantId = tid, RequestId = r.Id, ProductId = l.ProductId, Quantity = l.Quantity, Notes = l.Notes });
        db.Add(r);
        notifications.Publish(tid, "purchasing.request_pending", $"Purchase request {r.Number}", "A purchase request awaits approval.", NotificationSeverity.Info, Permissions.PurchasingApprove);
        await db.SaveChangesAsync(ct);
        return ToDto(r);
    }

    public async Task<PurchaseRequestDto> DecideRequestAsync(Guid id, bool approve, CancellationToken ct)
    {
        var r = await db.Set<PurchaseRequest>().Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("request.not_found", "Request not found.");
        if (r.Status != PurchaseRequestStatus.Submitted) throw new ConflictException("request.state", "Only submitted requests can be decided.");
        r.Status = approve ? PurchaseRequestStatus.Approved : PurchaseRequestStatus.Rejected;
        r.DecidedBy = ctx.UserId;
        audit.Write(approve ? "purchase_request.approved" : "purchase_request.rejected", nameof(PurchaseRequest), r.Id.ToString());
        await db.SaveChangesAsync(ct);
        return ToDto(r);
    }

    public async Task<IReadOnlyList<PurchaseRequestDto>> RequestsAsync(string? status, CancellationToken ct)
    {
        var q = db.Set<PurchaseRequest>().Include(r => r.Lines).AsNoTracking().AsQueryable();
        if (status is not null && Enum.TryParse<PurchaseRequestStatus>(status, true, out var s)) q = q.Where(r => r.Status == s);
        return (await q.OrderByDescending(r => r.CreatedAt).Take(500).ToListAsync(ct)).Where(r => ctx.CanAccessBranch(r.BranchId)).Select(ToDto).ToList();
    }

    // ---------- Purchase orders ----------
    public async Task<PurchaseOrderDto> CreateOrderAsync(CreatePurchaseOrderRequest req, CancellationToken ct)
    {
        new Validator().Require(req.Lines is { Count: > 0 and <= 500 }, "lines", "Between 1 and 500 lines.")
            .Require(req.Lines.All(l => l.ProductId is not null && l.Quantity > 0 && l.UnitCost >= 0 && l.TaxRatePercent is >= 0 and <= 100), "lines", "Each line needs a product, positive quantity and valid cost/tax.")
            .ThrowIfInvalid();
        var supplier = await db.Set<Supplier>().SingleOrDefaultAsync(s => s.Id == req.SupplierId && s.IsActive, ct) ?? throw ValidationException.For("supplierId", "Unknown or inactive supplier.");
        var branchId = await branches.GetWarehouseBranchAsync(req.WarehouseId, ct) ?? throw ValidationException.For("warehouseId", "Unknown warehouse.");
        ctx.EnsureBranch(branchId);
        var ids = req.Lines.Select(l => l.ProductId!.Value).Distinct().ToList();
        if (await db.Set<Product>().CountAsync(p => ids.Contains(p.Id) && p.Type == ProductType.Physical, ct) != ids.Count)
            throw ValidationException.For("lines", "Purchase order lines must be existing physical products.");
        var tid = ctx.RequireTenant();
        var po = new PurchaseOrder
        {
            TenantId = tid, SupplierId = supplier.Id, BranchId = branchId, WarehouseId = req.WarehouseId, Status = PurchaseOrderStatus.Draft,
            OrderDate = Today, ExpectedDate = req.ExpectedDate, Notes = req.Notes, PurchaseRequestId = req.PurchaseRequestId,
            Number = await sequences.NextAsync(DocumentTypes.PurchaseOrder, branchId, ct),
        };
        foreach (var l in req.Lines)
            po.Lines.Add(new PurchaseOrderLine { TenantId = tid, OrderId = po.Id, ProductId = l.ProductId!.Value, Quantity = l.Quantity, UnitCost = l.UnitCost, TaxRatePercent = l.TaxRatePercent });
        Recalculate(po);
        if (req.PurchaseRequestId is { } prId)
        {
            var pr = await db.Set<PurchaseRequest>().SingleOrDefaultAsync(r => r.Id == prId, ct) ?? throw ValidationException.For("purchaseRequestId", "Unknown request.");
            if (pr.Status != PurchaseRequestStatus.Approved) throw new ConflictException("request.not_approved", "Only approved requests can be converted.");
            pr.Status = PurchaseRequestStatus.Converted;
        }
        db.Add(po);
        audit.Write("purchase_order.created", nameof(PurchaseOrder), po.Id.ToString(), new { po.Total });
        await db.SaveChangesAsync(ct);
        return await GetOrderAsync(po.Id, ct);
    }

    /// <summary>Submit: users with approval permission approve directly; others put the order into PendingApproval.</summary>
    public async Task<PurchaseOrderDto> SubmitOrderAsync(Guid id, CancellationToken ct)
    {
        var po = await LoadOrderAsync(id, ct);
        if (po.Status != PurchaseOrderStatus.Draft) throw new ConflictException("po.state", "Only draft orders can be submitted.");
        if (ctx.HasPermission(Permissions.PurchasingApprove)) Approve(po);
        else
        {
            po.Status = PurchaseOrderStatus.PendingApproval;
            notifications.Publish(po.TenantId, "purchasing.approval_pending", $"Purchase order {po.Number} awaits approval", $"Total {po.Total:0.00}",
                NotificationSeverity.Info, Permissions.PurchasingApprove, link: $"/purchasing/orders/{po.Id}");
        }
        await db.SaveChangesAsync(ct);
        return await GetOrderAsync(id, ct);
    }

    public async Task<PurchaseOrderDto> ApproveOrderAsync(Guid id, CancellationToken ct)
    {
        var po = await LoadOrderAsync(id, ct);
        if (po.Status is not (PurchaseOrderStatus.PendingApproval or PurchaseOrderStatus.Draft)) throw new ConflictException("po.state", "This order cannot be approved.");
        if (po.CreatedBy == ctx.UserId && po.Status == PurchaseOrderStatus.PendingApproval && !ctx.HasPermission(Permissions.All))
            throw new ForbiddenException("po.self_approval", "You cannot approve an order you created.");
        Approve(po);
        await db.SaveChangesAsync(ct);
        return await GetOrderAsync(id, ct);
    }

    private void Approve(PurchaseOrder po)
    {
        po.Status = PurchaseOrderStatus.Approved;
        po.ApprovedBy = ctx.UserId;
        po.ApprovedAt = clock.UtcNow;
        audit.Write("purchase_order.approved", nameof(PurchaseOrder), po.Id.ToString(), new { po.Total });
    }

    public async Task<PurchaseOrderDto> CancelOrderAsync(Guid id, CancellationToken ct)
    {
        var po = await LoadOrderAsync(id, ct);
        if (po.Lines.Any(l => l.ReceivedQuantity > 0)) throw new ConflictException("po.received", "Orders with receipts cannot be cancelled; close them instead.");
        if (po.Status is PurchaseOrderStatus.Cancelled or PurchaseOrderStatus.Closed) throw new ConflictException("po.state", "Order is already closed.");
        po.Status = PurchaseOrderStatus.Cancelled;
        audit.Write("purchase_order.cancelled", nameof(PurchaseOrder), po.Id.ToString());
        await db.SaveChangesAsync(ct);
        return await GetOrderAsync(id, ct);
    }

    public async Task<PurchaseOrderDto> CloseOrderAsync(Guid id, CancellationToken ct)
    {
        var po = await LoadOrderAsync(id, ct);
        if (po.Status is not (PurchaseOrderStatus.PartiallyReceived or PurchaseOrderStatus.Received or PurchaseOrderStatus.Approved))
            throw new ConflictException("po.state", "This order cannot be closed.");
        po.Status = PurchaseOrderStatus.Closed;
        await db.SaveChangesAsync(ct);
        return await GetOrderAsync(id, ct);
    }

    /// <summary>Receive goods (partial deliveries supported): stock in at PO cost; Dr Inventory / Cr Goods-received-not-invoiced.</summary>
    public async Task<GoodsReceiptDto> ReceiveAsync(Guid orderId, ReceiveGoodsRequest req, CancellationToken ct)
    {
        new Validator().Require(req.Lines is { Count: > 0 }, "lines", "Nothing to receive.").Require(req.Lines.All(l => l.Quantity > 0), "lines", "Quantities must be positive.").ThrowIfInvalid();
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var po = await LoadOrderAsync(orderId, ct);
        if (po.Status is not (PurchaseOrderStatus.Approved or PurchaseOrderStatus.PartiallyReceived))
            throw new ConflictException("po.state", "Only approved orders can be received.");
        var tid = ctx.RequireTenant();
        var receipt = new GoodsReceipt { TenantId = tid, PurchaseOrderId = po.Id, WarehouseId = po.WarehouseId, Notes = req.Notes,
            Number = await sequences.NextAsync(DocumentTypes.GoodsReceipt, po.BranchId, ct) };
        decimal value = 0;
        foreach (var rl in req.Lines)
        {
            var line = po.Lines.SingleOrDefault(l => l.Id == rl.PurchaseOrderLineId) ?? throw ValidationException.For("lines", "Line does not belong to this order.");
            if (line.ReceivedQuantity + rl.Quantity > line.Quantity) throw ValidationException.For("lines", "Received quantity exceeds the ordered quantity.");
            await stock.MoveAsync(new MovementRequest(po.WarehouseId, line.ProductId, MovementType.Receipt, rl.Quantity, line.UnitCost, "goods_receipt", receipt.Id,
                receipt.Number, po.Number, rl.LotNumber, rl.ExpiryDate, rl.Serials), ct);
            line.ReceivedQuantity += rl.Quantity;
            value += Money.Round(rl.Quantity * line.UnitCost);
        }
        receipt.Value = value;
        po.Status = po.Lines.All(l => l.ReceivedQuantity >= l.Quantity) ? PurchaseOrderStatus.Received : PurchaseOrderStatus.PartiallyReceived;
        var b = new PostingBuilder().Debit(AccountRoles.Inventory, value).Credit(AccountRoles.GoodsReceivedNotInvoiced, value, po.Number, PartyTypes.Supplier, po.SupplierId);
        receipt.JournalEntryId = (await posting.PostAsync(Today, $"Goods receipt {receipt.Number} ({po.Number})", JournalSource.Purchasing, receipt.Id, po.BranchId, b, ct)).Id;
        db.Add(receipt);
        audit.Write("purchase_order.received", nameof(PurchaseOrder), po.Id.ToString(), new { receipt.Number, value });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new GoodsReceiptDto(receipt.Id, receipt.Number, po.Id, po.WarehouseId, value, receipt.CreatedAt);
    }

    // ---------- Supplier invoices ----------
    public async Task<SupplierInvoiceDto> CreateInvoiceAsync(CreateSupplierInvoiceRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.SupplierInvoiceNo, "supplierInvoiceNo", 60).Require(req.Lines is { Count: > 0 and <= 500 }, "lines", "Between 1 and 500 lines.")
            .Require(req.Lines.All(l => l.Quantity > 0 && l.UnitCost >= 0 && l.TaxRatePercent is >= 0 and <= 100), "lines", "Invalid line.")
            .Require(!req.ReceiveStock || req.WarehouseId is not null, "warehouseId", "Warehouse required to receive stock.").ThrowIfInvalid();
        var supplier = await db.Set<Supplier>().SingleOrDefaultAsync(s => s.Id == req.SupplierId, ct) ?? throw ValidationException.For("supplierId", "Unknown supplier.");
        if (await db.Set<SupplierInvoice>().AnyAsync(i => i.SupplierId == supplier.Id && i.SupplierInvoiceNo == req.SupplierInvoiceNo.Trim(), ct))
            throw new ConflictException("supplier_invoice.duplicate", "This supplier invoice number was already recorded.");
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var tid = ctx.RequireTenant();
        var inv = new SupplierInvoice
        {
            TenantId = tid, SupplierId = supplier.Id, SupplierInvoiceNo = req.SupplierInvoiceNo.Trim(), PurchaseOrderId = req.PurchaseOrderId, Date = req.Date,
            DueDate = req.DueDate ?? req.Date.AddDays(supplier.PaymentTermsDays), Status = InvoiceStatus.Posted,
            Number = await sequences.NextAsync(DocumentTypes.SupplierInvoice, null, ct),
        };
        var b = new PostingBuilder();
        decimal subtotal = 0, tax = 0;
        Guid? branchId = null;
        PurchaseOrder? po = null;
        if (req.PurchaseOrderId is { } poId)
        {
            po = await LoadOrderAsync(poId, ct);
            if (po.SupplierId != supplier.Id) throw ValidationException.For("purchaseOrderId", "Order belongs to another supplier.");
            branchId = po.BranchId;
        }
        else if (req.WarehouseId is { } wh) branchId = await branches.GetWarehouseBranchAsync(wh, ct) ?? throw ValidationException.For("warehouseId", "Unknown warehouse.");
        if (branchId is { } bid) ctx.EnsureBranch(bid);

        foreach (var l in req.Lines)
        {
            var net = Money.Round(l.Quantity * l.UnitCost);
            var lineTax = Money.Round(net * l.TaxRatePercent / 100m);
            subtotal += net; tax += lineTax;
            if (po is not null && l.ProductId is { } pid)
            {
                var poLine = po.Lines.FirstOrDefault(x => x.ProductId == pid && x.ReceivedQuantity - x.InvoicedQuantity >= l.Quantity)
                             ?? throw ValidationException.For("lines", "Invoiced quantity exceeds received, uninvoiced quantity on the order.");
                poLine.InvoicedQuantity += l.Quantity;
                var grni = Money.Round(l.Quantity * poLine.UnitCost);
                b.Debit(AccountRoles.GoodsReceivedNotInvoiced, grni, inv.Number, PartyTypes.Supplier, supplier.Id);
                b.Debit(AccountRoles.PurchasePriceVariance, net - grni);
            }
            else if (req.ReceiveStock && l.ProductId is { } spid)
            {
                await stock.MoveAsync(new MovementRequest(req.WarehouseId!.Value, spid, MovementType.Receipt, l.Quantity, l.UnitCost, "supplier_invoice", inv.Id, inv.Number), ct);
                b.Debit(AccountRoles.Inventory, net);
            }
            else if (req.ExpenseAccountId is { } ea) b.DebitAccount(ea, net, l.Description);
            else b.Debit(AccountRoles.GeneralExpense, net, l.Description);
        }
        inv.Subtotal = subtotal; inv.TaxTotal = tax; inv.Total = subtotal + tax;
        b.Debit(AccountRoles.VatDeductible, tax).Credit(AccountRoles.AccountsPayable, inv.Total, $"{inv.Number} / {inv.SupplierInvoiceNo}", PartyTypes.Supplier, supplier.Id);
        inv.JournalEntryId = (await posting.PostAsync(req.Date, $"Supplier invoice {inv.SupplierInvoiceNo} — {supplier.Name}", JournalSource.Purchasing, inv.Id, branchId, b, ct)).Id;
        db.Add(inv);
        audit.Write("supplier_invoice.posted", nameof(SupplierInvoice), inv.Id.ToString(), new { inv.Total });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetInvoiceAsync(inv.Id, ct);
    }

    public async Task<PaymentResultDto> PaySupplierAsync(SupplierPaymentRequest req, CancellationToken ct)
    {
        new Validator().Positive(req.Amount, "amount").NotEmpty(req.Method, "method", 20)
            .Require(req.Allocations.Sum(a => a.Amount) <= req.Amount, "allocations", "Allocations exceed the payment.")
            .Require(req.Allocations.All(a => a.Amount > 0), "allocations", "Allocation amounts must be positive.").ThrowIfInvalid();
        var supplier = await db.Set<Supplier>().SingleOrDefaultAsync(s => s.Id == req.SupplierId, ct) ?? throw ValidationException.For("supplierId", "Unknown supplier.");
        if (!await db.Set<Account>().AnyAsync(a => a.Id == req.PaymentAccountId && a.Type == AccountType.Asset && a.IsPostable, ct))
            throw ValidationException.For("paymentAccountId", "Choose a cash or bank account.");
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var tid = ctx.RequireTenant();
        var pay = new SupplierPayment { TenantId = tid, SupplierId = supplier.Id, Date = req.Date, Amount = Money.Round(req.Amount), Method = req.Method,
            PaymentAccountId = req.PaymentAccountId, Reference = req.Reference, Number = await sequences.NextAsync(DocumentTypes.SupplierPayment, null, ct) };
        foreach (var a in req.Allocations)
        {
            var inv = await db.Set<SupplierInvoice>().SingleOrDefaultAsync(i => i.Id == a.DocumentId && i.SupplierId == supplier.Id, ct)
                      ?? throw ValidationException.For("allocations", "Invoice not found for this supplier.");
            if (inv.AmountPaid + a.Amount > inv.Total) throw ValidationException.For("allocations", $"Allocation exceeds the outstanding amount of {inv.Number}.");
            inv.AmountPaid += a.Amount;
            inv.Status = inv.AmountPaid >= inv.Total ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid;
            pay.Allocations.Add(new SupplierPaymentAllocation { TenantId = tid, PaymentId = pay.Id, InvoiceId = inv.Id, Amount = a.Amount });
        }
        var b = new PostingBuilder().Debit(AccountRoles.AccountsPayable, pay.Amount, pay.Number, PartyTypes.Supplier, supplier.Id).CreditAccount(req.PaymentAccountId, pay.Amount, supplier.Name);
        pay.JournalEntryId = (await posting.PostAsync(req.Date, $"Supplier payment {pay.Number} — {supplier.Name}", JournalSource.Purchasing, pay.Id, null, b, ct)).Id;
        db.Add(pay);
        audit.Write("supplier.paid", nameof(SupplierPayment), pay.Id.ToString(), new { pay.Amount });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        var allocated = pay.Allocations.Sum(a => a.Amount);
        return new PaymentResultDto(pay.Id, pay.Number, pay.Amount, allocated, pay.Amount - allocated);
    }

    /// <summary>Return goods to a supplier (or record a price credit when a line has no product): Dr AP / Cr Inventory, VAT, variance.</summary>
    public async Task<PurchaseReturnDto> ReturnToSupplierAsync(PurchaseReturnRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Reason, "reason", 300).Require(req.Lines is { Count: > 0 }, "lines", "Add lines.")
            .Require(req.Lines.All(l => l.Quantity > 0 && l.UnitCost >= 0 && l.TaxRatePercent is >= 0 and <= 100), "lines", "Invalid line.").ThrowIfInvalid();
        var supplier = await db.Set<Supplier>().SingleOrDefaultAsync(s => s.Id == req.SupplierId, ct) ?? throw ValidationException.For("supplierId", "Unknown supplier.");
        var branchId = await branches.GetWarehouseBranchAsync(req.WarehouseId, ct) ?? throw ValidationException.For("warehouseId", "Unknown warehouse.");
        ctx.EnsureBranch(branchId);
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var tid = ctx.RequireTenant();
        var ret = new PurchaseReturn { TenantId = tid, SupplierId = supplier.Id, WarehouseId = req.WarehouseId, Date = req.Date, Reason = req.Reason,
            Number = await sequences.NextAsync(DocumentTypes.PurchaseReturn, branchId, ct) };
        var b = new PostingBuilder();
        decimal total = 0;
        foreach (var l in req.Lines)
        {
            var net = Money.Round(l.Quantity * l.UnitCost);
            var tax = Money.Round(net * l.TaxRatePercent / 100m);
            total += net + tax;
            if (l.ProductId is { } pid)
            {
                var r = await stock.MoveAsync(new MovementRequest(req.WarehouseId, pid, MovementType.PurchaseReturn, -l.Quantity, null, "purchase_return", ret.Id, ret.Number, req.Reason), ct);
                b.Credit(AccountRoles.Inventory, r.TotalCost).Credit(AccountRoles.PurchasePriceVariance, net - r.TotalCost);
            }
            else b.Credit(AccountRoles.PurchasePriceVariance, net, l.Description);
            b.Credit(AccountRoles.VatDeductible, tax);
        }
        ret.Total = total;
        b.Debit(AccountRoles.AccountsPayable, total, ret.Number, PartyTypes.Supplier, supplier.Id);
        ret.JournalEntryId = (await posting.PostAsync(req.Date, $"Purchase return / credit {ret.Number} — {supplier.Name}", JournalSource.Purchasing, ret.Id, branchId, b, ct)).Id;
        db.Add(ret);
        audit.Write("purchase_return.posted", nameof(PurchaseReturn), ret.Id.ToString(), new { total, req.Reason });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new PurchaseReturnDto(ret.Id, ret.Number, ret.SupplierId, ret.Total, ret.Reason, ret.CreatedAt);
    }

    // ---------- Queries ----------
    public async Task<PagedResult<PurchaseOrderDto>> OrdersAsync(PageQuery q, string? status, Guid? supplierId, bool outstandingOnly, CancellationToken ct)
    {
        var query = db.Set<PurchaseOrder>().AsNoTracking().AsQueryable();
        if (status is not null && Enum.TryParse<PurchaseOrderStatus>(status, true, out var s)) query = query.Where(o => o.Status == s);
        if (supplierId is { } sid) query = query.Where(o => o.SupplierId == sid);
        if (outstandingOnly) query = query.Where(o => o.Status == PurchaseOrderStatus.Approved || o.Status == PurchaseOrderStatus.PartiallyReceived);
        if (ctx.AllowedBranches is { } allowed) query = query.Where(o => allowed.Contains(o.BranchId));
        var page = await query.OrderByDescending(o => o.CreatedAt).Select(o => o.Id).ToPagedAsync(q, ct);
        var items = new List<PurchaseOrderDto>();
        foreach (var id in page.Items) items.Add(await GetOrderAsync(id, ct));
        return new PagedResult<PurchaseOrderDto>(items, page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<PurchaseOrderDto> GetOrderAsync(Guid id, CancellationToken ct)
    {
        var po = await db.Set<PurchaseOrder>().Include(o => o.Lines).AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, ct) ?? throw new NotFoundException("po.not_found", "Purchase order not found.");
        ctx.EnsureBranch(po.BranchId);
        var supplier = await db.Set<Supplier>().Where(s => s.Id == po.SupplierId).Select(s => s.Name).SingleAsync(ct);
        var pids = po.Lines.Select(l => l.ProductId).ToList();
        var products = await db.Set<Product>().Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        return new PurchaseOrderDto(po.Id, po.Number, po.SupplierId, supplier, po.BranchId, po.WarehouseId, po.Status.ToString(), po.OrderDate, po.ExpectedDate,
            po.Subtotal, po.TaxTotal, po.Total, po.Notes, po.ApprovedBy, po.ApprovedAt,
            po.Lines.Select(l => new PurchaseOrderLineDto(l.Id, l.ProductId, products[l.ProductId].Sku, products[l.ProductId].Name, l.Quantity, l.UnitCost, l.TaxRatePercent,
                l.ReceivedQuantity, l.InvoicedQuantity, Money.Round(l.Quantity * l.UnitCost * (1 + l.TaxRatePercent / 100m)))).ToList(), po.PurchaseRequestId);
    }

    public async Task<PagedResult<SupplierInvoiceDto>> InvoicesAsync(PageQuery q, Guid? supplierId, bool unpaidOnly, CancellationToken ct)
    {
        var query = from i in db.Set<SupplierInvoice>().AsNoTracking() join s in db.Set<Supplier>() on i.SupplierId equals s.Id select new { i, s.Name };
        if (supplierId is { } sid) query = query.Where(x => x.i.SupplierId == sid);
        if (unpaidOnly) query = query.Where(x => x.i.Status != InvoiceStatus.Paid);
        var page = await query.OrderByDescending(x => x.i.Date).ToPagedAsync(q, ct);
        return new PagedResult<SupplierInvoiceDto>(page.Items.Select(x => ToDto(x.i, x.Name)).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<SupplierInvoiceDto> GetInvoiceAsync(Guid id, CancellationToken ct)
    {
        var x = await (from i in db.Set<SupplierInvoice>().AsNoTracking() join s in db.Set<Supplier>() on i.SupplierId equals s.Id where i.Id == id select new { i, s.Name }).SingleOrDefaultAsync(ct)
                ?? throw new NotFoundException("supplier_invoice.not_found", "Invoice not found.");
        return ToDto(x.i, x.Name);
    }

    public async Task<IReadOnlyList<SupplierPerformanceDto>> PerformanceAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var suppliers = await db.Set<Supplier>().AsNoTracking().ToListAsync(ct);
        var orders = await db.Set<PurchaseOrder>().AsNoTracking().Where(o => o.OrderDate >= from && o.OrderDate <= to && o.Status != PurchaseOrderStatus.Cancelled).ToListAsync(ct);
        var receipts = await (from r in db.Set<GoodsReceipt>().AsNoTracking() join o in db.Set<PurchaseOrder>() on r.PurchaseOrderId equals o.Id
                              where o.OrderDate >= @from && o.OrderDate <= to select new { o.SupplierId, o.ExpectedDate, r.CreatedAt }).ToListAsync(ct);
        var result = new List<SupplierPerformanceDto>();
        foreach (var s in suppliers)
        {
            var so = orders.Where(o => o.SupplierId == s.Id).ToList();
            var sr = receipts.Where(r => r.SupplierId == s.Id).ToList();
            if (so.Count == 0 && sr.Count == 0) continue;
            var onTime = sr.Count(r => r.ExpectedDate is null || DateOnly.FromDateTime(r.CreatedAt.UtcDateTime) <= r.ExpectedDate);
            result.Add(new SupplierPerformanceDto(s.Id, s.Name, so.Count, so.Sum(o => o.Total), sr.Count, onTime, sr.Count == 0 ? 0 : (double)onTime / sr.Count,
                await accounting.PartyBalanceAsync(PartyTypes.Supplier, s.Id, ct)));
        }
        return result.OrderByDescending(r => r.Spend).ToList();
    }

    private async Task<PurchaseOrder> LoadOrderAsync(Guid id, CancellationToken ct)
    {
        var po = await db.Set<PurchaseOrder>().Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, ct) ?? throw new NotFoundException("po.not_found", "Purchase order not found.");
        ctx.EnsureBranch(po.BranchId);
        return po;
    }

    private static void Recalculate(PurchaseOrder po)
    {
        po.Subtotal = po.Lines.Sum(l => Money.Round(l.Quantity * l.UnitCost));
        po.TaxTotal = po.Lines.Sum(l => Money.Round(Money.Round(l.Quantity * l.UnitCost) * l.TaxRatePercent / 100m));
        po.Total = po.Subtotal + po.TaxTotal;
    }

    private static SupplierDto ToDto(Supplier s, decimal balance) => new(s.Id, s.Code, s.Name, s.Phone, s.Email, s.Address, s.TaxId, s.TradeRegisterNo,
        s.PaymentTermsDays, s.IsActive, s.Notes, balance);
    private static PurchaseRequestDto ToDto(PurchaseRequest r) => new(r.Id, r.Number, r.BranchId, r.Status.ToString(), r.Notes,
        r.Lines.Select(l => new PurchaseRequestLineDto(l.ProductId, l.Quantity, l.Notes)).ToList(), r.CreatedAt);
    private static SupplierInvoiceDto ToDto(SupplierInvoice i, string name) => new(i.Id, i.Number, i.SupplierInvoiceNo, i.SupplierId, name, i.PurchaseOrderId,
        i.Date, i.DueDate, i.Subtotal, i.TaxTotal, i.Total, i.AmountPaid, i.Total - i.AmountPaid, i.Status.ToString());
}
