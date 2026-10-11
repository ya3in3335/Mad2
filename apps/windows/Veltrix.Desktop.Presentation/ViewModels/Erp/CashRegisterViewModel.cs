using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Contracts.Pos;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Erp;

namespace Veltrix.Desktop.Presentation.ViewModels;

/// <summary>Cash register: open the day (float), cash in/out, cash book of the day, and closing with counted cash (reuses the POS shift close,
/// which posts any over/short to 6583).</summary>
public sealed partial class CashRegisterViewModel : ErpPageViewModel
{
    public CashRegisterViewModel(DesktopServices s) : base(s)
    {
        _day = TodayDateTime;
        _openingFloat = "0";
    }

    public override string TitleKey => "erp.nav.cash";

    public ObservableCollection<LocalCashMovement> Movements { get; } = [];
    public ObservableCollection<Choice<string>> CounterAccounts { get; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasOpenShift), nameof(NoOpenShift))] private CashRegisterSummary? _summary;
    [ObservableProperty] private DateTime? _day;
    [ObservableProperty] private string _openingFloat;
    /// <summary>true = cash in, false = cash out.</summary>
    [ObservableProperty] private bool _isCashIn = true;
    [ObservableProperty] private string _amount = "";
    [ObservableProperty] private string _reason = "";
    [ObservableProperty] private Choice<string>? _counterAccount;
    [ObservableProperty] private string _countedCash = "";
    [ObservableProperty] private string _closeNotes = "";
    [ObservableProperty] private ShiftDto? _lastClose;
    [ObservableProperty] private decimal _dayIn;
    [ObservableProperty] private decimal _dayOut;

    public bool HasOpenShift => Summary?.Shift is not null;
    public bool NoOpenShift => !HasOpenShift;

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        if (CounterAccounts.Count == 0)
        {
            Fill(CounterAccounts, (await Erp.AccountsAsync(true, ct)).Where(a => a.Role != AccountRoles.Cash).Select(a => new Choice<string>(a.Code, ErpText.AccountLabel(a, Lang))));
            CounterAccount = CounterAccounts.FirstOrDefault(a => a.Value == "512");
        }
        Summary = await Erp.CashRegisterAsync(ct);
        var day = ToDate(Day, Erp.Today);
        var moves = await Erp.CashMovementsAsync(day, day, ct);
        Fill(Movements, moves);
        DayIn = moves.Where(m => m.Amount > 0).Sum(m => m.Amount);
        DayOut = -moves.Where(m => m.Amount < 0).Sum(m => m.Amount);
        return true;
    }

    partial void OnDayChanged(DateTime? value) => _ = ReloadAsync();

    [RelayCommand]
    public Task OpenDayAsync() => SubmitAsync(async () =>
    {
        if (HasOpenShift) return;
        var amount = ParseAmount(OpeningFloat, allowZero: true);
        var reg = await S.Standalone.Store.EnsureRegisterAsync();
        await S.Pos.OpenShiftAsync(reg.Id, amount, default);
        LastClose = null;
        await ReloadAsync();
    });

    [RelayCommand]
    public Task RecordAsync() => SubmitAsync(async () =>
    {
        var amount = ParseAmount(Amount);
        if (string.IsNullOrWhiteSpace(Reason)) throw new InvalidOperationException("erp.err.descriptionRequired");
        var counter = CounterAccount?.Value ?? throw new InvalidOperationException("erp.err.accountInvalid");
        await Erp.RecordCashMovementAsync(IsCashIn ? amount : -amount, Reason, counter, Erp.Today);
        Amount = "";
        Reason = "";
        await ReloadAsync();
    });

    [RelayCommand]
    public Task CloseDayAsync() => SubmitAsync(async () =>
    {
        if (Summary?.Shift is not { } shift) return;
        var counted = ParseAmount(CountedCash, allowZero: true);
        if (!S.Ui.Confirm(Loc.Get("erp.confirm.closeDay"))) return;
        LastClose = await S.Pos.CloseShiftAsync(shift.Id, counted, string.IsNullOrWhiteSpace(CloseNotes) ? null : CloseNotes.Trim(), default);
        CountedCash = "";
        CloseNotes = "";
        await ReloadAsync();
    }, "erp.msg.dayClosed");
}
