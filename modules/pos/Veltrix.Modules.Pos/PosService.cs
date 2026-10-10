using Microsoft.EntityFrameworkCore;
using Veltrix.Contracts.Crm;
using Veltrix.Contracts.Pos;
using Veltrix.Modules.Accounting;
using Veltrix.Modules.Catalog;
using Veltrix.Modules.Crm;
using Veltrix.Modules.Inventory;
using Veltrix.Modules.Tenants;
using Veltrix.SharedKernel;

namespace Veltrix.Modules.Pos;

public sealed class PosService(
    DbContext db, ITenantContext ctx, PricingService pricing, StockEngine stock, PostingService posting, ISequenceService sequences,
    ISettingsService settings, CrmService crm, IAuditWriter audit, IClock clock, INotificationPublisher notifications)
{
    // =====================================================================
    // Sale completion
    // =====================================================================
    public async Task<SaleDto> CompleteSaleAsync(CompleteSaleRequest req, CancellationToken ct)
    {
        ValidateSale(req);
        if (await FindByKeyAsync(req.IdempotencyKey, ct) is { } existing) return existing;

        var tid = ctx.RequireTenant();
        var userId = ctx.RequireUser();
        var register = await db.Set<CashRegister>().SingleOrDefaultAsync(r => r.Id == req.RegisterId, ct) ?? throw ValidationException.For("registerId", "Unknown register.");
        ctx.EnsureBranch(register.BranchId);
        var shift = await db.Set<Shift>().SingleOrDefaultAsync(s => s.Id == req.ShiftId && s.RegisterId == register.Id, ct)
                    ?? throw ValidationException.For("shiftId", "Unknown shift for this register.");
        if (shift.Status != ShiftStatus.Open && !req.Offline) throw new DomainException("pos.shift_closed", "The shift is closed. Open a new shift.", 409);
        if (!register.IsActive && !req.Offline) throw new DomainException("pos.register_inactive", "This register is disabled.", 409);

        Customer? customer = null;
        CustomerGroup? group = null;
        if (req.CustomerId is { } cid)
        {
            customer = await db.Set<Customer>().SingleOrDefaultAsync(c => c.Id == cid, ct) ?? throw ValidationException.For("customerId", "Unknown customer.");
            if (customer.GroupId is { } gid) group = await db.Set<CustomerGroup>().SingleOrDefaultAsync(g => g.Id == gid, ct);
        }

        var flags = new List<string>();
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        try
        {
            var includeTax = await settings.GetAsync(SettingKeys.PricesIncludeTax, true, ct);
            var maxDiscountPct = await settings.GetAsync(SettingKeys.MaxDiscountWithoutPermissionPct, 0m, ct);
            var largeDiscountPct = await settings.GetAsync(SettingKeys.LargeDiscountThresholdPct, 30m, ct);
            var roundingIncrement = await settings.GetAsync(SettingKeys.CashRoundingIncrement, 0m, ct);

            var productIds = req.Lines.Select(l => l.ProductId).Distinct().ToList();
            var products = await db.Set<Product>().Include(p => p.Components).Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            if (products.Count != productIds.Count) throw ValidationException.For("lines", "One or more products are unknown.");
            var prices = await pricing.ResolveAsync(req.Lines.Select(l => (l.ProductId, l.Quantity)).ToList(), customer?.PriceListId ?? group?.PriceListId, ct);

            var sale = new PosSale
            {
                TenantId = tid, IdempotencyKey = req.IdempotencyKey, Status = SaleStatus.Completed, BranchId = register.BranchId, RegisterId = register.Id,
                ShiftId = shift.Id, WarehouseId = register.WarehouseId, CustomerId = customer?.Id, CashierId = userId, Offline = req.Offline,
                ClientCreatedAt = req.ClientCreatedAt, ClientNumber = req.ClientNumber, Notes = req.Notes,
            };
            if (req.Id is { } sid && sid != Guid.Empty) sale.Id = sid;

            // 1. Price and discount each line (gross = price x qty, in the tenant's price convention).
            var work = new List<(SaleLineInput In, Product P, decimal Price, decimal Gross, decimal Discount, decimal Rate)>();
            var lineNo = 0;
            foreach (var l in req.Lines)
            {
                var p = products[l.ProductId];
                if (p.Status != ProductStatus.Active && !req.Offline) throw new DomainException("pos.product_inactive", $"{p.Name} is not available for sale.", 409);
                var resolved = prices[l.ProductId];
                var price = l.UnitPrice ?? resolved.UnitPrice;
                if (price < 0) throw ValidationException.For("lines", "Price cannot be negative.");
                if (price < resolved.UnitPrice) flags.Add($"price_override:{p.Sku}");
                var gross = Money.Round(price * l.Quantity);
                var discount = Money.Round(gross * l.DiscountPercent / 100m) + Money.Round(l.DiscountAmount);
                if (discount == 0 && group is { DiscountPercent: > 0 }) discount = Money.Round(gross * group.DiscountPercent / 100m);
                discount = Math.Clamp(discount, 0, gross);
                work.Add((l, p, price, gross, discount, resolved.TaxRatePercent));
                lineNo++;
            }
            // Cart-level discount is spread over lines proportionally to their discounted value.
            if (req.CartDiscountAmount > 0)
            {
                var weights = work.Select(w => w.Gross - w.Discount).ToList();
                if (req.CartDiscountAmount > weights.Sum()) throw ValidationException.For("cartDiscountAmount", "Discount exceeds the sale amount.");
                var shares = Money.Allocate(req.CartDiscountAmount, weights);
                for (var i = 0; i < work.Count; i++) work[i] = work[i] with { Discount = work[i].Discount + shares[i] };
            }
            var grossTotal = work.Sum(w => w.Gross);
            var discountTotal = work.Sum(w => w.Discount);
            var discountPct = grossTotal == 0 ? 0 : discountTotal / grossTotal * 100m;
            var needsDiscountPermission = discountPct > maxDiscountPct || flags.Any(f => f.StartsWith("price_override"));
            if (needsDiscountPermission && !ctx.HasPermission(Permissions.PosDiscount))
            {
                if (!req.Offline) throw new ForbiddenException("pos.discount_not_allowed", "You are not allowed to give this discount or price change.");
                flags.Add("discount_without_permission");
            }
            if (discountPct >= largeDiscountPct && discountTotal > 0) flags.Add($"large_discount:{discountPct:0.#}%");

            // 2. Tax, lines and stock.
            decimal goodsNet = 0, serviceNet = 0, taxTotal = 0, total = 0, costTotal = 0;
            lineNo = 0;
            foreach (var w in work)
            {
                var amounts = TaxMath.FromAmount(w.Gross - w.Discount, w.Rate, includeTax);
                var line = new PosSaleLine
                {
                    TenantId = tid, SaleId = sale.Id, LineNo = ++lineNo, ProductId = w.P.Id, Sku = w.P.Sku, Description = Trim(w.P.Name, 200),
                    Quantity = w.In.Quantity, UnitPrice = w.Price, DiscountAmount = w.Discount, TaxRatePercent = w.Rate, TaxAmount = amounts.Tax,
                    NetAmount = amounts.Net, LineTotal = amounts.Gross, IsService = w.P.Type == ProductType.Service,
                    Serials = w.In.Serials is { Count: > 0 } s ? string.Join('\n', s) : null, LotNumber = w.In.LotNumber,
                };
                var cost = await IssueStockAsync(sale, w.P, w.In.Quantity, w.In.Serials, w.In.LotNumber, ct);
                line.UnitCost = w.In.Quantity == 0 ? 0 : Math.Round(cost / w.In.Quantity, 4);
                costTotal += cost;
                if (line.IsService) serviceNet += amounts.Net; else goodsNet += amounts.Net;
                taxTotal += amounts.Tax;
                total += amounts.Gross;
                sale.Lines.Add(line);
            }

            // 3. Payments.
            var pays = req.Payments.Select(p => (Method: Enum.Parse<PaymentMethod>(p.Method, true), p.Amount, p.Reference)).ToList();
            decimal Sum(PaymentMethod m) => pays.Where(p => p.Method == m).Sum(p => p.Amount);
            var cash = Sum(PaymentMethod.Cash);
            var onAccount = Sum(PaymentMethod.OnAccount);
            var nonCash = pays.Where(p => p.Method != PaymentMethod.Cash).Sum(p => p.Amount);
            var rounding = 0m;
            if (roundingIncrement > 0 && cash > 0 && nonCash == 0) rounding = Money.RoundToIncrement(total, roundingIncrement) - total;
            var due = total + rounding;
            if (nonCash > due) throw ValidationException.For("payments", "Non-cash payments exceed the amount due.");
            var cashNeeded = due - nonCash;
            if (cash < cashNeeded) throw new DomainException("pos.insufficient_payment", $"Payment is short by {cashNeeded - cash:0.00}.", 400);
            var change = cash - cashNeeded;
            if (onAccount > 0)
            {
                if (customer is null) throw ValidationException.For("payments", "A customer is required for payment on account.");
                if (req.Offline)
                {
                    try { await crm.EnsureCreditAvailableAsync(customer.Id, onAccount, ct); }
                    catch (DomainException) { flags.Add("credit_limit_exceeded_offline"); }
                }
                else await crm.EnsureCreditAvailableAsync(customer.Id, onAccount, ct);
            }
            foreach (var p in pays)
                sale.Payments.Add(new PosPayment { TenantId = tid, SaleId = sale.Id, Method = p.Method, Amount = Money.Round(p.Amount), Reference = p.Reference });

            sale.Subtotal = grossTotal; sale.DiscountTotal = discountTotal; sale.TaxTotal = taxTotal; sale.Total = total; sale.Rounding = rounding;
            sale.Paid = pays.Sum(p => p.Amount); sale.Change = change; sale.OnAccount = onAccount; sale.CostTotal = costTotal;
            sale.Number = await sequences.NextAsync(DocumentTypes.PosReceipt, register.BranchId, ct);

            // 4. Accounting: one balanced entry for revenue/tax/payments and cost of goods sold.
            var b = new PostingBuilder()
                .Debit(AccountRoles.Cash, cash - change)
                .Debit(AccountRoles.CardClearing, Sum(PaymentMethod.Card))
                .Debit(AccountRoles.Bank, Sum(PaymentMethod.BankTransfer) + Sum(PaymentMethod.Cheque))
                .Debit(AccountRoles.CustomerAdvances, Sum(PaymentMethod.Voucher) + Sum(PaymentMethod.Loyalty))
                .Debit(AccountRoles.AccountsReceivable, onAccount, $"POS {sale.Number}", PartyTypes.Customer, customer?.Id)
                .Credit(AccountRoles.SalesRevenue, goodsNet)
                .Credit(AccountRoles.ServiceRevenue, serviceNet)
                .Credit(AccountRoles.VatCollected, taxTotal)
                .Credit(AccountRoles.Rounding, rounding)
                .Debit(AccountRoles.CostOfGoodsSold, costTotal)
                .Credit(AccountRoles.Inventory, costTotal);
            var date = DateOnly.FromDateTime((req.Offline && req.ClientCreatedAt is { } cc ? cc : clock.UtcNow).UtcDateTime);
            var je = await PostWithFallbackDateAsync(date, $"POS sale {sale.Number}", sale.Id, register.BranchId, b, ct);
            sale.JournalEntryId = je.Id;

            if (customer is not null) crm.EarnPoints(customer, total, sale.Number, sale.Id);
            db.Add(sale);
            audit.Write("pos.sale_completed", nameof(PosSale), sale.Id.ToString(), new { sale.Number, sale.Total, req.Offline, flags });
            RaiseFlags(sale, flags);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ToDto(sale);
        }
        catch (DbUpdateException) when (tx.Owned)
        {
            // Concurrent replay of the same idempotency key: return the winner's result.
            if (await KeyExistsAfterRollbackAsync(req.IdempotencyKey, tx, ct)) return (await FindByKeyAsync(req.IdempotencyKey, ct))!;
            throw;
        }
    }

    /// <summary>Offline sales may arrive after their period was closed: they post on the first open date instead of failing.</summary>
    private async Task<JournalEntry> PostWithFallbackDateAsync(DateOnly date, string description, Guid sourceId, Guid branchId, PostingBuilder b, CancellationToken ct)
    {
        try { return await posting.PostAsync(date, description, JournalSource.Pos, sourceId, branchId, b, ct); }
        catch (DomainException e) when (e.Code == "accounting.period_closed")
        {
            var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
            return await posting.PostAsync(today, description + $" (originally {date:yyyy-MM-dd})", JournalSource.Pos, sourceId, branchId, b, ct);
        }
    }

    private async Task<bool> KeyExistsAfterRollbackAsync(string key, UnitOfWorkTransaction tx, CancellationToken ct)
    {
        await tx.RollbackAsync(ct);
        db.ChangeTracker.Clear();
        return await db.Set<PosSale>().AnyAsync(s => s.IdempotencyKey == key, ct);
    }

    private async Task<decimal> IssueStockAsync(PosSale sale, Product p, decimal qty, IReadOnlyList<string>? serials, string? lot, CancellationToken ct)
    {
        if (p.Type == ProductType.Service) return 0;
        if (p.Type == ProductType.Bundle)
        {
            decimal cost = 0;
            foreach (var c in p.Components)
                cost += (await stock.MoveAsync(new MovementRequest(sale.WarehouseId, c.ComponentProductId, MovementType.BundleComponent, -qty * c.Quantity, null,
                    "pos_sale", sale.Id, sale.Number, $"Bundle {p.Sku}", AllowNegativeOverride: sale.Offline), ct)).TotalCost;
            return cost;
        }
        if (!p.TrackStock) return Money.Round(p.CostPrice * qty);
        var r = await stock.MoveAsync(new MovementRequest(sale.WarehouseId, p.Id, MovementType.Sale, -qty, null, "pos_sale", sale.Id, sale.Number,
            null, lot, null, serials, AllowNegativeOverride: sale.Offline), ct);
        return r.TotalCost;
    }

    private static void ValidateSale(CompleteSaleRequest req) =>
        new Validator().Require(req.IdempotencyKey is { Length: >= 8 and <= 100 }, "idempotencyKey", "Idempotency key must be 8-100 characters.")
            .Require(req.Lines is { Count: > 0 and <= 500 }, "lines", "A sale needs 1 to 500 lines.")
            .Require(req.Lines.All(l => l.Quantity > 0 && l.Quantity <= 1_000_000), "lines", "Quantities must be positive.")
            .Require(req.Lines.All(l => l.DiscountPercent is >= 0 and <= 100 && l.DiscountAmount >= 0), "lines", "Invalid discount.")
            .Require(req.Payments is { Count: > 0 and <= 20 }, "payments", "At least one payment is required.")
            .Require(req.Payments.All(p => Enum.TryParse<PaymentMethod>(p.Method, true, out _) && p.Amount >= 0), "payments", "Invalid payment.")
            .Require(req.CartDiscountAmount >= 0, "cartDiscountAmount", "Must not be negative.")
            .MaxLength(req.Notes, "notes", 500).ThrowIfInvalid();

    private void RaiseFlags(PosSale sale, List<string> flags)
    {
        if (flags.Count == 0) return;
        var severity = flags.Any(f => f.Contains("without_permission") || f.Contains("credit_limit")) ? NotificationSeverity.Critical : NotificationSeverity.Warning;
        notifications.Publish(sale.TenantId, "pos.unusual_transaction", $"Review sale {sale.Number}", string.Join(", ", flags), severity,
            Permissions.SalesApprove, link: $"/pos/sales/{sale.Id}");
    }

    // =====================================================================
    // Returns, voids, exchanges
    // =====================================================================
    public async Task<SaleDto> ReturnAsync(Guid saleId, ReturnSaleRequest req, CancellationToken ct)
    {
        new Validator().Require(req.IdempotencyKey is { Length: >= 8 and <= 100 }, "idempotencyKey", "Idempotency key must be 8-100 characters.")
            .Require(req.Lines is { Count: > 0 }, "lines", "Select lines to return.").NotEmpty(req.Reason, "reason", 300)
            .Require(Enum.TryParse<PaymentMethod>(req.RefundMethod, true, out var refundMethod) && refundMethod is PaymentMethod.Cash or PaymentMethod.Card
                     or PaymentMethod.BankTransfer or PaymentMethod.OnAccount or PaymentMethod.Voucher, "refundMethod", "Unsupported refund method.")
            .ThrowIfInvalid();
        if (await FindByKeyAsync(req.IdempotencyKey, ct) is { } existing) return existing;

        var original = await db.Set<PosSale>().Include(s => s.Lines).SingleOrDefaultAsync(s => s.Id == saleId, ct) ?? throw new NotFoundException("sale.not_found", "Sale not found.");
        ctx.EnsureBranch(original.BranchId);
        if (original.IsReturn) throw new DomainException("pos.return_of_return", "A return cannot be returned.");
        if (original.Status == SaleStatus.Voided) throw new DomainException("pos.voided", "This sale was voided.");
        var register = await db.Set<CashRegister>().SingleOrDefaultAsync(r => r.Id == req.RegisterId, ct) ?? throw ValidationException.For("registerId", "Unknown register.");
        ctx.EnsureBranch(register.BranchId);
        var shift = await db.Set<Shift>().SingleOrDefaultAsync(s => s.Id == req.ShiftId && s.RegisterId == register.Id && s.Status == ShiftStatus.Open, ct)
                    ?? throw new DomainException("pos.shift_closed", "An open shift on this register is required for returns.", 409);
        if (refundMethod == PaymentMethod.OnAccount && original.CustomerId is null) throw ValidationException.For("refundMethod", "Refund on account needs a customer sale.");

        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        {
            var tid = ctx.RequireTenant();
            var ret = new PosSale
            {
                TenantId = tid, IdempotencyKey = req.IdempotencyKey, Status = SaleStatus.Completed, IsReturn = true, ReturnOfSaleId = original.Id,
                BranchId = register.BranchId, RegisterId = register.Id, ShiftId = shift.Id, WarehouseId = register.WarehouseId, CustomerId = original.CustomerId,
                CashierId = ctx.RequireUser(), Notes = Trim(req.Reason, 500),
                Number = await sequences.NextAsync(DocumentTypes.PosReturn, register.BranchId, ct),
            };
            if (req.Id is { } rid && rid != Guid.Empty) ret.Id = rid;
            decimal net = 0, tax = 0, total = 0, cost = 0;
            var products = await db.Set<Product>().Include(p => p.Components).Where(p => original.Lines.Select(l => l.ProductId).Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            var n = 0;
            foreach (var rl in req.Lines)
            {
                var line = original.Lines.SingleOrDefault(l => l.Id == rl.SaleLineId) ?? throw ValidationException.For("lines", "Line does not belong to this sale.");
                if (rl.Quantity <= 0 || rl.Quantity > line.Quantity - line.ReturnedQuantity)
                    throw ValidationException.For("lines", $"Return quantity for {line.Sku} exceeds the quantity still returnable ({line.Quantity - line.ReturnedQuantity:0.###}).");
                var ratio = rl.Quantity / line.Quantity;
                var lNet = Money.Round(line.NetAmount * ratio);
                var lTax = Money.Round(line.TaxAmount * ratio);
                var lCost = Money.Round(line.UnitCost * rl.Quantity);
                var serials = line.Serials?.Split('\n').Skip((int)line.ReturnedQuantity).Take((int)rl.Quantity).ToList();
                if (req.Restock) await RestockAsync(ret, products[line.ProductId], rl.Quantity, line.UnitCost, serials, line.LotNumber, ct);
                line.ReturnedQuantity += rl.Quantity;
                ret.Lines.Add(new PosSaleLine
                {
                    TenantId = tid, SaleId = ret.Id, LineNo = ++n, ProductId = line.ProductId, Sku = line.Sku, Description = line.Description,
                    Quantity = -rl.Quantity, UnitPrice = line.UnitPrice, DiscountAmount = -Money.Round(line.DiscountAmount * ratio), TaxRatePercent = line.TaxRatePercent,
                    TaxAmount = -lTax, NetAmount = -lNet, LineTotal = -(lNet + lTax), UnitCost = line.UnitCost, ReturnOfLineId = line.Id, IsService = line.IsService,
                    Serials = serials is { Count: > 0 } ? string.Join('\n', serials) : null, LotNumber = line.LotNumber,
                });
                net += lNet; tax += lTax; total += lNet + lTax;
                if (req.Restock && !line.IsService) cost += lCost;
            }
            ret.Subtotal = -total; ret.TaxTotal = -tax; ret.Total = -total; ret.Paid = -total; ret.CostTotal = -cost;
            ret.OnAccount = refundMethod == PaymentMethod.OnAccount ? -total : 0;
            ret.Payments.Add(new PosPayment { TenantId = tid, SaleId = ret.Id, Method = refundMethod, Amount = -total, Reference = original.Number });

            var refundRole = refundMethod switch
            {
                PaymentMethod.Cash => AccountRoles.Cash,
                PaymentMethod.Card => AccountRoles.CardClearing,
                PaymentMethod.BankTransfer => AccountRoles.Bank,
                PaymentMethod.Voucher => AccountRoles.CustomerAdvances,
                _ => AccountRoles.AccountsReceivable,
            };
            var b = new PostingBuilder()
                .Debit(AccountRoles.SalesReturns, net)
                .Debit(AccountRoles.VatCollected, tax)
                .Credit(refundRole, total, $"Return {ret.Number}", refundRole == AccountRoles.AccountsReceivable ? PartyTypes.Customer : null,
                    refundRole == AccountRoles.AccountsReceivable ? original.CustomerId : null)
                .Debit(AccountRoles.Inventory, cost)
                .Credit(AccountRoles.CostOfGoodsSold, cost);
            var je = await posting.PostAsync(DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), $"POS return {ret.Number} of {original.Number}", JournalSource.Pos, ret.Id, register.BranchId, b, ct);
            ret.JournalEntryId = je.Id;
            db.Add(ret);
            audit.Write("pos.sale_returned", nameof(PosSale), original.Id.ToString(), new { ret.Number, total, req.Reason, req.Restock });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ToDto(ret);
        }
    }

    private async Task RestockAsync(PosSale ret, Product p, decimal qty, decimal unitCost, IReadOnlyList<string>? serials, string? lot, CancellationToken ct)
    {
        if (p.Type == ProductType.Service) return;
        if (p.Type == ProductType.Bundle)
        {
            foreach (var c in p.Components)
                await stock.MoveAsync(new MovementRequest(ret.WarehouseId, c.ComponentProductId, MovementType.SaleReturn, qty * c.Quantity, null, "pos_return", ret.Id, ret.Number), ct);
            return;
        }
        if (!p.TrackStock) return;
        var lotExpiry = lot is null ? null : await db.Set<StockLot>().Where(l => l.ProductId == p.Id && l.LotNumber == lot).Select(l => l.ExpiryDate).FirstOrDefaultAsync(ct);
        await stock.MoveAsync(new MovementRequest(ret.WarehouseId, p.Id, MovementType.SaleReturn, qty, unitCost, "pos_return", ret.Id, ret.Number,
            null, lot ?? (p.TrackLot || p.TrackExpiry ? "RETURN" : null), lotExpiry, serials), ct);
    }

    /// <summary>Void: full reversal of a sale during its (still open) shift. Journal is reversed exactly and stock restored.</summary>
    public async Task<SaleDto> VoidAsync(Guid saleId, VoidSaleRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Reason, "reason", 300).ThrowIfInvalid();
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var sale = await db.Set<PosSale>().Include(s => s.Lines).Include(s => s.Payments).SingleOrDefaultAsync(s => s.Id == saleId, ct)
                   ?? throw new NotFoundException("sale.not_found", "Sale not found.");
        ctx.EnsureBranch(sale.BranchId);
        if (sale.IsReturn || sale.Status == SaleStatus.Voided) throw new DomainException("pos.void_invalid", "This sale cannot be voided.");
        if (sale.Lines.Any(l => l.ReturnedQuantity > 0)) throw new DomainException("pos.void_after_return", "Sales with returns cannot be voided; return the remaining items instead.");
        var shift = await db.Set<Shift>().SingleAsync(s => s.Id == sale.ShiftId, ct);
        if (shift.Status != ShiftStatus.Open) throw new DomainException("pos.void_shift_closed", "Only sales of the current open shift can be voided. Use a return.", 409);

        var tid = ctx.RequireTenant();
        var reversal = new PosSale
        {
            TenantId = tid, IdempotencyKey = "void:" + sale.Id, Status = SaleStatus.Completed, IsReturn = true, IsVoidReversal = true, ReturnOfSaleId = sale.Id,
            BranchId = sale.BranchId, RegisterId = sale.RegisterId, ShiftId = sale.ShiftId, WarehouseId = sale.WarehouseId, CustomerId = sale.CustomerId,
            CashierId = ctx.RequireUser(), Notes = Trim("VOID: " + req.Reason, 500), Number = await sequences.NextAsync(DocumentTypes.PosReturn, sale.BranchId, ct),
            Subtotal = -sale.Subtotal, DiscountTotal = -sale.DiscountTotal, TaxTotal = -sale.TaxTotal, Total = -sale.Total, Paid = -sale.Paid,
            Change = -sale.Change, Rounding = -sale.Rounding, OnAccount = -sale.OnAccount, CostTotal = -sale.CostTotal,
        };
        var products = await db.Set<Product>().Include(p => p.Components).Where(p => sale.Lines.Select(l => l.ProductId).Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        foreach (var l in sale.Lines)
        {
            await RestockAsync(reversal, products[l.ProductId], l.Quantity, l.UnitCost, l.Serials?.Split('\n'), l.LotNumber, ct);
            l.ReturnedQuantity = l.Quantity;
            reversal.Lines.Add(new PosSaleLine
            {
                TenantId = tid, SaleId = reversal.Id, LineNo = l.LineNo, ProductId = l.ProductId, Sku = l.Sku, Description = l.Description, Quantity = -l.Quantity,
                UnitPrice = l.UnitPrice, DiscountAmount = -l.DiscountAmount, TaxRatePercent = l.TaxRatePercent, TaxAmount = -l.TaxAmount, NetAmount = -l.NetAmount,
                LineTotal = -l.LineTotal, UnitCost = l.UnitCost, ReturnOfLineId = l.Id, IsService = l.IsService,
            });
        }
        foreach (var p in sale.Payments)
            reversal.Payments.Add(new PosPayment { TenantId = tid, SaleId = reversal.Id, Method = p.Method, Amount = -p.Amount, Reference = sale.Number });
        if (sale.JournalEntryId is { } je)
        {
            var rev = await posting.ReverseAsync(je, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), "POS void " + sale.Number, ct);
            reversal.JournalEntryId = rev.Id;
        }
        if (sale.CustomerId is { } cid)
        {
            var customer = await db.Set<Customer>().SingleAsync(c => c.Id == cid, ct);
            var earned = await db.Set<LoyaltyTransaction>().Where(t => t.ReferenceId == sale.Id).SumAsync(t => (decimal?)t.Points, ct) ?? 0;
            if (earned != 0)
            {
                customer.LoyaltyPoints -= earned;
                db.Add(new LoyaltyTransaction { TenantId = tid, CustomerId = cid, Points = -earned, Reason = "void", Reference = sale.Number, ReferenceId = reversal.Id });
            }
        }
        sale.Status = SaleStatus.Voided;
        db.Add(reversal);
        audit.Write("pos.sale_voided", nameof(PosSale), sale.Id.ToString(), new { sale.Number, sale.Total, req.Reason });
        notifications.Publish(tid, "pos.sale_voided", $"Sale {sale.Number} voided", req.Reason, NotificationSeverity.Warning, Permissions.SalesApprove, link: $"/pos/sales/{sale.Id}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(reversal);
    }

    /// <summary>Exchange = return + new sale in a single transaction.</summary>
    public async Task<ExchangeResult> ExchangeAsync(Guid saleId, ExchangeRequest req, CancellationToken ct)
    {
        if (await FindByKeyAsync(req.NewSale.IdempotencyKey, ct) is { } doneSale && await FindByKeyAsync(req.Return.IdempotencyKey, ct) is { } doneReturn)
            return new ExchangeResult(doneReturn, doneSale);
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var ret = await ReturnAsync(saleId, req.Return, ct);
        var sale = await CompleteSaleAsync(req.NewSale, ct);
        await tx.CommitAsync(ct);
        return new ExchangeResult(ret, sale);
    }

    // =====================================================================
    // Shifts & cash drawer
    // =====================================================================
    public async Task<ShiftDto> OpenShiftAsync(OpenShiftRequest req, CancellationToken ct)
    {
        new Validator().NonNegative(req.OpeningFloat, "openingFloat").ThrowIfInvalid();
        if (req.Id is { } existingId && await db.Set<Shift>().SingleOrDefaultAsync(s => s.Id == existingId, ct) is { } existing) return ToDto(existing);
        var register = await db.Set<CashRegister>().SingleOrDefaultAsync(r => r.Id == req.RegisterId, ct) ?? throw ValidationException.For("registerId", "Unknown register.");
        ctx.EnsureBranch(register.BranchId);
        if (await db.Set<Shift>().AnyAsync(s => s.RegisterId == register.Id && s.Status == ShiftStatus.Open, ct))
            throw new ConflictException("pos.shift_open", "A shift is already open on this register.");
        var shift = new Shift
        {
            TenantId = ctx.RequireTenant(), RegisterId = register.Id, BranchId = register.BranchId, OpenedBy = ctx.RequireUser(),
            OpenedAt = req.OpenedAt ?? clock.UtcNow, OpeningFloat = Money.Round(req.OpeningFloat), Status = ShiftStatus.Open,
        };
        if (req.Id is { } id && id != Guid.Empty) shift.Id = id;
        db.Add(shift);
        audit.Write("pos.shift_opened", nameof(Shift), shift.Id.ToString(), new { req.OpeningFloat });
        await db.SaveChangesAsync(ct);
        return ToDto(shift);
    }

    public async Task<ShiftReportDto> CloseShiftAsync(Guid shiftId, CloseShiftRequest req, CancellationToken ct)
    {
        new Validator().NonNegative(req.CountedCash, "countedCash").MaxLength(req.Notes, "notes", 500).ThrowIfInvalid();
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var shift = await db.Set<Shift>().SingleOrDefaultAsync(s => s.Id == shiftId, ct) ?? throw new NotFoundException("shift.not_found", "Shift not found.");
        ctx.EnsureBranch(shift.BranchId);
        if (shift.Status == ShiftStatus.Closed) return await ShiftReportAsync(shiftId, ct); // idempotent replay
        var report = await ShiftReportAsync(shiftId, ct);
        shift.Status = ShiftStatus.Closed;
        shift.ClosedAt = req.ClosedAt ?? clock.UtcNow;
        shift.ClosedBy = ctx.RequireUser();
        shift.CountedCash = Money.Round(req.CountedCash);
        shift.ExpectedCash = report.ExpectedCash;
        shift.Variance = shift.CountedCash - report.ExpectedCash;
        shift.Notes = req.Notes;
        if (shift.Variance != 0)
        {
            // Over: Dr Cash / Cr Over-short.  Short: Dr Over-short / Cr Cash.
            var b = new PostingBuilder().Debit(AccountRoles.Cash, shift.Variance.Value).Credit(AccountRoles.CashOverShort, shift.Variance.Value);
            await posting.PostAsync(DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), $"Cash variance shift {shift.Id.ToString()[..8]}", JournalSource.CashManagement, shift.Id, shift.BranchId, b, ct);
            if (Math.Abs(shift.Variance.Value) > 0)
                notifications.Publish(shift.TenantId, "pos.cash_variance", "Cash variance at shift close", $"Variance {shift.Variance:0.00} (expected {report.ExpectedCash:0.00}).",
                    Math.Abs(shift.Variance.Value) >= 1000 ? NotificationSeverity.Critical : NotificationSeverity.Warning, Permissions.PosShiftManage);
        }
        audit.Write("pos.shift_closed", nameof(Shift), shift.Id.ToString(), new { shift.CountedCash, shift.ExpectedCash, shift.Variance });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ShiftReportAsync(shiftId, ct);
    }

    public async Task<CashMovementDto> CashMovementAsync(Guid shiftId, CashMovementRequest req, CancellationToken ct)
    {
        new Validator().Positive(req.Amount, "amount").NotEmpty(req.Reason, "reason", 300)
            .Require(Enum.TryParse<CashMovementType>(req.Type, true, out var type), "type", "Use PayIn, PayOut or Drop.").ThrowIfInvalid();
        if (req.Id is { } eid && await db.Set<CashMovement>().SingleOrDefaultAsync(m => m.Id == eid, ct) is { } done)
            return new CashMovementDto(done.Id, done.ShiftId, done.Type.ToString(), done.Amount, done.Reason, done.CreatedAt);
        await using var tx = await db.Database.BeginOrJoinAsync(ct);
        var shift = await db.Set<Shift>().SingleOrDefaultAsync(s => s.Id == shiftId && s.Status == ShiftStatus.Open, ct)
                    ?? throw new DomainException("pos.shift_closed", "An open shift is required.", 409);
        ctx.EnsureBranch(shift.BranchId);
        var m = new CashMovement { TenantId = ctx.RequireTenant(), ShiftId = shift.Id, Type = type, Amount = Money.Round(req.Amount), Reason = req.Reason.Trim() };
        if (req.Id is { } id && id != Guid.Empty) m.Id = id;
        if (type != CashMovementType.Drop)
        {
            var b = new PostingBuilder();
            if (type == CashMovementType.PayIn)
            {
                b.Debit(AccountRoles.Cash, m.Amount);
                if (req.CounterAccountId is { } ca) b.CreditAccount(ca, m.Amount, m.Reason); else b.Credit(AccountRoles.Equity, m.Amount, m.Reason);
            }
            else
            {
                if (req.CounterAccountId is { } ca) b.DebitAccount(ca, m.Amount, m.Reason); else b.Debit(AccountRoles.GeneralExpense, m.Amount, m.Reason);
                b.Credit(AccountRoles.Cash, m.Amount);
            }
            m.JournalEntryId = (await posting.PostAsync(DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), $"Cash {type}: {m.Reason}", JournalSource.CashManagement, m.Id, shift.BranchId, b, ct)).Id;
        }
        db.Add(m);
        audit.Write("pos.cash_movement", nameof(CashMovement), m.Id.ToString(), new { type, m.Amount, m.Reason });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new CashMovementDto(m.Id, m.ShiftId, m.Type.ToString(), m.Amount, m.Reason, m.CreatedAt);
    }

    public async Task<ShiftReportDto> ShiftReportAsync(Guid shiftId, CancellationToken ct)
    {
        var shift = await db.Set<Shift>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == shiftId, ct) ?? throw new NotFoundException("shift.not_found", "Shift not found.");
        ctx.EnsureBranch(shift.BranchId);
        var sales = await db.Set<PosSale>().Include(s => s.Payments).AsNoTracking().Where(s => s.ShiftId == shiftId).ToListAsync(ct);
        var movements = await db.Set<CashMovement>().AsNoTracking().Where(m => m.ShiftId == shiftId).ToListAsync(ct);
        var cashIn = movements.Where(m => m.Type == CashMovementType.PayIn).Sum(m => m.Amount);
        var cashOut = movements.Where(m => m.Type != CashMovementType.PayIn).Sum(m => m.Amount);
        var cashFromSales = sales.Sum(s => s.Payments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount) - s.Change);
        var payments = sales.SelectMany(s => s.Payments.Select(p => (p.Method, Amount: p.Method == PaymentMethod.Cash ? p.Amount - (p.Amount > 0 ? s.Change : 0) : p.Amount)))
            .GroupBy(p => p.Method).Select(g => new PaymentTotalDto(g.Key.ToString(), g.Sum(x => x.Amount), g.Count())).ToList();
        var regular = sales.Where(s => !s.IsReturn).ToList();
        var returns = sales.Where(s => s.IsReturn).ToList();
        var expected = shift.OpeningFloat + cashFromSales + cashIn - cashOut;
        return new ShiftReportDto(ToDto(shift), regular.Count, returns.Count, regular.Sum(s => s.Total), -returns.Sum(s => s.Total), regular.Sum(s => s.DiscountTotal) + returns.Sum(s => s.DiscountTotal),
            sales.Sum(s => s.TaxTotal), sales.Sum(s => s.Total - s.TaxTotal), payments, cashIn, cashOut, expected);
    }

    public async Task<IReadOnlyList<ShiftDto>> ShiftsAsync(Guid? registerId, bool openOnly, CancellationToken ct)
    {
        var q = db.Set<Shift>().AsNoTracking().Where(s => (registerId == null || s.RegisterId == registerId) && (!openOnly || s.Status == ShiftStatus.Open));
        if (ctx.AllowedBranches is { } allowed) q = q.Where(s => allowed.Contains(s.BranchId));
        return (await q.OrderByDescending(s => s.OpenedAt).Take(200).ToListAsync(ct)).Select(ToDto).ToList();
    }

    // =====================================================================
    // Registers, parked carts, queries
    // =====================================================================
    public async Task<IReadOnlyList<RegisterDto>> RegistersAsync(CancellationToken ct)
    {
        var regs = await db.Set<CashRegister>().AsNoTracking().OrderBy(r => r.Code).ToListAsync(ct);
        var open = await db.Set<Shift>().Where(s => s.Status == ShiftStatus.Open).ToDictionaryAsync(s => s.RegisterId, s => s.Id, ct);
        return regs.Where(r => ctx.CanAccessBranch(r.BranchId))
            .Select(r => new RegisterDto(r.Id, r.BranchId, r.WarehouseId, r.Code, r.Name, r.IsActive, open.TryGetValue(r.Id, out var s) ? s : null)).ToList();
    }

    public async Task<RegisterDto> UpsertRegisterAsync(Guid? id, UpsertRegisterRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Code, "code", 20).NotEmpty(req.Name, "name", 100).ThrowIfInvalid();
        ctx.EnsureBranch(req.BranchId);
        var wh = await db.Set<Warehouse>().SingleOrDefaultAsync(w => w.Id == req.WarehouseId, ct) ?? throw ValidationException.For("warehouseId", "Unknown warehouse.");
        if (wh.BranchId != req.BranchId) throw ValidationException.For("warehouseId", "The warehouse must belong to the register's branch.");
        var code = req.Code.Trim().ToUpperInvariant();
        if (await db.Set<CashRegister>().AnyAsync(r => r.Code == code && r.Id != id, ct)) throw new ConflictException("register.code_taken", "Register code already exists.");
        var r = id is { } rid ? await db.Set<CashRegister>().SingleOrDefaultAsync(x => x.Id == rid, ct) ?? throw new NotFoundException("register.not_found", "Register not found.")
            : db.Add(new CashRegister { TenantId = ctx.RequireTenant() }).Entity;
        r.BranchId = req.BranchId; r.WarehouseId = req.WarehouseId; r.Code = code; r.Name = req.Name.Trim(); r.IsActive = req.IsActive;
        await db.SaveChangesAsync(ct);
        return (await RegistersAsync(ct)).Single(x => x.Id == r.Id);
    }

    public async Task<ParkedCartDto> ParkAsync(ParkCartRequest req, CancellationToken ct)
    {
        new Validator().NotEmpty(req.Name, "name", 100).Require(req.PayloadJson is { Length: > 0 and <= 200_000 }, "payloadJson", "Invalid cart.").ThrowIfInvalid();
        try { System.Text.Json.JsonDocument.Parse(req.PayloadJson).Dispose(); } catch (System.Text.Json.JsonException) { throw ValidationException.For("payloadJson", "Invalid JSON."); }
        var reg = await db.Set<CashRegister>().SingleOrDefaultAsync(r => r.Id == req.RegisterId, ct) ?? throw ValidationException.For("registerId", "Unknown register.");
        ctx.EnsureBranch(reg.BranchId);
        var c = db.Add(new ParkedCart { TenantId = ctx.RequireTenant(), RegisterId = reg.Id, Name = req.Name.Trim(), PayloadJson = req.PayloadJson }).Entity;
        await db.SaveChangesAsync(ct);
        return new ParkedCartDto(c.Id, c.RegisterId, c.Name, c.PayloadJson, c.CreatedBy ?? Guid.Empty, c.CreatedAt);
    }

    public async Task<IReadOnlyList<ParkedCartDto>> ParkedAsync(Guid registerId, CancellationToken ct) =>
        await db.Set<ParkedCart>().Where(c => c.RegisterId == registerId).OrderBy(c => c.CreatedAt)
            .Select(c => new ParkedCartDto(c.Id, c.RegisterId, c.Name, c.PayloadJson, c.CreatedBy ?? Guid.Empty, c.CreatedAt)).ToListAsync(ct);

    /// <summary>Resuming a parked cart removes it (the client re-submits it as a sale).</summary>
    public async Task<ParkedCartDto> ResumeAsync(Guid id, CancellationToken ct)
    {
        var c = await db.Set<ParkedCart>().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("cart.not_found", "Parked sale not found.");
        db.Remove(c);
        await db.SaveChangesAsync(ct);
        return new ParkedCartDto(c.Id, c.RegisterId, c.Name, c.PayloadJson, c.CreatedBy ?? Guid.Empty, c.CreatedAt);
    }

    public async Task<PagedResult<SaleDto>> SalesAsync(PageQuery q, Guid? shiftId, Guid? customerId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var query = db.Set<PosSale>().Include(s => s.Lines).Include(s => s.Payments).AsNoTracking().AsSplitQuery().AsQueryable();
        if (shiftId is { } sh) query = query.Where(s => s.ShiftId == sh);
        if (customerId is { } c) query = query.Where(s => s.CustomerId == c);
        if (from is { } f) query = query.Where(s => s.CreatedAt >= f);
        if (to is { } t) query = query.Where(s => s.CreatedAt <= t);
        if (!string.IsNullOrWhiteSpace(q.Search)) query = query.Where(s => s.Number.Contains(q.Search));
        if (ctx.AllowedBranches is { } allowed) query = query.Where(s => allowed.Contains(s.BranchId));
        var page = await query.OrderByDescending(s => s.CreatedAt).ToPagedAsync(q, ct);
        return new PagedResult<SaleDto>(page.Items.Select(ToDto).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<SaleDto> GetSaleAsync(Guid id, CancellationToken ct)
    {
        var s = await db.Set<PosSale>().Include(x => x.Lines).Include(x => x.Payments).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("sale.not_found", "Sale not found.");
        ctx.EnsureBranch(s.BranchId);
        return ToDto(s);
    }

    private async Task<SaleDto?> FindByKeyAsync(string key, CancellationToken ct)
    {
        var s = await db.Set<PosSale>().Include(x => x.Lines).Include(x => x.Payments).AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
        return s is null ? null : ToDto(s);
    }

    public async Task<PosSale> LoadForReceiptAsync(Guid id, bool reprint, CancellationToken ct)
    {
        var s = await db.Set<PosSale>().Include(x => x.Lines).Include(x => x.Payments).SingleOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("sale.not_found", "Sale not found.");
        ctx.EnsureBranch(s.BranchId);
        if (reprint)
        {
            s.ReprintCount++;
            audit.Write("pos.receipt_reprinted", nameof(PosSale), s.Id.ToString(), new { s.Number, s.ReprintCount });
            await db.SaveChangesAsync(ct);
        }
        return s;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];

    public static ShiftDto ToDto(Shift s) => new(s.Id, s.RegisterId, s.BranchId, s.OpenedBy, s.OpenedAt, s.OpeningFloat, s.Status.ToString(), s.ClosedAt, s.ClosedBy,
        s.CountedCash, s.ExpectedCash, s.Variance, s.Notes);

    public static SaleDto ToDto(PosSale s) => new(s.Id, s.Number, s.Status.ToString(), s.IsReturn, s.ReturnOfSaleId, s.BranchId, s.RegisterId, s.ShiftId, s.CustomerId,
        s.CashierId, s.Subtotal, s.DiscountTotal, s.TaxTotal, s.Total, s.Paid, s.Change, s.Rounding, s.OnAccount,
        s.Lines.OrderBy(l => l.LineNo).Select(l => new SaleLineDto(l.Id, l.ProductId, l.Sku, l.Description, l.Quantity, l.UnitPrice, l.DiscountAmount, l.TaxRatePercent,
            l.TaxAmount, l.NetAmount, l.LineTotal, l.ReturnedQuantity)).ToList(),
        s.Payments.Select(p => new PaymentDto(p.Method.ToString(), p.Amount, p.Reference)).ToList(), s.CreatedAt, s.ClientCreatedAt, s.Offline, s.Notes);
}

public sealed class PosCustomerHistory(DbContext db) : ICustomerHistoryProvider
{
    public async Task<IReadOnlyList<CustomerPurchaseDto>> PurchasesAsync(Guid customerId, int take, CancellationToken ct) =>
        await db.Set<PosSale>().Where(s => s.CustomerId == customerId && s.Status == SaleStatus.Completed && !s.IsVoidReversal)
            .OrderByDescending(s => s.CreatedAt).Take(take)
            .Select(s => new CustomerPurchaseDto("pos", s.Id, s.Number, s.CreatedAt, s.Total)).ToListAsync(ct);
}
