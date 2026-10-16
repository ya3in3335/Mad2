using Veltrix.Desktop.Core.Data;

namespace Veltrix.Desktop.Core.Workforce;

/// <summary>An effective punch (raw event not voided) in workplace local time.</summary>
public sealed record Punch(Guid Id, string Type, DateTime Local, string Source = PunchSources.Manual);

public static class DayStatuses
{
    public const string Present = "Present";
    public const string Incomplete = "Incomplete";
    public const string Absent = "Absent";
    public const string Leave = "Leave";
    public const string Holiday = "Holiday";
    public const string Rest = "Rest";
}

/// <summary>Approved leave covering a day.</summary>
public sealed record LeaveDay(string TypeCode, bool IsPaid);

/// <summary>Computed attendance of one employee for one (shift start) day.</summary>
public sealed record DayResult(Guid EmployeeId, DateOnly Date, string Status, string? ShiftCode, Guid? ShiftId, bool IsHoliday, bool IsRestDay,
    DateTime? FirstIn, DateTime? LastOut, int ScheduledMinutes, int WorkedMinutes, int BreakMinutes, int LateMinutes, int EarlyLeaveMinutes,
    int OvertimeMinutes, int NightMinutes, int ShortfallMinutes, bool MissedPunch, IReadOnlyList<string> Anomalies, LeaveDay? Leave = null)
{
    public bool IsScheduledWorkday => !IsRestDay && !IsHoliday;
}

/// <summary>Pure attendance engine: attributes punches to shift days and computes worked time, lateness, early departure, overtime …</summary>
public static class AttendanceCalculator
{
    /// <summary>
    /// Attributes punches to (shift start) days. Each punch goes to the planned shift of D−1, D or D+1 whose window
    /// [start − early window, end + late window] contains it and whose span is nearest (0 when inside the shift); ties go to the later day for an
    /// In punch and to the earlier day otherwise. Punches near no shift stay on their calendar day. An overnight shift therefore keeps its
    /// after-midnight punches on the day it started.
    /// </summary>
    public static Dictionary<DateOnly, List<Punch>> Attribute(IEnumerable<Punch> punches, Func<DateOnly, PlannedDay> plan, AttendancePolicy policy)
    {
        var result = new Dictionary<DateOnly, List<Punch>>();
        foreach (var p in punches.OrderBy(p => p.Local))
        {
            var day = DateOnly.FromDateTime(p.Local);
            var target = day;
            double best = double.MaxValue;
            foreach (var d in new[] { day.AddDays(-1), day, day.AddDays(1) })
            {
                var pl = plan(d);
                if (pl.Start is not { } s || pl.End is not { } e) continue;
                if (p.Local < s.AddMinutes(-policy.EarlyWindowMinutes) || p.Local > e.AddMinutes(policy.LateWindowMinutes)) continue;
                var dist = p.Local < s ? (s - p.Local).TotalMinutes : p.Local > e ? (p.Local - e).TotalMinutes : 0;
                if (dist < best || (dist == best && p.Type == PunchTypes.In && d > target))
                {
                    best = dist;
                    target = d;
                }
            }
            if (!result.TryGetValue(target, out var list)) result[target] = list = [];
            list.Add(p);
        }
        return result;
    }

    public static DayResult ComputeDay(PlannedDay plan, IReadOnlyList<Punch> punches, WorkforcePolicy policy, LeaveDay? leave = null)
    {
        var sorted = punches.OrderBy(p => p.Local).ToList();
        var anomalies = new List<string>();
        var work = new List<PlannedInterval>();
        var breaks = new List<PlannedInterval>();
        var missed = false;
        DateTime? workStart = null, breakStart = null;
        foreach (var p in sorted)
        {
            switch (p.Type)
            {
                case PunchTypes.In:
                    if (workStart is { } ws0) { missed = true; anomalies.Add("missing_out"); _ = ws0; }
                    if (breakStart is { } bs0) { breaks.Add(new(bs0, p.Local)); breakStart = null; anomalies.Add("missing_break_end"); missed = true; }
                    workStart = p.Local;
                    break;
                case PunchTypes.Out:
                    if (workStart is { } ws) { work.Add(new(ws, p.Local)); workStart = null; }
                    else if (breakStart is { } bs) { breaks.Add(new(bs, p.Local)); breakStart = null; missed = true; anomalies.Add("missing_break_end"); }
                    else { missed = true; anomalies.Add("out_without_in"); }
                    break;
                case PunchTypes.BreakStart:
                    if (workStart is { } ws2) { work.Add(new(ws2, p.Local)); workStart = null; breakStart = p.Local; }
                    else { missed = true; anomalies.Add("break_without_in"); breakStart = p.Local; }
                    break;
                case PunchTypes.BreakEnd:
                    if (breakStart is { } bs2) { breaks.Add(new(bs2, p.Local)); breakStart = null; }
                    else { missed = true; anomalies.Add("break_end_without_start"); }
                    workStart = p.Local;
                    break;
            }
        }
        if (workStart is not null) { missed = true; anomalies.Add("missing_out"); }
        if (breakStart is not null) { missed = true; anomalies.Add("missing_break_end"); }

        var shift = plan.Shift;
        var worked = work.Sum(i => i.Minutes);
        var breakMinutes = breaks.Sum(i => i.Minutes);
        if (shift is not null && breaks.Count == 0 && policy.Attendance.AutoDeductBreak && shift.BreakMinutes > 0 && worked > shift.BreakMinutes)
        {
            worked -= shift.BreakMinutes;
            breakMinutes = shift.BreakMinutes;
        }
        var firstIn = sorted.FirstOrDefault(p => p.Type == PunchTypes.In)?.Local;
        var lastOut = sorted.LastOrDefault(p => p.Type == PunchTypes.Out)?.Local;
        var scheduled = plan.ScheduledMinutes;

        int late = 0, early = 0, shortfall = 0;
        if (shift is not null && !plan.IsHoliday && sorted.Count > 0)
        {
            if (shift.IsFlexible)
            {
                var anchor = plan.Start ?? plan.Date.ToDateTime(TimeOnly.MinValue);
                if (shift.CoreStart is { } cs && firstIn is { } fi)
                {
                    var coreStart = DateOnly.FromDateTime(anchor).ToDateTime(cs);
                    if (coreStart < anchor) coreStart = coreStart.AddDays(1);
                    if ((fi - coreStart).TotalMinutes > shift.GraceInMinutes) late = (int)(fi - coreStart).TotalMinutes;
                    if (shift.CoreEnd is { } ce && lastOut is { } lo)
                    {
                        var coreEnd = DateOnly.FromDateTime(coreStart).ToDateTime(ce);
                        if (coreEnd <= coreStart) coreEnd = coreEnd.AddDays(1);
                        if ((coreEnd - lo).TotalMinutes > shift.GraceOutMinutes) early = (int)(coreEnd - lo).TotalMinutes;
                    }
                }
                shortfall = Math.Max(0, shift.RequiredMinutes - worked);
            }
            else
            {
                var segs = plan.Segments;
                for (var i = 0; i < segs.Count; i++)
                {
                    var lo = i == 0 ? DateTime.MinValue : Mid(segs[i - 1].End, segs[i].Start);
                    var hi = i == segs.Count - 1 ? DateTime.MaxValue : Mid(segs[i].End, segs[i + 1].Start);
                    var inSeg = sorted.Where(p => p.Local >= lo && p.Local < hi).ToList();
                    var segIn = inSeg.FirstOrDefault(p => p.Type == PunchTypes.In)?.Local;
                    var segOut = inSeg.LastOrDefault(p => p.Type == PunchTypes.Out)?.Local;
                    if (segIn is null && segOut is null)
                    {
                        anomalies.Add("segment_absent");
                        shortfall += segs[i].Minutes;
                        continue;
                    }
                    if (segIn is { } si && (si - segs[i].Start).TotalMinutes > shift.GraceInMinutes) late += (int)(si - segs[i].Start).TotalMinutes;
                    if (segOut is { } so && (segs[i].End - so).TotalMinutes > shift.GraceOutMinutes) early += (int)(segs[i].End - so).TotalMinutes;
                }
            }
        }

        var overtime = 0;
        if (worked > 0)
        {
            int raw;
            if (shift is null || plan.IsHoliday) raw = worked;
            else if (shift.IsFlexible) raw = worked - Math.Max(shift.RequiredMinutes, 0);
            else if (policy.Overtime.Basis == OvertimeBasis.DailyThreshold) raw = worked - policy.Overtime.DailyThresholdMinutes;
            else raw = plan.End is { } end ? work.Sum(i => Overlap(i.Start, i.End, end, DateTime.MaxValue)) : 0;
            overtime = ApplyOvertimeRules(raw, policy.Overtime);
        }

        var night = 0;
        if (work.Count > 0)
            for (var d = plan.Date.AddDays(-1); d <= plan.Date.AddDays(1); d = d.AddDays(1))
            {
                var ns = d.ToDateTime(policy.Payroll.NightStart);
                var ne = d.ToDateTime(policy.Payroll.NightEnd);
                if (ne <= ns) ne = ne.AddDays(1);
                night += work.Sum(i => Overlap(i.Start, i.End, ns, ne));
            }

        string status;
        if (sorted.Count == 0)
            status = plan.IsHoliday ? DayStatuses.Holiday
                : plan.IsRestDay ? DayStatuses.Rest
                : leave is not null ? DayStatuses.Leave
                : DayStatuses.Absent;
        else
        {
            status = missed ? DayStatuses.Incomplete : DayStatuses.Present;
            if (leave is not null) anomalies.Add("worked_during_leave");
        }
        if (status is DayStatuses.Absent or DayStatuses.Leave) shortfall = 0;

        return new DayResult(plan.EmployeeId, plan.Date, status, shift?.Code, shift?.Id, plan.IsHoliday, plan.IsRestDay, firstIn, lastOut, scheduled, worked,
            breakMinutes, late, early, overtime, night, shortfall, missed, anomalies, leave);
    }

    /// <summary>Rounding (step / mode) then the minimum: overtime below the minimum is ignored.</summary>
    public static int ApplyOvertimeRules(int rawMinutes, OvertimePolicy p)
    {
        if (rawMinutes <= 0) return 0;
        var v = rawMinutes;
        if (p.RoundingStepMinutes > 1)
        {
            var step = p.RoundingStepMinutes;
            v = p.Rounding switch
            {
                RoundingMode.Up => (v + step - 1) / step * step,
                RoundingMode.Nearest => (int)Math.Round(v / (double)step, MidpointRounding.AwayFromZero) * step,
                _ => v / step * step,
            };
        }
        return v < p.MinimumMinutes ? 0 : v;
    }

    private static DateTime Mid(DateTime a, DateTime b) => a + (b - a) / 2;

    public static int Overlap(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd)
    {
        var s = aStart > bStart ? aStart : bStart;
        var e = aEnd < bEnd ? aEnd : bEnd;
        return e > s ? (int)(e - s).TotalMinutes : 0;
    }
}
