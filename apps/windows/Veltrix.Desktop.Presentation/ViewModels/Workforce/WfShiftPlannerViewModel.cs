using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Workforce;

namespace Veltrix.Desktop.Presentation.ViewModels;

public sealed record ShiftCell(Guid EmployeeId, DateOnly Date, string Code, string Tooltip, bool IsRest, bool IsHoliday, bool IsConflict, bool IsToday, bool IsWeekend);
public sealed record ShiftGridRow(Guid EmployeeId, string EmployeeNo, string Employee, IReadOnlyList<ShiftCell> Cells, int ConflictCount);
public sealed record DayHeader(string Day, string Date, bool IsToday, bool IsWeekend);
public sealed record ConflictRow(string Employee, string Date, string Kind, string Detail);
public sealed record SwapRow(Guid Id, string Requester, string RequesterDate, string Counterpart, string CounterpartDate, string? Reason, string Status, string StatusLabel)
{
    public bool IsPending => Status == RequestStatuses.Pending;
}

/// <summary>One selectable weekday of an assignment (bit = DayOfWeek, Sunday = 0).</summary>
public sealed partial class WeekdayOption(DayOfWeek day, string label) : ObservableObject
{
    public DayOfWeek Day { get; } = day;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isChecked = day is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
}

/// <summary>Shift planner: week / month grid with conflict highlights, shift templates, rotations, assignments, day overrides and swaps.</summary>
public sealed partial class WfShiftPlannerViewModel : WorkforcePageViewModel
{
    public WfShiftPlannerViewModel(DesktopServices s) : base(s)
    {
        _anchor = Today;
        _assignFrom = FromDate(Today);
        _swapDate1 = FromDate(Today);
        _swapDate2 = FromDate(Today);
        var first = Loc.Culture.DateTimeFormat.FirstDayOfWeek;
        Weekdays = Enumerable.Range(0, 7).Select(i => (DayOfWeek)(((int)first + i) % 7))
            .Select(d => new WeekdayOption(d, Loc.Culture.DateTimeFormat.GetAbbreviatedDayName(d))).ToList();
    }

    public override string TitleKey => "wf.nav.shifts";

    public ObservableCollection<ShiftGridRow> Rows { get; } = [];
    public ObservableCollection<DayHeader> Headers { get; } = [];
    public ObservableCollection<ConflictRow> Conflicts { get; } = [];
    public ObservableCollection<WfShiftTemplate> Templates { get; } = [];
    public ObservableCollection<WfRotation> Rotations { get; } = [];
    public ObservableCollection<SwapRow> Swaps { get; } = [];
    public ObservableCollection<EmployeeChoice> Employees { get; } = [];
    public ObservableCollection<Choice<Guid?>> OverrideChoices { get; } = [];
    public IReadOnlyList<WeekdayOption> Weekdays { get; }

    private DateOnly _anchor;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsMonth))] private bool _isWeek = true;
    public bool IsMonth { get => !IsWeek; set => IsWeek = !value; }
    [ObservableProperty] private string _periodTitle = "";
    [ObservableProperty] private ShiftCell? _selectedCell;
    [ObservableProperty] private Guid? _overrideTemplateId;

    // Template editor
    [ObservableProperty] private WfShiftTemplate? _selectedTemplate;
    [ObservableProperty] private string _tplCode = "";
    [ObservableProperty] private string _tplName = "";
    [ObservableProperty] private string _tplSegments = "08:00-12:00, 13:00-17:00";
    [ObservableProperty] private string _tplBreak = "0";
    [ObservableProperty] private string _tplGraceIn = "5";
    [ObservableProperty] private string _tplGraceOut = "0";
    [ObservableProperty] private bool _tplFlexible;
    [ObservableProperty] private string _tplRequired = "0";

    // Rotation editor
    [ObservableProperty] private WfRotation? _selectedRotation;
    [ObservableProperty] private string _rotName = "";
    [ObservableProperty] private string _rotPattern = "";

    // Assignment
    [ObservableProperty] private EmployeeChoice? _assignEmployee;
    [ObservableProperty] private bool _assignUseRotation;
    [ObservableProperty] private WfShiftTemplate? _assignTemplate;
    [ObservableProperty] private WfRotation? _assignRotation;
    [ObservableProperty] private DateTime? _assignFrom;
    [ObservableProperty] private DateTime? _assignTo;
    [ObservableProperty] private bool _replaceOverlaps;

    // Swap
    [ObservableProperty] private EmployeeChoice? _swapEmployee1;
    [ObservableProperty] private DateTime? _swapDate1;
    [ObservableProperty] private EmployeeChoice? _swapEmployee2;
    [ObservableProperty] private DateTime? _swapDate2;
    [ObservableProperty] private string _swapReason = "";

    public bool CanManage => Has(WorkforcePermissions.ShiftsManage);
    public bool CanManageGlobal => HasGlobal(WorkforcePermissions.ShiftsManage);
    public bool CanRequestSwap => Has(WorkforcePermissions.SwapsRequest);
    public bool CanApproveSwap => Has(WorkforcePermissions.SwapsApprove);

    private (DateOnly From, DateOnly To) Range => IsWeek
        ? ShiftPlanner.Week(_anchor, Loc.Culture.DateTimeFormat.FirstDayOfWeek)
        : ShiftPlanner.Month(_anchor.Year, _anchor.Month);

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        var (from, to) = Range;
        var people = (await Wf.DirectoryAsync(Actor, ct)).Where(p => p.Status != EmployeeStatuses.Terminated).ToList();
        if (Employees.Count != people.Count) Fill(Employees, people.Select(EmployeeChoice.From));
        var templates = await Wf.ShiftTemplatesAsync(Actor, ct);
        Fill(Templates, templates);
        Fill(OverrideChoices, [new Choice<Guid?>(null, Loc.Get("wf.shift.rest")), .. templates.Select(t => new Choice<Guid?>(t.Id, $"{t.Code} — {t.Name}"))]);
        Fill(Rotations, await Wf.RotationsAsync(Actor, ct));
        var plan = await Wf.CalendarAsync(Actor, from, to, ct: ct);
        var conflicts = await Wf.DetectConflictsAsync(Actor, from, to, ct);
        var conflictSet = conflicts.Select(c => (c.EmployeeId, c.Date)).ToHashSet();
        var byEmp = plan.ToLookup(p => p.EmployeeId);
        var today = Today;
        var dates = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(from.AddDays).ToList();
        Fill(Headers, dates.Select(d => new DayHeader(Loc.Culture.DateTimeFormat.GetAbbreviatedDayName(d.DayOfWeek), d.Day.ToString(CultureInfo.InvariantCulture),
            d == today, d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)));
        Fill(Rows, people.Select(p =>
        {
            var days = byEmp[p.Id].ToDictionary(x => x.Date);
            var cells = dates.Select(d =>
            {
                var pd = days.GetValueOrDefault(d);
                var code = pd?.Shift?.Code ?? (pd?.IsHoliday == true ? "H" : "");
                var tip = pd?.Shift is { } sh ? $"{sh.Name} {string.Join(" ", pd.Segments.Select(sg => $"{sg.Start:HH:mm}-{sg.End:HH:mm}"))}" : Loc.Get("wf.shift.rest");
                if (pd?.HolidayName is { } hn) tip += " · " + hn;
                return new ShiftCell(p.Id, d, code, tip, pd?.Shift is null, pd?.IsHoliday == true, conflictSet.Contains((p.Id, d)), d == today,
                    d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
            }).ToList();
            return new ShiftGridRow(p.Id, p.Code, p.Name, cells, cells.Count(c => c.IsConflict));
        }));
        var names = people.ToDictionary(p => p.Id, p => p.Name);
        Fill(Conflicts, conflicts.Select(c => new ConflictRow(names.GetValueOrDefault(c.EmployeeId) ?? "", DateText(c.Date), T("conflict", c.Kind), c.Detail)));
        await LoadSwapsAsync(names, ct);
        PeriodTitle = IsWeek ? $"{DateText(from)} – {DateText(to)}" : FromDate(from).ToString("Y", Loc.Culture);
        return Rows.Count > 0 || Templates.Count > 0;
    }

    private async Task LoadSwapsAsync(Dictionary<Guid, string> names, CancellationToken ct)
    {
        var swaps = await Wf.SwapsAsync(Actor, null, ct);
        Fill(Swaps, swaps.OrderByDescending(x => x.RequestedAt).Take(100).Select(x => new SwapRow(x.Id, names.GetValueOrDefault(x.RequesterId) ?? "", DateText(x.RequesterDate),
            names.GetValueOrDefault(x.CounterpartId) ?? "", DateText(x.CounterpartDate), x.Reason, x.Status, T("req", x.Status))));
    }

    partial void OnIsWeekChanged(bool value) { if (!IsLoading) _ = ReloadAsync(); }

    [RelayCommand]
    public Task PreviousAsync()
    {
        _anchor = IsWeek ? _anchor.AddDays(-7) : _anchor.AddMonths(-1);
        return ReloadAsync();
    }

    [RelayCommand]
    public Task NextAsync()
    {
        _anchor = IsWeek ? _anchor.AddDays(7) : _anchor.AddMonths(1);
        return ReloadAsync();
    }

    [RelayCommand]
    public Task GoTodayAsync()
    {
        _anchor = Today;
        return ReloadAsync();
    }

    [RelayCommand]
    public void SelectCell(ShiftCell? cell)
    {
        SelectedCell = cell;
        if (cell is null) return;
        OverrideTemplateId = Templates.FirstOrDefault(t => t.Code == cell.Code)?.Id;
        AssignEmployee = Employees.FirstOrDefault(e => e.Id == cell.EmployeeId) ?? AssignEmployee;
        SwapEmployee1 = AssignEmployee;
        SwapDate1 = FromDate(cell.Date);
    }

    [RelayCommand]
    public Task ApplyOverrideAsync() => SubmitAsync(async () =>
    {
        if (SelectedCell is not { } c) throw new InvalidOperationException("wf.err.selectCell");
        await Wf.SetShiftOverrideAsync(Actor, c.EmployeeId, c.Date, OverrideTemplateId);
        await ReloadAsync();
    });

    // ---- templates

    partial void OnSelectedTemplateChanged(WfShiftTemplate? value)
    {
        if (value is null) return;
        TplCode = value.Code;
        TplName = value.Name;
        TplSegments = string.Join(", ", ShiftDefinition.ParseSegments(value.SegmentsJson).Select(sg => $"{sg.Start:HH\\:mm}-{sg.End:HH\\:mm}"));
        TplBreak = Text(value.BreakMinutes);
        TplGraceIn = Text(value.GraceInMinutes);
        TplGraceOut = Text(value.GraceOutMinutes);
        TplFlexible = value.IsFlexible;
        TplRequired = Text(value.RequiredMinutes);
    }

    [RelayCommand]
    public void NewTemplate()
    {
        SelectedTemplate = null;
        (TplCode, TplName, TplSegments, TplBreak, TplGraceIn, TplGraceOut, TplFlexible, TplRequired) = ("", "", "08:00-12:00, 13:00-17:00", "0", "5", "0", false, "0");
    }

    /// <summary>"08:00-12:00, 13:00-17:00" → segments (an end before the start crosses midnight).</summary>
    public static IReadOnlyList<ShiftSegment> ParseSegments(string text)
    {
        var list = new List<ShiftSegment>();
        foreach (var part in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ends = part.Split('-', StringSplitOptions.TrimEntries);
            if (ends.Length != 2 || !TimeOnly.TryParse(ends[0], CultureInfo.InvariantCulture, out var a) || !TimeOnly.TryParse(ends[1], CultureInfo.InvariantCulture, out var b))
                throw new InvalidOperationException("wf.err.segments");
            list.Add(new ShiftSegment(a, b));
        }
        if (list.Count == 0) throw new InvalidOperationException("wf.err.segments");
        return list;
    }

    [RelayCommand]
    public Task SaveTemplateAsync() => SubmitAsync(async () =>
    {
        var input = new ShiftTemplateInput(TplCode.Trim(), TplName.Trim(), ParseSegments(TplSegments), ParseInt(TplBreak, "wf.err.minutes"),
            ParseInt(TplGraceIn, "wf.err.minutes"), ParseInt(TplGraceOut, "wf.err.minutes"), TplFlexible, RequiredMinutes: ParseInt(TplRequired, "wf.err.minutes"));
        var saved = await Wf.UpsertShiftTemplateAsync(Actor, SelectedTemplate?.Id, input);
        await ReloadAsync();
        SelectedTemplate = Templates.FirstOrDefault(t => t.Id == saved.Id);
    });

    // ---- rotations

    partial void OnSelectedRotationChanged(WfRotation? value)
    {
        if (value is null) return;
        RotName = value.Name;
        var codes = Templates.ToDictionary(t => t.Id, t => t.Code);
        RotPattern = string.Join(", ", ShiftPlanner.ParsePattern(value.PatternJson).Select(id => id is { } x ? codes.GetValueOrDefault(x) ?? "?" : "-"));
    }

    [RelayCommand]
    public void NewRotation()
    {
        SelectedRotation = null;
        RotName = "";
        RotPattern = "";
    }

    /// <summary>Pattern as template codes separated by commas, "-" = rest day (e.g. "M, M, N, N, -, -").</summary>
    [RelayCommand]
    public Task SaveRotationAsync() => SubmitAsync(async () =>
    {
        var byCode = Templates.ToDictionary(t => t.Code, t => t.Id, StringComparer.OrdinalIgnoreCase);
        var pattern = RotPattern.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c == "-" ? (Guid?)null : byCode.TryGetValue(c, out var id) ? id : throw new InvalidOperationException("wf.err.unknownTemplate")).ToList();
        var saved = await Wf.UpsertRotationAsync(Actor, SelectedRotation?.Id, RotName, pattern);
        await ReloadAsync();
        SelectedRotation = Rotations.FirstOrDefault(r => r.Id == saved.Id);
    });

    // ---- assignment

    [RelayCommand]
    public Task AssignAsync() => SubmitAsync(async () =>
    {
        var emp = AssignEmployee ?? throw new InvalidOperationException("erp.err.unknownEmployee");
        var from = ToDate(AssignFrom, Today);
        DateOnly? to = AssignTo is { } t ? DateOnly.FromDateTime(t) : null;
        var mask = Weekdays.Where(w => w.IsChecked).Aggregate(0, (m, w) => m | (1 << (int)w.Day));
        var input = AssignUseRotation
            ? new AssignmentInput(emp.Id, from, to, RotationId: (AssignRotation ?? throw new InvalidOperationException("wf.err.assignment")).Id, RotationAnchor: from)
            : new AssignmentInput(emp.Id, from, to, (AssignTemplate ?? throw new InvalidOperationException("wf.err.assignment")).Id, DaysMask: mask == 0x7F ? 0 : mask);
        var r = await Wf.AssignShiftAsync(Actor, input, ReplaceOverlaps);
        await ReloadAsync();
        if (r.Conflicts.Count > 0) StatusMessage = Loc.Format("wf.shift.assignedWithConflicts", r.Conflicts.Count);
    });

    // ---- swaps

    [RelayCommand]
    public Task RequestSwapAsync() => SubmitAsync(async () =>
    {
        var a = SwapEmployee1 ?? throw new InvalidOperationException("erp.err.unknownEmployee");
        var b = SwapEmployee2 ?? throw new InvalidOperationException("erp.err.unknownEmployee");
        await Wf.RequestSwapAsync(Actor, a.Id, ToDate(SwapDate1, Today), b.Id, ToDate(SwapDate2, Today), string.IsNullOrWhiteSpace(SwapReason) ? null : SwapReason.Trim());
        SwapReason = "";
        await ReloadAsync();
    }, "wf.swap.requested");

    [RelayCommand] public Task ApproveSwapAsync(SwapRow? row) => DecideSwapAsync(row, true);
    [RelayCommand] public Task RejectSwapAsync(SwapRow? row) => DecideSwapAsync(row, false);

    private Task DecideSwapAsync(SwapRow? row, bool approve) => SubmitAsync(async () =>
    {
        if (row is null || !row.IsPending) return;
        await Wf.DecideSwapAsync(Actor, row.Id, approve);
        await ReloadAsync();
    });
}
