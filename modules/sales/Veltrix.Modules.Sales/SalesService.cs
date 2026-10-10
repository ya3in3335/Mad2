using Microsoft.EntityFrameworkCore;
using Veltrix.Contracts.Crm;
using Veltrix.Contracts.Sales;
using Veltrix.Modules.Accounting;
using Veltrix.Modules.Catalog;
using Veltrix.Modules.Crm;
using Veltrix.Modules.Identity;
using Veltrix.Modules.Inventory;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Sales;

public sealed class SalesService(DbContext db, ITenantContext ctx, ISequenceService sequences, PricingService pricing, StockEngine stock,
    PostingService posting, CrmService crm, IAuditWriter audit, IClock clock, IBranchDirectory branches, INotificationPublisher notifications)
{
    private DateOnly Today => DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
    private const string ReservationRef = "sales_order";

    public async Task<SalesDocumentDto> CreateAsync(CreateSalesDocumentRequest req, CancellationToken ct)
    {
        if (!Enum.TryParse<SalesDocumentType>(req.Type, true, out var type) || type == SalesDocumentType.CreditNote)
            throw ValidationException.For("type", "Use Quotation, Order or Invoice (credit notes are created from invoices).");
        new Validator().Require(req.Lines is { Count: > 0 and <= 500 }, "lines", "Between 1 and 500 lines.")
            .Require(req.Lines.All(l => l.Quantity > 0 && l.DiscountPercent is >= 0 and <= 100 && (l.UnitPrice ?? 0) >= 0), "lines", "Invalid line.")
            .MaxLength(req.Notes, "notes", 2000).ThrowIfInvalid();
        var customer = await db.Set<Customer>().SingleOrDefaultAsync(c => c.Id == req.CustomerId && c.IsActive, ct) ?? throw ValidationException.For("customerId", "Unknown or inactive customer.");
        var branchId = await branches.GetWarehouseBranchAsync(req.WarehouseId, ct) ?? throw ValidationException.For("warehouseId", "Unknown warehouse.");
        ctx.EnsureBranch(branchId);
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var doc = new SalesDocument
        {
            TenantId = ctx.RequireTenant(), Type = type, CustomerId = customer.Id, BranchId = branchId, WarehouseId = req.WarehouseId, Date = req.Date ?? Today,
            DueDate = req.DueDate, ValidUntil = req.ValidUntil, SalespersonId = req.SalespersonId ?? ctx.UserId, Notes = req.Notes,
            Status = type == SalesDocumentType.Invoice ? SalesStatus.Issued : SalesStatus.Draft,
            Number = await sequences.NextAsync(type switch
            {
                SalesDocumentType.Quotation => DocumentTypes.Quotation,
                SalesDocumentType.Order => DocumentTypes.SalesOrder,
                _ => DocumentTypes.SalesInvoice,
            }, branchId, ct),
        };
        var priceListId = customer.PriceListId ?? (customer.GroupId is { } g ? await db.Set<CustomerGroup>().Where(x => x.Id == g).Select(x => x.PriceListId).SingleOrDefaultAsync(ct) : null);
        var resolved = await pricing.ResolveAsync(req.Lines.Select(l => (l.ProductId, l.Quantity)).ToList(), priceListId, ct);
        var products = await db.Set<Product>().Where(p => req.Lines.Select(l => l.ProductId).Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var n = 0;
        foreach (var l in req.Lines)
        {
            var price = l.UnitPrice ?? resolved[l.ProductId].UnitPrice;
            if (price < resolved[l.ProductId].UnitPrice) ctx.EnsurePermission(Permissions.PosDiscount);
            doc.Lines.Add(BuildLine(doc, ++n, products[l.ProductId], l.Quantity, price, l.DiscountPercent, resolved[l.ProductId].TaxRatePercent, l.Description));
        }
        Totals(doc);
        if (type == SalesDocumentType.Invoice)
        {
            doc.DueDate ??= doc.Date.AddDays(30);
            await crm.EnsureCreditAvailableAsync(customer.Id, doc.Total, ct);
            await PostInvoiceAsync(doc, issueStock: true, ct);
        }
        db.Add(doc);
        audit.Write($"sales.{type.ToString().ToLowerInvariant()}_created", nameof(SalesDocument), doc.Id.ToString(), new { doc.Number, doc.Total });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(doc.Id, ct);
    }

    private static SalesDocumentLine BuildLine(SalesDocument doc, int n, Product p, decimal qty, decimal unitPrice, decimal discountPct, decimal taxRate, string? description)
    {
        var gross = Money.Round(qty * unitPrice);
        var net = gross - Money.Round(gross * discountPct / 100m);
        var tax = Money.Round(net * taxRate / 100m);
        return new SalesDocumentLine
        {
            TenantId = doc.TenantId, DocumentId = doc.Id, LineNo = n, ProductId = p.Id, Description = description ?? p.Name, Quantity = qty, UnitPrice = unitPrice,
            DiscountPercent = discountPct, TaxRatePercent = taxRate, NetAmount = net, TaxAmount = tax, LineTotal = net + tax,
        };
    }

    private static void Totals(SalesDocument d)
    {
        d.Subtotal = d.Lines.Sum(l => Money.Round(l.Quantity * l.UnitPrice));
        d.DiscountTotal = d.Subtotal - d.Lines.Sum(l => l.NetAmount);
        d.TaxTotal = d.Lines.Sum(l => l.TaxAmount);
        d.Total = d.Lines.Sum(l => l.LineTotal);
    }

    /// <summary>Invoice posting: Dr AR / Cr revenue / Cr VAT. When the invoice itself ships goods (no prior delivery), it issues stock and books COGS.</summary>
    private async Task PostInvoiceAsync(SalesDocument inv, bool issueStock, CancellationToken ct)
    {
        var products = await db.Set<Product>().Include(p => p.Components).Where(p => inv.Lines.Select(l => l.ProductId).Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        decimal goods = 0, services = 0, cogs = 0;
        foreach (var l in inv.Lines)
        {
            var p = products[l.ProductId];
            if (p.Type == ProductType.Service) services += l.NetAmount; else goods += l.NetAmount;
            if (issueStock && p.Type != ProductType.Service)
            {
                var cost = await IssueAsync(inv.WarehouseId, p, l.Quantity, "sales_invoice", inv.Id, inv.Number, null, null, ct);
                l.UnitCost = Math.Round(cost / l.Quantity, 4);
                cogs += cost;
            }
        }
        var b = new PostingBuilder()
            .Debit(AccountRoles.AccountsReceivable, inv.Total, inv.Number, PartyTypes.Customer, inv.CustomerId)
            .Credit(AccountRoles.SalesRevenue, goods).Credit(AccountRoles.ServiceRevenue, services).Credit(AccountRoles.VatCollected, inv.TaxTotal)
            .Debit(AccountRoles.CostOfGoodsSold, cogs).Credit(AccountRoles.Inventory, cogs);
        inv.JournalEntryId = (await posting.PostAsync(inv.Date, $"Sales invoice {inv.Number}", JournalSource.Sales, inv.Id, inv.BranchId, b, ct)).Id;
    }

    private async Task<decimal> IssueAsync(Guid warehouseId, Product p, decimal qty, string refType, Guid refId, string number, IReadOnlyList<string>? serials, string? lot, CancellationToken ct)
    {
        if (p.Type == ProductType.Bundle)
        {
            decimal c = 0;
            foreach (var comp in p.Components)
                c += (await stock.MoveAsync(new MovementRequest(warehouseId, comp.ComponentProductId, MovementType.BundleComponent, -qty * comp.Quantity, null, refType, refId, number), ct)).TotalCost;
            return c;
        }
        if (!p.TrackStock) return Money.Round(p.CostPrice * qty);
        return (await stock.MoveAsync(new MovementRequest(warehouseId, p.Id, MovementType.Delivery, -qty, null, refType, refId, number, null, lot, null, serials), ct)).TotalCost;
    }

    // ---------- Quotation → order ----------
    public async Task<SalesDocumentDto> SetQuotationStatusAsync(Guid id, string status, CancellationToken ct)
    {
        var q = await LoadAsync(id, SalesDocumentType.Quotation, ct);
        if (status is not (SalesStatus.Sent or SalesStatus.Accepted or SalesStatus.Rejected)) throw ValidationException.For("status", "Use Sent, Accepted or Rejected.");
        if (q.Status is SalesStatus.Converted or SalesStatus.Rejected) throw new ConflictException("quotation.closed", "This quotation is closed.");
        q.Status = status;
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<SalesDocumentDto> ConvertQuotationAsync(Guid id, CancellationToken ct)
    {
        var q = await LoadAsync(id, SalesDocumentType.Quotation, ct);
        if (q.Status is SalesStatus.Converted or SalesStatus.Rejected) throw new ConflictException("quotation.closed", "This quotation is closed.");
        if (q.ValidUntil is { } v && v < Today) throw new DomainException("quotation.expired", "This quotation has expired.");
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var order = new SalesDocument
        {
            TenantId = q.TenantId, Type = SalesDocumentType.Order, CustomerId = q.CustomerId, BranchId = q.BranchId, WarehouseId = q.WarehouseId, Date = Today,
            SalespersonId = q.SalespersonId, SourceDocumentId = q.Id, Notes = q.Notes, Status = SalesStatus.Draft,
            Number = await sequences.NextAsync(DocumentTypes.SalesOrder, q.BranchId, ct),
        };
        foreach (var l in q.Lines)
            order.Lines.Add(new SalesDocumentLine
            {
                TenantId = q.TenantId, DocumentId = order.Id, LineNo = l.LineNo, ProductId = l.ProductId, Description = l.Description, Quantity = l.Quantity,
                UnitPrice = l.UnitPrice, DiscountPercent = l.DiscountPercent, TaxRatePercent = l.TaxRatePercent, NetAmount = l.NetAmount, TaxAmount = l.TaxAmount,
                LineTotal = l.LineTotal, SourceLineId = l.Id,
            });
        Totals(order);
        q.Status = SalesStatus.Converted;
        db.Add(order);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(order.Id, ct);
    }

    // ---------- Orders ----------
    public async Task<SalesDocumentDto> SubmitOrderAsync(Guid id, CancellationToken ct)
    {
        var o = await LoadAsync(id, SalesDocumentType.Order, ct);
        if (o.Status != SalesStatus.Draft) throw new ConflictException("order.state", "Only draft orders can be submitted.");
        if (ctx.HasPermission(Permissions.SalesApprove)) return await ApproveOrderAsync(id, ct);
        o.Status = SalesStatus.PendingApproval;
        notifications.Publish(o.TenantId, "sales.approval_pending", $"Sales order {o.Number} awaits approval", $"Total {o.Total:0.00}", NotificationSeverity.Info, Permissions.SalesApprove);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Approve: checks credit and reserves stock for physical lines.</summary>
    public async Task<SalesDocumentDto> ApproveOrderAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var o = await LoadAsync(id, SalesDocumentType.Order, ct);
        if (o.Status is not (SalesStatus.Draft or SalesStatus.PendingApproval)) throw new ConflictException("order.state", "This order cannot be approved.");
        await crm.EnsureCreditAvailableAsync(o.CustomerId, o.Total, ct);
        var physical = await db.Set<Product>().Where(p => o.Lines.Select(l => l.ProductId).Contains(p.Id) && p.TrackStock).Select(p => p.Id).ToListAsync(ct);
        foreach (var l in o.Lines.Where(l => physical.Contains(l.ProductId)))
            await stock.ReserveAsync(o.WarehouseId, l.ProductId, l.Quantity, ReservationRef, o.Id, null, ct);
        o.Status = SalesStatus.Approved;
        o.ApprovedBy = ctx.UserId;
        o.ApprovedAt = clock.UtcNow;
        audit.Write("sales.order_approved", nameof(SalesDocument), o.Id.ToString(), new { o.Total });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<SalesDocumentDto> CancelOrderAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var o = await LoadAsync(id, SalesDocumentType.Order, ct);
        if (o.Lines.Any(l => l.DeliveredQuantity > 0 || l.InvoicedQuantity > 0)) throw new ConflictException("order.fulfilled", "Delivered or invoiced orders cannot be cancelled.");
        if (o.Status == SalesStatus.Cancelled) return await GetAsync(id, ct);
        await stock.ReleaseReservationsAsync(ReservationRef, o.Id, ct);
        o.Status = SalesStatus.Cancelled;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Partial or full delivery: stock leaves the warehouse and COGS is recognized; reservations shrink accordingly.</summary>
    public async Task<DeliveryNoteDto> DeliverAsync(Guid orderId, DeliverRequest req, CancellationToken ct)
    {
        new Validator().Require(req.Lines is { Count: > 0 } && req.Lines.All(l => l.Quantity > 0), "lines", "Nothing to deliver.").ThrowIfInvalid();
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var o = await LoadAsync(orderId, SalesDocumentType.Order, ct);
        if (o.Status is not (SalesStatus.Approved or SalesStatus.PartiallyDelivered or SalesStatus.Invoiced)) throw new ConflictException("order.state", "Only approved orders can be delivered.");
        var dn = new DeliveryNote { TenantId = o.TenantId, OrderId = o.Id, WarehouseId = o.WarehouseId, Notes = req.Notes,
            Number = await sequences.NextAsync(DocumentTypes.DeliveryNote, o.BranchId, ct) };
        await stock.ReleaseReservationsAsync(ReservationRef, o.Id, ct);
        var products = await db.Set<Product>().Include(p => p.Components).Where(p => o.Lines.Select(l => l.ProductId).Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        decimal cost = 0;
        foreach (var dl in req.Lines)
        {
            var line = o.Lines.SingleOrDefault(l => l.Id == dl.OrderLineId) ?? throw ValidationException.For("lines", "Line does not belong to this order.");
            if (line.DeliveredQuantity + dl.Quantity > line.Quantity) throw ValidationException.For("lines", "Delivery exceeds the ordered quantity.");
            var p = products[line.ProductId];
            if (p.Type == ProductType.Service) { line.DeliveredQuantity += dl.Quantity; continue; }
            var c = await IssueAsync(o.WarehouseId, p, dl.Quantity, "delivery_note", dn.Id, dn.Number, dl.Serials, dl.LotNumber, ct);
            line.UnitCost = line.DeliveredQuantity + dl.Quantity == 0 ? 0 : Math.Round((line.UnitCost * line.DeliveredQuantity + c) / (line.DeliveredQuantity + dl.Quantity), 4);
            line.DeliveredQuantity += dl.Quantity;
            cost += c;
        }
        // Re-reserve what is still undelivered.
        foreach (var l in o.Lines.Where(l => l.Quantity > l.DeliveredQuantity && products[l.ProductId].TrackStock))
            await stock.ReserveAsync(o.WarehouseId, l.ProductId, l.Quantity - l.DeliveredQuantity, ReservationRef, o.Id, null, ct);
        dn.CostValue = cost;
        if (cost != 0)
            dn.JournalEntryId = (await posting.PostAsync(Today, $"Delivery {dn.Number} ({o.Number})", JournalSource.Sales, dn.Id, o.BranchId,
                new PostingBuilder().Debit(AccountRoles.CostOfGoodsSold, cost).Credit(AccountRoles.Inventory, cost), ct)).Id;
        var fullyDelivered = o.Lines.All(l => l.DeliveredQuantity >= l.Quantity);
        if (o.Status != SalesStatus.Invoiced || !fullyDelivered) o.Status = fullyDelivered ? SalesStatus.Delivered : SalesStatus.PartiallyDelivered;
        if (fullyDelivered && o.Lines.All(l => l.InvoicedQuantity >= l.Quantity)) o.Status = SalesStatus.Invoiced;
        db.Add(dn);
        audit.Write("sales.delivered", nameof(SalesDocument), o.Id.ToString(), new { dn.Number, cost });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new DeliveryNoteDto(dn.Id, dn.Number, o.Id, o.WarehouseId, dn.CostValue, dn.CreatedAt);
    }

    /// <summary>Invoice an order: bills delivered-but-uninvoiced quantities (or undelivered service lines). Stock was already issued at delivery.</summary>
    public async Task<SalesDocumentDto> InvoiceOrderAsync(Guid orderId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var o = await LoadAsync(orderId, SalesDocumentType.Order, ct);
        if (o.Status is SalesStatus.Draft or SalesStatus.PendingApproval or SalesStatus.Cancelled) throw new ConflictException("order.state", "Approve the order first.");
        var services = await db.Set<Product>().Where(p => o.Lines.Select(l => l.ProductId).Contains(p.Id) && p.Type == ProductType.Service).Select(p => p.Id).ToListAsync(ct);
        var inv = new SalesDocument
        {
            TenantId = o.TenantId, Type = SalesDocumentType.Invoice, CustomerId = o.CustomerId, BranchId = o.BranchId, WarehouseId = o.WarehouseId, Date = Today,
            DueDate = Today.AddDays(30), SalespersonId = o.SalespersonId, SourceDocumentId = o.Id, Status = SalesStatus.Issued,
            Number = await sequences.NextAsync(DocumentTypes.SalesInvoice, o.BranchId, ct),
        };
        var n = 0;
        foreach (var l in o.Lines)
        {
            var billable = (services.Contains(l.ProductId) ? l.Quantity : l.DeliveredQuantity) - l.InvoicedQuantity;
            if (billable <= 0) continue;
            var ratio = billable / l.Quantity;
            var net = Money.Round(l.NetAmount * ratio);
            var tax = Money.Round(l.TaxAmount * ratio);
            inv.Lines.Add(new SalesDocumentLine
            {
                TenantId = o.TenantId, DocumentId = inv.Id, LineNo = ++n, ProductId = l.ProductId, Description = l.Description, Quantity = billable, UnitPrice = l.UnitPrice,
                DiscountPercent = l.DiscountPercent, TaxRatePercent = l.TaxRatePercent, NetAmount = net, TaxAmount = tax, LineTotal = net + tax, UnitCost = l.UnitCost, SourceLineId = l.Id,
            });
            l.InvoicedQuantity += billable;
        }
        if (inv.Lines.Count == 0) throw new DomainException("order.nothing_to_invoice", "Nothing delivered remains to be invoiced.");
        Totals(inv);
        await PostInvoiceAsync(inv, issueStock: false, ct);
        if (o.Lines.All(l => l.InvoicedQuantity >= l.Quantity)) o.Status = SalesStatus.Invoiced;
        db.Add(inv);
        audit.Write("sales.invoice_created", nameof(SalesDocument), inv.Id.ToString(), new { inv.Number, inv.Total, order = o.Number });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(inv.Id, ct);
    }

    // ---------- Credit notes ----------
    public async Task<SalesDocumentDto> CreditInvoiceAsync(Guid invoiceId, CreateCreditNoteRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Reason, "reason", 300).Require(req.Lines is { Count: > 0 } && req.Lines.All(l => l.Quantity > 0), "lines", "Select lines.").ThrowIfInvalid();
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var inv = await LoadAsync(invoiceId, SalesDocumentType.Invoice, ct);
        var cn = new SalesDocument
        {
            TenantId = inv.TenantId, Type = SalesDocumentType.CreditNote, CustomerId = inv.CustomerId, BranchId = inv.BranchId, WarehouseId = inv.WarehouseId, Date = Today,
            SourceDocumentId = inv.Id, SalespersonId = inv.SalespersonId, Status = SalesStatus.Issued, Notes = req.Reason,
            Number = await sequences.NextAsync(DocumentTypes.CreditNote, inv.BranchId, ct),
        };
        var products = await db.Set<Product>().Include(p => p.Components).Where(p => inv.Lines.Select(l => l.ProductId).Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        decimal goods = 0, services = 0, cost = 0;
        var n = 0;
        foreach (var cl in req.Lines)
        {
            var l = inv.Lines.SingleOrDefault(x => x.Id == cl.InvoiceLineId) ?? throw ValidationException.For("lines", "Line does not belong to this invoice.");
            if (l.CreditedQuantity + cl.Quantity > l.Quantity) throw ValidationException.For("lines", "Credit exceeds the invoiced quantity.");
            var ratio = cl.Quantity / l.Quantity;
            var net = Money.Round(l.NetAmount * ratio);
            var tax = Money.Round(l.TaxAmount * ratio);
            var p = products[l.ProductId];
            if (p.Type == ProductType.Service) services += net; else goods += net;
            if (req.Restock && p.TrackStock)
            {
                await stock.MoveAsync(new MovementRequest(inv.WarehouseId, p.Id, MovementType.SaleReturn, cl.Quantity, l.UnitCost, "credit_note", cn.Id, cn.Number, req.Reason,
                    p.TrackLot || p.TrackExpiry ? "RETURN" : null), ct);
                cost += Money.Round(l.UnitCost * cl.Quantity);
            }
            l.CreditedQuantity += cl.Quantity;
            cn.Lines.Add(new SalesDocumentLine
            {
                TenantId = inv.TenantId, DocumentId = cn.Id, LineNo = ++n, ProductId = l.ProductId, Description = l.Description, Quantity = cl.Quantity, UnitPrice = l.UnitPrice,
                DiscountPercent = l.DiscountPercent, TaxRatePercent = l.TaxRatePercent, NetAmount = net, TaxAmount = tax, LineTotal = net + tax, UnitCost = l.UnitCost, SourceLineId = l.Id,
            });
        }
        Totals(cn);
        var b = new PostingBuilder().Debit(AccountRoles.SalesReturns, goods + services).Debit(AccountRoles.VatCollected, cn.TaxTotal)
            .Credit(AccountRoles.AccountsReceivable, cn.Total, cn.Number, PartyTypes.Customer, cn.CustomerId)
            .Debit(AccountRoles.Inventory, cost).Credit(AccountRoles.CostOfGoodsSold, cost);
        cn.JournalEntryId = (await posting.PostAsync(Today, $"Credit note {cn.Number} for {inv.Number}", JournalSource.Sales, cn.Id, cn.BranchId, b, ct)).Id;
        // The credit reduces what is owed on the invoice.
        inv.AmountPaid = Math.Min(inv.Total, inv.AmountPaid + cn.Total);
        inv.Status = inv.AmountPaid >= inv.Total ? SalesStatus.Paid : inv.AmountPaid > 0 ? SalesStatus.PartiallyPaid : SalesStatus.Issued;
        db.Add(cn);
        audit.Write("sales.credit_note", nameof(SalesDocument), cn.Id.ToString(), new { cn.Number, cn.Total, invoice = inv.Number, req.Reason });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(cn.Id, ct);
    }

    // ---------- Payments ----------
    public async Task<Veltrix.Contracts.Purchasing.PaymentResultDto> ReceivePaymentAsync(CustomerPaymentRequest req, CancellationToken ct)
    {
        new Validator().Positive(req.Amount, "amount").NotEmpty(req.Method, "method", 20)
            .Require(req.Allocations.Sum(a => a.Amount) <= req.Amount, "allocations", "Allocations exceed the payment.")
            .Require(req.Allocations.All(a => a.Amount > 0), "allocations", "Allocation amounts must be positive.").ThrowIfInvalid();
        if (req.Id is { } existing && await db.Set<CustomerPayment>().Include(p => p.Allocations).SingleOrDefaultAsync(p => p.Id == existing, ct) is { } done)
            return new(done.Id, done.Number, done.Amount, done.Allocations.Sum(a => a.Amount), done.Amount - done.Allocations.Sum(a => a.Amount));
        if (!await db.Set<Customer>().AnyAsync(c => c.Id == req.CustomerId, ct)) throw ValidationException.For("customerId", "Unknown customer.");
        if (!await db.Set<Account>().AnyAsync(a => a.Id == req.PaymentAccountId && a.Type == AccountType.Asset && a.IsPostable, ct))
            throw ValidationException.For("paymentAccountId", "Choose a cash or bank account.");
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var tid = ctx.RequireTenant();
        var pay = new CustomerPayment { TenantId = tid, CustomerId = req.CustomerId, Date = req.Date, Amount = Money.Round(req.Amount), Method = req.Method,
            PaymentAccountId = req.PaymentAccountId, Reference = req.Reference, Number = await sequences.NextAsync(DocumentTypes.CustomerPayment, null, ct) };
        if (req.Id is { } id && id != Guid.Empty) pay.Id = id;
        foreach (var a in req.Allocations)
        {
            var inv = await db.Set<SalesDocument>().SingleOrDefaultAsync(d => d.Id == a.DocumentId && d.Type == SalesDocumentType.Invoice && d.CustomerId == req.CustomerId, ct)
                      ?? throw ValidationException.For("allocations", "Invoice not found for this customer.");
            if (inv.AmountPaid + a.Amount > inv.Total) throw ValidationException.For("allocations", $"Allocation exceeds the outstanding amount of {inv.Number}.");
            inv.AmountPaid += a.Amount;
            inv.Status = inv.AmountPaid >= inv.Total ? SalesStatus.Paid : SalesStatus.PartiallyPaid;
            pay.Allocations.Add(new CustomerPaymentAllocation { TenantId = tid, PaymentId = pay.Id, InvoiceId = inv.Id, Amount = a.Amount });
        }
        var b = new PostingBuilder().DebitAccount(req.PaymentAccountId, pay.Amount).Credit(AccountRoles.AccountsReceivable, pay.Amount, pay.Number, PartyTypes.Customer, req.CustomerId);
        pay.JournalEntryId = (await posting.PostAsync(req.Date, $"Customer payment {pay.Number}", JournalSource.Sales, pay.Id, null, b, ct)).Id;
        db.Add(pay);
        audit.Write("sales.payment_received", nameof(CustomerPayment), pay.Id.ToString(), new { pay.Amount });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        var allocated = pay.Allocations.Sum(x => x.Amount);
        return new(pay.Id, pay.Number, pay.Amount, allocated, pay.Amount - allocated);
    }

    // ---------- Queries & analytics ----------
    public async Task<PagedResult<SalesDocumentDto>> ListAsync(PageQuery q, string? type, string? status, Guid? customerId, bool outstandingOnly, CancellationToken ct)
    {
        var query = db.Set<SalesDocument>().AsNoTracking().AsQueryable();
        if (type is not null && Enum.TryParse<SalesDocumentType>(type, true, out var t)) query = query.Where(d => d.Type == t);
        if (status is not null) query = query.Where(d => d.Status == status);
        if (customerId is { } c) query = query.Where(d => d.CustomerId == c);
        if (outstandingOnly) query = query.Where(d => d.Type == SalesDocumentType.Invoice && d.AmountPaid < d.Total);
        if (!string.IsNullOrWhiteSpace(q.Search)) query = query.Where(d => d.Number.Contains(q.Search));
        if (ctx.AllowedBranches is { } allowed) query = query.Where(d => allowed.Contains(d.BranchId));
        var page = await query.OrderByDescending(d => d.Date).ThenByDescending(d => d.Number).Select(d => d.Id).ToPagedAsync(q, ct);
        var items = new List<SalesDocumentDto>();
        foreach (var id in page.Items) items.Add(await GetAsync(id, ct));
        return new PagedResult<SalesDocumentDto>(items, page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<SalesDocumentDto> GetAsync(Guid id, CancellationToken ct)
    {
        var d = await db.Set<SalesDocument>().Include(x => x.Lines).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("sales.not_found", "Document not found.");
        ctx.EnsureBranch(d.BranchId);
        var customer = await db.Set<Customer>().Where(c => c.Id == d.CustomerId).Select(c => c.Name).SingleAsync(ct);
        var skus = await db.Set<Product>().Where(p => d.Lines.Select(l => l.ProductId).Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Sku, ct);
        return new SalesDocumentDto(d.Id, d.Type.ToString(), d.Number, d.Status, d.CustomerId, customer, d.BranchId, d.WarehouseId, d.Date, d.DueDate, d.ValidUntil,
            d.SalespersonId, d.SourceDocumentId, d.Subtotal, d.DiscountTotal, d.TaxTotal, d.Total, d.AmountPaid,
            d.Type == SalesDocumentType.Invoice ? d.Total - d.AmountPaid : 0, d.Notes, d.ApprovedBy,
            d.Lines.OrderBy(l => l.LineNo).Select(l => new SalesDocumentLineDto(l.Id, l.ProductId, skus.GetValueOrDefault(l.ProductId, ""), l.Description, l.Quantity, l.UnitPrice,
                l.DiscountPercent, l.TaxRatePercent, l.NetAmount, l.TaxAmount, l.LineTotal, l.DeliveredQuantity, l.InvoicedQuantity, l.CreditedQuantity)).ToList(), d.CreatedAt);
    }

    private async Task<SalesDocument> LoadAsync(Guid id, SalesDocumentType type, CancellationToken ct)
    {
        var d = await db.Set<SalesDocument>().Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id && x.Type == type, ct)
                ?? throw new NotFoundException("sales.not_found", $"{type} not found.");
        ctx.EnsureBranch(d.BranchId);
        return d;
    }

    public async Task<IReadOnlyList<PipelineRowDto>> PipelineAsync(CancellationToken ct) =>
        await db.Set<SalesDocument>().Where(d => d.Type == SalesDocumentType.Quotation || d.Type == SalesDocumentType.Order)
            .GroupBy(d => new { d.Type, d.Status }).Select(g => new PipelineRowDto(g.Key.Type.ToString(), g.Key.Status, g.Count(), g.Sum(x => x.Total))).ToListAsync(ct);

    public async Task<IReadOnlyList<CommissionRuleDto>> CommissionRulesAsync(CancellationToken ct) =>
        await db.Set<CommissionRule>().Select(r => new CommissionRuleDto(r.UserId, r.RatePercent)).ToListAsync(ct);

    public async Task SetCommissionRuleAsync(CommissionRuleDto rule, CancellationToken ct)
    {
        if (rule.RatePercent is < 0 or > 100) throw ValidationException.For("ratePercent", "0-100.");
        if (!await db.Set<User>().AnyAsync(u => u.Id == rule.UserId && u.TenantId == ctx.TenantId, ct)) throw ValidationException.For("userId", "Unknown user.");
        var r = await db.Set<CommissionRule>().SingleOrDefaultAsync(x => x.UserId == rule.UserId, ct) ?? db.Add(new CommissionRule { TenantId = ctx.RequireTenant(), UserId = rule.UserId }).Entity;
        r.RatePercent = rule.RatePercent;
        audit.Write("sales.commission_rule", nameof(CommissionRule), r.Id.ToString(), rule);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Commission = rate × (invoiced net − credited net) per salesperson over the period.</summary>
    public async Task<IReadOnlyList<CommissionRowDto>> CommissionsAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var docs = await db.Set<SalesDocument>().Where(d => d.SalespersonId != null && d.Date >= from && d.Date <= to &&
                                                           (d.Type == SalesDocumentType.Invoice || d.Type == SalesDocumentType.CreditNote))
            .GroupBy(d => new { d.SalespersonId, d.Type }).Select(g => new { g.Key.SalespersonId, g.Key.Type, Net = g.Sum(x => x.Total - x.TaxTotal) }).ToListAsync(ct);
        var rules = await db.Set<CommissionRule>().ToDictionaryAsync(r => r.UserId, r => r.RatePercent, ct);
        var ids = docs.Select(d => d.SalespersonId!.Value).Distinct().ToList();
        var names = await db.Set<User>().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        return ids.Select(id =>
        {
            var inv = docs.Where(d => d.SalespersonId == id && d.Type == SalesDocumentType.Invoice).Sum(d => d.Net);
            var cr = docs.Where(d => d.SalespersonId == id && d.Type == SalesDocumentType.CreditNote).Sum(d => d.Net);
            var rate = rules.GetValueOrDefault(id);
            return new CommissionRowDto(id, names.GetValueOrDefault(id, "?"), inv, cr, inv - cr, rate, Money.Round((inv - cr) * rate / 100m));
        }).OrderByDescending(r => r.CommissionBase).ToList();
    }
}

public sealed class SalesCustomerHistory(DbContext db) : ICustomerHistoryProvider
{
    public async Task<IReadOnlyList<CustomerPurchaseDto>> PurchasesAsync(Guid customerId, int take, CancellationToken ct) =>
        await db.Set<SalesDocument>().Where(d => d.CustomerId == customerId && d.Type == SalesDocumentType.Invoice).OrderByDescending(d => d.Date).Take(take)
            .Select(d => new CustomerPurchaseDto("invoice", d.Id, d.Number, d.CreatedAt, d.Total)).ToListAsync(ct);
}
