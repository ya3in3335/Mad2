using System.Windows;
using System.Windows.Controls;
using Veltrix.Desktop.Presentation.ViewModels;

namespace Veltrix.Desktop.Views;

public partial class OnboardingView : UserControl
{
    public OnboardingView() => InitializeComponent();

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is not OnboardingViewModel vm) return;
        vm.OwnerPassword = Password.Password;
        vm.OwnerPasswordConfirm = PasswordConfirm.Password;
    }
}
