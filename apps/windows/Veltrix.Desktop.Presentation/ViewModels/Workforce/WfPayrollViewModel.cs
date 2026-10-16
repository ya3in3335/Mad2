using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Workforce;

namespace Veltrix.Desktop.Presentation.ViewModels;

/// <summary>Payroll preparation: period → compute (attendance, overtime, absences, items, statutory rules) → review → post to accounting; statutory rules editor.</summary>
public sealed partial class WfPayrollViewModel : WorkforcePageViewModel
{
    public WfPayrollViewModel(DesktopServices s) : base(s)
    {
        var prev = Today.AddMonths(-1);
        _year = prev.Year.ToString(CultureInfo.InvariantCulture);
        _month = prev.Month;
    }

    public override string TitleKey => "wf.nav.payroll";
    public static IReadOnlyList<int> Months { get; } = Enumerable.Range(1, 12).ToList();
    /// <summary>Built-in English notice of the core engine (always shown with the localized one).</summary>
    public static string NoticeText => StatutoryNotice.Text;

    public ObservableCollection<WfPayrollPrepLine> Lines { get; } = [];
    public ObservableCollection<WfStatutoryRule> Rules { get; } = [];

    /// <summary>0 preparation, 1 statutory rules.</summary>
    [ObservableProperty] private int _tabIndex;
    [ObservableProperty] private string _year;
    [ObservableProperty] private int _month;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanPost), nameof(IsPosted), nameof(HasPrep))] private WfPayrollPrep? _prep;
    [ObservableProperty] private decimal _totalGross;
    [ObservableProperty] private decimal _totalNet;
    [ObservableProperty] private decimal _totalEmployer;

    [ObservableProperty] private WfStatutoryRule? _selectedRule;
    [ObservableProperty] private string _ruleCode = "";
    [ObservableProperty] private string _ruleName = "";
    [ObservableProperty] private string _ruleOrder = "1";
    [ObservableProperty] private string _ruleEmployeeRate = "0";
    [ObservableProperty] private string _ruleEmployerRate = "0";
    [ObservableProperty] private string _ruleBrackets = "";
    [ObservableProperty] private bool _ruleAfterPrevious;
    [ObservableProperty] private bool _ruleEnabled;
    [ObservableProperty] private string _ruleNotes = "";

    public bool HasPrep => Prep is not null;
    public bool IsPosted => Prep?.PayrollRunId is not null;
    public bool CanPrepare => HasGlobal(WorkforcePermissions.PayrollPrepare);
    public bool CanPost => HasGlobal(WorkforcePermissions.PayrollPost) && Prep is { PayrollRunId: null };
    public bool CanConfigure => HasGlobal(WorkforcePermissions.PayrollConfigure);

    private int YearValue => ParseInt(Year, "wf.err.period", 2000, 2100);

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        var view = await Wf.PayrollPrepAsync(Actor, YearValue, Month, ct);
        Show(view);
        if (CanConfigure) Fill(Rules, (await Wf.StatutoryRulesAsync(Actor, ct)).OrderBy(r => r.Order));
        return true;
    }

    private void Show(PayrollPrepView? view)
    {
        Prep = view?.Prep;
        Fill(Lines, view?.Lines.OrderBy(l => l.EmployeeName, StringComparer.CurrentCultureIgnoreCase) ?? Enumerable.Empty<WfPayrollPrepLine>());
        TotalGross = Lines.Sum(l => l.Gross);
        TotalNet = Lines.Sum(l => l.Net);
        TotalEmployer = Lines.Sum(l => l.StatutoryEmployer);
    }

    partial void OnMonthChanged(int value) { if (!IsLoading && IsReady) _ = ReloadAsync(); }

    [RelayCommand] public Task LoadPeriodAsync() => ReloadAsync();

    [RelayCommand]
    public Task ComputeAsync() => SubmitAsync(async () =>
    {
        if (IsPosted) throw new InvalidOperationException("wf.err.alreadyPosted");
        Show(await Wf.PreparePayrollAsync(Actor, YearValue, Month));
    }, "wf.pay.computed");

    [RelayCommand]
    public Task PostAsync() => SubmitAsync(async () =>
    {
        if (!CanPost || !S.Ui.Confirm(Loc.Format("wf.pay.postConfirm", Month, YearValue))) return;
        var run = await Wf.PostPayrollPrepAsync(Actor, YearValue, Month);
        await ReloadAsync();
        StatusMessage = Loc.Format("wf.pay.posted", run.Number);
    }, null);

    [RelayCommand]
    public Task ExportCsvAsync() => SubmitAsync(() => ExportCsvAsync($"payroll-{YearValue}-{Month:00}", Lines.Select(l => new
    {
        l.EmployeeName, l.BaseSalary, WorkedHours = Math.Round(l.WorkedMinutes / 60m, 2), OvertimeHours = Math.Round(l.OvertimeMinutes / 60m, 2), l.OvertimeAmount, l.NightPremium,
        l.AbsenceDays, l.AbsenceDeduction, l.LateMinutes, l.LateDeduction, l.Bonuses, l.Allowances, l.ShiftPremiums, l.OtherDeductions, l.Gross, l.StatutoryEmployee,
        l.StatutoryEmployer, l.Net,
    })), null);

    // ---- statutory rules

    partial void OnSelectedRuleChanged(WfStatutoryRule? value)
    {
        if (value is null) return;
        RuleCode = value.Code;
        RuleName = value.Name;
        RuleOrder = Text(value.Order);
        RuleEmployeeRate = Text(value.EmployeeRatePercent);
        RuleEmployerRate = Text(value.EmployerRatePercent);
        RuleBrackets = FormatBrackets(value.BracketsJson);
        RuleAfterPrevious = value.BaseAfterPreviousRules;
        RuleEnabled = value.IsEnabled;
        RuleNotes = value.Notes ?? "";
    }

    [RelayCommand]
    public void NewRule()
    {
        SelectedRule = null;
        (RuleCode, RuleName, RuleOrder, RuleEmployeeRate, RuleEmployerRate, RuleBrackets, RuleAfterPrevious, RuleEnabled, RuleNotes) =
            ("", "", Text(Rules.Count + 1), "0", "0", "", false, false, "");
    }

    /// <summary>Brackets, one per line: "from-to rate" (empty "to" = no upper bound), e.g. "0-30000 0" / "30000-120000 23" / "120000- 27".</summary>
    public static IReadOnlyList<TaxBracket>? ParseBrackets(string text)
    {
        var lines = text.Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return null;
        return lines.Select(line =>
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var range = parts.Length == 2 ? parts[0].Split('-') : [];
            if (range.Length != 2 || !Parse.TryDecimal(range[0], out var from) || !Parse.TryDecimal(parts[1], out var rate))
                throw new InvalidOperationException("wf.err.brackets");
            decimal? to = range[1].Length == 0 ? null : Parse.TryDecimal(range[1], out var t) ? t : throw new InvalidOperationException("wf.err.brackets");
            return new TaxBracket(from, to, rate);
        }).ToList();
    }

    public static string FormatBrackets(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            var list = JsonSerializer.Deserialize<List<TaxBracket>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
            return string.Join("\n", list.Select(b => $"{Text(b.From)}-{(b.To is { } t ? Text(t) : "")} {Text(b.RatePercent)}"));
        }
        catch (JsonException) { return ""; }
    }

    [RelayCommand]
    public Task SaveRuleAsync() => SubmitAsync(async () =>
    {
        var input = new StatutoryRuleInput(RuleCode.Trim(), RuleName.Trim(), ParseInt(RuleOrder, "wf.err.rate", 0, 1000), ParseDecimal(RuleEmployeeRate, "wf.err.rate"),
            ParseDecimal(RuleEmployerRate, "wf.err.rate"), ParseBrackets(RuleBrackets), RuleAfterPrevious, RuleEnabled, string.IsNullOrWhiteSpace(RuleNotes) ? null : RuleNotes.Trim());
        // Enabling a rule is an explicit act: the user confirms the rates were verified with official sources.
        if (RuleEnabled && SelectedRule is not { IsEnabled: true } && !S.Ui.Confirm(Loc.Get("wf.pay.enableConfirm"))) return;
        var saved = await Wf.UpsertStatutoryRuleAsync(Actor, SelectedRule?.Id, input);
        await ReloadAsync();
        SelectedRule = Rules.FirstOrDefault(r => r.Id == saved.Id);
    });
}
