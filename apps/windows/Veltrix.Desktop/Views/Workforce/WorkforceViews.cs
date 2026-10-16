using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Veltrix.Desktop.Presentation.ViewModels;

namespace Veltrix.Desktop.Views.Workforce;

/// <summary>Runs the view model's PollCommand on a timer while the view is shown (live screens).</summary>
internal static class Polling
{
    public static void Attach(FrameworkElement view, TimeSpan interval, Func<object?, ICommand?> command)
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += (_, _) =>
        {
            if (command(view.DataContext) is { } c && c.CanExecute(null)) c.Execute(null);
        };
        view.Loaded += (_, _) => timer.Start();
        view.Unloaded += (_, _) => timer.Stop();
    }
}

public partial class WfDashboardView : UserControl
{
    public WfDashboardView()
    {
        InitializeComponent();
        Polling.Attach(this, TimeSpan.FromSeconds(15), dc => (dc as WfDashboardViewModel)?.PollCommand);
    }
}

public partial class WfLiveView : UserControl
{
    public WfLiveView()
    {
        InitializeComponent();
        Polling.Attach(this, TimeSpan.FromSeconds(10), dc => (dc as WfLiveViewModel)?.PollCommand);
    }
}

public partial class WfAttendanceView : UserControl
{
    public WfAttendanceView() => InitializeComponent();

    private void OnClearEmployee(object sender, RoutedEventArgs e)
    {
        if (DataContext is WfAttendanceViewModel vm) vm.Employee = null;
    }
}

public partial class WfShiftPlannerView : UserControl
{
    public WfShiftPlannerView() => InitializeComponent();
}

public partial class WfLeaveView : UserControl
{
    public WfLeaveView() => InitializeComponent();
}

public partial class WfOvertimeView : UserControl
{
    public WfOvertimeView() => InitializeComponent();
}

public partial class WfAccessView : UserControl
{
    public WfAccessView() => InitializeComponent();
}

public partial class WfDevicesView : UserControl
{
    public WfDevicesView() => InitializeComponent();
}

public partial class WfPayrollView : UserControl
{
    public WfPayrollView() => InitializeComponent();
}

public partial class WfReportsView : UserControl
{
    public WfReportsView() => InitializeComponent();
}

public partial class WfSettingsView : UserControl
{
    public WfSettingsView() => InitializeComponent();
}

public partial class WfTerminalsView : UserControl
{
    public WfTerminalsView()
    {
        InitializeComponent();
        // Write-only password: the box is cleared whenever the view model resets it (selection change, after save).
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is WfTerminalsViewModel old) old.PropertyChanged -= OnVmChanged;
            if (e.NewValue is WfTerminalsViewModel vm) vm.PropertyChanged += OnVmChanged;
        };
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WfTerminalsViewModel.Password) && sender is WfTerminalsViewModel { Password.Length: 0 } && PasswordBox.Password.Length > 0)
            PasswordBox.Clear();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is WfTerminalsViewModel vm) vm.Password = PasswordBox.Password;
    }
}

public partial class WfReviewView : UserControl
{
    public WfReviewView() => InitializeComponent();
}
