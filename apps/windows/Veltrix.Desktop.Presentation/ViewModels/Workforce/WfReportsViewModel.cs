using System.Collections;
using System.Data;
using System.Globalization;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Workforce;

namespace Veltrix.Desktop.Presentation.ViewModels;

/// <summary>One workforce report: date range or month period.</summary>
public sealed record WfReportDef(string Key, string TitleKey, bool ByMonth)
{
    public string Title { get; init; } = "";
    public override string ToString() => Title;
}

/// <summary>Workforce reports: every report provider of <see cref="WorkforceReports"/>, shown as a grid, exported to CSV or printed (PDF via a PDF printer).</summary>
public sealed partial class WfReportsViewModel : WorkforcePageViewModel
{
    public WfReportsViewModel(DesktopServices s) : base(s)
    {
        _from = FromDate(new DateOnly(Today.Year, Today.Month, 1));
        _to = FromDate(Today);
        _year = Today.Year.ToString(CultureInfo.InvariantCulture);
        _month = Today.Month;
        ReportList = Definitions.Select(d => d with { Title = Loc.Get(d.TitleKey) }).ToList();
        _selectedReport = ReportList[0];
    }

    public override string TitleKey => "wf.nav.reports";
    public static IReadOnlyList<int> Months { get; } = Enumerable.Range(1, 12).ToList();

    public static readonly IReadOnlyList<WfReportDef> Definitions =
    [
        new("daily", "wf.rep.daily", false), new("late", "wf.rep.late", false), new("absence", "wf.rep.absence", false), new("overtime", "wf.rep.overtime", false),
        new("monthly", "wf.rep.monthly", true), new("department", "wf.rep.department", false), new("leave", "wf.rep.leave", false),
        new("leaveBalances", "wf.rep.leaveBalances", false), new("coverage", "wf.rep.coverage", false), new("payroll", "wf.rep.payroll", true),
        new("devices", "wf.rep.devices", false), new("denials", "wf.rep.denials", false),
    ];

    public IReadOnlyList<WfReportDef> ReportList { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsByMonth))] private WfReportDef _selectedReport;
    [ObservableProperty] private DateTime? _from;
    [ObservableProperty] private DateTime? _to;
    [ObservableProperty] private string _year;
    [ObservableProperty] private int _month;
    [ObservableProperty] private DataTable? _table;
    [ObservableProperty] private int _rowCount;

    public bool IsByMonth => SelectedReport.ByMonth;
    private IList? _rows;
    private Type? _rowType;

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        var (rows, type) = await RunAsync(SelectedReport.Key, ct);
        _rows = rows;
        _rowType = type;
        Table = ToTable(rows, type);
        RowCount = rows.Count;
        return rows.Count > 0;
    }

    partial void OnSelectedReportChanged(WfReportDef value) { if (!IsLoading && (IsReady || IsEmpty || IsError)) _ = ReloadAsync(); }

    [RelayCommand] public Task RunReportAsync() => ReloadAsync();

    private async Task<(IList Rows, Type Type)> RunAsync(string key, CancellationToken ct)
    {
        var from = ToDate(From, Today);
        var to = ToDate(To, from);
        if (to < from) (from, to) = (to, from);
        var year = ParseInt(Year, "wf.err.period", 2000, 2100);
        var a = Actor;
        return key switch
        {
            "daily" => Rows(await Reports.DailyAsync(a, from, to, ct: ct)),
            "late" => Rows(await Reports.LateAsync(a, from, to, ct)),
            "absence" => Rows(await Reports.AbsenceAsync(a, from, to, ct)),
            "overtime" => Rows(await Reports.OvertimeAsync(a, from, to, ct)),
            "monthly" => Rows(await Reports.MonthlyAsync(a, year, Month, ct: ct)),
            "department" => Rows(await Reports.DepartmentAsync(a, from, to, ct)),
            "leave" => Rows(await Reports.LeaveAsync(a, from, to, ct)),
            "leaveBalances" => Rows(await Reports.LeaveBalancesAsync(a, ct)),
            "coverage" => Rows(await Reports.ShiftCoverageAsync(a, from, to, ct)),
            "payroll" => Rows(await Reports.PayrollPrepAsync(a, year, Month, ct)),
            "devices" => Rows(await Reports.DeviceActivityAsync(a, LocalStartUtc(from), LocalStartUtc(to.AddDays(1)), ct)),
            "denials" => Rows(await Reports.AccessDenialsAsync(a, LocalStartUtc(from), LocalStartUtc(to.AddDays(1)), ct)),
            _ => throw new InvalidOperationException("wf.err.unknown"),
        };
    }

    private static (IList, Type) Rows<T>(IReadOnlyList<T> rows) => (rows.ToList(), typeof(T));

    private static PropertyInfo[] Props(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0 && p.Name is not ("Id" or "PrepId" or "EmployeeId" or "LeaveTypeId")).ToArray();

    /// <summary>Grid table with localized column titles and culture-formatted values (dates, times, amounts).</summary>
    public DataTable ToTable(IList rows, Type type)
    {
        var props = Props(type);
        var dt = new DataTable(SelectedReport.Key) { Locale = Loc.Culture };
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in props)
        {
            var title = WfText.Label(Loc, "col", p.Name);
            while (!used.Add(title)) title += " ";
            dt.Columns.Add(title, typeof(string));
        }
        foreach (var r in rows)
            dt.Rows.Add(props.Select(p => (object)Format(p.GetValue(r))).ToArray());
        return dt;
    }

    private string Format(object? v) => v switch
    {
        null => "",
        DateOnly d => d.ToString("d", Loc.Culture),
        DateTime t => t.ToString("g", Loc.Culture),
        DateTimeOffset o => DateTimeText(o),
        decimal m => m.ToString("N2", Loc.Culture),
        bool b => b ? "✓" : "",
        IFormattable f => f.ToString(null, Loc.Culture),
        _ => v.ToString() ?? "",
    };

    [RelayCommand]
    public Task ExportCsvAsync() => SubmitAsync(async () =>
    {
        if (_rows is null || _rowType is null) return;
        var method = typeof(WorkforcePageViewModel).GetMethod(nameof(ExportCsvAsync), BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(_rowType);
        var cast = typeof(Enumerable).GetMethod(nameof(Enumerable.Cast))!.MakeGenericMethod(_rowType).Invoke(null, [_rows]);
        await (Task<bool>)method.Invoke(this, [$"{SelectedReport.Key}", cast, SelectedReport.Title])!;
    }, null);

    /// <summary>Prints the grid (choose "Microsoft Print to PDF" for a PDF file).</summary>
    [RelayCommand]
    public void Print()
    {
        if (Table is null) return;
        var title = $"{SelectedReport.Title} — " + (IsByMonth ? $"{Month:00}/{Year}" : $"{DateText(ToDate(From, Today))} – {DateText(ToDate(To, Today))}");
        if (!S.Ui.PrintTable(title, Table)) StatusMessage = Loc.Get("wf.rep.printCancelled");
    }
}
