using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Workforce;

namespace Veltrix.Desktop.Presentation.ViewModels;

public sealed record HolidayRow(Guid Id, string Date, string Name, bool IsPaid);

/// <summary>Workforce settings: attendance policy (duplicate window, punch windows), overtime policy and rounding, payroll multipliers, rest,
/// anti-passback per zone, leave types and holidays. Grace periods are set per shift template (Shift planner).</summary>
public sealed partial class WfSettingsViewModel : WorkforcePageViewModel
{
    public WfSettingsViewModel(DesktopServices s) : base(s)
    {
        _holidayDate = FromDate(Today);
        _holidayYear = Today.Year.ToString(CultureInfo.InvariantCulture);
    }

    public override string TitleKey => "wf.nav.settings";
    public static IReadOnlyList<RoundingMode> RoundingModes { get; } = Enum.GetValues<RoundingMode>();
    public static IReadOnlyList<OvertimeBasis> OvertimeBases { get; } = Enum.GetValues<OvertimeBasis>();
    public static IReadOnlyList<AbsenceDeductionMethod> AbsenceMethods { get; } = Enum.GetValues<AbsenceDeductionMethod>();
    public static IReadOnlyList<LateAdjustmentMethod> LateMethods { get; } = Enum.GetValues<LateAdjustmentMethod>();
    public static IReadOnlyList<string> AntiPassbackModesList => WfAccessViewModel.AntiPassbackModesList;

    public ObservableCollection<WfLeaveType> LeaveTypes { get; } = [];
    public ObservableCollection<HolidayRow> Holidays { get; } = [];
    public ObservableCollection<WfZone> Zones { get; } = [];

    private WorkforcePolicy _policy = new();

    // Attendance
    [ObservableProperty] private string _duplicateWindow = "60";
    [ObservableProperty] private string _earlyWindow = "240";
    [ObservableProperty] private string _lateWindow = "360";
    [ObservableProperty] private bool _autoDeductBreak = true;
    [ObservableProperty] private string _timeZoneId = "";
    // Overtime
    [ObservableProperty] private OvertimeBasis _otBasis;
    [ObservableProperty] private string _otThreshold = "480";
    [ObservableProperty] private string _otMinimum = "15";
    [ObservableProperty] private string _otStep = "15";
    [ObservableProperty] private RoundingMode _otRounding;
    // Payroll
    [ObservableProperty] private string _monthlyHours = "173.33";
    [ObservableProperty] private string _tier1Minutes = "120";
    [ObservableProperty] private string _tier1Multiplier = "1.5";
    [ObservableProperty] private string _tier2Multiplier = "1.75";
    [ObservableProperty] private string _restDayMultiplier = "2";
    [ObservableProperty] private string _holidayMultiplier = "2";
    [ObservableProperty] private string _nightPremium = "0";
    [ObservableProperty] private AbsenceDeductionMethod _absenceMethod;
    [ObservableProperty] private LateAdjustmentMethod _lateMethod;
    [ObservableProperty] private string _lateTolerance = "0";
    [ObservableProperty] private string _minRestHours = "11";

    // Leave types
    [ObservableProperty] private WfLeaveType? _selectedLeaveType;
    [ObservableProperty] private string _ltCode = "";
    [ObservableProperty] private string _ltName = "";
    [ObservableProperty] private bool _ltPaid = true;
    [ObservableProperty] private string _ltAccrual = "0";
    [ObservableProperty] private bool _ltTracks;
    [ObservableProperty] private bool _ltNegative;
    [ObservableProperty] private bool _ltActive = true;

    // Holidays
    [ObservableProperty] private string _holidayYear;
    [ObservableProperty] private DateTime? _holidayDate;
    [ObservableProperty] private string _holidayName = "";
    [ObservableProperty] private bool _holidayPaid = true;

    // Anti-passback
    [ObservableProperty] private WfZone? _selectedZone;
    [ObservableProperty] private string _zoneMode = AntiPassbackModes.None;

    public bool CanPolicy => HasGlobal(WorkforcePermissions.PayrollConfigure);
    public bool CanLeave => HasGlobal(WorkforcePermissions.LeaveManage);
    public bool CanHolidays => HasGlobal(WorkforcePermissions.ShiftsManage);
    public bool CanZones => HasGlobal(WorkforcePermissions.AccessManage);

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        _policy = await Wf.PolicyAsync(ct);
        var a = _policy.Attendance;
        (DuplicateWindow, EarlyWindow, LateWindow, AutoDeductBreak, TimeZoneId) =
            (Text(a.DuplicateWindowSeconds), Text(a.EarlyWindowMinutes), Text(a.LateWindowMinutes), a.AutoDeductBreak, a.TimeZoneId ?? "");
        var o = _policy.Overtime;
        (OtBasis, OtThreshold, OtMinimum, OtStep, OtRounding) = (o.Basis, Text(o.DailyThresholdMinutes), Text(o.MinimumMinutes), Text(o.RoundingStepMinutes), o.Rounding);
        var p = _policy.Payroll;
        (MonthlyHours, Tier1Minutes, Tier1Multiplier, Tier2Multiplier, RestDayMultiplier, HolidayMultiplier, NightPremium, AbsenceMethod, LateMethod, LateTolerance) =
            (Text(p.StandardMonthlyHours), Text(p.Tier1MinutesPerDay), Text(p.Tier1Multiplier), Text(p.Tier2Multiplier), Text(p.RestDayMultiplier), Text(p.HolidayMultiplier),
             Text(p.NightPremiumPercent), p.AbsenceMethod, p.LateMethod, Text(p.LateToleranceMinutesPerMonth));
        MinRestHours = Text(_policy.Rest.MinRestHours);
        await Wf.EnsureDefaultLeaveTypesAsync(ct);
        Fill(LeaveTypes, await Wf.LeaveTypesAsync(ct));
        if (Has(WorkforcePermissions.ShiftsView)) await LoadHolidaysAsync(ct);
        if (CanZones) Fill(Zones, await Wf.ZonesAsync(Actor, ct));
        return true;
    }

    private async Task LoadHolidaysAsync(CancellationToken ct)
    {
        int? year = int.TryParse(HolidayYear, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : null;
        Fill(Holidays, (await Wf.HolidaysAsync(Actor, year, ct)).Select(h => new HolidayRow(h.Id, DateText(h.Date), h.Name, h.IsPaid)));
    }

    public WorkforcePolicy BuildPolicy() => _policy with
    {
        Attendance = _policy.Attendance with
        {
            DuplicateWindowSeconds = ParseInt(DuplicateWindow, "wf.err.minutes", 0, 3600), EarlyWindowMinutes = ParseInt(EarlyWindow, "wf.err.minutes", 0, 1440),
            LateWindowMinutes = ParseInt(LateWindow, "wf.err.minutes", 0, 1440), AutoDeductBreak = AutoDeductBreak,
            TimeZoneId = string.IsNullOrWhiteSpace(TimeZoneId) ? null : TimeZoneId.Trim(),
        },
        Overtime = _policy.Overtime with
        {
            Basis = OtBasis, DailyThresholdMinutes = ParseInt(OtThreshold, "wf.err.minutes", 0, 1440), MinimumMinutes = ParseInt(OtMinimum, "wf.err.minutes", 0, 1440),
            RoundingStepMinutes = ParseInt(OtStep, "wf.err.minutes", 0, 240), Rounding = OtRounding,
        },
        Payroll = _policy.Payroll with
        {
            StandardMonthlyHours = ParseDecimal(MonthlyHours, "wf.err.rate"), Tier1MinutesPerDay = ParseInt(Tier1Minutes, "wf.err.minutes", 0, 1440),
            Tier1Multiplier = ParseDecimal(Tier1Multiplier, "wf.err.rate"), Tier2Multiplier = ParseDecimal(Tier2Multiplier, "wf.err.rate"),
            RestDayMultiplier = ParseDecimal(RestDayMultiplier, "wf.err.rate"), HolidayMultiplier = ParseDecimal(HolidayMultiplier, "wf.err.rate"),
            NightPremiumPercent = ParseDecimal(NightPremium, "wf.err.rate"), AbsenceMethod = AbsenceMethod, LateMethod = LateMethod,
            LateToleranceMinutesPerMonth = ParseInt(LateTolerance, "wf.err.minutes", 0, 100000),
        },
        Rest = _policy.Rest with { MinRestHours = ParseDecimal(MinRestHours, "wf.err.rate") },
    };

    [RelayCommand]
    public Task SavePolicyAsync() => SubmitAsync(async () =>
    {
        var policy = BuildPolicy();
        await Wf.SavePolicyAsync(Actor, policy);
        _policy = policy;
    });

    // ---- leave types

    partial void OnSelectedLeaveTypeChanged(WfLeaveType? value)
    {
        if (value is null) return;
        (LtCode, LtName, LtPaid, LtAccrual, LtTracks, LtNegative, LtActive) =
            (value.Code, value.Name, value.IsPaid, Text(value.AccrualDaysPerMonth), value.TracksBalance, value.AllowNegative, value.IsActive);
    }

    [RelayCommand]
    public void NewLeaveType()
    {
        SelectedLeaveType = null;
        (LtCode, LtName, LtPaid, LtAccrual, LtTracks, LtNegative, LtActive) = ("", "", true, "0", false, false, true);
    }

    [RelayCommand]
    public Task SaveLeaveTypeAsync() => SubmitAsync(async () =>
    {
        var t = await Wf.UpsertLeaveTypeAsync(Actor, SelectedLeaveType?.Id,
            new LeaveTypeInput(LtCode.Trim(), LtName.Trim(), LtPaid, ParseDecimal(LtAccrual, "wf.err.accrual"), LtTracks, LtNegative, LtActive));
        Fill(LeaveTypes, await Wf.LeaveTypesAsync());
        SelectedLeaveType = LeaveTypes.FirstOrDefault(x => x.Id == t.Id);
    });

    // ---- holidays

    [RelayCommand] public Task LoadHolidayYearAsync() => TryActionAsync(() => LoadHolidaysAsync(CancellationToken.None));

    [RelayCommand]
    public Task AddHolidayAsync() => SubmitAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(HolidayName)) throw new InvalidOperationException("wf.err.nameRequired");
        await Wf.AddHolidayAsync(Actor, ToDate(HolidayDate, Today), HolidayName.Trim(), HolidayPaid);
        HolidayName = "";
        await LoadHolidaysAsync(CancellationToken.None);
    });

    [RelayCommand]
    public Task RemoveHolidayAsync(HolidayRow? row) => SubmitAsync(async () =>
    {
        if (row is null || !S.Ui.Confirm(Loc.Get("wf.set.removeHolidayConfirm"))) return;
        await Wf.RemoveHolidayAsync(Actor, row.Id);
        await LoadHolidaysAsync(CancellationToken.None);
    });

    // ---- anti-passback

    partial void OnSelectedZoneChanged(WfZone? value)
    {
        if (value is not null) ZoneMode = value.AntiPassback;
    }

    [RelayCommand]
    public Task SaveZoneModeAsync() => SubmitAsync(async () =>
    {
        if (SelectedZone is not { } z) throw new InvalidOperationException("wf.err.unknownZone");
        await Wf.UpsertZoneAsync(Actor, z.Id, z.Name, ZoneMode);
        Fill(Zones, await Wf.ZonesAsync(Actor));
        SelectedZone = Zones.FirstOrDefault(x => x.Id == z.Id);
    });
}
