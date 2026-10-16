using System.ComponentModel;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Veltrix.Desktop.Platform;
using Veltrix.Desktop.Presentation.ViewModels;

namespace Veltrix.Desktop.Views.Workforce;

/// <summary>
/// Attendance terminal view: ticking clock, permanent focus on the scan box (keyboard-wedge readers type into it), result animation + sound,
/// and the full-screen kiosk window.
/// </summary>
public partial class WfKioskView : UserControl
{
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(5) };
    private WfKioskViewModel? _vm;
    private static KioskWindow? _window;

    public WfKioskView()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => _vm?.Tick();
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            _vm?.DismissFeedbackCommand.Execute(null);
        };
        DataContextChanged += (_, _) => Bind(DataContext as WfKioskViewModel);
        Loaded += (_, _) =>
        {
            Bind(DataContext as WfKioskViewModel);
            _clock.Start();
            FocusScan();
        };
        Unloaded += (_, _) =>
        {
            _clock.Stop();
            Bind(null);
        };
        // Clicking anywhere (but the keypad) returns the focus to the capture box so the next card is not lost.
        PreviewMouseUp += (_, _) => Dispatcher.BeginInvoke(FocusScan, DispatcherPriority.Input);
    }

    private void Bind(WfKioskViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm is not null)
        {
            _vm.FeedbackShown -= OnFeedback;
            _vm.PropertyChanged -= OnVmChanged;
        }
        _vm = vm;
        if (vm is null) return;
        vm.FeedbackShown += OnFeedback;
        vm.PropertyChanged += OnVmChanged;
    }

    private void FocusScan()
    {
        if (!IsVisible || Keyboard.FocusedElement is TextBox { } t && !ReferenceEquals(t, ScanBox) && IsAncestorOf(t)) return;
        ScanBox.Focus();
        Keyboard.Focus(ScanBox);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WfKioskViewModel.IsFullScreen) || _vm is null || Window.GetWindow(this) is KioskWindow) return;
        if (_vm.IsFullScreen && _window is null)
        {
            _window = new KioskWindow(_vm) { Owner = Window.GetWindow(this) };
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }
        // Leaving full screen: the kiosk window closes itself.
    }

    private void OnFeedback(object? sender, KioskFeedback fb)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // While the kiosk window is open only its own view reacts (one sound, one animation).
            if (!IsVisible || (_window is not null && Window.GetWindow(this) is not KioskWindow)) return;
            (fb.Success ? SystemSounds.Asterisk : SystemSounds.Hand).Play();
            var reduced = _vm?.ReducedMotion == true;
            if (!reduced)
            {
                var pop = new DoubleAnimation(0.85, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } };
                FeedbackScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                FeedbackScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
                if (!fb.Success)
                {
                    // Short horizontal shake on failure.
                    var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(360) };
                    foreach (var (x, ms) in new[] { (-12.0, 60), (12.0, 120), (-8.0, 180), (8.0, 240), (-4.0, 300), (0.0, 360) })
                        shake.KeyFrames.Add(new LinearDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))));
                    FeedbackShift.BeginAnimation(TranslateTransform.XProperty, shake);
                }
            }
            _hide.Stop();
            _hide.Start();
            FocusScan();
        });
    }
}

/// <summary>Full-screen, top-most kiosk window (Esc asks for confirmation before leaving).</summary>
public sealed class KioskWindow : Window
{
    private readonly WfKioskViewModel _vm;

    public KioskWindow(WfKioskViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        Topmost = true;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Brush.background");
        SetResourceReference(ForegroundProperty, "Brush.textPrimary");
        Title = App.Services?.Loc.Get("wf.nav.kiosk") ?? "Kiosk";
        if (App.Services is { } s) WpfPlatformUi.ApplyTo(this, s.Settings.Language);
        Content = new Border { Padding = new Thickness(40), Child = new WfKioskView { DataContext = vm } };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; _vm.ExitFullScreenCommand.Execute(null); }
            else if (e.Key == Key.F4 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) e.Handled = true;
        };
        _vm.PropertyChanged += OnVmChanged;
        Closing += (_, e) =>
        {
            // Only leaving kiosk mode (confirmed in the view model) closes the window.
            if (_vm.IsFullScreen) e.Cancel = true;
        };
        Closed += (_, _) => _vm.PropertyChanged -= OnVmChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WfKioskViewModel.IsFullScreen) && !_vm.IsFullScreen) Close();
    }
}
