using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Erp;

namespace Veltrix.Desktop.Presentation.ViewModels;

public sealed partial class ManualLineEditor : ObservableObject
{
    [ObservableProperty] private Choice<string>? _account;
    [ObservableProperty] private string _debit = "";
    [ObservableProperty] private string _credit = "";
    [ObservableProperty] private string _description = "";

    public decimal DebitValue => Parse.DecimalOr(Debit, 0);
    public decimal CreditValue => Parse.DecimalOr(Credit, 0);
}

/// <summary>Manual journal entry form: shows running totals and only allows saving a balanced entry.</summary>
public sealed partial class ManualEntryEditor : ObservableObject
{
    public ManualEntryEditor()
    {
        Lines.CollectionChanged += (_, e) =>
        {
            foreach (ManualLineEditor l in e.NewItems ?? Array.Empty<ManualLineEditor>()) l.PropertyChanged += (_, _) => Recalc();
            Recalc();
        };
        Lines.Add(new ManualLineEditor());
        Lines.Add(new ManualLineEditor());
    }

    public ObservableCollection<ManualLineEditor> Lines { get; } = [];
    [ObservableProperty] private DateTime? _date;
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private ManualLineEditor? _selectedLine;

    public decimal TotalDebit => Lines.Sum(l => l.DebitValue);
    public decimal TotalCredit => Lines.Sum(l => l.CreditValue);
    public decimal Difference => TotalDebit - TotalCredit;
    public bool IsBalanced => TotalDebit > 0 && Difference == 0;

    private void Recalc()
    {
        OnPropertyChanged(nameof(TotalDebit));
        OnPropertyChanged(nameof(TotalCredit));
        OnPropertyChanged(nameof(Difference));
        OnPropertyChanged(nameof(IsBalanced));
    }

    [RelayCommand] public void AddLine() => Lines.Add(new ManualLineEditor());

    [RelayCommand]
    public void RemoveLine(ManualLineEditor? line)
    {
        if ((line ?? SelectedLine) is { } l && Lines.Count > 2) Lines.Remove(l);
        Recalc();
    }

    public ManualEntryInput ToInput(DateOnly date)
    {
        if (string.IsNullOrWhiteSpace(Description)) throw new InvalidOperationException("erp.err.descriptionRequired");
        var lines = Lines.Where(l => l.Account is not null || l.DebitValue != 0 || l.CreditValue != 0).ToList();
        if (lines.Count < 2) throw new InvalidOperationException("erp.err.linesRequired");
        if (lines.Any(l => l.Account is null)) throw new InvalidOperationException("erp.err.accountInvalid");
        if (lines.Any(l => !Parse.TryDecimal(string.IsNullOrWhiteSpace(l.Debit) ? "0" : l.Debit, out _) || !Parse.TryDecimal(string.IsNullOrWhiteSpace(l.Credit) ? "0" : l.Credit, out _)))
            throw new InvalidOperationException("erp.err.lineInvalid");
        if (!IsBalanced) throw new InvalidOperationException("erp.err.unbalanced");
        return new ManualEntryInput(date, Description, lines.Select(l => new ManualLineInput(l.Account!.Value, l.DebitValue, l.CreditValue, l.Description)).ToList());
    }
}

public sealed record PeriodRow(string Period, bool Closed);

/// <summary>Accounting: journal (with manual entries), general ledger, trial balance, income statement, balance sheet and period lock.</summary>
public sealed partial class AccountingViewModel : ErpPageViewModel
{
    public AccountingViewModel(DesktopServices s) : base(s)
    {
        _from = MonthStart;
        _to = TodayDateTime;
        _periodYear = Erp.Today.Year.ToString(CultureInfo.InvariantCulture);
        _periodMonth = Erp.Today.Month;
    }

    public override string TitleKey => "erp.nav.accounting";

    public static IReadOnlyList<string> Sources { get; } =
    [
        "", JournalSources.Manual, JournalSources.Pos, JournalSources.SalesReturn, JournalSources.Purchase, JournalSources.PurchaseReturn, JournalSources.SupplierPayment,
        JournalSources.CustomerPayment, JournalSources.Expense, JournalSources.Payroll, JournalSources.PayrollPayment, JournalSources.SalaryAdvance, JournalSources.Cash,
        JournalSources.Inventory,
    ];
    public static IReadOnlyList<int> Months { get; } = Enumerable.Range(1, 12).ToList();

    public ObservableCollection<Choice<string>> Accounts { get; } = [];
    public ObservableCollection<JournalEntryView> Journal { get; } = [];
    public ObservableCollection<PeriodRow> Periods { get; } = [];

    /// <summary>0 journal, 1 ledger, 2 trial balance, 3 income statement, 4 balance sheet, 5 periods.</summary>
    [ObservableProperty] private int _tabIndex;
    [ObservableProperty] private DateTime? _from;
    [ObservableProperty] private DateTime? _to;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _source = "";
    [ObservableProperty] private JournalEntryView? _selectedEntry;
    [ObservableProperty] private Choice<string>? _ledgerAccount;
    [ObservableProperty] private LedgerReport? _ledger;
    [ObservableProperty] private TrialBalanceReport? _trialBalance;
    [ObservableProperty] private IncomeStatementReport? _incomeStatement;
    [ObservableProperty] private BalanceSheetReport? _balanceSheet;
    [ObservableProperty] private ManualEntryEditor? _editor;
    [ObservableProperty] private string _periodYear;
    [ObservableProperty] private int _periodMonth;

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        if (Accounts.Count == 0)
            Fill(Accounts, (await Erp.AccountsAsync(true, ct)).Select(a => new Choice<string>(a.Code, ErpText.AccountLabel(a, Lang))));
        var from = ToDate(From, Erp.Today);
        var to = ToDate(To, Erp.Today);
        switch (TabIndex)
        {
            case 0:
                Fill(Journal, await Erp.JournalAsync(from, to, string.IsNullOrEmpty(Source) ? null : Source, Search, Lang, ct));
                return Journal.Count > 0;
            case 1:
                LedgerAccount ??= Accounts.FirstOrDefault(a => a.Value == "530");
                Ledger = LedgerAccount is null ? null : await Erp.LedgerAsync(LedgerAccount.Value, from, to, Lang, ct);
                return Ledger is not null;
            case 2:
                TrialBalance = await Erp.TrialBalanceAsync(from, to, Lang, ct);
                return TrialBalance.Rows.Count > 0;
            case 3:
                IncomeStatement = await Erp.IncomeStatementAsync(from, to, Lang, ct);
                return true;
            case 4:
                BalanceSheet = await Erp.BalanceSheetAsync(to, Lang, ct);
                return true;
            default:
                var closed = (await Erp.ClosedPeriodsAsync(ct)).Select(p => p.Period).ToHashSet();
                var months = Enumerable.Range(0, 24).Select(i => new DateOnly(Erp.Today.Year, Erp.Today.Month, 1).AddMonths(-i)).Select(Ledger_Key);
                Fill(Periods, months.Union(closed).OrderByDescending(p => p, StringComparer.Ordinal).Select(p => new PeriodRow(p, closed.Contains(p))));
                return true;
        }
    }

    private static string Ledger_Key(DateOnly d) => Core.Erp.Ledger.PeriodKey(d);

    partial void OnTabIndexChanged(int value) => _ = ReloadAsync();
    partial void OnLedgerAccountChanged(Choice<string>? value) { if (TabIndex == 1) _ = ReloadAsync(); }
    partial void OnSourceChanged(string value) { if (TabIndex == 0) _ = ReloadAsync(); }

    [RelayCommand]
    public Task ApplyAsync() => ReloadAsync();

    [RelayCommand]
    public void NewEntry()
    {
        FormError = null;
        Editor = new ManualEntryEditor { Date = TodayDateTime };
    }

    [RelayCommand]
    public void CancelEntry()
    {
        FormError = null;
        Editor = null;
    }

    [RelayCommand]
    public Task SaveEntryAsync() => SubmitAsync(async () =>
    {
        if (Editor is null) return;
        await Erp.CreateManualEntryAsync(Editor.ToInput(ToDate(Editor.Date, Erp.Today)));
        Editor = null;
        TabIndex = 0;
        await ReloadAsync();
    }, "erp.msg.entryPosted");

    private (int Year, int Month) Period() =>
        int.TryParse(PeriodYear, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) && y is >= 2000 and <= 2100 && PeriodMonth is >= 1 and <= 12
            ? (y, PeriodMonth) : throw new InvalidOperationException("erp.err.period");

    [RelayCommand]
    public Task ClosePeriodAsync() => SubmitAsync(async () =>
    {
        var (y, m) = Period();
        if (!S.Ui.Confirm(Loc.Get("erp.confirm.closePeriod"))) return;
        await Erp.ClosePeriodAsync(y, m);
        await ReloadAsync();
    });

    [RelayCommand]
    public Task ReopenPeriodAsync() => SubmitAsync(async () =>
    {
        var (y, m) = Period();
        await Erp.ReopenPeriodAsync(y, m);
        await ReloadAsync();
    });

    /// <summary>CSV of the trial balance (opened in a spreadsheet by the accountant).</summary>
    [RelayCommand]
    public Task ExportTrialBalanceAsync() => TryActionAsync(async () =>
    {
        var tb = TrialBalance ?? await Erp.TrialBalanceAsync(ToDate(From, Erp.Today), ToDate(To, Erp.Today), Lang);
        if (S.Ui.PickSaveFile($"trial-balance-{tb.To:yyyy-MM-dd}.csv", "CSV|*.csv") is not { } path) return;
        var sb = new System.Text.StringBuilder("Code;Account;Type;Debit;Credit;Balance\n");
        foreach (var r in tb.Rows)
            sb.Append(CultureInfo.InvariantCulture, $"{r.Code};\"{r.Name.Replace("\"", "\"\"")}\";{r.Type};{r.Debit};{r.Credit};{r.Balance}\n");
        sb.Append(CultureInfo.InvariantCulture, $";TOTAL;;{tb.TotalDebit};{tb.TotalCredit};\n");
        await File.WriteAllTextAsync(path, sb.ToString(), new System.Text.UTF8Encoding(true));
    }, "dk.saved");
}
