using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Workforce;

namespace Veltrix.Desktop.Presentation.ViewModels;

public sealed record AttendanceDayRow(Guid EmployeeId, DateOnly Date, string DateText, string EmployeeNo, string Employee, string Shift, string Status, string StatusLabel,
    string FirstIn, string LastOut, string Worked, string Late, string Early, string Overtime, bool MissedPunch, string Anomalies);

public sealed record PunchRow(Guid Id, string Time, string Type, string TypeLabel, string Source, string? Reason, string? AuthorizedBy, bool IsVoid);

public sealed record CorrectionRow(Guid Id, string EmployeeNo, string Employee, string Kind, string PunchType, string When, string Reason, string RequestedBy,
    string RequestedAt, string Status, string StatusLabel, string? DecisionNote)
{
    public bool IsPending => Status == RequestStatuses.Pending;
}

/// <summary>Attendance history (computed days, raw punches of a day) with correction requests and their approval.</summary>
public sealed partial class WfAttendanceViewModel : WorkforcePageViewModel
{
    public WfAttendanceViewModel(DesktopServices s) : base(s)
    {
        _from = FromDate(Today.AddDays(-6));
        _to = FromDate(Today);
        _correctionDate = FromDate(Today);
    }

    public override string TitleKey => "wf.nav.history";

    public ObservableCollection<AttendanceDayRow> Days { get; } = [];
    public ObservableCollection<PunchRow> Punches { get; } = [];
    public ObservableCollection<CorrectionRow> Corrections { get; } = [];
    public ObservableCollection<EmployeeChoice> Employees { get; } = [];
    public IReadOnlyList<Choice<string?>> StatusFilters =>
        [new(null, Loc.Get("wf.all")), .. new[] { DayStatuses.Present, DayStatuses.Incomplete, DayStatuses.Absent, DayStatuses.Leave, DayStatuses.Holiday, DayStatuses.Rest }
            .Select(st => new Choice<string?>(st, T("status", st)))];
    public IReadOnlyList<Choice<string?>> CorrectionFilters =>
        [new(RequestStatuses.Pending, T("req", RequestStatuses.Pending)), new(RequestStatuses.Approved, T("req", RequestStatuses.Approved)),
         new(RequestStatuses.Rejected, T("req", RequestStatuses.Rejected)), new(null, Loc.Get("wf.all"))];
    public IReadOnlyList<Choice<string>> CorrectionKindChoices => [new(CorrectionKinds.AddPunch, T("corr", CorrectionKinds.AddPunch)), new(CorrectionKinds.VoidPunch, T("corr", CorrectionKinds.VoidPunch))];
    public IReadOnlyList<Choice<string>> PunchTypeChoices => PunchTypes.Punches.Select(p => new Choice<string>(p, T("punch", p))).ToList();

    [ObservableProperty] private EmployeeChoice? _employee;
    [ObservableProperty] private DateTime? _from;
    [ObservableProperty] private DateTime? _to;
    [ObservableProperty] private string? _statusFilter;
    [ObservableProperty] private AttendanceDayRow? _selectedDay;
    [ObservableProperty] private PunchRow? _selectedPunch;
    [ObservableProperty] private string? _correctionFilter = RequestStatuses.Pending;
    [ObservableProperty] private string? _decisionNote;
    [ObservableProperty] private string? _chainStatus;

    // Correction request form
    [ObservableProperty] private EmployeeChoice? _correctionEmployee;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsAddPunch))] private string _correctionKind = CorrectionKinds.AddPunch;
    [ObservableProperty] private string _correctionPunchType = PunchTypes.In;
    [ObservableProperty] private DateTime? _correctionDate;
    [ObservableProperty] private string _correctionTime = "08:00";
    [ObservableProperty] private string _correctionReason = "";

    public bool IsAddPunch => CorrectionKind == CorrectionKinds.AddPunch;
    public bool CanRequest => Has(WorkforcePermissions.CorrectionsRequest);
    public bool CanApprove => Has(WorkforcePermissions.CorrectionsApprove);
    public bool CanVerifyChain => Has(WorkforcePermissions.AuditView);

    private Dictionary<Guid, EmployeeRef> _people = [];

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        var people = await Wf.DirectoryAsync(Actor, ct);
        _people = people.ToDictionary(p => p.Id);
        if (Employees.Count != people.Count)
        {
            var keep = Employee?.Id;
            Fill(Employees, people.Select(EmployeeChoice.From));
            Employee = Employees.FirstOrDefault(e => e.Id == keep);
            CorrectionEmployee ??= Actor.EmployeeId is { } self ? Employees.FirstOrDefault(e => e.Id == self) : Employees.FirstOrDefault();
        }
        var from = ToDate(From, Today);
        var to = ToDate(To, from);
        if (to < from) (from, to) = (to, from);
        if (to.DayNumber - from.DayNumber > 92) throw new InvalidOperationException("wf.err.rangeTooLong");
        var days = await Wf.ComputeAttendanceAsync(Actor, from, to, Employee?.Id, ct);
        Fill(Days, days.Where(d => StatusFilter is null || d.Status == StatusFilter)
            .OrderByDescending(d => d.Date).ThenBy(d => _people.GetValueOrDefault(d.EmployeeId)?.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(ToRow));
        await LoadCorrectionsAsync(ct);
        return Days.Count > 0 || Corrections.Count > 0;
    }

    private AttendanceDayRow ToRow(DayResult d)
    {
        var p = _people.GetValueOrDefault(d.EmployeeId);
        return new AttendanceDayRow(d.EmployeeId, d.Date, DateText(d.Date), p?.Code ?? "", p?.Name ?? "", d.ShiftCode ?? "", d.Status, T("status", d.Status),
            Time(d.FirstIn), Time(d.LastOut), Minutes(d.WorkedMinutes), d.LateMinutes > 0 ? Minutes(d.LateMinutes) : "", d.EarlyLeaveMinutes > 0 ? Minutes(d.EarlyLeaveMinutes) : "",
            d.OvertimeMinutes > 0 ? Minutes(d.OvertimeMinutes) : "", d.MissedPunch, string.Join(", ", d.Anomalies.Select(a => T("anomaly", a))));
    }

    private async Task LoadCorrectionsAsync(CancellationToken ct)
    {
        var list = await Wf.CorrectionsAsync(Actor, CorrectionFilter, ct);
        Fill(Corrections, list.OrderByDescending(c => c.RequestedAt).Select(c =>
        {
            var p = _people.GetValueOrDefault(c.EmployeeId);
            return new CorrectionRow(c.Id, p?.Code ?? "", p?.Name ?? "", T("corr", c.Kind), T("punch", c.PunchType),
                c.OccurredAtUtc is { } at ? DateTimeText(at) : "", c.Reason, c.RequestedBy, DateTimeText(c.RequestedAt), c.Status, T("req", c.Status), c.DecisionNote);
        }));
    }

    partial void OnEmployeeChanged(EmployeeChoice? value) { if (IsReady || IsEmpty) _ = ReloadAsync(); }
    partial void OnStatusFilterChanged(string? value) { if (IsReady || IsEmpty) _ = ReloadAsync(); }

    partial void OnCorrectionFilterChanged(string? value)
    {
        if (IsReady || IsEmpty) _ = TryActionAsync(() => LoadCorrectionsAsync(CancellationToken.None));
    }

    partial void OnSelectedDayChanged(AttendanceDayRow? value)
    {
        Punches.Clear();
        if (value is not null) _ = LoadPunchesAsync(value);
    }

    private async Task LoadPunchesAsync(AttendanceDayRow day)
    {
        try
        {
            // Night shifts end the next morning: show the day and the following one until noon.
            var from = LocalStartUtc(day.Date.AddDays(-1)).AddHours(18);
            var to = LocalStartUtc(day.Date.AddDays(1)).AddHours(12);
            var events = await Wf.EventsAsync(Actor, day.EmployeeId, from, to);
            var voided = events.Where(e => e.Type == PunchTypes.Void).Select(e => e.CompensatesEventId).OfType<Guid>().ToHashSet();
            if (SelectedDay != day) return;
            Fill(Punches, events.OrderBy(e => e.OccurredTicks).Select(e => new PunchRow(e.Id, DateTimeText(e.OccurredAtUtc), e.Type, T("punch", e.Type), T("source", e.Source),
                e.Reason, e.AuthorizedBy, e.Type == PunchTypes.Void || voided.Contains(e.Id))));
            CorrectionEmployee = Employees.FirstOrDefault(x => x.Id == day.EmployeeId) ?? CorrectionEmployee;
            CorrectionDate = FromDate(day.Date);
        }
        catch (Exception e) { S.Log.Warn("Punches failed", e); StatusMessage = Describe(e); }
    }

    [RelayCommand] public Task SearchNowAsync() => ReloadAsync();

    /// <summary>Pre-fills a void request for the selected punch.</summary>
    [RelayCommand]
    public void RequestVoid(PunchRow? punch)
    {
        if (punch is null || punch.IsVoid) return;
        SelectedPunch = punch;
        CorrectionKind = CorrectionKinds.VoidPunch;
    }

    [RelayCommand]
    public Task RequestCorrectionAsync() => SubmitAsync(async () =>
    {
        var emp = CorrectionEmployee ?? throw new InvalidOperationException("erp.err.unknownEmployee");
        if (string.IsNullOrWhiteSpace(CorrectionReason)) throw new InvalidOperationException("wf.err.reasonRequired");
        CorrectionInput input;
        if (IsAddPunch)
        {
            if (!TimeOnly.TryParse(CorrectionTime, CultureInfo.InvariantCulture, out var t) && !TimeOnly.TryParse(CorrectionTime, Loc.Culture, out t))
                throw new InvalidOperationException("wf.err.time");
            var local = ToDate(CorrectionDate, Today).ToDateTime(t);
            var at = new DateTimeOffset(local, S.Time.LocalTimeZone.GetUtcOffset(local)).ToUniversalTime();
            input = new CorrectionInput(emp.Id, CorrectionKinds.AddPunch, CorrectionReason.Trim(), CorrectionPunchType, at);
        }
        else
        {
            var target = SelectedPunch ?? throw new InvalidOperationException("wf.err.selectPunch");
            input = new CorrectionInput(emp.Id, CorrectionKinds.VoidPunch, CorrectionReason.Trim(), TargetEventId: target.Id);
        }
        await Wf.RequestCorrectionAsync(Actor, input);
        CorrectionReason = "";
        await LoadCorrectionsAsync(CancellationToken.None);
    }, "wf.corr.requested");

    [RelayCommand]
    public Task ApproveCorrectionAsync(CorrectionRow? row) => DecideAsync(row, true);

    [RelayCommand]
    public Task RejectCorrectionAsync(CorrectionRow? row) => DecideAsync(row, false);

    private Task DecideAsync(CorrectionRow? row, bool approve) => SubmitAsync(async () =>
    {
        if (row is null || !row.IsPending) return;
        await Wf.DecideCorrectionAsync(Actor, row.Id, approve, string.IsNullOrWhiteSpace(DecisionNote) ? null : DecisionNote.Trim());
        DecisionNote = null;
        await ReloadAsync();
    }, approve ? "wf.corr.approved" : "wf.corr.rejected");

    [RelayCommand]
    public Task VerifyChainAsync() => SubmitAsync(async () =>
    {
        var r = await Wf.VerifyChainAsync(Actor);
        ChainStatus = r.IsValid ? Loc.Format("wf.chain.ok", r.Count) : Loc.Format("wf.chain.broken", r.BrokenAtSeq ?? 0);
    }, null);

    [RelayCommand]
    public Task ExportCsvAsync() => SubmitAsync(() => ExportCsvAsync("attendance", Days.Select(d => new
    {
        Date = d.Date, d.EmployeeNo, d.Employee, d.Shift, d.Status, d.FirstIn, d.LastOut, d.Worked, d.Late, d.Early, d.Overtime, d.MissedPunch, d.Anomalies,
    })), null);
}
