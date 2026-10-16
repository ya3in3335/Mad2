using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Diagnostics;
using Veltrix.Desktop.Core.Security;
using Veltrix.Desktop.Core.Session;
using Veltrix.Desktop.Core.Settings;
using Veltrix.Desktop.Core.Standalone;
using Veltrix.Desktop.Core.Workforce;
using Veltrix.Desktop.Core.Workforce.Devices;
using Veltrix.Desktop.Presentation;
using Veltrix.Desktop.Presentation.ViewModels;

namespace Veltrix.Desktop.Tests;

/// <summary>Workforce screens (view models): kiosk scan flow with the virtual terminal, RBAC navigation, correction approval, CSV import.</summary>
public sealed class WorkforceUiTests : IAsyncLifetime
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly WorkforceTests.ManualTime _time = new(new DateTimeOffset(2026, 10, 12, 7, 55, 0, TimeSpan.Zero)); // Monday
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "veltrix-wfui-" + Guid.NewGuid().ToString("N"));
    private static readonly WorkforceActor Owner = new("owner", LocalRoles.Owner);
    private StandaloneService _standalone = null!;

    private LocalDb Db() => new(new DbContextOptionsBuilder<LocalDb>().UseSqlite(_conn).Options);

    public async Task InitializeAsync()
    {
        await _conn.OpenAsync();
        await using var db = Db();
        await db.InitializeAsync();
        Directory.CreateDirectory(_dir);
        _standalone = new StandaloneService(new SecureStore(Path.Combine(_dir, "store"), new PlainProtector()), Db, _time, () => true, PasswordHasher.MinimumIterations);
    }

    public async Task DisposeAsync()
    {
        await _conn.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private WorkforceService Wf => _standalone.Workforce;

    /// <summary>View-model services for a local user of <paramref name="role"/> (no server, no UI).</summary>
    private DesktopServices Services(string role, FakeUi? ui = null, Guid? employeeId = null)
    {
        var store = new SecureStore(Path.Combine(_dir, "session-" + Guid.NewGuid().ToString("N")), new PlainProtector());
        var session = new SessionService(null!, new SecureTokenStore(store), store);
        session.SignInLocal(new LocalUser(Guid.NewGuid(), role + "-user", role, "x", _time.GetUtcNow(), true, employeeId));
        return new DesktopServices
        {
            Api = null!, Settings = new AppSettings(), SettingsStore = null!, Secrets = store, Tokens = null!, License = null!, Binding = null!, Session = session,
            Db = Db, Sync = null!, Pos = null!, Loc = new KeyLocalizer(), Ui = ui ?? new FakeUi(), Time = _time, Log = new AppLog(Path.Combine(_dir, "logs"), _time),
            Version = "test", Standalone = _standalone,
        };
    }

    private async Task<LocalEmployee> EmployeeAsync(string name) =>
        await Wf.UpsertEmployeeAsync(Owner, null, new EmployeeProfileInput(name, 40000, new DateOnly(2026, 1, 1)));

    // ------------------------------------------------------------------ kiosk

    [Fact]
    public async Task Kiosk_scan_flow_with_the_virtual_terminal_punches_in_then_out_and_rejects_unknown_cards()
    {
        var amine = await EmployeeAsync("Amine");
        await Wf.AssignCardAsync(Owner, amine.Id, "04:A1:B2:C3");
        var vm = new WfKioskViewModel(Services(LocalRoles.Owner));
        var shown = new List<KioskFeedback>();
        vm.FeedbackShown += (_, f) => shown.Add(f);
        await using var terminal = new VirtualTerminal(Guid.NewGuid(), _time);
        vm.AttachDevice(terminal);
        await terminal.ConnectAsync();

        terminal.SimulateCard("04a1b2c3");
        var first = await vm.LastScan!;
        Assert.True(first.Success);
        Assert.Equal(PunchTypes.In, first.PunchType);
        Assert.Equal("Amine", first.EmployeeName);
        Assert.Equal("wf.kiosk.recorded", first.Detail);

        // Same card again within the duplicate window: acknowledged, not recorded twice.
        terminal.SimulateCard("04A1B2C3");
        var dup = await vm.LastScan!;
        Assert.True(dup.Success);
        Assert.Equal("wf.kiosk.duplicate", dup.Detail);

        _time.Advance(TimeSpan.FromHours(8));
        terminal.SimulateCard("04A1B2C3");
        Assert.Equal(PunchTypes.Out, (await vm.LastScan!).PunchType);

        terminal.SimulateCard("DEADBEEF");
        var unknown = await vm.LastScan!;
        Assert.False(unknown.Success);
        Assert.Null(unknown.EmployeeName);
        Assert.Equal(AccessReasons.UnknownCredential, unknown.Detail);
        Assert.Same(unknown, vm.Feedback);

        Assert.Equal(4, shown.Count);
        var events = await Wf.EventsAsync(Owner, amine.Id, _time.GetUtcNow().AddDays(-1), _time.GetUtcNow());
        Assert.Equal([PunchTypes.In, PunchTypes.Out], events.Select(e => e.Type));
        Assert.All(events, e => Assert.Equal(PunchSources.Card, e.Source));
        Assert.All(events, e => Assert.Equal(terminal.DeviceId, e.DeviceId));

        // Detached: further scans are ignored by the kiosk.
        vm.DetachDevice(terminal);
        var last = vm.LastScan;
        terminal.SimulateCard("04A1B2C3");
        Assert.Same(last, vm.LastScan);
    }

    [Fact]
    public async Task Kiosk_offline_terminal_logs_are_replayed_on_reconnect_with_their_original_time()
    {
        var sara = await EmployeeAsync("Sara");
        await Wf.AssignCardAsync(Owner, sara.Id, "11223344");
        var vm = new WfKioskViewModel(Services(LocalRoles.Owner));
        await using var terminal = new VirtualTerminal(Guid.NewGuid(), _time);
        vm.AttachDevice(terminal);
        var scannedAt = _time.GetUtcNow();
        terminal.SimulateCard("11223344"); // terminal offline: kept in its log
        Assert.Null(vm.LastScan);
        _time.Advance(TimeSpan.FromMinutes(30));
        await terminal.ConnectAsync();
        Assert.True((await vm.LastScan!).Success);
        var e = Assert.Single(await Wf.EventsAsync(Owner, sara.Id, scannedAt.AddHours(-1), _time.GetUtcNow()));
        Assert.Equal(scannedAt, e.OccurredAtUtc);
    }

    [Fact]
    public async Task Kiosk_keyboard_wedge_text_and_pin_keypad_record_punches()
    {
        var karim = await EmployeeAsync("Karim"); // E0001
        await Wf.AssignCardAsync(Owner, karim.Id, "A0B0C0D0");
        await Wf.SetPinAsync(Owner, karim.Id, "4321");
        var vm = new WfKioskViewModel(Services(LocalRoles.Owner));

        // A keyboard-wedge reader types the UID then Enter in the capture box.
        vm.ScanText = "A0B0C0D0";
        await vm.SubmitScanCommand.ExecuteAsync(null);
        Assert.True(vm.Feedback!.Success);
        Assert.Equal("", vm.ScanText);

        // QR tokens typed by 2D scanners carry the "VQR:" prefix.
        Assert.Equal((CredentialKinds.Qr, "VQ1.abc"), WfKioskViewModel.Classify("VQR:VQ1.abc"));
        Assert.Equal((CredentialKinds.Card, "04A1"), WfKioskViewModel.Classify("04A1"));

        _time.Advance(TimeSpan.FromHours(4));
        // Keypad: "1" resolves to employee number E0001, OK moves to the PIN, OK again punches.
        vm.KeypadDigitCommand.Execute("1");
        await vm.KeypadOkCommand.ExecuteAsync(null);
        Assert.False(vm.KeypadOnEmployee);
        foreach (var d in "4321") vm.KeypadDigitCommand.Execute(d.ToString());
        Assert.Equal("••••", vm.PinMask);
        await vm.KeypadOkCommand.ExecuteAsync(null);
        Assert.True(vm.Feedback!.Success);
        Assert.Equal(PunchTypes.Out, vm.Feedback.PunchType);
        Assert.Equal("", vm.Pin);

        _time.Advance(TimeSpan.FromHours(1));
        vm.KeypadDigitCommand.Execute("1");
        vm.FocusPinCommand.Execute(null);
        foreach (var d in "9999") vm.KeypadDigitCommand.Execute(d.ToString());
        await vm.KeypadOkCommand.ExecuteAsync(null);
        Assert.False(vm.Feedback!.Success);

        var types = (await Wf.EventsAsync(Owner, karim.Id, _time.GetUtcNow().AddDays(-1), _time.GetUtcNow())).Select(e => (e.Type, e.Source));
        Assert.Equal([(PunchTypes.In, PunchSources.Card), (PunchTypes.Out, PunchSources.Pin)], types);
    }

    // ------------------------------------------------------------------ navigation (RBAC)

    [Fact]
    public void Workforce_navigation_follows_the_role_permissions()
    {
        string[] all = ["wf.dashboard", "wf.employees", "wf.kiosk", "wf.history", "wf.live", "wf.shifts", "wf.leave", "wf.overtime", "wf.access", "wf.devices",
            "wf.payroll", "wf.reports", "wf.settings"];
        Assert.Equal(all, WorkforceNav.Entries.Select(e => e.Key));
        Assert.Equal(all, WorkforceNav.VisibleKeysForRole(LocalRoles.Owner));
        Assert.Equal(all, WorkforceNav.VisibleKeysForRole(LocalRoles.HrAdmin));
        Assert.Equal(["wf.dashboard", "wf.kiosk", "wf.history", "wf.live", "wf.shifts", "wf.leave"], WorkforceNav.VisibleKeysForRole(LocalRoles.Employee));
        Assert.Equal(["wf.dashboard", "wf.employees", "wf.history", "wf.live", "wf.shifts", "wf.access", "wf.payroll", "wf.reports"],
            WorkforceNav.VisibleKeysForRole(LocalRoles.Auditor));
        // Department managers are scoped: organisation-wide pages are hidden, the emergency unlock keeps the access page.
        var manager = WorkforceNav.VisibleKeysForRole(LocalRoles.DeptManager);
        Assert.Contains("wf.overtime", manager);
        Assert.Contains("wf.access", manager);
        Assert.DoesNotContain("wf.devices", manager);
        Assert.DoesNotContain("wf.settings", manager);
        Assert.DoesNotContain("wf.payroll", manager);
        Assert.DoesNotContain("wf.overtime", WorkforceNav.VisibleKeysForRole(LocalRoles.AttendanceSupervisor));
        Assert.Empty(WorkforceNav.VisibleKeysForRole(LocalRoles.Cashier));

        // The shell applies the same rule through the session permissions.
        var s = Services(LocalRoles.Employee);
        var visible = WorkforceNav.VisibleKeys(s.Session.HasPermission, WorkforcePermissions.IsScoped(LocalRoles.Employee));
        Assert.Equal(WorkforceNav.VisibleKeysForRole(LocalRoles.Employee), visible);
        foreach (var key in all) Assert.Equal(key, ShortcutScopeOrKey(WorkforceNav.Create(key, Services(LocalRoles.Owner)), key));
    }

    private static string ShortcutScopeOrKey(PageViewModel page, string key)
    {
        // Every workforce page has its own view model and title; shortcut scopes resolve to the page type.
        Assert.Equal("wf.nav." + key[3..], page.TitleKey);
        if (ShortcutMap.ScopeTypes.TryGetValue(key, out var t)) Assert.IsType(t, page);
        return key;
    }

    // ------------------------------------------------------------------ corrections

    [Fact]
    public async Task Correction_approval_command_applies_the_correction_and_needs_the_permission()
    {
        var nadia = await EmployeeAsync("Nadia");
        var at = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
        var request = await Wf.RequestCorrectionAsync(Owner, new CorrectionInput(nadia.Id, CorrectionKinds.AddPunch, "forgot badge", PunchTypes.In, at));

        // A supervisor may request but not approve: the button is hidden and the service refuses.
        var supervisorVm = new WfAttendanceViewModel(Services(LocalRoles.AttendanceSupervisor));
        await supervisorVm.RefreshAsync();
        Assert.False(supervisorVm.CanApprove);
        var row = Assert.Single(supervisorVm.Corrections);
        await supervisorVm.ApproveCorrectionCommand.ExecuteAsync(row);
        Assert.Equal("err.permission", supervisorVm.FormError);
        Assert.Single(await Wf.CorrectionsAsync(Owner, RequestStatuses.Pending));

        var vm = new WfAttendanceViewModel(Services(LocalRoles.HrAdmin));
        await vm.RefreshAsync();
        Assert.True(vm.CanApprove);
        row = Assert.Single(vm.Corrections);
        Assert.Equal(request.Id, row.Id);
        Assert.True(row.IsPending);
        vm.DecisionNote = "checked with CCTV";
        await vm.ApproveCorrectionCommand.ExecuteAsync(row);
        Assert.Null(vm.FormError);
        Assert.Equal("wf.corr.approved", vm.StatusMessage);
        Assert.Empty(vm.Corrections); // filter = pending

        var decided = Assert.Single(await Wf.CorrectionsAsync(Owner, RequestStatuses.Approved));
        Assert.Equal("checked with CCTV", decided.DecisionNote);
        var ev = Assert.Single(await Wf.EventsAsync(Owner, nadia.Id, at.AddHours(-1), at.AddHours(1)));
        Assert.Equal(PunchSources.Correction, ev.Source);
        // Approving twice is impossible (only pending rows carry the command).
        await vm.ApproveCorrectionCommand.ExecuteAsync(row with { Status = RequestStatuses.Approved });
        Assert.Single(await Wf.CorrectionsAsync(Owner, RequestStatuses.Approved));
    }

    [Fact]
    public async Task Correction_request_form_validates_and_submits()
    {
        var omar = await EmployeeAsync("Omar");
        var vm = new WfAttendanceViewModel(Services(LocalRoles.Owner));
        await vm.RefreshAsync();
        vm.CorrectionEmployee = vm.Employees.Single(e => e.Id == omar.Id);
        await vm.RequestCorrectionCommand.ExecuteAsync(null);
        Assert.Equal("wf.err.reasonRequired", vm.FormError);
        vm.CorrectionReason = "reader down";
        vm.CorrectionTime = "17:05";
        vm.CorrectionPunchType = PunchTypes.Out;
        await vm.RequestCorrectionCommand.ExecuteAsync(null);
        Assert.Null(vm.FormError);
        var c = Assert.Single(vm.Corrections);
        Assert.Equal(PunchTypes.Out, c.PunchType); // key localizer: untranslated codes fall back to the raw value
    }

    // ------------------------------------------------------------------ employees CSV

    [Fact]
    public async Task Employee_csv_import_creates_and_updates_by_employee_number()
    {
        var existing = await EmployeeAsync("Yacine"); // E0001
        var vm = new WfEmployeesViewModel(Services(LocalRoles.HrAdmin));
        await vm.RefreshAsync();
        var csv = "Code;Name;Department;Salary;HireDate;Status\n" +
                  $"{existing.Code};Yacine B.;Sales;52000,50;2026-02-01;Active\n" +
                  ";\"Lina, K.\";Sales;30000;2026-03-01;Active\n" +
                  ";;Sales;1;2026-03-01;Active\n";
        var (created, updated, errors) = await vm.ImportAsync(csv);
        Assert.Equal((1, 1), (created, updated));
        Assert.Single(errors);
        var all = await Wf.EmployeesAsync(Owner);
        Assert.Equal(52000.50m, all.Single(e => e.Id == existing.Id).Salary);
        Assert.Contains(all, e => e.Name == "Lina, K.");
        var sales = Assert.Single(await Wf.DepartmentsAsync(Owner));
        Assert.All(all, e => Assert.Equal(sales.Id, e.DepartmentId));
        Assert.Equal(new List<List<string>> { new() { "a", "b,c", "=x" } }, CsvReader.Parse("a,\"b,c\",'=x\r\n"));
    }

    // ------------------------------------------------------------------ fakes

    private sealed class KeyLocalizer : ILocalizer
    {
        public string Get(string key) => key;
        public CultureInfo Culture => CultureInfo.InvariantCulture;
        public event EventHandler? LanguageChanged { add { } remove { } }
    }

    private sealed class FakeUi : IPlatformUi
    {
        public bool ConfirmResult { get; set; } = true;
        public void ApplyLanguage(string code) { }
        public void ApplyTheme(string theme) { }
        public void ApplyAccent(string accent) { }
        public bool PrintReceipt(string title, Core.Pos.ReceiptDocument receipt) => false;
        public bool PrintTable(string title, System.Data.DataTable table) => false;
        public string? PickSaveFile(string suggestedName, string filter) => null;
        public string? PickOpenFile(string filter) => null;
        public void CopyToClipboard(string text) { }
        public bool Confirm(string message) => ConfirmResult;
        public void ShowImage(string path, string title) { }
        public bool SystemReducedMotion => true;
    }

    private sealed class PlainProtector : IDataProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.Reverse().ToArray();
        public byte[] Unprotect(byte[] ciphertext) => ciphertext.Reverse().ToArray();
    }
}
