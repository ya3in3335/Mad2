using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Veltrix.Desktop.Presentation.ViewModels;

namespace Veltrix.Desktop.Views;

public partial class ShellView : UserControl
{
    private const double ExpandedWidth = 248, CollapsedWidth = 72;
    private ShellViewModel? _vm;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _collapsed;

    public ShellView()
    {
        InitializeComponent();
        NavList.Tag = false;
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = e.NewValue as ShellViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        };
        _clock.Tick += (_, _) => UpdateClock();
        Loaded += (_, _) => { UpdateClock(); UpdateThemeGlyph(); _clock.Start(); };
        Unloaded += (_, _) => _clock.Stop();
    }

    /// <summary>System "show animations" off, or the app's own reduced-motion preference.</summary>
    private bool ReducedMotion => _vm is null || !SystemParameters.ClientAreaAnimation || App.Services?.ReducedMotion == true;

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.CurrentPage):
                // Navigation change: 180 ms content fade + 6 px slide (mirrored automatically in RTL). Reduced motion: short fade only.
                var d = TimeSpan.FromMilliseconds(ReducedMotion ? 120 : 180);
                PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d));
                if (!ReducedMotion)
                {
                    var t = new TranslateTransform();
                    PageHost.RenderTransform = t;
                    t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(6, 0, d) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
                }
                else PageHost.RenderTransform = Transform.Identity;
                break;
            case nameof(ShellViewModel.IsSyncing):
                // Sync running: two-arrow rotation, 1 s loop, paused when idle.
                if (_vm!.IsSyncing && !ReducedMotion)
                    SyncRotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
                else
                    SyncRotation.BeginAnimation(RotateTransform.AngleProperty, null);
                break;
        }
    }

    private void UpdateClock()
    {
        var culture = CultureInfo.CurrentCulture;
        var now = DateTime.Now;
        ClockTime.Text = now.ToString("t", culture);
        ClockDate.Text = now.ToString("ddd d MMM", culture);
    }

    private void OnToggleSidebar(object sender, RoutedEventArgs e)
    {
        _collapsed = !_collapsed;
        NavList.Tag = _collapsed;
        var hidden = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        BrandText.Visibility = hidden;
        BrandMark.Visibility = hidden;
        SignOutText.Visibility = hidden;
        VersionText.Visibility = hidden;
        var to = _collapsed ? CollapsedWidth : ExpandedWidth;
        if (ReducedMotion)
        {
            Sidebar.BeginAnimation(WidthProperty, null);
            Sidebar.Width = to;
        }
        else
        {
            Sidebar.BeginAnimation(WidthProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(180)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private bool IsDark => Application.Current.TryFindResource("Color.background") is Color c && c.R + c.G + c.B < 384;

    private void UpdateThemeGlyph() =>
        ThemeToggle.Content = Application.Current.TryFindResource(IsDark ? "Glyph.Sun" : "Glyph.Moon");

    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        // Same path as Settings → Theme, so the choice is persisted and the Settings page stays in sync.
        if (_vm?.NavItems.FirstOrDefault(n => n.Key == "settings")?.Page is SettingsViewModel settings)
            settings.IsDarkTheme = !settings.IsDarkTheme;
        else if (App.Services is { } s)
        {
            s.Settings.Theme = IsDark ? "light" : "dark";
            s.SaveSettings();
            s.Ui.ApplyTheme(s.Settings.Theme);
        }
        UpdateThemeGlyph();
    }
}
