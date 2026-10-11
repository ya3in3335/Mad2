using CommunityToolkit.Mvvm.ComponentModel;
using Veltrix.Desktop.Core.Licensing;
using Veltrix.Desktop.Core.Security;

namespace Veltrix.Desktop.Presentation.ViewModels;

public enum StartRoute { NotLicensed, Welcome, Onboarding, Login, Shell }

public static class StartupRouter
{
    /// <summary>Chooses the first screen after the splash. Without a valid device binding nothing else is reachable.</summary>
    public static StartRoute Decide(bool bound, bool firstLaunchDone) =>
        !bound ? StartRoute.NotLicensed : !firstLaunchDone ? StartRoute.Welcome : StartRoute.Login;
}

/// <summary>Root view model: owns the current top-level screen (not licensed, welcome, onboarding, login or shell).</summary>
public sealed partial class AppViewModel(DesktopServices services) : ObservableObject
{
    [ObservableProperty] private object? _current;

    public DesktopServices Services { get; } = services;

    /// <summary>Real startup work, run while the splash animates: device binding check first (no data access when it
    /// fails), then the local database and the standalone register.</summary>
    public async Task<StartRoute> InitializeAsync(CancellationToken ct)
    {
        var s = Services;
        var binding = s.Binding.Verify();
        s.Log.Info($"Device binding: {binding.Status}");
        if (!binding.IsLicensed) return StartRoute.NotLicensed;

        await using (var db = s.Db()) await db.InitializeAsync(ct);
        await s.Standalone.Store.EnsureRegisterAsync(ct);

        var route = StartupRouter.Decide(true, s.Settings.FirstLaunchDone);
        s.Log.Info($"Startup complete: route={route}");
        return route;
    }

    public void Navigate(StartRoute route) => Current = route switch
    {
        StartRoute.NotLicensed => new NotLicensedViewModel(Services),
        StartRoute.Welcome => new WelcomeViewModel(Services, this),
        StartRoute.Onboarding => new OnboardingViewModel(Services, this),
        StartRoute.Login => new LoginViewModel(Services, this),
        _ => new ShellViewModel(Services, this),
    };
}
