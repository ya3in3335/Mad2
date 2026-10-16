using Veltrix.Connector;

// Windows service "VeltrixConnector" (LocalSystem, automatic start) installed by the VELTRIX OS setup. Runs as a console app when started by hand.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = ConnectorWorker.ServiceName);
if (OperatingSystem.IsWindows())
    builder.Logging.AddEventLog(o => o.SourceName = "VELTRIX Connector");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<ConnectorWorker>();
builder.Build().Run();
