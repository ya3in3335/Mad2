using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Veltrix.Desktop.Core.Pos;
using Veltrix.Desktop.Presentation.ViewModels;

namespace Veltrix.Desktop.Views;

public partial class PosView : UserControl
{
    private PosViewModel? _vm;

    public PosView()
    {
        InitializeComponent();
        Loaded += (_, _) => ScanBox.Focus();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = e.NewValue as PosViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PosViewModel.IsPaying) && _vm!.IsPaying) Dispatcher.BeginInvoke(() => CashBox.Focus());
        if (e.PropertyName == nameof(PosViewModel.SaleCompletedCount)) PlaySuccess();
        if (e.PropertyName == nameof(PosViewModel.HasShift) && _vm!.HasShift) Dispatcher.BeginInvoke(() => ScanBox.Focus());
    }

    private void PlaySuccess()
    {
        var reduced = !SystemParameters.ClientAreaAnimation;
        var sb = new Storyboard();
        var show = new DoubleAnimationUsingKeyFrames();
        show.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(reduced ? 0 : 120))));
        show.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1400))));
        show.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1700))));
        Storyboard.SetTarget(show, SuccessOverlay);
        Storyboard.SetTargetProperty(show, new PropertyPath(OpacityProperty));
        sb.Children.Add(show);
        // 600 ms check-mark draw.
        var draw = new DoubleAnimation(reduced ? 0 : 12, 0, TimeSpan.FromMilliseconds(reduced ? 1 : 600)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(draw, SuccessCheck);
        Storyboard.SetTargetProperty(draw, new PropertyPath(System.Windows.Shapes.Shape.StrokeDashOffsetProperty));
        sb.Children.Add(draw);
        sb.Completed += (_, _) => ScanBox.Focus();
        sb.Begin();
    }

    private void OnResultChosen(object sender, MouseButtonEventArgs e) => AddSelected();

    private void OnResultKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return) return;
        AddSelected();
        e.Handled = true;
    }

    private void AddSelected()
    {
        if (_vm is null || Results.SelectedItem is not SellableProduct p) return;
        _vm.AddProductCommand.Execute(p);
        ScanBox.Focus();
    }
}
