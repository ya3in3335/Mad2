using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Workforce;

namespace Veltrix.Desktop.Presentation.ViewModels;

public sealed record OvertimeRow(Guid EmployeeId, DateOnly Date, string DateText, string EmployeeNo, string Employee, string Shift, string Worked, int OvertimeMinutes,
    string Overtime, string Night, string Status, string StatusLabel, string? DecidedBy, string? Note)
{
    public bool IsPending => Status == RequestStatuses.Pending;
}

/// <summary>Overtime review: computed overtime per employee and day (after the overtime policy), approved or rejected by a manager.</summary>
public sealed partial class WfOvertimeViewModel : WorkforcePageViewModel
{
    public WfOvertimeViewModel(DesktopServices s) : base(s)
    {
        _from = FromDate(new DateOnly(Today.Year, Today.Month, 1));
        _to = FromDate(Today);
    }

    public override string TitleKey => "wf.nav.overtime";

    public ObservableCollection<OvertimeRow> Rows { get; } = [];
    public IReadOnlyList<Choice<string?>> StatusFilters =>
        [new(RequestStatuses.Pending, T("req", RequestStatuses.Pending)), new(RequestStatuses.Approved, T("req", RequestStatuses.Approved)),
         new(RequestStatuses.Rejected, T("req", RequestStatuses.Rejected)), new(null, Loc.Get("wf.all"))];

    [ObservableProperty] private DateTime? _from;
    [ObservableProperty] private DateTime? _to;
    [ObservableProperty] private string? _statusFilter = RequestStatuses.Pending;
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _totalPending = "0:00";
    [ObservableProperty] private string _totalApproved = "0:00";

    public bool CanApprove => Has(WorkforcePermissions.CorrectionsApprove);

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        var from = ToDate(From, Today);
        var to = ToDate(To, from);
        if (to < from) (from, to) = (to, from);
        if (to.DayNumber - from.DayNumber > 92) throw new InvalidOperationException("wf.err.rangeTooLong");
        var people = (await Wf.DirectoryAsync(Actor, ct)).ToDictionary(p => p.Id);
        var days = (await Wf.ComputeAttendanceAsync(Actor, from, to, ct: ct)).Where(d => d.OvertimeMinutes > 0).ToList();
        var decisions = (await Wf.OvertimeDecisionsAsync(Actor, from, to, ct)).ToDictionary(d => (d.EmployeeId, d.Date));
        var all = days.Select(d =>
        {
            var p = people.GetValueOrDefault(d.EmployeeId);
            var dec = decisions.GetValueOrDefault((d.EmployeeId, d.Date));
            // A decision covers the minutes it was taken on: recomputed (changed) overtime goes back to review.
            var status = dec is not null && dec.Minutes == d.OvertimeMinutes ? dec.Status : RequestStatuses.Pending;
            return new OvertimeRow(d.EmployeeId, d.Date, DateText(d.Date), p?.Code ?? "", p?.Name ?? "", d.ShiftCode ?? "", Minutes(d.WorkedMinutes), d.OvertimeMinutes,
                Minutes(d.OvertimeMinutes), d.NightMinutes > 0 ? Minutes(d.NightMinutes) : "", status, T("req", status), dec?.DecidedBy, dec?.Note);
        }).OrderByDescending(r => r.Date).ThenBy(r => r.Employee, StringComparer.CurrentCultureIgnoreCase).ToList();
        TotalPending = Minutes(all.Where(r => r.IsPending).Sum(r => r.OvertimeMinutes));
        TotalApproved = Minutes(all.Where(r => r.Status == RequestStatuses.Approved).Sum(r => r.OvertimeMinutes));
        Fill(Rows, all.Where(r => StatusFilter is null || r.Status == StatusFilter));
        return Rows.Count > 0;
    }

    partial void OnStatusFilterChanged(string? value) { if (!IsLoading) _ = ReloadAsync(); }

    [RelayCommand] public Task SearchNowAsync() => ReloadAsync();
    [RelayCommand] public Task ApproveAsync(OvertimeRow? row) => DecideAsync(row is null ? [] : [row], true);
    [RelayCommand] public Task RejectAsync(OvertimeRow? row) => DecideAsync(row is null ? [] : [row], false);

    /// <summary>Approves every pending row currently listed.</summary>
    [RelayCommand]
    public Task ApproveAllAsync()
    {
        var pending = Rows.Where(r => r.IsPending).ToList();
        if (pending.Count == 0 || !S.Ui.Confirm(Loc.Format("wf.ot.approveAllConfirm", pending.Count))) return Task.CompletedTask;
        return DecideAsync(pending, true);
    }

    private Task DecideAsync(IReadOnlyList<OvertimeRow> rows, bool approve) => SubmitAsync(async () =>
    {
        foreach (var r in rows)
            await Wf.DecideOvertimeAsync(Actor, r.EmployeeId, r.Date, r.OvertimeMinutes, approve, string.IsNullOrWhiteSpace(Note) ? null : Note.Trim());
        Note = "";
        await ReloadAsync();
    }, approve ? "wf.ot.approved" : "wf.ot.rejected");

    [RelayCommand]
    public Task ExportCsvAsync() => SubmitAsync(() => ExportCsvAsync("overtime", Rows), null);
}
