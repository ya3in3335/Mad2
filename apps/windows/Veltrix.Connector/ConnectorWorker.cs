using System.Reflection;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Standalone;
using Veltrix.Desktop.Core.Workforce;
using Veltrix.Desktop.Core.Workforce.Terminals;
using Veltrix.Desktop.Core.Replication;
using Veltrix.LinkServer;
using System.Net;
using Microsoft.EntityFrameworkCore;

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
        // The service runs as LocalSystem, whose own profile is under Windows\System32: derive the Users folder from the public profile.
        var usersRoot = Path.GetDirectoryName(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments)) ?? "") ?? "";
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
        await Task.WhenAll(loop.RunAsync(ct), HubAsync(options, ct));
    }

    /// <summary>
    /// VELTRIX Link hub (company PC): while "This PC is the company hub" is on in the app, serves the branch PCs over HTTPS on the LAN
    /// (TCP 47800, certificate kept encrypted in ProgramData) and answers discovery broadcasts (UDP 47801).
    /// </summary>
    private async Task HubAsync(DbContextOptions<LocalDb> options, CancellationToken ct)
    {
        LocalDb Db(LinkCapture? c) => new(options) { Link = c };
        LinkHubServer? server = null;
        CancellationTokenSource? discovery = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool enabled;
                await using (var db = Db(null)) enabled = await db.GetValueAsync(LinkProtocol.HubFlagKey, ct) == "1";
                if (enabled && server is null && OperatingSystem.IsWindows())
                {
                    try
                    {
                        var cert = LinkCertificate.LoadOrCreate(Path.Combine(LinkDirectory, "hub.pfx"), new MachineDataProtector(), "VELTRIX Hub " + Environment.MachineName);
                        server = await LinkHubServer.StartAsync(Db, cert, time, IPAddress.Any, LinkProtocol.TcpPort, m => logger.LogInformation("{Message}", m), ct);
                        discovery = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var token = discovery.Token;
                        _ = Task.Run(() => LinkDiscoveryResponder.RunAsync(() => HubName(Db), LinkProtocol.TcpPort, LinkProtocol.DiscoveryPort, token), CancellationToken.None);
                        logger.LogInformation("VELTRIX Link hub listening on TCP {Port}", LinkProtocol.TcpPort);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "VELTRIX Link hub could not start");
                        server = null;
                    }
                }
                else if (!enabled && server is not null)
                {
                    await StopHubAsync(server, discovery);
                    (server, discovery) = (null, null);
                    logger.LogInformation("VELTRIX Link hub stopped");
                }
                if (!await DelayAsync(TimeSpan.FromSeconds(15), ct)) break;
            }
        }
        finally
        {
            if (server is not null) await StopHubAsync(server, discovery);
        }
    }

    private static string LinkDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VELTRIX", "Link");

    private static string HubName(Func<LinkCapture?, LocalDb> db)
    {
        using var ctx = db(null);
        return ctx.Values.Find(LinkProtocol.HubNameKey)?.Value ?? Environment.MachineName;
    }

    private static async Task StopHubAsync(LinkHubServer server, CancellationTokenSource? discovery)
    {
        if (discovery is not null) { await discovery.CancelAsync(); discovery.Dispose(); }
        await server.DisposeAsync();
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
