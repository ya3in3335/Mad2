using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Veltrix.Desktop.Presentation;
using Veltrix.Desktop.Presentation.ViewModels;

namespace Veltrix.Desktop.Platform;

/// <summary>
/// Central keyboard-shortcut dispatcher for the signed-in shell (table: <see cref="ShortcutMap"/>). Listens on the window's
/// PreviewKeyDown, resolves the gesture against the current page, then every page, then the shell; respects CanExecute and
/// leaves standard text editing (Delete, Enter, Ctrl+A…) to the focused text box. Also adds gesture hints to tooltips.
/// </summary>
internal sealed class ShortcutService
{
    private static ShortcutService? _current;
    private static readonly DependencyProperty HintProperty = DependencyProperty.RegisterAttached("ShortcutHint", typeof(string), typeof(ShortcutService));

    private readonly FrameworkElement _root;
    private readonly ShellViewModel _vm;
    private readonly Action _toggleTheme;
    private Window? _window;
    private Dictionary<ICommand, string> _hints = new(ReferenceEqualityComparer.Instance);

    static ShortcutService()
    {
        // Buttons created later (item templates, editors opening) pick up their hint when they load.
        EventManager.RegisterClassHandler(typeof(ButtonBase), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is ButtonBase { Command: { } c } b && _current?._hints.TryGetValue(c, out var g) == true) Hint(b, g!);
        }));
    }

    public ShortcutService(FrameworkElement root, ShellViewModel vm, Action toggleTheme)
    {
        _root = root;
        _vm = vm;
        _toggleTheme = toggleTheme;
    }

    public void Attach()
    {
        _window = Window.GetWindow(_root);
        if (_window is null) return;
        _window.PreviewKeyDown += OnPreviewKeyDown;
        _vm.PropertyChanged += OnVmChanged;
        if (App.Services is { } s) s.Loc.LanguageChanged += OnLanguageChanged;
        _current = this;
        RefreshHints();
    }

    public void Detach()
    {
        if (_window is not null) _window.PreviewKeyDown -= OnPreviewKeyDown;
        _vm.PropertyChanged -= OnVmChanged;
        if (App.Services is { } s) s.Loc.LanguageChanged -= OnLanguageChanged;
        if (ReferenceEquals(_current, this)) _current = null;
        _window = null;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentPage))
            _root.Dispatcher.BeginInvoke(RefreshHints, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => _root.Dispatcher.BeginInvoke(RefreshHints);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || e.IsRepeat && e.Key is Key.Delete or Key.Return) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Token(key) is not { } token) return;
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Windows)) return;
        var gesture = ShortcutMap.Canonical(mods.HasFlag(ModifierKeys.Control), mods.HasFlag(ModifierKeys.Shift), mods.HasFlag(ModifierKeys.Alt), token);

        if (_vm.IsShortcutsOpen)
        {
            if (gesture is "Esc" or "F1" or "Ctrl+/" or "Enter") { _vm.CloseShortcuts(); e.Handled = true; }
            return;
        }

        var focus = Keyboard.FocusedElement as DependencyObject;
        // An open drop-down owns Esc / Enter / arrows.
        if (focus is not null && Ancestor<ComboBox>(focus) is { IsDropDownOpen: true }) return;
        var inText = focus is TextBoxBase or PasswordBox;
        // Ctrl+Tab inside a tab control switches its tabs (standard behaviour).
        if (token == "Tab" && focus is not null && Ancestor<TabControl>(focus) is not null) return;

        foreach (var s in ShortcutMap.Active(_vm.Selected?.Key, gesture))
        {
            if (s.YieldsToText && (inText || (token == "Enter" && focus is ButtonBase))) continue;
            if (TryRun(s)) { e.Handled = true; return; }
        }
    }

    private bool TryRun(Shortcut s)
    {
        switch (s.Command)
        {
            case ShortcutMap.ToggleTheme:
                _toggleTheme();
                return true;
            case ShortcutMap.FocusSearch:
                return Focus(FindBox("Search") ?? FindBox("ScanText"));
            case ShortcutMap.FocusScan:
                return Focus(FindBox("ScanText"));
            case ShortcutMap.Launch:
                return false;
        }
        object? target = s.Scope == ShortcutMap.Global ? _vm : _vm.CurrentPage;
        if (target is null || !ShortcutMap.Applies(s, target) || ShortcutMap.Resolve(s, target) is not { } r) return false;
        if (!r.Command.CanExecute(r.Parameter)) return false;
        r.Command.Execute(r.Parameter);
        return true;
    }

    private TextBox? FindBox(string path) =>
        Descendants(_root).OfType<TextBox>().FirstOrDefault(t => t.IsVisible && t.IsEnabled &&
            BindingOperations.GetBinding(t, TextBox.TextProperty)?.Path?.Path == path);

    private static bool Focus(TextBox? box)
    {
        if (box is null) return false;
        box.Focus();
        Keyboard.Focus(box);
        box.SelectAll();
        return true;
    }

    /// <summary>Canonical key token (see <see cref="ShortcutMap.Canonical"/>), or null for keys the map never uses.</summary>
    internal static string? Token(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.F1 and <= Key.F24 => key.ToString(),
        Key.OemQuestion or Key.Divide => "/",
        Key.Return => "Enter",
        Key.Escape => "Esc",
        Key.Delete => "Delete",
        Key.Tab => "Tab",
        _ => null,
    };

    private void RefreshHints()
    {
        _hints = ShortcutMap.Hints(_vm);
        foreach (var b in Descendants(_root).OfType<ButtonBase>())
            if (b.Command is { } c && _hints.TryGetValue(c, out var g)) Hint(b, g);
    }

    /// <summary>Tooltip "Label  Ctrl+S" (label follows the button's text / accessible name, so it stays localized) + AcceleratorKey.</summary>
    internal static void Hint(FrameworkElement b, string gesture)
    {
        if ((string?)b.GetValue(HintProperty) == gesture) return;
        var first = b.GetValue(HintProperty) is null;
        b.SetValue(HintProperty, gesture);
        AutomationProperties.SetAcceleratorKey(b, gesture);
        var label = new TextBlock();
        if (b is ContentControl { Content: string }) label.SetBinding(TextBlock.TextProperty, new Binding(nameof(ContentControl.Content)) { Source = b });
        else if (!string.IsNullOrEmpty(AutomationProperties.GetName(b)))
            label.SetBinding(TextBlock.TextProperty, new Binding { Source = b, Path = new PropertyPath(AutomationProperties.NameProperty) });
        else if (first && b.ToolTip is string text) label.Text = text;
        var keys = new TextBlock { Text = gesture, FlowDirection = FlowDirection.LeftToRight, FontWeight = FontWeights.SemiBold, Opacity = 0.75 };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (!string.IsNullOrEmpty(label.Text) || BindingOperations.IsDataBound(label, TextBlock.TextProperty))
        {
            panel.Children.Add(label);
            keys.Margin = new Thickness(10, 0, 0, 0);
        }
        panel.Children.Add(keys);
        b.ToolTip = panel;
        ToolTipService.SetShowOnDisabled(b, true);
    }

    private static T? Ancestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var cur = d; cur is not null; cur = cur is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (cur is T t) return t;
        return null;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            yield return d;
            if (d is not Visual and not System.Windows.Media.Media3D.Visual3D) continue;
            for (var i = VisualTreeHelper.GetChildrenCount(d) - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
    }
}
