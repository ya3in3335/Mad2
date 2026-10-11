using System.Windows;
using Veltrix.Desktop.Presentation.ViewModels;

namespace Veltrix.Desktop.Views;

/// <summary>Add/edit a camera: brand presets, LAN discovery (ONVIF) and a connection test.</summary>
public partial class CameraEditWindow : Window
{
    private readonly CameraEditorViewModel _vm;

    public CameraEditWindow(CameraEditorViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        PasswordInput.Password = vm.Password;
        vm.CloseRequested += (_, _) => { DialogResult = true; };
    }

    private void Password_Changed(object sender, RoutedEventArgs e) => _vm.Password = PasswordInput.Password;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
