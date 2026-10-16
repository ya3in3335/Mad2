using System.Globalization;
using System.Text.Json;
using Veltrix.Desktop.Core.Data;

namespace Veltrix.Desktop.Core.Workforce;

public static class AccessReasons
{
    public const string Granted = "granted";
    public const string GrantedSoftAntiPassback = "granted_soft_antipassback";
    public const string UnknownCredential = "unknown_credential";
    public const string EmployeeInactive = "employee_inactive";
    public const string NoAccessGroup = "no_access_group";
    public const string OutsideSchedule = "outside_schedule";
    public const string Holiday = "holiday";
    public const string AntiPassback = "antipassback";
    public const string EmergencyUnlock = "emergency_unlock";
}

/// <summary>Weekly access window; <see cref="Day"/> 0 = Sunday. End ≤ Start crosses midnight.</summary>
public sealed record AccessWindow(int Day, string Start, string End);

public sealed record AccessRequest(WfDoor Door, WfZone Zone, LocalEmployee? Employee, IReadOnlyList<WfAccessGroup> EmployeeGroups, bool? IsInsideZone,
    DateTime LocalTime, bool IsHoliday);

public sealed record AccessDecision(bool Granted, string Reason, bool AntiPassbackViolation = false);

/// <summary>
/// Pure access decision. Safety: this engine only decides whether to send an UNLOCK; it never commands a lock during an alarm.
/// Egress must be guaranteed by fail-safe hardware (fail-safe locks / REX buttons / fire-alarm relay cutting lock power) — software is not a life-safety device.
/// </summary>
public static class AccessEngine
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<AccessWindow> ParseSchedule(string json) => JsonSerializer.Deserialize<List<AccessWindow>>(json, Json) ?? [];
    public static IReadOnlyList<Guid> ParseDoors(string json) => JsonSerializer.Deserialize<List<Guid>>(json, Json) ?? [];

    public static AccessDecision Decide(AccessRequest r)
    {
        if (r.Employee is null) return new(false, AccessReasons.UnknownCredential);
        if (r.Employee.Status is not EmployeeStatuses.Active || !r.Employee.IsActive) return new(false, AccessReasons.EmployeeInactive);
        var groups = r.EmployeeGroups.Where(g => ParseDoors(g.DoorIdsJson).Contains(r.Door.Id)).ToList();
        if (groups.Count == 0) return new(false, AccessReasons.NoAccessGroup);
        var open = groups.Where(g => !r.IsHoliday || g.AllowOnHolidays).ToList();
        if (open.Count == 0) return new(false, AccessReasons.Holiday);
        if (!open.Any(g => InSchedule(ParseSchedule(g.ScheduleJson), r.LocalTime))) return new(false, AccessReasons.OutsideSchedule);

        var violation = r.Zone.AntiPassback != AntiPassbackModes.None && r.IsInsideZone is { } inside &&
            (r.Door.Direction == "In" ? inside : !inside);
        if (violation && r.Zone.AntiPassback == AntiPassbackModes.Hard) return new(false, AccessReasons.AntiPassback, true);
        return new(true, violation ? AccessReasons.GrantedSoftAntiPassback : AccessReasons.Granted, violation);
    }

    public static bool InSchedule(IReadOnlyList<AccessWindow> windows, DateTime t)
    {
        if (windows.Count == 0) return true;
        var dow = (int)t.DayOfWeek;
        var time = TimeOnly.FromDateTime(t);
        foreach (var w in windows)
        {
            var s = TimeOnly.Parse(w.Start, CultureInfo.InvariantCulture);
            var e = TimeOnly.Parse(w.End, CultureInfo.InvariantCulture);
            if (e > s) { if (w.Day == dow && time >= s && time < e) return true; }
            else if ((w.Day == dow && time >= s) || ((w.Day + 1) % 7 == dow && time < e)) return true;
        }
        return false;
    }

    public static string Mask(string value) => value.Length <= 4 ? new string('*', value.Length) : new string('*', value.Length - 4) + value[^4..];
}
