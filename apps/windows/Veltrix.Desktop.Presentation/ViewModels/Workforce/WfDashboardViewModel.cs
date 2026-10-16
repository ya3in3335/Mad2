using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Workforce;

namespace Veltrix.Desktop.Presentation.ViewModels;

/// <summary>One line of the live punch feed.</summary>
public sealed record PunchFeedItem(string Time, string EmployeeNo, string Employee, string Type, string TypeLabel, string Source);

/// <summary>Workforce dashboard: today's present / late / absent / on-leave counts and the latest punches (polled while visible).</summary>
public sealed partial class WfDashboardViewModel(DesktopServices s) : WorkforcePageViewModel(s)
{
    public override string TitleKey => "wf.nav.dashboard";

    public ObservableCollection<PunchFeedItem> Feed { get; } = [];

    [ObservableProperty] private int _scheduled;
    [ObservableProperty] private int _present;
    [ObservableProperty] private int _late;
    [ObservableProperty] private int _absent;
    [ObservableProperty] private int _onLeave;
    [ObservableProperty] private int _pendingCorrections;
    [ObservableProperty] private int _pendingLeave;
    [ObservableProperty] private string _todayText = "";
    [ObservableProperty] private string _updatedAt = "";

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        await LoadDataAsync(ct);
        return true;
    }

    /// <summary>Silent refresh (no loading state) — the view calls it on a timer.</summary>
    [RelayCommand]
    public async Task PollAsync()
    {
        if (!IsReady) return;
        try { await LoadDataAsync(CancellationToken.None); }
        catch (Exception e) { S.Log.Warn("Workforce dashboard poll failed", e); }
    }

    private async Task LoadDataAsync(CancellationToken ct)
    {
        var today = Today;
        var days = await Wf.ComputeAttendanceAsync(Actor, today, today, ct: ct);
        Scheduled = days.Count(d => d.IsScheduledWorkday);
        Present = days.Count(d => d.FirstIn is not null);
        Late = days.Count(d => d.LateMinutes > 0);
        Absent = days.Count(d => d.Status == DayStatuses.Absent);
        OnLeave = days.Count(d => d.Status == DayStatuses.Leave);
        PendingCorrections = Has(WorkforcePermissions.CorrectionsApprove) ? (await Wf.CorrectionsAsync(Actor, RequestStatuses.Pending, ct)).Count : 0;
        PendingLeave = Has(WorkforcePermissions.LeaveApprove) ? (await Wf.LeaveRequestsAsync(Actor, RequestStatuses.Pending, ct)).Count : 0;
        var feed = await Wf.RecentPunchesAsync(Actor, LocalStartUtc(today.AddDays(-1)), Now, 30, ct);
        Fill(Feed, feed.Select(p => new PunchFeedItem(Time(p.OccurredAtUtc), p.EmployeeNo, p.Employee, p.Type, T("punch", p.Type), T("source", p.Source))));
        TodayText = LocalNow.ToString("D", Loc.Culture);
        UpdatedAt = Time(Now);
    }
}

/// <summary>Who is in right now: one row per employee in scope with the current state (in / on break / out / not arrived).</summary>
public sealed record LiveRow(string EmployeeNo, string Employee, string State, string StateLabel, string LastPunch, string Shift, string Worked, int LateMinutes, string Late);

public sealed partial class WfLiveViewModel(DesktopServices s) : WorkforcePageViewModel(s)
{
    public const string StateIn = "in", StateBreak = "break", StateOut = "out", StateAway = "away";

    public override string TitleKey => "wf.nav.live";

    public ObservableCollection<LiveRow> Rows { get; } = [];
    private List<LiveRow> _all = [];

    [ObservableProperty] private string _search = "";
    /// <summary>"" = everyone, or one of the State* codes.</summary>
    [ObservableProperty] private string _stateFilter = "";
    [ObservableProperty] private int _inCount;
    [ObservableProperty] private int _breakCount;
    [ObservableProperty] private int _outCount;
    [ObservableProperty] private int _awayCount;
    [ObservableProperty] private string _updatedAt = "";

    public IReadOnlyList<Choice<string>> StateFilters =>
    [
        new("", Loc.Get("wf.all")), new(StateIn, Loc.Get("wf.live.in")), new(StateBreak, Loc.Get("wf.live.break")),
        new(StateOut, Loc.Get("wf.live.out")), new(StateAway, Loc.Get("wf.live.away")),
    ];

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        await LoadDataAsync(ct);
        return _all.Count > 0;
    }

    [RelayCommand]
    public async Task PollAsync()
    {
        if (!IsReady) return;
        try { await LoadDataAsync(CancellationToken.None); }
        catch (Exception e) { S.Log.Warn("Live attendance poll failed", e); }
    }

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnStateFilterChanged(string value) => ApplyFilter();

    private async Task LoadDataAsync(CancellationToken ct)
    {
        var today = Today;
        var people = await Wf.DirectoryAsync(Actor, ct);
        var days = (await Wf.ComputeAttendanceAsync(Actor, today, today, ct: ct)).ToDictionary(d => d.EmployeeId);
        var punches = (await Wf.RecentPunchesAsync(Actor, LocalStartUtc(today.AddDays(-1)), Now, 5000, ct)).ToLookup(p => p.EmployeeId);
        _all = people.Where(p => p.Status != EmployeeStatuses.Terminated).Select(p =>
        {
            var last = punches[p.Id].FirstOrDefault();
            var state = last?.Type switch
            {
                PunchTypes.In or PunchTypes.BreakEnd => StateIn,
                PunchTypes.BreakStart => StateBreak,
                PunchTypes.Out => StateOut,
                _ => StateAway,
            };
            var d = days.GetValueOrDefault(p.Id);
            return new LiveRow(p.Code, p.Name, state, Loc.Get("wf.live." + state), last is null ? "" : Time(last.OccurredAtUtc),
                d?.ShiftCode ?? "", Minutes(d?.WorkedMinutes ?? 0), d?.LateMinutes ?? 0, d is { LateMinutes: > 0 } ? Minutes(d.LateMinutes) : "");
        }).OrderBy(r => r.State switch { StateIn => 0, StateBreak => 1, StateOut => 2, _ => 3 }).ThenBy(r => r.Employee, StringComparer.CurrentCultureIgnoreCase).ToList();
        InCount = _all.Count(r => r.State == StateIn);
        BreakCount = _all.Count(r => r.State == StateBreak);
        OutCount = _all.Count(r => r.State == StateOut);
        AwayCount = _all.Count(r => r.State == StateAway);
        UpdatedAt = Time(Now);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = Search.Trim();
        Fill(Rows, _all.Where(r => (StateFilter.Length == 0 || r.State == StateFilter)
            && (q.Length == 0 || r.Employee.Contains(q, StringComparison.CurrentCultureIgnoreCase) || r.EmployeeNo.Contains(q, StringComparison.OrdinalIgnoreCase))));
    }
}
