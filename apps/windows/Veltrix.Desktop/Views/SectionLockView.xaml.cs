using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Veltrix.Desktop.Presentation.ViewModels;

namespace Veltrix.Desktop.Views;

/// <summary>Section password gate. Passwords flow one way (box → view model) and the boxes are cleared after every attempt.</summary>
public partial class SectionLockView : UserControl
{
    public SectionLockView()
    {
        InitializeComponent();
        Loaded += (_, _) => PasswordBox.Focus();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SectionLockViewModel old) old.ClearRequested -= OnClear;
            if (e.NewValue is SectionLockViewModel vm) vm.ClearRequested += OnClear;
        };
    }

    private SectionLockViewModel? Vm => DataContext as SectionLockViewModel;

    private void OnClear(object? sender, EventArgs e)
    {
        PasswordBox.Clear();
        ConfirmBox.Clear();
        CurrentBox.Clear();
        OwnerBox.Clear();
        PasswordBox.Focus();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e) { if (Vm is { } vm) vm.Password = PasswordBox.Password; }
    private void OnConfirmChanged(object sender, RoutedEventArgs e) { if (Vm is { } vm) vm.Confirm = ConfirmBox.Password; }
    private void OnCurrentChanged(object sender, RoutedEventArgs e) { if (Vm is { } vm) vm.Current = CurrentBox.Password; }
    private void OnOwnerChanged(object sender, RoutedEventArgs e) { if (Vm is { } vm) vm.OwnerPassword = OwnerBox.Password; }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm?.SubmitCommand.CanExecute(null) == true)
        {
            Vm.SubmitCommand.Execute(null);
            e.Handled = true;
        }
    }
}
