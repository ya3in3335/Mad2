using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Veltrix.Desktop.Presentation.ViewModels;

namespace Veltrix.Desktop.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (string.IsNullOrEmpty(EmailBox.Text)) EmailBox.Focus(); else PasswordBox.Focus();
        };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is LoginViewModel old) old.PropertyChanged -= OnVmChanged;
            if (e.NewValue is LoginViewModel vm) vm.PropertyChanged += OnVmChanged;
        };
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm) vm.Password = PasswordBox.Password;
    }

    private void OnConfirmChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm) vm.ConfirmPassword = ConfirmBox.Password;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LoginViewModel.MfaRequired) && sender is LoginViewModel { MfaRequired: true })
            Dispatcher.BeginInvoke(() => MfaBox.Focus());
        if (e.PropertyName == nameof(LoginViewModel.FailureCount) && SystemParameters.ClientAreaAnimation)
        {
            // Error feedback: 2 × 4 px horizontal shake, no red flashing.
            var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(300) };
            foreach (var (x, ms) in new[] { (4.0, 50), (-4.0, 110), (4.0, 170), (-4.0, 230), (0.0, 300) })
                shake.KeyFrames.Add(new LinearDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))));
            Shake.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, shake);
        }
    }
}
