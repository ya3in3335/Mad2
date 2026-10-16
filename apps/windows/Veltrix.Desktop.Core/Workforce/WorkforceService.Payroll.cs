using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Erp;

namespace Veltrix.Desktop.Core.Workforce;

public sealed record PayItemInput(string Code, string Name, string Kind, decimal Amount, DateOnly From, DateOnly? To = null, Guid? EmployeeId = null,
    bool IsPercentOfBase = false, Guid? TemplateId = null, bool IsActive = true);

public sealed record StatutoryRuleInput(string Code, string Name, int Order, decimal EmployeeRatePercent, decimal EmployerRatePercent = 0,
    IReadOnlyList<TaxBracket>? Brackets = null, bool BaseAfterPreviousRules = false, bool IsEnabled = false, string? Notes = null);

public sealed record PayrollPrepView(WfPayrollPrep Prep, IReadOnlyList<WfPayrollPrepLine> Lines, string StatutoryNotice);

public sealed partial class WorkforceService
{
    public Task<WfPayItem> UpsertPayItemAsync(WorkforceActor actor, Guid? id, PayItemInput input, CancellationToken ct = default) => InTransactionAsync(async (db, _) =>
    {
        AuthorizeGlobal(actor, WorkforcePermissions.PayrollConfigure);
        Require(!string.IsNullOrWhiteSpace(input.Code) && !string.IsNullOrWhiteSpace(input.Name), "wf.err.nameRequired");
        Require(input.Kind is PayItemKinds.Bonus or PayItemKinds.Allowance or PayItemKinds.ShiftPremium or PayItemKinds.Deduction, "wf.err.payItemKind");
        Require(input.Amount >= 0 && (input.To is null || input.To >= input.From), "wf.err.payItem");
        var i = id is { } x ? await db.WfPayItems.FindAsync([x], ct) ?? throw new InvalidOperationException("wf.err.unknown") : null;
        if (i is null) db.WfPayItems.Add(i = new WfPayItem { Id = Guid.NewGuid() });
        (i.Code, i.Name, i.Kind, i.Amount, i.From, i.To, i.EmployeeId, i.IsPercentOfBase, i.TemplateId, i.IsActive) =
            (input.Code.Trim(), input.Name.Trim(), input.Kind, input.Amount, input.From, input.To, input.EmployeeId, input.IsPercentOfBase, input.TemplateId, input.IsActive);
        return i;
    }, ct);

    /// <summary>User-entered statutory rule. See <see cref="StatutoryNotice.Text"/>: nothing is shipped; verify with official sources before enabling.</summary>
    public Task<WfStatutoryRule> UpsertStatutoryRuleAsync(WorkforceActor actor, Guid? id, StatutoryRuleInput input, CancellationToken ct = default) =>
        InTransactionAsync(async (db, _) =>
        {
            AuthorizeGlobal(actor, WorkforcePermissions.PayrollConfigure);
            Require(!string.IsNullOrWhiteSpace(input.Code) && !string.IsNullOrWhiteSpace(input.Name), "wf.err.nameRequired");
            Require(input.EmployeeRatePercent is >= 0 and <= 100 && input.EmployerRatePercent is >= 0 and <= 100, "wf.err.rate");
            Require(input.Brackets is null || input.Brackets.All(b => b.RatePercent is >= 0 and <= 100 && (b.To is null || b.To > b.From)), "wf.err.brackets");
            var r = id is { } x ? await db.WfStatutoryRules.FindAsync([x], ct) ?? throw new InvalidOperationException("wf.err.unknown") : null;
            if (r is null) db.WfStatutoryRules.Add(r = new WfStatutoryRule { Id = Guid.NewGuid() });
            r.Code = input.Code.Trim();
            r.Name = input.Name.Trim();
            r.Order = input.Order;
            r.EmployeeRatePercent = input.EmployeeRatePercent;
            r.EmployerRatePercent = input.EmployerRatePercent;
            r.BracketsJson = input.Brackets is { Count: > 0 } b ? System.Text.Json.JsonSerializer.Serialize(b, Json) : null;
            r.BaseAfterPreviousRules = input.BaseAfterPreviousRules;
            r.IsEnabled = input.IsEnabled;
            r.Notes = O(input.Notes);
            return r;
        }, ct);

    public async Task<IReadOnlyList<WfStatutoryRule>> StatutoryRulesAsync(WorkforceActor actor, CancellationToken ct = default)
    {
        Authorize(actor, WorkforcePermissions.PayrollView);
        await using var db = dbFactory();
        return (await db.WfStatutoryRules.AsNoTracking().ToListAsync(ct)).OrderBy(r => r.Order).ToList();
    }

    /// <summary>Prepares (or re-prepares while still a draft) the payroll of a month from attendance, leave, pay items and statutory rules.</summary>
    public Task<PayrollPrepView> PreparePayrollAsync(WorkforceActor actor, int year, int month, CancellationToken ct = default) => InTransactionAsync(async (db, now) =>
    {
        AuthorizeGlobal(actor, WorkforcePermissions.PayrollPrepare);
        Require(year is >= 2000 and <= 2100 && month is >= 1 and <= 12, "erp.err.period");
        var existing = await db.WfPayrollPreps.Where(p => p.Year == year && p.Month == month).ToListAsync(ct);
        Require(existing.All(p => p.Status == "Draft"), "erp.err.payrollExists");
        foreach (var p in existing)
        {
            db.WfPayrollPrepLines.RemoveRange(db.WfPayrollPrepLines.Where(l => l.PrepId == p.Id));
            db.WfPayrollPreps.Remove(p);
        }
        var (from, to) = ShiftPlanner.Month(year, month);
        var policy = WorkforcePolicy.FromJson(await db.GetValueAsync(WorkforcePolicy.Key, ct));
        var employees = (await db.Employees.AsNoTracking().ToListAsync(ct))
            .Where(e => e.HireDate <= to && (e.TerminationDate is null || e.TerminationDate >= from) && (e.IsActive || e.TerminationDate is not null)
                && (e.Salary > 0 || e.HourlyRate > 0)).OrderBy(e => e.Name).ToList();
        var days = (await ComputeCoreAsync(db, employees, from, to, ct)).ToLookup(d => d.EmployeeId);
        var items = await db.WfPayItems.AsNoTracking().ToListAsync(ct);
        var rules = await db.WfStatutoryRules.AsNoTracking().ToListAsync(ct);
        var prep = new WfPayrollPrep { Id = Guid.NewGuid(), Year = year, Month = month, CreatedAt = now, CreatedBy = actor.UserName };
        db.WfPayrollPreps.Add(prep);
        var lines = new List<WfPayrollPrepLine>();
        foreach (var e in employees)
        {
            var line = PayrollCalculator.Compute(e, days[e.Id].ToList(), policy.Payroll, items, rules, from, to);
            line.PrepId = prep.Id;
            lines.Add(line);
        }
        db.WfPayrollPrepLines.AddRange(lines);
        return new PayrollPrepView(prep, lines, StatutoryNotice.Text);
    }, ct);

    public async Task<PayrollPrepView?> PayrollPrepAsync(WorkforceActor actor, int year, int month, CancellationToken ct = default)
    {
        Authorize(actor, WorkforcePermissions.PayrollView);
        await using var db = dbFactory();
        var p = await db.WfPayrollPreps.AsNoTracking().FirstOrDefaultAsync(x => x.Year == year && x.Month == month, ct);
        if (p is null) return null;
        return new PayrollPrepView(p, (await db.WfPayrollPrepLines.AsNoTracking().Where(l => l.PrepId == p.Id).ToListAsync(ct)).OrderBy(l => l.EmployeeName).ToList(),
            StatutoryNotice.Text);
    }

    /// <summary>Posts a prepared payroll through the existing ERP payroll run (Dr 631 / Cr 421, 425 advances, 431 statutory).</summary>
    public async Task<LocalPayrollRun> PostPayrollPrepAsync(WorkforceActor actor, int year, int month, CancellationToken ct = default)
    {
        AuthorizeGlobal(actor, WorkforcePermissions.PayrollPost);
        PayrollPrepView view;
        await using (var db = dbFactory())
        {
            var p = await db.WfPayrollPreps.AsNoTracking().FirstOrDefaultAsync(x => x.Year == year && x.Month == month, ct) ?? throw new InvalidOperationException("wf.err.noPrep");
            Require(p.Status == "Draft", "erp.err.payrollExists");
            view = new PayrollPrepView(p, await db.WfPayrollPrepLines.AsNoTracking().Where(l => l.PrepId == p.Id).ToListAsync(ct), StatutoryNotice.Text);
        }
        var amounts = view.Lines.Where(l => l.Gross > 0).ToDictionary(l => l.EmployeeId, l => (l.Gross, l.StatutoryEmployee));
        var run = await new ErpService(dbFactory, time).RunPayrollAsync(year, month, amounts, ct);
        await using (var db = dbFactory())
        {
            var p = await db.WfPayrollPreps.FirstAsync(x => x.Id == view.Prep.Id, ct);
            p.Status = "Posted";
            p.PayrollRunId = run.Id;
            await db.SaveChangesAsync(ct);
        }
        return run;
    }
}
