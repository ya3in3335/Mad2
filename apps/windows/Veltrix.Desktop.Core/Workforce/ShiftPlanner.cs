using System.Globalization;
using System.Text.Json;
using Veltrix.Desktop.Core.Data;

namespace Veltrix.Desktop.Core.Workforce;

/// <summary>One working segment of a shift. End ≤ Start means the segment crosses midnight.</summary>
public sealed record ShiftSegment(TimeOnly Start, TimeOnly End);

/// <summary>Immutable view of a <see cref="WfShiftTemplate"/>.</summary>
public sealed record ShiftDefinition(Guid Id, string Code, string Name, IReadOnlyList<ShiftSegment> Segments, int BreakMinutes, int GraceInMinutes,
    int GraceOutMinutes, bool IsFlexible = false, TimeOnly? CoreStart = null, TimeOnly? CoreEnd = null, int RequiredMinutes = 0)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record SegmentDto(string Start, string End);

    public bool IsSplit => Segments.Count > 1;

    public static string SerializeSegments(IEnumerable<ShiftSegment> segments) =>
        JsonSerializer.Serialize(segments.Select(s => new SegmentDto(s.Start.ToString("HH:mm", CultureInfo.InvariantCulture), s.End.ToString("HH:mm", CultureInfo.InvariantCulture))), Json);

    public static IReadOnlyList<ShiftSegment> ParseSegments(string json) =>
        (JsonSerializer.Deserialize<List<SegmentDto>>(json, Json) ?? [])
        .Select(d => new ShiftSegment(TimeOnly.Parse(d.Start, CultureInfo.InvariantCulture), TimeOnly.Parse(d.End, CultureInfo.InvariantCulture))).ToList();

    public static ShiftDefinition From(WfShiftTemplate t) => new(t.Id, t.Code, t.Name, ParseSegments(t.SegmentsJson), t.BreakMinutes, t.GraceInMinutes,
        t.GraceOutMinutes, t.IsFlexible, t.CoreStart, t.CoreEnd, t.RequiredMinutes);

    /// <summary>Segments placed on the calendar for a shift starting on <paramref name="date"/> (later segments follow earlier ones, crossing midnight as needed).</summary>
    public IReadOnlyList<PlannedInterval> Place(DateOnly date)
    {
        var list = new List<PlannedInterval>();
        DateTime? prevEnd = null;
        foreach (var s in Segments)
        {
            var start = date.ToDateTime(s.Start);
            while (prevEnd is { } p && start < p) start = start.AddDays(1);
            var end = DateOnly.FromDateTime(start).ToDateTime(s.End);
            if (end <= start) end = end.AddDays(1);
            list.Add(new PlannedInterval(start, end));
            prevEnd = end;
        }
        return list;
    }

    public bool IsOvernight(DateOnly date) => Place(date) is { Count: > 0 } p && DateOnly.FromDateTime(p[^1].End.AddTicks(-1)) > date;

    /// <summary>Scheduled paid minutes: segment minutes − unpaid break, or the required minutes for a flexible shift.</summary>
    public int ScheduledMinutes(DateOnly date) => IsFlexible && RequiredMinutes > 0
        ? RequiredMinutes
        : Math.Max(0, (int)Place(date).Sum(i => (i.End - i.Start).TotalMinutes) - BreakMinutes);
}

public sealed record PlannedInterval(DateTime Start, DateTime End)
{
    public int Minutes => (int)(End - Start).TotalMinutes;
}

/// <summary>An employee's plan for one day (local time). <see cref="Shift"/> null = rest day.</summary>
public sealed record PlannedDay(Guid EmployeeId, DateOnly Date, ShiftDefinition? Shift, IReadOnlyList<PlannedInterval> Segments, bool IsHoliday,
    string? HolidayName = null)
{
    public bool IsRestDay => Shift is null;
    public bool IsScheduledWorkday => Shift is not null && !IsHoliday;
    public DateTime? Start => Segments.Count > 0 ? Segments[0].Start : null;
    public DateTime? End => Segments.Count > 0 ? Segments[^1].End : null;
    public int ScheduledMinutes => Shift?.ScheduledMinutes(Date) ?? 0;
}

public static class ConflictKinds
{
    public const string Overlap = "overlap";
    public const string RestViolation = "rest_violation";
    public const string AssignmentOverlap = "assignment_overlap";
}

public sealed record ShiftConflict(Guid EmployeeId, DateOnly Date, string Kind, string Detail);

/// <summary>Pure shift planning: resolves assignments / rotations / overrides / holidays into daily plans and detects conflicts.</summary>
public sealed class ShiftPlanner
{
    private readonly Dictionary<Guid, ShiftDefinition> _templates;
    private readonly Dictionary<Guid, IReadOnlyList<Guid?>> _rotations;
    private readonly ILookup<Guid, WfShiftAssignment> _assignments;
    private readonly Dictionary<(Guid, DateOnly), WfShiftOverride> _overrides;
    private readonly Dictionary<DateOnly, WfHoliday> _holidays;

    public ShiftPlanner(IEnumerable<WfShiftTemplate> templates, IEnumerable<WfRotation> rotations, IEnumerable<WfShiftAssignment> assignments,
        IEnumerable<WfShiftOverride> overrides, IEnumerable<WfHoliday> holidays)
    {
        _templates = templates.ToDictionary(t => t.Id, ShiftDefinition.From);
        _rotations = rotations.ToDictionary(r => r.Id, r => ParsePattern(r.PatternJson));
        _assignments = assignments.ToLookup(a => a.EmployeeId);
        _overrides = new();
        foreach (var o in overrides) _overrides[(o.EmployeeId, o.Date)] = o;
        _holidays = new();
        foreach (var h in holidays) _holidays[h.Date] = h;
    }

    public static IReadOnlyList<Guid?> ParsePattern(string json) => JsonSerializer.Deserialize<List<Guid?>>(json) ?? [];

    public bool IsHoliday(DateOnly date) => _holidays.ContainsKey(date);

    public ShiftDefinition? Template(Guid id) => _templates.GetValueOrDefault(id);

    public PlannedDay Plan(Guid employeeId, DateOnly date)
    {
        var shift = ResolveShift(employeeId, date);
        _holidays.TryGetValue(date, out var h);
        return new PlannedDay(employeeId, date, shift, shift?.Place(date) ?? [], h is not null, h?.Name);
    }

    private ShiftDefinition? ResolveShift(Guid employeeId, DateOnly date)
    {
        if (_overrides.TryGetValue((employeeId, date), out var o)) return o.TemplateId is { } ot ? _templates.GetValueOrDefault(ot) : null;
        var a = _assignments[employeeId].Where(x => x.From <= date && (x.To is null || x.To >= date)).OrderByDescending(x => x.From).FirstOrDefault();
        if (a is null) return null;
        if (a.RotationId is { } rid && _rotations.TryGetValue(rid, out var pattern) && pattern.Count > 0)
        {
            var anchor = a.RotationAnchor ?? a.From;
            var idx = ((date.DayNumber - anchor.DayNumber) % pattern.Count + pattern.Count) % pattern.Count;
            return pattern[idx] is { } tid ? _templates.GetValueOrDefault(tid) : null;
        }
        if (a.TemplateId is not { } t) return null;
        if (a.DaysMask != 0 && (a.DaysMask & (1 << (int)date.DayOfWeek)) == 0) return null;
        return _templates.GetValueOrDefault(t);
    }

    public IReadOnlyList<PlannedDay> Calendar(IEnumerable<Guid> employeeIds, DateOnly from, DateOnly to)
    {
        var list = new List<PlannedDay>();
        foreach (var e in employeeIds)
            for (var d = from; d <= to; d = d.AddDays(1)) list.Add(Plan(e, d));
        return list;
    }

    /// <summary>Monday-based week containing <paramref name="date"/>.</summary>
    public static (DateOnly From, DateOnly To) Week(DateOnly date, DayOfWeek firstDay = DayOfWeek.Monday)
    {
        var from = date.AddDays(-(((int)date.DayOfWeek - (int)firstDay + 7) % 7));
        return (from, from.AddDays(6));
    }

    public static (DateOnly From, DateOnly To) Month(int year, int month) => (new DateOnly(year, month, 1), new DateOnly(year, month, 1).AddMonths(1).AddDays(-1));

    /// <summary>Overlapping shifts and rest violations (gap between consecutive shifts below <paramref name="minRestHours"/>).</summary>
    public IReadOnlyList<ShiftConflict> DetectConflicts(Guid employeeId, DateOnly from, DateOnly to, decimal minRestHours)
    {
        var result = new List<ShiftConflict>();
        PlannedDay? prev = null;
        for (var d = from.AddDays(-1); d <= to; d = d.AddDays(1))
        {
            var day = Plan(employeeId, d);
            if (day.IsRestDay) continue;
            if (prev is { End: { } pe } && day.Start is { } s && d >= from)
            {
                if (s < pe) result.Add(new ShiftConflict(employeeId, d, ConflictKinds.Overlap, $"{prev.Shift!.Code} {prev.Date:yyyy-MM-dd} overlaps {day.Shift!.Code}"));
                else if ((decimal)(s - pe).TotalHours < minRestHours)
                    result.Add(new ShiftConflict(employeeId, d, ConflictKinds.RestViolation,
                        string.Create(CultureInfo.InvariantCulture, $"rest {(s - pe).TotalHours:0.##} h < {minRestHours} h")));
            }
            prev = day;
        }
        foreach (var pair in _assignments[employeeId].SelectMany((a, i) => _assignments[employeeId].Skip(i + 1).Select(b => (a, b))))
        {
            var (a, b) = pair;
            if (RangesOverlap(a.From, a.To, b.From, b.To))
                result.Add(new ShiftConflict(employeeId, a.From > b.From ? a.From : b.From, ConflictKinds.AssignmentOverlap, "assignments overlap"));
        }
        return result;
    }

    public static bool RangesOverlap(DateOnly aFrom, DateOnly? aTo, DateOnly bFrom, DateOnly? bTo) =>
        aFrom <= (bTo ?? DateOnly.MaxValue) && bFrom <= (aTo ?? DateOnly.MaxValue);
}
