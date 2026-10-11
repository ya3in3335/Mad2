using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Veltrix.Desktop.Core.Api;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Diagnostics;
using Veltrix.Desktop.Core.Licensing;
using Veltrix.Desktop.Core.Pos;
using Veltrix.Desktop.Core.Security;
using Veltrix.Desktop.Core.Session;
using Veltrix.Desktop.Core.Settings;
using Veltrix.Desktop.Core.Sync;
using Veltrix.Desktop.Platform;
using Veltrix.Desktop.Presentation;
using Veltrix.Desktop.Presentation.ViewModels;
using Veltrix.Desktop.Views;

namespace Veltrix.Desktop;

public partial class App : Application
{
    private static Mutex? _singleInstance;
    private DesktopServices? _services;

    /// <summary>Running services, for view code-behind that needs app-wide visual preferences (e.g. reduced motion).</summary>
    internal static DesktopServices? Services => (Current as App)?._services;
    private AppLog? _log;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Any(a => string.Equals(a, "--bind", StringComparison.OrdinalIgnoreCase)))
        {
            // Installer-only path (elevated): create/repair the device binding, report through the exit code, no UI.
            Shutdown(RunBind());
            return;
        }
        _singleInstance = new Mutex(true, @"Local\VELTRIX-OS-Desktop", out var first);
        if (!first)
        {
            MessageBox.Show("VELTRIX OS is already running.", "VELTRIX OS", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => { _log?.Error("Unobserved task exception", args.Exception); args.SetObserved(); };

        var services = _services = Compose();
        var ui = (WpfPlatformUi)services.Ui;
        ui.ApplyTheme(services.Settings.Theme);
        ui.ApplyAccent(services.Settings.Accent); // before the splash so it already glows in the chosen accent
        ui.ApplyLanguage(services.Settings.Language);

        var app = new AppViewModel(services);
        var splash = new SplashWindow(services.ReducedMotion, firstLaunch: !services.Settings.FirstLaunchDone);
        WpfPlatformUi.ApplyTo(splash, services.Settings.Language);
        splash.Show();

        StartRoute route;
        try
        {
            var init = app.InitializeAsync(CancellationToken.None);
            await splash.PlayAsync(init);
            route = await init;
            // Sale-photo retention (default 30 days): off the UI thread, never blocks startup.
            if (services.Cameras is { } cams)
                _ = Task.Run(async () =>
                {
                    try { var n = await cams.Snapshots.CleanupAsync(); if (n > 0) _log?.Info($"Deleted {n} expired camera snapshot(s)"); }
                    catch (Exception ex) { _log?.Warn("Snapshot cleanup failed", ex); }
                });
        }
        catch (Exception ex)
        {
            _log?.Error("Startup failed", ex);
            route = services.Binding.IsBound ? StartRoute.Login : StartRoute.NotLicensed;
        }

        var main = new MainWindow { DataContext = app };
        WpfPlatformUi.ApplyTo(main, services.Settings.Language);
        app.Navigate(route);
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        await splash.CrossFadeToAsync(main);
    }

    private DesktopServices Compose()
    {
        Directory.CreateDirectory(AppPaths.Root);
        var time = TimeProvider.System;
        var log = _log = new AppLog(AppPaths.Logs, time);
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";
        log.Info($"VELTRIX OS {version} starting on {Environment.OSVersion}");

        var settingsStore = new SettingsStore(AppPaths.Settings);
        var settings = settingsStore.Load();
        settingsStore.Save(settings); // persists a newly generated installation id
        var secrets = new SecureStore(AppPaths.Secrets, new DpapiProtector());
        var tokens = new SecureTokenStore(secrets);
        var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"VeltrixOS-Windows/{version}");
        var api = new ApiClient(http, tokens, new SettingsServerAddress(settings)) { DeviceId = settings.DeviceId };

        Directory.CreateDirectory(AppPaths.Data);
        var dbOptions = LocalDb.OptionsFor($"Data Source={AppPaths.Database}");
        Func<LocalDb> db = () => new LocalDb(dbOptions);

        var pinned = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "VeltrixVendorPublicKey")?.Value;
        var identity = new DeviceIdentity(secrets, settings.InstallationId, WindowsFingerprint.Read());
        var license = new LicenseManager(api, secrets, settings, () => settingsStore.Save(settings), identity, time, version, pinned);
        // Anti-copy: binding to this PC (DPAPI LocalMachine) written by the elevated installer; the app only verifies it.
        var binding = CreateBinding(time);
        var localizer = new WpfLocalizer(settings);

        return new DesktopServices
        {
            Api = api, Settings = settings, SettingsStore = settingsStore, Secrets = secrets, Tokens = tokens, License = license, Binding = binding,
            Session = new SessionService(api, tokens, secrets), Db = db, Sync = new SyncEngine(db, new ApiSyncTransport(api), time),
            Pos = new PosService(api, db, time, () => binding.IsBound), Loc = localizer, Ui = new WpfPlatformUi(localizer), Time = time, Log = log, Version = version,
            Standalone = new Core.Standalone.StandaloneService(secrets, db, time, () => binding.IsBound),
            // CCTV (LAN only): camera credentials encrypted with DPAPI (current user); sale photos via the camera's HTTP snapshot, else a libVLC frame.
            Cameras = new Core.Cameras.CameraService(db, new DpapiProtector(), AppPaths.Root,
                new Core.Cameras.FallbackSnapshotCapturer(new Core.Cameras.HttpSnapshotCapturer(), new VlcFrameCapturer()), settings, time, log),
        };
    }

    private static DeviceBindingManager CreateBinding(TimeProvider time) => new(new WindowsBindingStore(), new DpapiMachineProtector(),
        DeviceFingerprint.ComputeBinding(WindowsFingerprint.Read()), time, WindowsBindingStore.IsElevated);

    /// <summary>VeltrixOS.exe --bind: 0 = bound, 2 = not elevated, 1 = any other failure.</summary>
    private static int RunBind()
    {
        try
        {
            var result = CreateBinding(TimeProvider.System).Bind();
            return result.IsLicensed ? 0 : result.Status == BindingStatus.NotElevated ? 2 : 1;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("Unhandled UI exception", e.Exception);
        var text = _services?.Loc.Get("err.generic") ?? "Something went wrong.";
        MessageBox.Show(text, "VELTRIX OS", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Info("Exit");
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
