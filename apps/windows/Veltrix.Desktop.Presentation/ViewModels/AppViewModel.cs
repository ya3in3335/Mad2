using CommunityToolkit.Mvvm.ComponentModel;
using Veltrix.Desktop.Core.Licensing;
using Veltrix.Desktop.Core.Security;

namespace Veltrix.Desktop.Presentation.ViewModels;

public enum StartRoute { Activation, Onboarding, Login, Shell }

public static class StartupRouter
{
    /// <summary>Chooses the first screen after the splash from the offline license evaluation and the stored session.</summary>
    public static StartRoute Decide(bool hasServer, LicenseEvaluation license, bool setupCompleted, bool hasSetupToken, bool hasSession, bool standalone = false)
    {
        // Standalone (offline license): no server at all; the login screen creates the local owner on first run.
        if (standalone && !license.RequiresActivation && license.State is not (LicenseState.ClockRollback or LicenseState.Suspended)) return StartRoute.Login;
        if (!hasServer || license.RequiresActivation || license.State is LicenseState.ClockRollback or LicenseState.Suspended) return StartRoute.Activation;
        if (!setupCompleted && hasSetupToken) return StartRoute.Onboarding;
        return hasSession ? StartRoute.Shell : StartRoute.Login;
    }
}

/// <summary>Root view model: owns the current top-level screen (activation, onboarding, login or shell).</summary>
public sealed partial class AppViewModel(DesktopServices services) : ObservableObject
{
    [ObservableProperty] private object? _current;

    public DesktopServices Services { get; } = services;

    /// <summary>Real startup work, run while the splash animates: database, license check/renewal, session restore.</summary>
    public async Task<StartRoute> InitializeAsync(CancellationToken ct)
    {
        var s = Services;
        await using (var db = s.Db()) await db.InitializeAsync(ct);
        s.Api.DeviceId = s.Settings.DeviceId;

        var eval = s.License.Evaluate();
        if (s.IsStandalone) await s.Standalone.Store.EnsureRegisterAsync(ct);
        if (!s.IsStandalone && s.Settings.ServerUrl is not null && s.Settings.DeviceId is not null && eval.State is not LicenseState.Missing)
        {
            // Renew the certificate if the server answers quickly; otherwise continue on the offline grace period.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            try { await s.License.RefreshAsync(timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (Exception e) { s.Log.Warn("License refresh at startup failed", e); }
            eval = s.License.Current ?? eval;
        }

        var hasSession = false;
        if (!s.IsStandalone && !eval.RequiresActivation && s.Settings.ServerUrl is not null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            try { hasSession = await s.Session.TryRestoreAsync(timeout.Token) is not null; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { hasSession = s.Tokens.Profile is not null && s.Tokens.RefreshToken is not null; }
        }

        var route = StartupRouter.Decide(s.Settings.ServerUrl is not null, eval, s.Settings.SetupCompleted,
            s.Secrets.Get(SecureStore.SetupToken) is not null, hasSession, s.IsStandalone);
        s.Log.Info($"Startup complete: license={eval.State}, route={route}");
        return route;
    }

    public void Navigate(StartRoute route) => Current = route switch
    {
        StartRoute.Activation => new ActivationViewModel(Services, this),
        StartRoute.Onboarding => new OnboardingViewModel(Services, this),
        StartRoute.Login => new LoginViewModel(Services, this),
        _ => new ShellViewModel(Services, this),
    };
}
