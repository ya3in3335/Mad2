using System.Text.Json;

namespace Veltrix.Desktop.Core.Workforce;

public enum RoundingMode { Down, Nearest, Up }

/// <summary>Overtime is counted after a daily worked-minutes threshold, or after the scheduled shift end.</summary>
public enum OvertimeBasis { DailyThreshold, AfterShiftEnd }

public enum AbsenceDeductionMethod { None, PerWorkingDay, PerCalendarDay30, PerHour }

public enum LateAdjustmentMethod { None, PerMinute }

public sealed record AttendancePolicy
{
    /// <summary>Same employee + same punch type within this window = duplicate scan (rejected).</summary>
    public int DuplicateWindowSeconds { get; init; } = 60;
    /// <summary>How long before a shift start a punch is attributed to that shift.</summary>
    public int EarlyWindowMinutes { get; init; } = 240;
    /// <summary>How long after a shift end a punch is still attributed to that shift (overnight shifts → shift start day).</summary>
    public int LateWindowMinutes { get; init; } = 360;
    /// <summary>Deduct the template's unpaid break when the employee did not punch any break.</summary>
    public bool AutoDeductBreak { get; init; } = true;
    /// <summary>Workplace time zone (IANA or Windows id); null = the machine's local zone.</summary>
    public string? TimeZoneId { get; init; }
}

public sealed record OvertimePolicy
{
    public OvertimeBasis Basis { get; init; } = OvertimeBasis.AfterShiftEnd;
    /// <summary>DailyThreshold basis: minutes after which work is overtime.</summary>
    public int DailyThresholdMinutes { get; init; } = 480;
    /// <summary>Overtime below this is ignored.</summary>
    public int MinimumMinutes { get; init; } = 15;
    /// <summary>Rounding step in minutes (0/1 = none).</summary>
    public int RoundingStepMinutes { get; init; } = 15;
    public RoundingMode Rounding { get; init; } = RoundingMode.Down;
}

public sealed record PayrollPolicy
{
    /// <summary>Hours per month used to derive an hourly rate from a monthly salary.</summary>
    public decimal StandardMonthlyHours { get; init; } = 173.33m;
    /// <summary>First tier: overtime minutes per day paid at <see cref="Tier1Multiplier"/>; beyond at <see cref="Tier2Multiplier"/>.</summary>
    public int Tier1MinutesPerDay { get; init; } = 120;
    public decimal Tier1Multiplier { get; init; } = 1.5m;
    public decimal Tier2Multiplier { get; init; } = 1.75m;
    /// <summary>Multiplier for work on a rest day (no shift planned).</summary>
    public decimal RestDayMultiplier { get; init; } = 2m;
    public decimal HolidayMultiplier { get; init; } = 2m;
    public TimeOnly NightStart { get; init; } = new(21, 0);
    public TimeOnly NightEnd { get; init; } = new(5, 0);
    /// <summary>Extra % of the hourly rate per night hour worked (0 = off).</summary>
    public decimal NightPremiumPercent { get; init; }
    /// <summary>Pay overtime that has not been reviewed yet (no approve / reject decision). Off by default: only approved overtime is paid.</summary>
    public bool PayPendingOvertime { get; init; }
    public AbsenceDeductionMethod AbsenceMethod { get; init; } = AbsenceDeductionMethod.PerWorkingDay;
    public LateAdjustmentMethod LateMethod { get; init; } = LateAdjustmentMethod.None;
    /// <summary>Late minutes per month tolerated before the late adjustment applies.</summary>
    public int LateToleranceMinutesPerMonth { get; init; }
}

/// <summary>Minimum rest between two shifts of the same employee.</summary>
public sealed record RestPolicy
{
    public decimal MinRestHours { get; init; } = 11m;
}

/// <summary>All configurable workforce rules, stored as JSON in the key/value table (included in backups).</summary>
public sealed record WorkforcePolicy
{
    public const string Key = "workforce.policy";
    public AttendancePolicy Attendance { get; init; } = new();
    public OvertimePolicy Overtime { get; init; } = new();
    public PayrollPolicy Payroll { get; init; } = new();
    public RestPolicy Rest { get; init; } = new();

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static WorkforcePolicy FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<WorkforcePolicy>(json, Json) ?? new(); }
        catch (JsonException) { return new(); }
    }
}

/// <summary>Statutory deductions are user-entered (see <c>WfStatutoryRule</c>); nothing is shipped.</summary>
public static class StatutoryNotice
{
    public const string Text =
        "No statutory rates are shipped with VELTRIX. Social-security (e.g. CNAS) and income-tax (e.g. IRG) rules must be entered by the user, " +
        "verified against current official sources (laws, decrees, administration tables) and enabled explicitly. Rules are disabled by default.";
}
