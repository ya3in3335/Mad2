using System.Windows;
using System.Windows.Controls;
using Veltrix.Desktop.Presentation.ViewModels;

namespace Veltrix.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void OnNewUserSecretChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.NewUserSecret = NewUserSecretBox.Password;
    }

    private void OnBackupPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.BackupPassword = BackupPasswordBox.Password;
    }
}
