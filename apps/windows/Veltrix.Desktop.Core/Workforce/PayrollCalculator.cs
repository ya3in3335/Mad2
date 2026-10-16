using System.Text.Json;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Erp;

namespace Veltrix.Desktop.Core.Workforce;

public sealed record TaxBracket(decimal From, decimal? To, decimal RatePercent);

/// <summary>
/// Pure payroll preparation for one employee and period: base salary (monthly or hourly), overtime tiers, rest-day / holiday multipliers,
/// night premium, absence and late adjustments, configurable pay items, then the user-entered statutory rules (none are shipped).
/// </summary>
public static class PayrollCalculator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static decimal HourlyRate(LocalEmployee e, PayrollPolicy p) =>
        e.PayBasis == "Hourly" ? e.HourlyRate : p.StandardMonthlyHours > 0 ? e.Salary / p.StandardMonthlyHours : 0;

    public static WfPayrollPrepLine Compute(LocalEmployee e, IReadOnlyList<DayResult> days, PayrollPolicy p, IEnumerable<WfPayItem> items,
        IEnumerable<WfStatutoryRule> rules, DateOnly periodStart, DateOnly periodEnd)
    {
        var hourly = e.PayBasis == "Hourly";
        var rate = HourlyRate(e, p);
        var perMinute = rate / 60m;
        var line = new WfPayrollPrepLine { Id = Guid.NewGuid(), EmployeeId = e.Id, EmployeeName = e.Name };

        decimal ot = 0, night = 0;
        int otMinutes = 0, worked = 0, regular = 0, late = 0;
        foreach (var d in days)
        {
            worked += d.WorkedMinutes;
            late += d.LateMinutes;
            otMinutes += d.OvertimeMinutes;
            if (d.IsHoliday) ot += d.OvertimeMinutes * perMinute * p.HolidayMultiplier;
            else if (d.IsRestDay) ot += d.OvertimeMinutes * perMinute * p.RestDayMultiplier;
            else
            {
                var t1 = Math.Min(d.OvertimeMinutes, p.Tier1MinutesPerDay);
                ot += t1 * perMinute * p.Tier1Multiplier + (d.OvertimeMinutes - t1) * perMinute * p.Tier2Multiplier;
                regular += Math.Max(0, d.WorkedMinutes - d.OvertimeMinutes);
            }
            if (d.Leave is { IsPaid: true } && d.WorkedMinutes == 0 && d.IsScheduledWorkday) regular += d.ScheduledMinutes;
            night += d.NightMinutes * perMinute * p.NightPremiumPercent / 100m;
        }
        line.WorkedMinutes = worked;
        line.OvertimeMinutes = otMinutes;
        line.OvertimeAmount = Money.Round(ot);
        line.NightPremium = Money.Round(night);
        line.LateMinutes = late;
        line.BaseSalary = Money.Round(hourly ? regular * perMinute : e.Salary);

        // Absences: scheduled workdays without punches and without paid leave (unpaid leave counts as absence for pay).
        var absent = days.Where(d => d.IsScheduledWorkday && (d.Status == DayStatuses.Absent || d.Status == DayStatuses.Leave && d.Leave is { IsPaid: false })).ToList();
        line.AbsenceDays = absent.Count;
        if (!hourly)
        {
            var planned = days.Count(d => d.IsScheduledWorkday);
            line.AbsenceDeduction = Money.Round(p.AbsenceMethod switch
            {
                AbsenceDeductionMethod.PerWorkingDay => planned > 0 ? e.Salary / planned * absent.Count : 0,
                AbsenceDeductionMethod.PerCalendarDay30 => e.Salary / 30m * absent.Count,
                AbsenceDeductionMethod.PerHour => absent.Sum(d => d.ScheduledMinutes) * perMinute,
                _ => 0,
            });
            if (p.LateMethod == LateAdjustmentMethod.PerMinute)
                line.LateDeduction = Money.Round(Math.Max(0, late - p.LateToleranceMinutesPerMonth) * perMinute);
        }

        foreach (var i in items.Where(i => i.IsActive && (i.EmployeeId is null || i.EmployeeId == e.Id) && i.From <= periodEnd && (i.To is null || i.To >= periodStart)))
        {
            var amount = i.IsPercentOfBase ? line.BaseSalary * i.Amount / 100m : i.Amount;
            switch (i.Kind)
            {
                case PayItemKinds.Bonus: line.Bonuses += Money.Round(amount); break;
                case PayItemKinds.Allowance: line.Allowances += Money.Round(amount); break;
                case PayItemKinds.ShiftPremium:
                    var count = days.Count(d => d.WorkedMinutes > 0 && (i.TemplateId is null || d.ShiftId == i.TemplateId));
                    line.ShiftPremiums += Money.Round(amount * count);
                    break;
                case PayItemKinds.Deduction: line.OtherDeductions += Money.Round(amount); break;
            }
        }

        line.Gross = Math.Max(0, Money.Round(line.BaseSalary + line.OvertimeAmount + line.NightPremium + line.Bonuses + line.Allowances + line.ShiftPremiums
            - line.AbsenceDeduction - line.LateDeduction - line.OtherDeductions));
        line.NetBeforeStatutory = line.Gross;
        (line.StatutoryEmployee, line.StatutoryEmployer) = Statutory(line.Gross, rules);
        line.Net = line.Gross - line.StatutoryEmployee;
        return line;
    }

    /// <summary>Applies the enabled user-entered rules in order. Returns (employee withholding, employer charge).</summary>
    public static (decimal Employee, decimal Employer) Statutory(decimal gross, IEnumerable<WfStatutoryRule> rules)
    {
        decimal employee = 0, employer = 0;
        foreach (var r in rules.Where(r => r.IsEnabled).OrderBy(r => r.Order))
        {
            var b = r.BaseAfterPreviousRules ? gross - employee : gross;
            var brackets = string.IsNullOrWhiteSpace(r.BracketsJson) ? null : JsonSerializer.Deserialize<List<TaxBracket>>(r.BracketsJson, Json);
            var emp = brackets is { Count: > 0 } ? Progressive(b, brackets) : b * r.EmployeeRatePercent / 100m;
            employee += Money.Round(emp);
            employer += Money.Round(b * r.EmployerRatePercent / 100m);
        }
        return (employee, employer);
    }

    public static decimal Progressive(decimal amount, IEnumerable<TaxBracket> brackets)
    {
        decimal total = 0;
        foreach (var br in brackets.OrderBy(b => b.From))
        {
            if (amount <= br.From) break;
            var top = br.To is { } t ? Math.Min(amount, t) : amount;
            total += (top - br.From) * br.RatePercent / 100m;
        }
        return total;
    }
}
