using System.Reflection;
using System.Windows.Input;
using Veltrix.Desktop.Presentation.ViewModels;

namespace Veltrix.Desktop.Presentation;

/// <summary>
/// One keyboard shortcut. <see cref="Command"/> is the name of an existing <see cref="ICommand"/> property on the scope's view model
/// (ShellViewModel for <see cref="ShortcutMap.Global"/>, the current page otherwise), or a view action starting with '@'
/// (focus a box, toggle the theme…) that the WPF shortcut service performs itself.
/// </summary>
/// <param name="Scope">"global", "page" (every page), a page key (NavItem.Key) or "system" (outside the app, display only).</param>
/// <param name="Gesture">Canonical gesture: modifiers in the order Ctrl, Shift, Alt, then the key (e.g. "Ctrl+Shift+T", "F5", "Esc").</param>
/// <param name="Parameter">Name of a view-model property passed as the command parameter (e.g. the selected row).</param>
/// <param name="Argument">Constant command parameter when <paramref name="Parameter"/> is null.</param>
/// <param name="When">Name of a view-model property that must be true / non-null for the shortcut to apply (e.g. an open editor).</param>
/// <param name="YieldsToText">Standard text editing keeps priority: ignored while a text box (or, for Enter, a button) has focus.</param>
public sealed record Shortcut(string Scope, string Gesture, string LabelKey, string Command,
    string? Parameter = null, object? Argument = null, string? When = null, bool YieldsToText = false)
{
    public bool IsViewAction => Command.StartsWith('@');
}

public sealed record ShortcutRow(string Gesture, string Label);
public sealed record ShortcutGroup(string Title, IReadOnlyList<ShortcutRow> Rows);

/// <summary>The app-wide shortcut table (Windows conventions + classic POS F-keys), mapped onto existing commands only.</summary>
public static class ShortcutMap
{
    public const string Global = "global", Page = "page", System = "system";

    // View actions (performed by the WPF layer).
    public const string FocusSearch = "@focusSearch", FocusScan = "@focusScan", ToggleTheme = "@theme", Launch = "@launch";

    /// <summary>Global launch hotkey set on the Start Menu shortcut by the installer (Win+V is reserved by Windows for the clipboard).</summary>
    public const string LaunchHotkey = "Ctrl+Alt+V";

    public static readonly IReadOnlyList<Shortcut> All = Build();

    /// <summary>View-model type each scope resolves its commands against.</summary>
    public static readonly IReadOnlyDictionary<string, Type> ScopeTypes = new Dictionary<string, Type>
    {
        [Global] = typeof(ShellViewModel),
        [Page] = typeof(PageViewModel),
        ["pos"] = typeof(PosViewModel),
        ["products"] = typeof(ProductsViewModel),
        ["customers"] = typeof(CustomersViewModel),
        ["sales"] = typeof(SalesViewModel),
        ["cameras"] = typeof(CamerasViewModel),
        ["erp.suppliers"] = typeof(SuppliersViewModel),
        ["erp.purchases"] = typeof(PurchasesViewModel),
        ["erp.accounting"] = typeof(AccountingViewModel),
        ["erp.hr"] = typeof(EmployeesViewModel),
        ["wf.employees"] = typeof(WfEmployeesViewModel),
        ["wf.kiosk"] = typeof(WfKioskViewModel),
        ["wf.history"] = typeof(WfAttendanceViewModel),
        ["wf.shifts"] = typeof(WfShiftPlannerViewModel),
        ["wf.leave"] = typeof(WfLeaveViewModel),
        ["wf.overtime"] = typeof(WfOvertimeViewModel),
        ["wf.access"] = typeof(WfAccessViewModel),
        ["wf.terminals"] = typeof(WfTerminalsViewModel),
        ["wf.review"] = typeof(WfReviewViewModel),
        ["wf.devices"] = typeof(WfDevicesViewModel),
        ["wf.payroll"] = typeof(WfPayrollViewModel),
        ["wf.reports"] = typeof(WfReportsViewModel),
    };

    /// <summary>Group title keys of the non-page scopes in the help overlay.</summary>
    public static readonly IReadOnlyDictionary<string, string> GroupTitleKeys = new Dictionary<string, string>
    {
        [Global] = "sc.group.global",
        [Page] = "sc.group.page",
        [System] = "sc.group.system",
    };

    private static List<Shortcut> Build()
    {
        var list = new List<Shortcut>();
        void Add(string scope, string gesture, string label, string command, string? parameter = null, object? argument = null, string? when = null, bool yields = false) =>
            list.Add(new Shortcut(scope, gesture, label, command, parameter, argument, when, yields));

        // Shell (always available once signed in).
        for (var i = 1; i <= 9; i++) Add(Global, $"Ctrl+{i}", "sc.goto", "NavigateToIndexCommand", argument: i - 1);
        Add(Global, "Ctrl+Tab", "sc.next", "NextPageCommand");
        Add(Global, "Ctrl+Shift+Tab", "sc.prev", "PreviousPageCommand");
        Add(Global, "Ctrl+L", "sc.signOut", "SignOutCommand");
        Add(Global, "Ctrl+Shift+T", "sc.theme", ToggleTheme);
        Add(Global, "F1", "sc.help", "ShowShortcutsCommand");
        Add(Global, "Ctrl+/", "sc.help", "ShowShortcutsCommand");

        // Every page.
        Add(Page, "F5", "sc.refresh", "RefreshCommand");
        Add(Page, "Ctrl+F", "sc.search", FocusSearch);

        // Point of sale: classic F-keys plus the common gestures.
        Add("pos", "F2", "sc.pos.remove", "RemoveLineCommand", parameter: "SelectedLine");
        Add("pos", "Delete", "sc.pos.remove", "RemoveLineCommand", parameter: "SelectedLine", yields: true);
        Add("pos", "F3", "sc.pos.inc", "IncrementCommand", parameter: "SelectedLine");
        Add("pos", "F4", "sc.pos.dec", "DecrementCommand", parameter: "SelectedLine");
        Add("pos", "F7", "sc.pos.scan", FocusScan);
        Add("pos", "F8", "sc.pos.pay", "BeginPaymentCommand", when: "CanSell");
        Add("pos", "F9", "sc.pos.complete", "CompleteSaleCommand", when: "IsPaying");
        Add("pos", "Enter", "sc.pos.complete", "CompleteSaleCommand", when: "IsPaying", yields: true);
        Add("pos", "Esc", "sc.pos.cancelPay", "CancelPaymentCommand", when: "IsPaying");
        Add("pos", "F12", "sc.pos.newSale", "ClearCartCommand");
        Add("pos", "Ctrl+N", "sc.pos.newSale", "ClearCartCommand");
        Add("pos", "Ctrl+P", "sc.pos.reprint", "PrintLastReceiptCommand");
        Add("pos", "Ctrl+F3", "sc.pos.reprint", "PrintLastReceiptCommand");

        // Record editors (New / Save on the open editor).
        foreach (var scope in new[] { "products", "customers", "erp.suppliers", "erp.hr" })
        {
            Add(scope, "Ctrl+N", "sc.new", "NewCommand");
            Add(scope, "Ctrl+S", "sc.save", "SaveCommand", when: "Editor");
        }

        Add("sales", "Ctrl+P", "sc.print", "ReprintCommand");

        Add("cameras", "Ctrl+N", "sc.new", "AddCommand");
        Add("cameras", "Ctrl+E", "sc.edit", "EditCommand");
        Add("cameras", "Delete", "sc.delete", "DeleteCommand", yields: true);

        Add("erp.purchases", "Ctrl+N", "sc.new", "NewInvoiceCommand");
        Add("erp.purchases", "Ctrl+S", "sc.save", "SaveDraftCommand", when: "Editor");
        Add("erp.purchases", "Ctrl+E", "sc.edit", "EditDraftCommand");
        Add("erp.purchases", "Delete", "sc.delete", "DeleteDraftCommand", yields: true);
        Add("erp.purchases", "Esc", "sc.cancel", "CancelEditCommand", when: "Editor");

        Add("erp.accounting", "Ctrl+N", "sc.new", "NewEntryCommand");
        Add("erp.accounting", "Ctrl+S", "sc.save", "SaveEntryCommand", when: "Editor");
        Add("erp.accounting", "Esc", "sc.cancel", "CancelEntryCommand", when: "Editor");

        // Workforce & attendance.
        Add("wf.employees", "Ctrl+N", "sc.new", "NewCommand");
        Add("wf.employees", "Ctrl+S", "sc.save", "SaveDetailsCommand", when: "Details");
        Add("wf.employees", "Esc", "sc.cancel", "CloseDetailsCommand", when: "Details");
        Add("wf.employees", "Ctrl+I", "sc.wf.import", "ImportCsvCommand");
        Add("wf.kiosk", "F7", "sc.wf.scan", FocusScan);
        Add("wf.kiosk", "F11", "sc.wf.fullScreen", "EnterFullScreenCommand");
        Add("wf.kiosk", "Esc", "sc.wf.exitKiosk", "ExitFullScreenCommand", when: "IsFullScreen");
        Add("wf.shifts", "Ctrl+Shift+P", "sc.wf.prevPeriod", "PreviousCommand");
        Add("wf.shifts", "Ctrl+Shift+N", "sc.wf.nextPeriod", "NextCommand");
        Add("wf.shifts", "Ctrl+D", "sc.wf.today", "GoTodayCommand");
        Add("wf.terminals", "Ctrl+N", "sc.new", "NewCommand");
        Add("wf.terminals", "Ctrl+S", "sc.save", "SaveCommand", when: "IsEditing");
        Add("wf.terminals", "Ctrl+T", "sc.wf.test", "DiagnoseCommand", when: "Selected");
        Add("wf.devices", "Ctrl+N", "sc.new", "NewCommand");
        Add("wf.devices", "Ctrl+S", "sc.save", "SaveCommand", when: "IsEditing");
        Add("wf.devices", "Ctrl+T", "sc.wf.test", "TestCommand", when: "Selected");
        Add("wf.payroll", "F9", "sc.wf.compute", "ComputeCommand");
        Add("wf.reports", "Ctrl+Enter", "sc.wf.run", "RunReportCommand");
        Add("wf.reports", "Ctrl+P", "sc.print", "PrintCommand");
        foreach (var scope in new[] { "wf.employees", "wf.history", "wf.leave", "wf.overtime", "wf.payroll", "wf.reports" })
            Add(scope, "Ctrl+E", "sc.wf.export", "ExportCsvCommand");
        Add("wf.access", "Ctrl+E", "sc.wf.export", "ExportLogsCommand");

        // Outside the app: Start Menu shortcut hotkey (launches, or brings the running window to front).
        Add(System, LaunchHotkey, "sc.launch", Launch);
        return list;
    }

    /// <summary>Shortcuts that can fire on <paramref name="pageKey"/>, most specific scope first.</summary>
    public static IEnumerable<Shortcut> Active(string? pageKey, string gesture) =>
        All.Where(s => s.Gesture == gesture && (s.Scope == pageKey || s.Scope == Page || s.Scope == Global))
            .OrderBy(s => s.Scope == pageKey ? 0 : s.Scope == Page ? 1 : 2);

    /// <summary>Canonical form of a gesture: modifiers Ctrl, Shift, Alt (in that order) then the key token.</summary>
    public static string Canonical(bool ctrl, bool shift, bool alt, string key) =>
        (ctrl ? "Ctrl+" : "") + (shift ? "Shift+" : "") + (alt ? "Alt+" : "") + key;

    /// <summary>Re-normalizes a written gesture ("shift+ctrl+t" → "Ctrl+Shift+T").</summary>
    public static string Normalize(string gesture)
    {
        var parts = gesture.Split('+');
        // "Ctrl+/" and "Ctrl++" style keys: the last token is the key even when it contains '+'.
        var key = parts[^1].Length == 0 && parts.Length > 1 ? "+" : parts[^1];
        var mods = parts.Take(parts.Length - (key == "+" ? 2 : 1)).Select(m => m.Trim().ToLowerInvariant()).ToHashSet();
        key = key.Length == 1 ? key.ToUpperInvariant() : char.ToUpperInvariant(key[0]) + key[1..];
        return Canonical(mods.Contains("ctrl") || mods.Contains("control"), mods.Contains("shift"), mods.Contains("alt"), key);
    }

    /// <summary>True when the shortcut's <see cref="Shortcut.When"/> condition holds on <paramref name="target"/>.</summary>
    public static bool Applies(Shortcut s, object target) => s.When is null || Read(target, s.When) is true or (not null and not bool);

    /// <summary>The existing command (and parameter) a shortcut maps to on <paramref name="target"/>; null for view actions or a missing command.</summary>
    public static (ICommand Command, object? Parameter)? Resolve(Shortcut s, object target)
    {
        if (s.IsViewAction || Read(target, s.Command) is not ICommand cmd) return null;
        return (cmd, s.Parameter is null ? s.Argument : Read(target, s.Parameter));
    }

    private static object? Read(object target, string property) =>
        target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);

    /// <summary>Display text for several gestures sharing one label ("F1 / Ctrl+/", "Ctrl+1 … Ctrl+9").</summary>
    public static string Display(IReadOnlyList<string> gestures) =>
        gestures.Count > 2 ? $"{gestures[0]} … {gestures[^1]}" : string.Join(" / ", gestures);

    /// <summary>Help overlay content: shell, every page, then each visible page (sidebar order), then the system hotkey.</summary>
    public static IReadOnlyList<ShortcutGroup> Groups(ILocalizer loc, IEnumerable<(string Key, string Title)> pages)
    {
        ShortcutGroup Group(string title, string scope) => new(title, All.Where(s => s.Scope == scope)
            .GroupBy(s => s.LabelKey)
            .Select(g => new ShortcutRow(Display(g.Select(s => s.Gesture).ToList()), loc.Get(g.Key)))
            .ToList());
        var groups = new List<ShortcutGroup> { Group(loc.Get(GroupTitleKeys[Global]), Global), Group(loc.Get(GroupTitleKeys[Page]), Page) };
        groups.AddRange(pages.Where(p => All.Any(s => s.Scope == p.Key)).Select(p => Group(p.Title, p.Key)));
        groups.Add(Group(loc.Get(GroupTitleKeys[System]), System));
        return groups;
    }

    /// <summary>Gesture text per command for the current page (tooltips / AcceleratorKey hints).</summary>
    public static Dictionary<ICommand, string> Hints(ShellViewModel shell)
    {
        var map = new Dictionary<ICommand, List<string>>(ReferenceEqualityComparer.Instance);
        var pageKey = shell.Selected?.Key;
        foreach (var s in All)
        {
            object? target = s.Scope == Global ? shell : s.Scope == Page || s.Scope == pageKey ? shell.CurrentPage : null;
            if (s.Scope == Global && s.Argument is not null) continue; // Ctrl+1..9 share one command: no hint
            if (target is null || Resolve(s, target) is not { } r) continue;
            if (!map.TryGetValue(r.Command, out var list)) map[r.Command] = list = [];
            if (!list.Contains(s.Gesture)) list.Add(s.Gesture);
        }
        var hints = new Dictionary<ICommand, string>(ReferenceEqualityComparer.Instance);
        foreach (var (cmd, gestures) in map) hints[cmd] = Display(gestures);
        return hints;
    }
}
