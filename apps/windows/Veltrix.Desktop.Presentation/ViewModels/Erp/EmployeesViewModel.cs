using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Erp;

namespace Veltrix.Desktop.Presentation.ViewModels;

public sealed partial class EmployeeEditor : ObservableObject
{
    public LocalEmployee? Original { get; init; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _role = "";
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _salary = "0";
    [ObservableProperty] private DateTime? _hireDate;
    [ObservableProperty] private bool _isActive = true;

    public static EmployeeEditor For(LocalEmployee e) => new()
    {
        Original = e, Name = e.Name, Role = e.Role ?? "", Phone = e.Phone ?? "", Salary = DocLineEditor.Text(e.Salary), HireDate = e.HireDate.ToDateTime(TimeOnly.MinValue), IsActive = e.IsActive,
    };

    public EmployeeInput ToInput(DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidOperationException("erp.err.nameRequired");
        if (!Parse.TryDecimal(Salary, out var salary) || salary < 0) throw new InvalidOperationException("erp.err.salary");
        return new EmployeeInput(Name, Role, Phone, salary, HireDate is { } d ? DateOnly.FromDateTime(d) : today, IsActive);
    }
}

/// <summary>HR (basic): employees, salary advances and the monthly payroll run (internal calculation — no tax or social-security computation).</summary>
public sealed partial class EmployeesViewModel : ErpPageViewModel
{
    public EmployeesViewModel(DesktopServices s) : base(s)
    {
        _runYear = Erp.Today.Year.ToString(CultureInfo.InvariantCulture);
        _runMonth = Erp.Today.Month;
        _advanceDate = TodayDateTime;
        _payDate = TodayDateTime;
    }

    public override string TitleKey => "erp.nav.hr";
    public static IReadOnlyList<int> Months => AccountingViewModel.Months;

    public ObservableCollection<LocalEmployee> Items { get; } = [];
    public ObservableCollection<AdvanceRow> Advances { get; } = [];
    public ObservableCollection<LocalPayrollRun> Runs { get; } = [];
    public ObservableCollection<LocalPayslip> Payslips { get; } = [];

    /// <summary>0 employees, 1 advances, 2 payroll.</summary>
    [ObservableProperty] private int _tabIndex;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _activeOnly = true;
    [ObservableProperty] private LocalEmployee? _selected;
    [ObservableProperty] private EmployeeEditor? _editor;

    [ObservableProperty] private LocalEmployee? _advanceEmployee;
    [ObservableProperty] private string _advanceAmount = "";
    [ObservableProperty] private string _advanceMethod = PayMethods.Cash;
    [ObservableProperty] private DateTime? _advanceDate;

    [ObservableProperty] private string _runYear;
    [ObservableProperty] private int _runMonth;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanPay))] private LocalPayrollRun? _selectedRun;
    [ObservableProperty] private string _payMethod = PayMethods.Bank;
    [ObservableProperty] private DateTime? _payDate;

    public bool CanPay => SelectedRun is { Status: PayrollStatuses.Posted };
    public decimal MonthlyPayroll => Items.Where(e => e.IsActive).Sum(e => e.Salary);

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        Fill(Items, await Erp.EmployeesAsync(Search, ActiveOnly, ct));
        OnPropertyChanged(nameof(MonthlyPayroll));
        Fill(Advances, await Erp.AdvancesAsync(null, false, ct));
        var keep = SelectedRun?.Id;
        Fill(Runs, await Erp.PayrollRunsAsync(ct));
        if (keep is { } id) SelectedRun = Runs.FirstOrDefault(r => r.Id == id);
        return TabIndex switch { 1 => Advances.Count > 0 || Items.Count > 0, 2 => true, _ => Items.Count > 0 };
    }

    partial void OnActiveOnlyChanged(bool value) => _ = ReloadAsync();

    partial void OnSelectedChanged(LocalEmployee? value)
    {
        FormError = null;
        Editor = value is null ? null : EmployeeEditor.For(value);
    }

    partial void OnSelectedRunChanged(LocalPayrollRun? value)
    {
        Payslips.Clear();
        if (value is not null) _ = LoadPayslipsAsync(value.Id);
    }

    private async Task LoadPayslipsAsync(Guid runId)
    {
        try
        {
            var slips = await Erp.PayslipsAsync(runId);
            if (SelectedRun?.Id == runId) Fill(Payslips, slips);
        }
        catch (Exception e) { S.Log.Warn("Payslips failed", e); }
    }

    [RelayCommand] public Task SearchNowAsync() => ReloadAsync();

    [RelayCommand]
    public void New()
    {
        Selected = null;
        FormError = null;
        Editor = new EmployeeEditor { HireDate = TodayDateTime };
    }

    [RelayCommand]
    public Task SaveAsync() => SubmitAsync(async () =>
    {
        if (Editor is null) return;
        var saved = await Erp.UpsertEmployeeAsync(Editor.Original?.Id, Editor.ToInput(Erp.Today));
        await ReloadAsync();
        Selected = Items.FirstOrDefault(i => i.Id == saved.Id);
    });

    [RelayCommand]
    public Task GiveAdvanceAsync() => SubmitAsync(async () =>
    {
        var e = AdvanceEmployee ?? throw new InvalidOperationException("erp.err.unknownEmployee");
        await Erp.GiveAdvanceAsync(e.Id, ParseAmount(AdvanceAmount), AdvanceMethod, ToDate(AdvanceDate, Erp.Today));
        AdvanceAmount = "";
        await ReloadAsync();
    }, "erp.msg.paymentRecorded");

    [RelayCommand]
    public Task RunPayrollAsync() => SubmitAsync(async () =>
    {
        if (!int.TryParse(RunYear, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)) throw new InvalidOperationException("erp.err.period");
        if (!S.Ui.Confirm(Loc.Get("erp.confirm.payroll"))) return;
        var run = await Erp.RunPayrollAsync(y, RunMonth);
        await ReloadAsync();
        SelectedRun = Runs.FirstOrDefault(r => r.Id == run.Id);
    }, "erp.msg.payrollPosted");

    [RelayCommand]
    public Task PayRunAsync() => SubmitAsync(async () =>
    {
        if (SelectedRun is not { } run || !CanPay) return;
        await Erp.PayPayrollAsync(run.Id, PayMethod, ToDate(PayDate, Erp.Today));
        await ReloadAsync();
    }, "erp.msg.paymentRecorded");
}
