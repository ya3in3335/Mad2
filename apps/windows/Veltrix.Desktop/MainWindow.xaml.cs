using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Veltrix.Desktop.Presentation.ViewModels;

namespace Veltrix.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is AppViewModel old) old.PropertyChanged -= OnAppChanged;
            if (e.NewValue is AppViewModel vm) vm.PropertyChanged += OnAppChanged;
        };
        Closing += OnClosing;
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppViewModel.Current) || sender is not AppViewModel vm) return;
        // Screen change (e.g. login → dashboard): 300 ms fade + 8 px slide; reduced motion: plain 150 ms fade.
        var reduced = vm.Services.ReducedMotion;
        var duration = TimeSpan.FromMilliseconds(reduced ? 150 : 300);
        Host.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
        if (!reduced)
        {
            var t = new TranslateTransform();
            Host.RenderTransform = t;
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is AppViewModel { Current: ShellViewModel shell }) shell.Dispose();
    }
}
