using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Standalone;
using Veltrix.Desktop.Core.Workforce;

namespace Veltrix.Desktop.Presentation.ViewModels;

/// <summary>One sidebar entry of the Workforce group: visible when the user holds any of <see cref="AnyOf"/> (and is not scoped for global pages).</summary>
public sealed record WorkforceNavEntry(string Key, string TitleKey, IReadOnlyList<string> AnyOf, bool GlobalOnly = false, string? Alternative = null);

/// <summary>The Workforce navigation group (standalone mode) and its RBAC visibility rules.</summary>
public static class WorkforceNav
{
    public const string GroupTitleKey = "wf.nav.group";
    public const string KeyPrefix = "wf.";

    public static readonly IReadOnlyList<WorkforceNavEntry> Entries =
    [
        new("wf.dashboard", "wf.nav.dashboard", [WorkforcePermissions.AttendanceView]),
        new("wf.employees", "wf.nav.employees", [WorkforcePermissions.EmployeesView]),
        new("wf.kiosk", "wf.nav.kiosk", [WorkforcePermissions.AttendanceSelf]),
        new("wf.history", "wf.nav.history", [WorkforcePermissions.AttendanceView]),
        new("wf.live", "wf.nav.live", [WorkforcePermissions.AttendanceView]),
        new("wf.shifts", "wf.nav.shifts", [WorkforcePermissions.ShiftsView]),
        new("wf.leave", "wf.nav.leave", [WorkforcePermissions.LeaveRequest, WorkforcePermissions.LeaveApprove, WorkforcePermissions.LeaveManage]),
        new("wf.overtime", "wf.nav.overtime", [WorkforcePermissions.CorrectionsApprove]),
        // Door configuration and logs are organisation-wide; scoped roles holding the emergency permission still get the page (unlock only).
        new("wf.access", "wf.nav.access", [WorkforcePermissions.AccessView], GlobalOnly: true, Alternative: WorkforcePermissions.EmergencyUnlock),
        new("wf.devices", "wf.nav.devices", [WorkforcePermissions.DevicesManage], GlobalOnly: true),
        new("wf.payroll", "wf.nav.payroll", [WorkforcePermissions.PayrollView]),
        new("wf.reports", "wf.nav.reports", [WorkforcePermissions.ReportsView]),
        new("wf.settings", "wf.nav.settings", [WorkforcePermissions.PayrollConfigure, WorkforcePermissions.LeaveManage, WorkforcePermissions.ShiftsManage], GlobalOnly: true),
    ];

    public static bool IsVisible(WorkforceNavEntry e, Func<string, bool> has, bool scoped) =>
        (e.AnyOf.Any(has) && !(e.GlobalOnly && scoped)) || (e.Alternative is { } alt && has(alt));

    /// <summary>Keys of the Workforce pages the user may open, in sidebar order.</summary>
    public static IReadOnlyList<string> VisibleKeys(Func<string, bool> has, bool scoped) =>
        Entries.Where(e => IsVisible(e, has, scoped)).Select(e => e.Key).ToList();

    /// <summary>Same rule for a local role (owner = every permission).</summary>
    public static IReadOnlyList<string> VisibleKeysForRole(string role)
    {
        var perms = LocalRoles.PermissionsFor(role);
        return VisibleKeys(p => perms.Contains("*") || perms.Contains(p), WorkforcePermissions.IsScoped(role));
    }

    public static PageViewModel Create(string key, DesktopServices s) => key switch
    {
        "wf.dashboard" => new WfDashboardViewModel(s),
        "wf.employees" => new WfEmployeesViewModel(s),
        "wf.kiosk" => new WfKioskViewModel(s),
        "wf.history" => new WfAttendanceViewModel(s),
        "wf.live" => new WfLiveViewModel(s),
        "wf.shifts" => new WfShiftPlannerViewModel(s),
        "wf.leave" => new WfLeaveViewModel(s),
        "wf.overtime" => new WfOvertimeViewModel(s),
        "wf.access" => new WfAccessViewModel(s),
        "wf.devices" => new WfDevicesViewModel(s),
        "wf.payroll" => new WfPayrollViewModel(s),
        "wf.reports" => new WfReportsViewModel(s),
        "wf.settings" => new WfSettingsViewModel(s),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
    };
}

/// <summary>Base for the workforce screens: the acting user, localized form errors, date/time formatting in the UI culture and CSV export.</summary>
public abstract partial class WorkforcePageViewModel(DesktopServices s) : PageViewModel(s)
{
    protected WorkforceService Wf => S.Standalone.Workforce;
    protected WorkforceReports Reports => _reports ??= new WorkforceReports(Wf, S.Db);
    private WorkforceReports? _reports;

    /// <summary>The signed-in local user as a workforce actor (no permission at all without a local session).</summary>
    public WorkforceActor Actor => S.Session.LocalUser is { } u ? WorkforceActor.From(u) : new WorkforceActor(S.Session.User?.FullName ?? "", "");

    /// <summary>Department managers and employees only see their own scope (organisation-wide operations are refused).</summary>
    public bool IsScoped => WorkforcePermissions.IsScoped(Actor.Role);

    protected bool Has(string permission) => Can(permission);
    protected bool HasGlobal(string permission) => Can(permission) && !IsScoped;

    [ObservableProperty] private string? _formError;

    protected DateTimeOffset Now => S.Time.GetUtcNow();
    protected DateTime LocalNow => TimeZoneInfo.ConvertTime(Now, S.Time.LocalTimeZone).DateTime;
    protected DateOnly Today => DateOnly.FromDateTime(LocalNow);
    protected static DateTime FromDate(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);
    protected static DateOnly ToDate(DateTime? d, DateOnly fallback) => d is { } v ? DateOnly.FromDateTime(v) : fallback;

    /// <summary>UTC instant of local midnight of <paramref name="d"/>.</summary>
    protected DateTimeOffset LocalStartUtc(DateOnly d)
    {
        var local = d.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, S.Time.LocalTimeZone.GetUtcOffset(local)).ToUniversalTime();
    }

    protected string Time(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, S.Time.LocalTimeZone).ToString("t", Loc.Culture);
    protected string Time(DateTime? local) => local is { } v ? v.ToString("t", Loc.Culture) : "";
    protected string DateTimeText(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, S.Time.LocalTimeZone).ToString("g", Loc.Culture);
    protected string DateText(DateOnly d) => d.ToString("d", Loc.Culture);
    protected string Minutes(int m) => WfText.Duration(m);
    protected string T(string prefix, string? value) => WfText.Label(Loc, prefix, value);

    /// <summary>Runs a form submit: clears/sets <see cref="FormError"/> (localized) and the status message.</summary>
    protected async Task<bool> SubmitAsync(Func<Task> action, string? successKey = "dk.saved")
    {
        FormError = null;
        var ok = await TryActionAsync(action, successKey);
        if (!ok) FormError = StatusMessage;
        return ok;
    }

    protected static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    /// <summary>Asks for a destination and writes the rows as CSV (UTF-8 with BOM for spreadsheet apps). Returns false when cancelled.</summary>
    protected async Task<bool> ExportCsvAsync<T>(string fileStem, IEnumerable<T> rows, string? title = null)
    {
        var path = S.Ui.PickSaveFile($"{fileStem}-{LocalNow:yyyyMMdd-HHmm}.csv", "CSV (*.csv)|*.csv");
        if (path is null) return false;
        await File.WriteAllTextAsync(path, WorkforceReports.ToCsv(rows, fileStem, title), new UTF8Encoding(true));
        return true;
    }

    protected static decimal ParseDecimal(string? text, string errorKey)
    {
        if (!Parse.TryDecimal(text, out var v) || v < 0) throw new InvalidOperationException(errorKey);
        return v;
    }

    protected static int ParseInt(string? text, string errorKey, int min = 0, int max = int.MaxValue)
    {
        if (!int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < min || v > max) throw new InvalidOperationException(errorKey);
        return v;
    }

    protected static string Text(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    protected static string Text(int v) => v.ToString(CultureInfo.InvariantCulture);
}

public static class WfText
{
    /// <summary>Localized label of a code (status, punch type, reason…): "wf.{prefix}.{value}", falling back to the raw value.</summary>
    public static string Label(ILocalizer loc, string prefix, string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var key = $"wf.{prefix}.{value}";
        var text = loc.Get(key);
        return text == key ? value : text;
    }

    /// <summary>Minutes as h:mm (culture-neutral digits; shown left-to-right).</summary>
    public static string Duration(int minutes) =>
        minutes == 0 ? "0:00" : $"{(minutes < 0 ? "-" : "")}{Math.Abs(minutes) / 60}:{Math.Abs(minutes) % 60:00}";

    public static string Csv(IEnumerable<string> cells) => string.Join(',', cells.Select(c => c.Contains(',') || c.Contains('"') ? "\"" + c.Replace("\"", "\"\"") + "\"" : c));
}

/// <summary>Minimal RFC 4180 CSV reader (quoted fields, doubled quotes, CR/LF), used by the employee import.</summary>
public static class CsvReader
{
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        // Spreadsheet apps in French/Arabic locales save with ';' — the header line decides.
        var nl = text.IndexOf('\n');
        var header = nl >= 0 ? text[..nl] : text;
        var sep = header.Count(ch => ch == ';') > header.Count(ch => ch == ',') ? ';' : ',';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; break;
                case var x when x == sep: row.Add(cell.ToString()); cell.Clear(); break;
                case '\r': break;
                case '\n': row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = []; break;
                default: cell.Append(c); break;
            }
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows.Where(r => r.Any(c => c.Length > 0)).Select(r => r.Select(Clean).ToList()).ToList();
    }

    // Undo the formula protection of the CSV export ('=… → =…) and trim.
    private static string Clean(string v)
    {
        v = v.Trim();
        return v.Length > 1 && v[0] == '\'' && v[1] is '=' or '+' or '-' or '@' ? v[1..] : v;
    }
}

/// <summary>Employee choice for combo boxes ("E0001 — Name").</summary>
public sealed record EmployeeChoice(Guid Id, string Code, string Name)
{
    public string Text => $"{Code} — {Name}";
    public override string ToString() => Text;
    public static EmployeeChoice From(LocalEmployee e) => new(e.Id, e.Code, e.Name);
    public static EmployeeChoice From(EmployeeRef e) => new(e.Id, e.Code, e.Name);
}
