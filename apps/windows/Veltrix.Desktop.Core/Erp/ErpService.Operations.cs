using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;

namespace Veltrix.Desktop.Core.Erp;

public sealed record EmployeeInput(string Name, string? Role, string? Phone, decimal Salary, DateOnly HireDate, bool IsActive = true);
public sealed record AdvanceRow(LocalSalaryAdvance Advance, string EmployeeName);
public sealed record ReturnableLine(LocalSaleLine Line, decimal Returned)
{
    public decimal Returnable => Line.Quantity - Returned;
}
public sealed record SaleForReturn(LocalSale Sale, string? CustomerName, IReadOnlyList<ReturnableLine> Lines);
public sealed record SaleReturnLineInput(Guid SaleLineId, decimal Quantity);
public sealed record SaleReturnInput(Guid SaleId, IReadOnlyList<SaleReturnLineInput> Lines, string Method, DateOnly Date, string? Reason = null);
public sealed record CashRegisterSummary(LocalShift? Shift, decimal OpeningFloat, decimal CashSales, decimal CashIn, decimal CashOut, decimal Expected, decimal CashAccountBalance);

/// <summary>Methods a sales return can be refunded with: cash, bank, or credited to the customer's account.</summary>
public static class RefundMethods
{
    public const string OnAccount = "OnAccount";
    public static readonly IReadOnlyList<string> All = [PayMethods.Cash, PayMethods.Bank, OnAccount];
}

public sealed partial class ErpService
{
    // ------------------------------------------------------------------ employees & payroll (internal calculation, no tax / social-security computation)

    public async Task<IReadOnlyList<LocalEmployee>> EmployeesAsync(string? search = null, bool activeOnly = false, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var s = search?.Trim() ?? "";
        return (await db.Employees.AsNoTracking().ToListAsync(ct))
            .Where(e => (!activeOnly || e.IsActive) && (s.Length == 0 || Matches(e.Name, s) || Matches(e.Code, s) || Matches(e.Role, s) || Matches(e.Phone, s)))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public Task<LocalEmployee> UpsertEmployeeAsync(Guid? id, EmployeeInput input, CancellationToken ct = default) => InTransactionAsync(async (db, _) =>
    {
        Require(!string.IsNullOrWhiteSpace(input.Name), "erp.err.nameRequired");
        Require(input.Salary >= 0, "erp.err.salary");
        var e = id is { } existing ? await db.Employees.FindAsync([existing], ct) ?? throw new InvalidOperationException("erp.err.unknownEmployee") : null;
        if (e is null)
            db.Employees.Add(e = new LocalEmployee { Id = Guid.NewGuid(), Code = "E" + (await db.Employees.CountAsync(ct) + 1).ToString("0000", CultureInfo.InvariantCulture) });
        e.Name = input.Name.Trim();
        e.Role = O(input.Role);
        e.Phone = O(input.Phone);
        e.Salary = Money.Round(input.Salary);
        e.HireDate = input.HireDate;
        e.IsActive = input.IsActive;
        return e;
    }, ct);

    /// <summary>Salary advance: Dr 425 / Cr 530 or 512. Deducted by the next payroll run.</summary>
    public Task<LocalSalaryAdvance> GiveAdvanceAsync(Guid employeeId, decimal amount, string method, DateOnly date, CancellationToken ct = default) =>
        InTransactionAsync(async (db, now) =>
        {
            Require(amount > 0, "erp.err.amountPositive");
            Require(PayMethods.All.Contains(method), "erp.err.method");
            var e = await db.Employees.FindAsync([employeeId], ct) ?? throw new InvalidOperationException("erp.err.unknownEmployee");
            var a = new LocalSalaryAdvance { Id = Guid.NewGuid(), Number = await Ledger.NextNumberAsync(db, "ADV", ct), EmployeeId = e.Id, Date = date, Amount = Money.Round(amount), Method = method };
            var b = new PostingBuilder().Debit(AccountRoles.EmployeeAdvances, a.Amount, e.Name, PartyKinds.Employee, e.Id).Credit(AccountRoles.ForMethod(method), a.Amount, e.Name);
            a.JournalEntryId = (await Ledger.PostAsync(db, date, $"Salary advance {a.Number} — {e.Name}", JournalSources.SalaryAdvance, a.Id, b, now, ct))?.Id;
            if (method == PayMethods.Cash) await Ledger.RecordCashAsync(db, date, now, -a.Amount, $"{a.Number} {e.Name}", "Advance", a.Id, a.JournalEntryId, "425", ct);
            db.SalaryAdvances.Add(a);
            return a;
        }, ct);

    public async Task<IReadOnlyList<AdvanceRow>> AdvancesAsync(Guid? employeeId = null, bool outstandingOnly = false, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var names = (await db.Employees.AsNoTracking().ToListAsync(ct)).ToDictionary(e => e.Id, e => e.Name);
        return (await db.SalaryAdvances.AsNoTracking().ToListAsync(ct))
            .Where(a => (employeeId is null || a.EmployeeId == employeeId) && (!outstandingOnly || a.PayrollRunId is null))
            .OrderByDescending(a => a.Date).ThenByDescending(a => a.Number, StringComparer.Ordinal)
            .Select(a => new AdvanceRow(a, names.GetValueOrDefault(a.EmployeeId, "?"))).ToList();
    }

    /// <summary>
    /// Monthly payroll (internal calculation): gross = monthly salary of each active employee hired by the month end; outstanding advances are
    /// deducted (oldest first, never more than the gross); net = gross − advances. Posts Dr 631 gross / Cr 421 net, Cr 425 advances.
    /// </summary>
    public Task<LocalPayrollRun> RunPayrollAsync(int year, int month, CancellationToken ct = default) => InTransactionAsync(async (db, now) =>
    {
        Require(year is >= 2000 and <= 2100 && month is >= 1 and <= 12, "erp.err.period");
        Require(!await db.PayrollRuns.AnyAsync(r => r.Year == year && r.Month == month, ct), "erp.err.payrollExists");
        var end = new DateOnly(year, month, 1).AddMonths(1).AddDays(-1);
        var employees = (await db.Employees.Where(e => e.IsActive).ToListAsync(ct)).Where(e => e.HireDate <= end && e.Salary > 0).OrderBy(e => e.Name).ToList();
        Require(employees.Count > 0, "erp.err.noEmployees");
        var run = new LocalPayrollRun { Id = Guid.NewGuid(), Number = await Ledger.NextNumberAsync(db, "PAYR", ct), Year = year, Month = month, Date = end, Status = PayrollStatuses.Posted };
        var outstanding = (await db.SalaryAdvances.Where(a => a.PayrollRunId == null).ToListAsync(ct)).Where(a => a.Date <= end).ToLookup(a => a.EmployeeId);
        var b = new PostingBuilder();
        foreach (var e in employees)
        {
            decimal deducted = 0;
            foreach (var a in outstanding[e.Id].OrderBy(a => a.Date).ThenBy(a => a.Number, StringComparer.Ordinal))
            {
                if (deducted + a.Amount > e.Salary) break;
                deducted += a.Amount;
                a.PayrollRunId = run.Id;
            }
            var slip = new LocalPayslip { Id = Guid.NewGuid(), RunId = run.Id, EmployeeId = e.Id, EmployeeName = e.Name, Gross = e.Salary, Advances = deducted, Net = e.Salary - deducted };
            db.Payslips.Add(slip);
            run.TotalGross += slip.Gross;
            run.TotalAdvances += slip.Advances;
            run.TotalNet += slip.Net;
            b.Credit(AccountRoles.SalariesPayable, slip.Net, e.Name, PartyKinds.Employee, e.Id).Credit(AccountRoles.EmployeeAdvances, slip.Advances, e.Name, PartyKinds.Employee, e.Id);
        }
        b.Debit(AccountRoles.Salaries, run.TotalGross, $"{year:0000}-{month:00}");
        run.JournalEntryId = (await Ledger.PostAsync(db, end, $"Payroll {run.Number} {year:0000}-{month:00}", JournalSources.Payroll, run.Id, b, now, ct))?.Id;
        db.PayrollRuns.Add(run);
        return run;
    }, ct);

    /// <summary>Pays the net salaries of a run: Dr 421 / Cr 530 or 512.</summary>
    public Task<LocalPayrollRun> PayPayrollAsync(Guid runId, string method, DateOnly date, CancellationToken ct = default) => InTransactionAsync(async (db, now) =>
    {
        Require(PayMethods.All.Contains(method), "erp.err.method");
        var run = await db.PayrollRuns.FindAsync([runId], ct) ?? throw new InvalidOperationException("erp.err.unknownDocument");
        Require(run.Status == PayrollStatuses.Posted, "erp.err.alreadyPaid");
        var b = new PostingBuilder().Debit(AccountRoles.SalariesPayable, run.TotalNet, run.Number).Credit(AccountRoles.ForMethod(method), run.TotalNet, run.Number);
        run.PaymentEntryId = (await Ledger.PostAsync(db, date, $"Salaries paid {run.Number}", JournalSources.PayrollPayment, run.Id, b, now, ct))?.Id;
        if (method == PayMethods.Cash && run.TotalNet > 0)
            await Ledger.RecordCashAsync(db, date, now, -run.TotalNet, $"{run.Number} salaries", "Payroll", run.Id, run.PaymentEntryId, "421", ct);
        run.Status = PayrollStatuses.Paid;
        run.PaidMethod = method;
        run.PaidDate = date;
        return run;
    }, ct);

    public async Task<IReadOnlyList<LocalPayrollRun>> PayrollRunsAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory();
        return (await db.PayrollRuns.AsNoTracking().ToListAsync(ct)).OrderByDescending(r => r.Year).ThenByDescending(r => r.Month).ToList();
    }

    public async Task<IReadOnlyList<LocalPayslip>> PayslipsAsync(Guid runId, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        return (await db.Payslips.AsNoTracking().Where(p => p.RunId == runId).ToListAsync(ct)).OrderBy(p => p.EmployeeName).ToList();
    }

    // ------------------------------------------------------------------ sales returns

    public async Task<SaleForReturn> FindSaleForReturnAsync(string number, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var n = number.Trim();
        var sale = await db.Sales.AsNoTracking().FirstOrDefaultAsync(s => s.ClientNumber == n, ct) ?? throw new InvalidOperationException("erp.err.unknownSale");
        var customer = sale.CustomerId is { } cid ? await db.Customers.FindAsync([cid], ct) : null;
        var lines = await db.SaleLines.AsNoTracking().Where(l => l.SaleId == sale.Id).ToListAsync(ct);
        var returned = await ReturnedQuantitiesAsync(db, sale.Id, ct);
        return new SaleForReturn(sale, customer?.Name, lines.Select(l => new ReturnableLine(l, returned.GetValueOrDefault(l.Id))).ToList());
    }

    private static async Task<Dictionary<Guid, decimal>> ReturnedQuantitiesAsync(LocalDb db, Guid saleId, CancellationToken ct)
    {
        var ids = await db.SaleReturns.Where(r => r.SaleId == saleId).Select(r => r.Id).ToListAsync(ct);
        return (await db.SaleReturnLines.AsNoTracking().Where(l => ids.Contains(l.ReturnId)).ToListAsync(ct))
            .GroupBy(l => l.SaleLineId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
    }

    /// <summary>Customer return (port of the server credit-note rule): goods back to stock at the sale's unit cost;
    /// Dr 709 returns + Dr 4457 VAT / Cr refund (530, 512 or 411); Dr 30 / Cr 600 for the cost.</summary>
    public async Task<LocalSaleReturn> CreateSaleReturnAsync(SaleReturnInput input, CancellationToken ct = default)
    {
        Require(input.Lines is { Count: > 0 } && input.Lines.All(l => l.Quantity > 0), "erp.err.lineInvalid");
        Require(RefundMethods.All.Contains(input.Method), "erp.err.method");
        var wid = await WarehouseIdAsync(ct);
        return await InTransactionAsync(async (db, now) =>
        {
            var sale = await db.Sales.FindAsync([input.SaleId], ct) ?? throw new InvalidOperationException("erp.err.unknownSale");
            var lines = (await db.SaleLines.Where(l => l.SaleId == sale.Id).ToListAsync(ct)).ToDictionary(l => l.Id);
            var returned = await ReturnedQuantitiesAsync(db, sale.Id, ct);
            LocalCustomer? customer = sale.CustomerId is { } cid ? await db.Customers.FindAsync([cid], ct) : null;
            Require(input.Method != RefundMethods.OnAccount || customer is not null, "erp.err.customerRequired");
            var ret = new LocalSaleReturn
            {
                Id = Guid.NewGuid(), Number = await Ledger.NextNumberAsync(db, "SR", ct), SaleId = sale.Id, CustomerId = sale.CustomerId, Date = input.Date,
                Method = input.Method, Reason = O(input.Reason), CreatedAt = now,
            };
            foreach (var g in input.Lines.GroupBy(l => l.SaleLineId))
            {
                var line = lines.GetValueOrDefault(g.Key) ?? throw new InvalidOperationException("erp.err.lineInvalid");
                var qty = g.Sum(l => l.Quantity);
                Require(qty <= line.Quantity - returned.GetValueOrDefault(line.Id), "erp.err.returnExceeds");
                var ratio = qty / line.Quantity;
                var net = Money.Round(line.Net * ratio);
                var tax = Money.Round(line.Tax * ratio);
                var product = await db.Products.FindAsync([line.ProductId], ct);
                decimal cost = 0;
                if (product is { TrackStock: true })
                {
                    await Costing.ReceiveAsync(db, wid, product, qty, line.UnitCost, now, ct);
                    cost = Money.Round(qty * line.UnitCost);
                }
                db.SaleReturnLines.Add(new LocalSaleReturnLine { Id = Guid.NewGuid(), ReturnId = ret.Id, SaleLineId = line.Id, ProductId = line.ProductId, Name = line.Name, Quantity = qty, Net = net, Tax = tax, UnitCost = line.UnitCost });
                ret.Net += net;
                ret.Tax += tax;
                ret.Cost += cost;
            }
            ret.Total = ret.Net + ret.Tax;
            var refundRole = input.Method == RefundMethods.OnAccount ? AccountRoles.AccountsReceivable : AccountRoles.ForMethod(input.Method);
            var b = new PostingBuilder().Debit(AccountRoles.SalesReturns, ret.Net).Debit(AccountRoles.VatCollected, ret.Tax)
                .Credit(refundRole, ret.Total, $"Return {ret.Number}", input.Method == RefundMethods.OnAccount ? PartyKinds.Customer : null, input.Method == RefundMethods.OnAccount ? customer!.Id : null)
                .Debit(AccountRoles.Inventory, ret.Cost).Credit(AccountRoles.CostOfGoodsSold, ret.Cost);
            ret.JournalEntryId = (await Ledger.PostAsync(db, input.Date, $"Sales return {ret.Number} ({sale.ClientNumber})", JournalSources.SalesReturn, ret.Id, b, now, ct))?.Id;
            if (input.Method == RefundMethods.OnAccount) customer!.Balance -= ret.Total;
            if (input.Method == PayMethods.Cash) await Ledger.RecordCashAsync(db, input.Date, now, -ret.Total, $"{ret.Number} ({sale.ClientNumber})", "SalesReturn", ret.Id, ret.JournalEntryId, "709", ct);
            db.SaleReturns.Add(ret);
            return ret;
        }, ct);
    }

    public async Task<IReadOnlyList<LocalSaleReturn>> SaleReturnsAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        return (await db.SaleReturns.AsNoTracking().Where(r => r.Date >= from && r.Date <= to).ToListAsync(ct)).OrderByDescending(r => r.CreatedAt).ToList();
    }

    // ------------------------------------------------------------------ cash book & register

    /// <summary>Manual cash in (amount &gt; 0) or out (amount &lt; 0) against a counter account (e.g. 512 bank deposit, 101 owner contribution).</summary>
    public Task<LocalCashMovement> RecordCashMovementAsync(decimal amount, string reason, string counterAccountCode, DateOnly date, CancellationToken ct = default) =>
        InTransactionAsync(async (db, now) =>
        {
            Require(amount != 0, "erp.err.amountPositive");
            Require(!string.IsNullOrWhiteSpace(reason), "erp.err.descriptionRequired");
            var counter = await db.Accounts.FindAsync([counterAccountCode.Trim()], ct);
            Require(counter is { IsPostable: true, IsActive: true } && counter.Role != AccountRoles.Cash, "erp.err.accountInvalid");
            var b = new PostingBuilder().Debit(AccountRoles.Cash, amount, reason).CreditAccount(counter!.Code, amount, reason);
            var je = await Ledger.PostAsync(db, date, $"Cash {(amount > 0 ? "in" : "out")}: {reason.Trim()}", JournalSources.Cash, null, b, now, ct);
            var m = await Ledger.RecordCashAsync(db, date, now, amount, reason.Trim(), "Manual", null, je?.Id, counter.Code, ct);
            if (je is not null) je.SourceId = m.Id;
            return m;
        }, ct);

    public async Task<IReadOnlyList<LocalCashMovement>> CashMovementsAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        return (await db.CashMovements.AsNoTracking().Where(m => m.Date >= from && m.Date <= to).ToListAsync(ct)).OrderByDescending(m => m.CreatedAt).ToList();
    }

    /// <summary>Cash the drawer should hold: opening float + cash sales (net of change) + cash-book movements of the shift.</summary>
    public static async Task<decimal> ExpectedCashAsync(LocalDb db, LocalShift shift, CancellationToken ct)
    {
        var sales = await db.Sales.AsNoTracking().Where(s => s.ShiftId == shift.Id).ToListAsync(ct);
        var moves = await db.CashMovements.AsNoTracking().Where(m => m.ShiftId == shift.Id && m.Source != "ShiftVariance").ToListAsync(ct);
        return shift.OpeningFloat + sales.Sum(s => s.CashReceived - s.Change) + moves.Sum(m => m.Amount);
    }

    /// <summary>Current register state: open shift (if any), its cash sales and movements, the expected drawer and the cash account balance.</summary>
    public async Task<CashRegisterSummary> CashRegisterAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var shift = (await db.Shifts.AsNoTracking().Where(s => s.Status == "Open").ToListAsync(ct)).OrderByDescending(s => s.OpenedAt).FirstOrDefault();
        var cashLines = await db.JournalLines.AsNoTracking().Where(l => l.AccountCode == "530").ToListAsync(ct);
        var balance = cashLines.Sum(l => l.Debit - l.Credit);
        if (shift is null) return new CashRegisterSummary(null, 0, 0, 0, 0, 0, balance);
        var sales = await db.Sales.AsNoTracking().Where(s => s.ShiftId == shift.Id).ToListAsync(ct);
        var moves = await db.CashMovements.AsNoTracking().Where(m => m.ShiftId == shift.Id && m.Source != "ShiftVariance").ToListAsync(ct);
        var cashSales = sales.Sum(s => s.CashReceived - s.Change);
        var cin = moves.Where(m => m.Amount > 0).Sum(m => m.Amount);
        var cout = -moves.Where(m => m.Amount < 0).Sum(m => m.Amount);
        return new CashRegisterSummary(shift, shift.OpeningFloat, cashSales, cin, cout, shift.OpeningFloat + cashSales + cin - cout, balance);
    }
}
