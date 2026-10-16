using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Veltrix.Desktop.Views.Workforce;

public partial class WfEmployeesView : UserControl
{
    public WfEmployeesView() => InitializeComponent();

    /// <summary>"Tap card on the reader": focus the UID box so the keyboard-wedge reader types into it (then Enter assigns).</summary>
    private void OnTapCard(object sender, RoutedEventArgs e)
    {
        CardBox.Clear();
        CardBox.Focus();
        Keyboard.Focus(CardBox);
    }
}
