using System.Reflection;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Standalone;
using Veltrix.Desktop.Core.Workforce;
using Veltrix.Desktop.Core.Workforce.Terminals;

namespace Veltrix.Connector;

/// <summary>
/// Hosts the shared <see cref="TerminalConnectorLoop"/> for the database the desktop app published in <see cref="ConnectorConfig"/>.
/// Works without the app open; follows the app if its database moves; idles (and retries) until the app has run once on this PC.
/// </summary>
public sealed class ConnectorWorker(TimeProvider time, ILogger<ConnectorWorker> logger) : BackgroundService
{
    public const string ServiceName = "VeltrixConnector";
    private static readonly TimeSpan ConfigCheck = TimeSpan.FromSeconds(30);

    public static string ConfigDirectory => Environment.GetEnvironmentVariable("VELTRIX_CONNECTOR_DIR") is { Length: > 0 } d ? d : ConnectorConfig.DefaultDirectory;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";
        logger.LogInformation("VELTRIX Connector {Version} started; configuration folder {Dir}", version, ConfigDirectory);
        string? waitingFor = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var config = ConnectorConfig.Read(ConfigDirectory);
            var rejected = config is null ? null : DatabasePathProblem(config.DatabasePath);
            if (config is null || rejected is not null)
            {
                var reason = config is null ? "no configuration yet (start VELTRIX OS once)" : rejected!;
                if (reason != waitingFor) logger.LogWarning("Waiting: {Reason}", reason);
                waitingFor = reason;
                if (!await DelayAsync(ConfigCheck, stoppingToken)) return;
                continue;
            }
            waitingFor = null;
            using var run = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var loopTask = RunLoopAsync(config.DatabasePath, version, run.Token);
            // Restart the loop when the app points to another database.
            while (!loopTask.IsCompleted)
            {
                var finished = await Task.WhenAny(loopTask, Task.Delay(ConfigCheck, time, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default));
                if (finished == loopTask || stoppingToken.IsCancellationRequested) break;
                var now = ConnectorConfig.Read(ConfigDirectory);
                if (now is not null && !string.Equals(now.DatabasePath, config.DatabasePath, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("Database moved to {Path}; restarting", now.DatabasePath);
                    await run.CancelAsync();
                }
            }
            try { await loopTask; }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Connector loop failed; restarting in {Seconds}s", ConfigCheck.TotalSeconds);
                if (!await DelayAsync(ConfigCheck, stoppingToken)) return;
            }
        }
    }

    /// <summary>
    /// The configuration file is user-writable, and this service runs as LocalSystem: only the VELTRIX OS database location inside a
    /// user profile is accepted (…\Users\name\AppData\Local\VELTRIX OS\Data\veltrix.db), without junctions / symbolic links on the way.
    /// </summary>
    public static string? DatabasePathProblem(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return "invalid database path"; }
        var usersRoot = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "";
        var expectedTail = Path.Combine("AppData", "Local", "VELTRIX OS", "Data", "veltrix.db");
        if (usersRoot.Length == 0 || !full.StartsWith(usersRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !full.EndsWith(Path.DirectorySeparatorChar + expectedTail, StringComparison.OrdinalIgnoreCase)
            || full.Length - usersRoot.Length - expectedTail.Length < 3 // \name\
            || full[(usersRoot.Length + 1)..^(expectedTail.Length + 1)].Contains(Path.DirectorySeparatorChar))
            return "database path outside a VELTRIX OS user data folder: " + full;
        if (!File.Exists(full)) return "database not found: " + full;
        for (var p = full; p.Length > usersRoot.Length; p = Path.GetDirectoryName(p)!)
            if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) return "database path contains a link: " + p;
        return null;
    }

    private async Task RunLoopAsync(string databasePath, string version, CancellationToken ct)
    {
        var options = LocalDb.OptionsFor($"Data Source={databasePath}");
        Func<LocalDb> db = () => new LocalDb(options);
        await using (var ctx = db()) await ctx.InitializeAsync(ct); // same schema migrations as the app (idempotent)
        var wf = new WorkforceService(db, time, PasswordHasher.DefaultIterations);
        if (OperatingSystem.IsWindows()) wf.TerminalProtector = new MachineDataProtector();
        var loop = new TerminalConnectorLoop(new TerminalSyncCoordinator(wf, new NamedMutexIngestionLock(), ConnectorOrigins.Service), time,
            new ConnectorOptions { Origin = ConnectorOrigins.Service, Version = version }, Log);
        logger.LogInformation("Polling terminals of {Path}", databasePath);
        await loop.RunAsync(ct);
    }

    private void Log(string level, string message)
    {
        switch (level)
        {
            case "Error": logger.LogError("{Message}", message); break;
            case "Warning": logger.LogWarning("{Message}", message); break;
            default: logger.LogInformation("{Message}", message); break;
        }
    }

    private async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, time, ct); return true; }
        catch (OperationCanceledException) { return false; }
    }
}
