using System.Windows.Controls;
using System.Windows.Threading;
using Veltrix.Desktop.Presentation.ViewModels;

namespace Veltrix.Desktop.Views;

public partial class LinkView : UserControl
{
    public LinkView()
    {
        InitializeComponent();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) => (DataContext as LinkViewModel)?.PollCommand.Execute(null);
        Loaded += (_, _) => timer.Start();
        Unloaded += (_, _) => timer.Stop();
    }
}
