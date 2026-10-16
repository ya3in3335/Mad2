using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Workforce;

namespace Veltrix.Desktop.Presentation.ViewModels;

public sealed record LeaveRow(Guid Id, string EmployeeNo, string Employee, string LeaveType, string From, string To, decimal Days, string? Reason, string RequestedBy,
    string Status, string StatusLabel, string? Attachment)
{
    public bool IsPending => Status == RequestStatuses.Pending;
    public bool IsCancellable => Status is RequestStatuses.Pending or RequestStatuses.Approved;
}

/// <summary>Leave: requests (request / approve / reject / cancel), balances (accrual, adjustments).</summary>
public sealed partial class WfLeaveViewModel : WorkforcePageViewModel
{
    public WfLeaveViewModel(DesktopServices s) : base(s)
    {
        _from = FromDate(Today);
        _to = FromDate(Today);
        _accrualYear = Today.Year.ToString(CultureInfo.InvariantCulture);
        _accrualMonth = Today.Month;
    }

    public override string TitleKey => "wf.nav.leave";
    public static IReadOnlyList<int> Months { get; } = Enumerable.Range(1, 12).ToList();

    public ObservableCollection<LeaveRow> Requests { get; } = [];
    public ObservableCollection<LeaveBalanceRow> Balances { get; } = [];
    public ObservableCollection<EmployeeChoice> Employees { get; } = [];
    public ObservableCollection<WfLeaveType> LeaveTypes { get; } = [];
    public IReadOnlyList<Choice<string?>> StatusFilters =>
        [new(RequestStatuses.Pending, T("req", RequestStatuses.Pending)), new(RequestStatuses.Approved, T("req", RequestStatuses.Approved)),
         new(RequestStatuses.Rejected, T("req", RequestStatuses.Rejected)), new(RequestStatuses.Cancelled, T("req", RequestStatuses.Cancelled)), new(null, Loc.Get("wf.all"))];

    /// <summary>0 requests, 1 balances.</summary>
    [ObservableProperty] private int _tabIndex;
    [ObservableProperty] private string? _statusFilter = RequestStatuses.Pending;
    [ObservableProperty] private EmployeeChoice? _employee;
    [ObservableProperty] private WfLeaveType? _leaveType;
    [ObservableProperty] private DateTime? _from;
    [ObservableProperty] private DateTime? _to;
    [ObservableProperty] private string _reason = "";
    [ObservableProperty] private string? _attachment;

    [ObservableProperty] private EmployeeChoice? _adjustEmployee;
    [ObservableProperty] private WfLeaveType? _adjustType;
    [ObservableProperty] private string _adjustDays = "";
    [ObservableProperty] private string _adjustNote = "";
    [ObservableProperty] private string _accrualYear;
    [ObservableProperty] private int _accrualMonth;

    public bool CanRequest => Has(WorkforcePermissions.LeaveRequest);
    public bool CanApprove => Has(WorkforcePermissions.LeaveApprove);
    public bool CanManage => HasGlobal(WorkforcePermissions.LeaveManage);

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        var people = await Wf.DirectoryAsync(Actor, ct);
        var names = people.ToDictionary(p => p.Id);
        if (Employees.Count != people.Count)
        {
            Fill(Employees, people.Select(EmployeeChoice.From));
            Employee ??= Actor.EmployeeId is { } self ? Employees.FirstOrDefault(e => e.Id == self) : Employees.FirstOrDefault();
        }
        var types = await Wf.LeaveTypesAsync(ct);
        if (LeaveTypes.Count != types.Count)
        {
            Fill(LeaveTypes, types.Where(t => t.IsActive));
            LeaveType ??= LeaveTypes.FirstOrDefault();
        }
        var typeNames = types.ToDictionary(t => t.Id, t => t.Name);
        var list = await Wf.LeaveRequestsAsync(Actor, StatusFilter, ct);
        Fill(Requests, list.OrderByDescending(r => r.RequestedAt).Select(r => new LeaveRow(r.Id, names.GetValueOrDefault(r.EmployeeId)?.Code ?? "",
            names.GetValueOrDefault(r.EmployeeId)?.Name ?? "", typeNames.GetValueOrDefault(r.LeaveTypeId) ?? "", DateText(r.From), DateText(r.To), r.Days, r.Reason,
            r.RequestedBy, r.Status, T("req", r.Status), r.AttachmentPath)));
        Fill(Balances, await Wf.LeaveBalancesAsync(Actor, ct: ct));
        return Requests.Count > 0 || Balances.Count > 0 || CanRequest;
    }

    partial void OnStatusFilterChanged(string? value) { if (!IsLoading) _ = ReloadAsync(); }

    [RelayCommand]
    public void PickAttachment() => Attachment = S.Ui.PickOpenFile(Loc.Get("wf.doc.filter") + "|*.*") ?? Attachment;

    [RelayCommand]
    public Task RequestAsync() => SubmitAsync(async () =>
    {
        var emp = Employee ?? throw new InvalidOperationException("erp.err.unknownEmployee");
        var type = LeaveType ?? throw new InvalidOperationException("wf.err.unknownLeaveType");
        await Wf.RequestLeaveAsync(Actor, new LeaveRequestInput(emp.Id, type.Id, ToDate(From, Today), ToDate(To, Today),
            string.IsNullOrWhiteSpace(Reason) ? null : Reason.Trim(), Attachment));
        Reason = "";
        Attachment = null;
        await ReloadAsync();
    }, "wf.leave.requested");

    [RelayCommand] public Task ApproveAsync(LeaveRow? row) => DecideAsync(row, true);
    [RelayCommand] public Task RejectAsync(LeaveRow? row) => DecideAsync(row, false);

    private Task DecideAsync(LeaveRow? row, bool approve) => SubmitAsync(async () =>
    {
        if (row is null || !row.IsPending) return;
        await Wf.DecideLeaveAsync(Actor, row.Id, approve);
        await ReloadAsync();
    }, approve ? "wf.leave.approved" : "wf.leave.rejected");

    [RelayCommand]
    public Task CancelRequestAsync(LeaveRow? row) => SubmitAsync(async () =>
    {
        if (row is null || !row.IsCancellable || !S.Ui.Confirm(Loc.Get("wf.leave.cancelConfirm"))) return;
        await Wf.CancelLeaveAsync(Actor, row.Id);
        await ReloadAsync();
    });

    [RelayCommand]
    public Task AdjustAsync() => SubmitAsync(async () =>
    {
        var emp = AdjustEmployee ?? throw new InvalidOperationException("erp.err.unknownEmployee");
        var type = AdjustType ?? throw new InvalidOperationException("wf.err.unknownLeaveType");
        if (!Parse.TryDecimal(AdjustDays, out var days) && !decimal.TryParse(AdjustDays, NumberStyles.Number, CultureInfo.InvariantCulture, out days))
            throw new InvalidOperationException("wf.err.adjustment");
        await Wf.AdjustLeaveBalanceAsync(Actor, emp.Id, type.Id, days, AdjustNote);
        AdjustDays = "";
        AdjustNote = "";
        await ReloadAsync();
    });

    [RelayCommand]
    public Task AccrueAsync() => SubmitAsync(async () =>
    {
        var year = ParseInt(AccrualYear, "wf.err.period", 2000, 2100);
        var n = await Wf.AccrueLeaveAsync(Actor, year, AccrualMonth);
        await ReloadAsync();
        StatusMessage = Loc.Format("wf.leave.accrued", n);
    }, null);

    [RelayCommand]
    public Task ExportCsvAsync() => SubmitAsync(() => TabIndex == 1 ? ExportCsvAsync("leave-balances", Balances) : ExportCsvAsync("leave-requests", Requests), null);
}
