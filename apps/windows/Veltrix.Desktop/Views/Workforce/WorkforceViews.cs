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
