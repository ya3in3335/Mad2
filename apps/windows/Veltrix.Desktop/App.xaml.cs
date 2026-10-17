using Veltrix.Desktop.Core.Workforce.Terminals;
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
    private static SingleInstance? _singleInstance;
    private DesktopServices? _services;

    /// <summary>Running services, for view code-behind that needs app-wide visual preferences (e.g. reduced motion).</summary>
    internal static DesktopServices? Services => (Current as App)?._services;
    private AppLog? _log;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Any(a => string.Equals(a, "--install-zk-sdk", StringComparison.OrdinalIgnoreCase)))
        {
            // Elevated helper started from the attendance-terminals screen: downloads the ZKTeco SDK into the admin-only program folder.
            Shutdown(RunInstallZkSdk());
            return;
        }
        if (e.Args.Any(a => string.Equals(a, "--bind", StringComparison.OrdinalIgnoreCase)))
        {
            // Installer-only path (elevated): create/repair the device binding, report through the exit code, no UI.
            Shutdown(RunBind());
            return;
        }
        // Single instance: a second launch (or the Ctrl+Alt+V Start Menu hotkey) brings the running window to front instead.
        AllowSetForegroundWindow(-1); // ASFW_ANY: this (foreground) launch lets the running copy take the foreground
        _singleInstance = SingleInstance.TryClaim("VELTRIX-OS-Desktop", () => Dispatcher.BeginInvoke(BringToFront));
        if (_singleInstance is null)
        {
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
            StartTerminalConnector(services);
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

    private CancellationTokenSource? _connectorCts;

    /// <summary>
    /// Attendance terminals: tells the connector service where the database is, then polls terminals in-process as a fallback. While the
    /// service's heartbeat is fresh the in-app loop stays idle (the service owns ingestion; a machine-wide mutex keeps a single writer).
    /// </summary>
    private void StartTerminalConnector(DesktopServices services)
    {
        if (!services.Binding.IsBound) return;
        var version = services.Version;
        new ConnectorConfig(Path.GetFullPath(AppPaths.Database), services.Time.GetUtcNow(), version).TryWrite(ConnectorConfig.DefaultDirectory);
        _connectorCts = new CancellationTokenSource();
        var loop = new TerminalConnectorLoop(new TerminalSyncCoordinator(services.Standalone.Workforce, services.IngestionLock, ConnectorOrigins.App), services.Time,
            new ConnectorOptions { Origin = ConnectorOrigins.App, DeferToServiceWhenFresherThan = TimeSpan.FromMinutes(2), Version = version },
            (level, message) => { if (level == "Error") _log?.Error(message, null); else _log?.Info("[terminals] " + message); });
        _ = Task.Run(() => loop.RunAsync(_connectorCts.Token));
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
        var standalone = new Core.Standalone.StandaloneService(secrets, db, time, () => binding.IsBound);
        // Terminal passwords: DPAPI LocalMachine, so the connector service (LocalSystem) on this PC can use them too.
        standalone.Workforce.TerminalProtector = new MachineDataProtector();

        return new DesktopServices
        {
            Api = api, Settings = settings, SettingsStore = settingsStore, Secrets = secrets, Tokens = tokens, License = license, Binding = binding,
            Session = new SessionService(api, tokens, secrets), Db = db, Sync = new SyncEngine(db, new ApiSyncTransport(api), time),
            Pos = new PosService(api, db, time, () => binding.IsBound), Loc = localizer, Ui = new WpfPlatformUi(localizer), Time = time, Log = log, Version = version,
            Standalone = standalone,
            IngestionLock = new NamedMutexIngestionLock(),
            InstallZkTecoSdk = InstallZkSdkElevatedAsync,
            ZkTecoSdkDirectory = ZkTecoSdkFiles.BridgeDirectory(AppContext.BaseDirectory),
            // CCTV (LAN only): camera credentials encrypted with DPAPI (current user); sale photos via the camera's HTTP snapshot, else a libVLC frame.
            Cameras = new Core.Cameras.CameraService(db, new DpapiProtector(), AppPaths.Root,
                new Core.Cameras.FallbackSnapshotCapturer(new Core.Cameras.HttpSnapshotCapturer(), new VlcFrameCapturer()), settings, time, log),
        };
    }

    private static DeviceBindingManager CreateBinding(TimeProvider time) => new(new WindowsBindingStore(), new DpapiMachineProtector(),
        DeviceFingerprint.ComputeBinding(WindowsFingerprint.Read()), time, WindowsBindingStore.IsElevated);

    /// <summary>VeltrixOS.exe --install-zk-sdk: 0 = installed, 2 = not elevated, 1 = download / verification failure.</summary>
    private static int RunInstallZkSdk()
    {
        if (!WindowsBindingStore.IsElevated()) return 2;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            ZkTecoSdkFiles.DownloadAsync(http, ZkTecoSdkFiles.BridgeDirectory(AppContext.BaseDirectory)).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    /// <summary>Starts the elevated helper (UAC prompt) and waits for it. False when refused or failed.</summary>
    private static async Task<bool> InstallZkSdkElevatedAsync()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--install-zk-sdk")
            {
                UseShellExecute = true, Verb = "runas",
            });
            if (p is null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; } // UAC cancelled
    }

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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Restores and activates the main window (or the splash while starting).</summary>
    private void BringToFront()
    {
        var w = MainWindow ?? Windows.OfType<Window>().FirstOrDefault(x => x.IsVisible);
        if (w is null) return;
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show();
        w.Activate();
        // Topmost flip: reliable z-order raise even when Windows refuses a plain activation.
        w.Topmost = true;
        w.Topmost = false;
        w.Focus();
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
        _connectorCts?.Cancel();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
