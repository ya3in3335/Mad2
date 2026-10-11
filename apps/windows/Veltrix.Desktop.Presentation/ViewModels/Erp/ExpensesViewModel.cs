using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Erp;

namespace Veltrix.Desktop.Presentation.ViewModels;

/// <summary>Expenses by category (rent, electricity, salaries…) paid by cash or bank; each posts Dr category account / Cr 530|512.</summary>
public sealed partial class ExpensesViewModel : ErpPageViewModel
{
    public ExpensesViewModel(DesktopServices s) : base(s)
    {
        _from = MonthStart;
        _to = TodayDateTime;
        _date = TodayDateTime;
    }

    public override string TitleKey => "erp.nav.expenses";

    public ObservableCollection<ExpenseRow> Items { get; } = [];
    public ObservableCollection<LocalExpenseCategory> Categories { get; } = [];
    public ObservableCollection<Choice<string>> ExpenseAccounts { get; } = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private DateTime? _from;
    [ObservableProperty] private DateTime? _to;
    [ObservableProperty] private LocalExpenseCategory? _categoryFilter;
    [ObservableProperty] private decimal _total;

    // New expense form
    [ObservableProperty] private DateTime? _date;
    [ObservableProperty] private LocalExpenseCategory? _category;
    [ObservableProperty] private string _amount = "";
    [ObservableProperty] private string _taxAmount = "0";
    [ObservableProperty] private string _method = PayMethods.Cash;
    [ObservableProperty] private string _payee = "";
    [ObservableProperty] private string _description = "";

    // New category form
    [ObservableProperty] private string _newCategoryName = "";
    [ObservableProperty] private Choice<string>? _newCategoryAccount;

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        if (Categories.Count == 0) await LoadCategoriesAsync(ct);
        var rows = await Erp.ExpensesAsync(ToDate(From, Erp.Today.AddYears(-10)), ToDate(To, Erp.Today), CategoryFilter?.Id, Search, ct);
        Fill(Items, rows);
        Total = rows.Sum(r => r.Expense.Amount);
        return Items.Count > 0;
    }

    private async Task LoadCategoriesAsync(CancellationToken ct = default)
    {
        Fill(Categories, (await Erp.ExpenseCategoriesAsync(ct)).Where(c => c.IsActive));
        Fill(ExpenseAccounts, (await Erp.AccountsAsync(true, ct)).Where(a => a.Type == AccountTypes.Expense).Select(a => new Choice<string>(a.Code, ErpText.AccountLabel(a, Lang))));
        Category ??= Categories.FirstOrDefault();
    }

    partial void OnCategoryFilterChanged(LocalExpenseCategory? value) => _ = ReloadAsync();

    [RelayCommand]
    public Task SearchNowAsync() => ReloadAsync();

    [RelayCommand]
    public Task CreateAsync() => SubmitAsync(async () =>
    {
        var cat = Category ?? throw new InvalidOperationException("erp.err.unknownCategory");
        var amount = ParseAmount(Amount);
        if (!Parse.TryDecimal(TaxAmount, out var tax) || tax < 0 || tax >= amount) throw new InvalidOperationException("erp.err.taxInvalid");
        await Erp.CreateExpenseAsync(new ExpenseInput(ToDate(Date, Erp.Today), cat.Id, amount, tax, Method, Payee, Description));
        Amount = "";
        TaxAmount = "0";
        Payee = "";
        Description = "";
        await ReloadAsync();
    }, "erp.msg.expenseRecorded");

    [RelayCommand]
    public Task AddCategoryAsync() => SubmitAsync(async () =>
    {
        var account = NewCategoryAccount?.Value ?? throw new InvalidOperationException("erp.err.accountInvalid");
        var c = await Erp.UpsertExpenseCategoryAsync(null, NewCategoryName, account);
        NewCategoryName = "";
        await LoadCategoriesAsync();
        Category = Categories.FirstOrDefault(x => x.Id == c.Id);
    });
}
