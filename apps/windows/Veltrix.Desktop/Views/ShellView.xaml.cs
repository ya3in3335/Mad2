using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Veltrix.Desktop.Presentation.ViewModels;

namespace Veltrix.Desktop.Views;

public partial class ShellView : UserControl
{
    private ShellViewModel? _vm;

    public ShellView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = e.NewValue as ShellViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        };
    }

    private bool ReducedMotion => _vm is null || !SystemParameters.ClientAreaAnimation;

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.CurrentPage):
                // Navigation change: 180 ms content fade + 6 px slide (mirrored automatically in RTL).
                var d = TimeSpan.FromMilliseconds(ReducedMotion ? 120 : 180);
                PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d));
                if (!ReducedMotion)
                {
                    var t = new TranslateTransform();
                    PageHost.RenderTransform = t;
                    t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(6, 0, d) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
                }
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
}
