using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Standalone;
using Veltrix.Desktop.Core.Workforce;
using Veltrix.Desktop.Core.Workforce.Terminals;

namespace Veltrix.Desktop.Tests;

/// <summary>ZKTeco adapter against a FAKE bridge (replies shaped like VeltrixZkBridge). No SDK, no hardware involved.</summary>
public sealed class ZkTecoAdapterTests : IAsyncLifetime
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly WorkforceTests.ManualTime _time = new(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
    private static readonly WorkforceActor Owner = new("owner", LocalRoles.Owner);
    private LocalDb Db() => new(new DbContextOptionsBuilder<LocalDb>().UseSqlite(_conn).Options);

    public async Task InitializeAsync()
    {
        await _conn.OpenAsync();
        await using var db = Db();
        await db.InitializeAsync();
    }

    public async Task DisposeAsync() => await _conn.DisposeAsync();

    private sealed class FakeBridge : IZkBridgeRunner
    {
        public readonly List<JsonElement> Requests = [];
        public List<(string User, string Time, int Verify)> Log { get; } = [];
        public string? Fail { get; set; }

        public Task<string> RunAsync(string requestJson, CancellationToken ct)
        {
            var req = JsonDocument.Parse(requestJson).RootElement.Clone();
            Requests.Add(req);
            if (Fail is { } kind) return Task.FromResult(JsonSerializer.Serialize(new { ok = false, errorKind = kind, error = "boom" }));
            var since = req.GetProperty("since").ValueKind == JsonValueKind.String ? req.GetProperty("since").GetString() : null;
            var logs = Log.Where(l => since is null || string.CompareOrdinal(l.Time, since) >= 0)
                .Select(l => new { enrollNumber = l.User, localTime = l.Time, verifyMode = l.Verify, inOutMode = 0, workCode = 0 });
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                ok = true, serial = "ZK123", firmware = "Ver 6.60", product = "K40", deviceTime = "2026-10-12T13:00:00",
                logs = req.GetProperty("op").GetString() == "logs" ? logs : null,
            }));
        }
    }

    private sealed class Factory(FakeBridge bridge) : ITerminalAdapterFactory
    {
        public ITerminalAdapter Create(WfDevice device, TerminalConnection connection) =>
            new ZkTecoSdkAdapter(connection, bridge) { OffsetMinutes = device.TimeZoneOffsetMinutes };
    }

    [Fact]
    public async Task Imports_logs_incrementally_through_the_pipeline()
    {
        var bridge = new FakeBridge();
        var wf = new WorkforceService(Db, _time, PasswordHasher.MinimumIterations) { TerminalFactory = new Factory(bridge) };
        var d = await wf.UpsertTerminalAsync(Owner, null, new TerminalInput("ZK", TerminalManufacturers.ZKTeco) { Host = "192.168.1.201", TimeZoneOffsetMinutes = 60 });
        var e = await wf.UpsertEmployeeAsync(Owner, null, new EmployeeProfileInput("Ali", 30000, new DateOnly(2026, 1, 1)));
        await wf.MapTerminalUserAsync(Owner, e.Id, d.Id, "15");
        bridge.Log.Add(("15", "2026-10-12T08:00:00", 1));
        bridge.Log.Add(("15", "2026-10-12T12:30:00", 15));

        var r = await wf.SyncTerminalAsync(d.Id, ConnectorOrigins.Manual);
        Assert.True(r.Ok, r.Error);
        Assert.Equal((2, 2, 0), (r.Received, r.Accepted, r.Duplicates));
        Assert.Equal(0, r.DriftSeconds); // 13:00 at UTC+1 == 12:00 UTC
        Assert.Equal(4370, bridge.Requests[^1].GetProperty("port").GetInt32());

        bridge.Log.Add(("15", "2026-10-12T12:45:00", 2));
        var r2 = await wf.SyncTerminalAsync(d.Id, ConnectorOrigins.Manual);
        Assert.Equal("2026-10-12T12:30:00", bridge.Requests[^1].GetProperty("since").GetString()); // cursor = last device time
        Assert.Equal((1, 1), (r2.Accepted, r2.Duplicates)); // the cursor-second overlap is deduplicated
        await using var db = Db();
        Assert.Equal("ZK123", (await db.WfDevices.SingleAsync()).SerialNumber);
        Assert.Equal([VerifyMethods.Finger, VerifyMethods.Face, VerifyMethods.Card],
            (await db.WfDeviceInbox.OrderBy(i => i.OccurredAtUtc).ToListAsync()).Select(i => i.Method));
    }

    [Fact]
    public async Task Bridge_errors_map_to_terminal_errors_and_comm_key_must_be_numeric()
    {
        var bridge = new FakeBridge { Fail = "Unsupported" };
        var a = new ZkTecoSdkAdapter(new TerminalConnection("10.0.0.5", null, null, null, "1234"), bridge);
        Assert.Equal(TerminalErrorKind.Unsupported, (await Assert.ThrowsAsync<TerminalException>(() => a.ValidateConnectionAsync())).Kind);
        Assert.Equal(1234, bridge.Requests[0].GetProperty("commKey").GetInt32());
        bridge.Fail = "Unreachable";
        Assert.False((await a.HealthAsync()).Reachable);
        var bad = new ZkTecoSdkAdapter(new TerminalConnection("10.0.0.5", null, null, null, "abc"), bridge);
        Assert.Equal(TerminalErrorKind.AuthFailed, (await Assert.ThrowsAsync<TerminalException>(() => bad.ValidateConnectionAsync())).Kind);
        Assert.Equal(VerifyMethods.Unknown, ZkTecoSdkAdapter.Method(99));
    }
}

/// <summary>SDK download: every file is hash-checked before anything is written.</summary>
public sealed class ZkTecoSdkFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "veltrix-zk-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class Handler(Func<string, byte[]> body) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body(request.RequestUri.ToString())) });
        }
    }

    [Fact]
    public async Task Tampered_download_is_refused_and_nothing_is_written()
    {
        var h = new Handler(_ => "not the real dll"u8.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => ZkTecoSdkFiles.DownloadAsync(new HttpClient(h), _dir));
        Assert.False(Directory.Exists(_dir) && Directory.EnumerateFiles(_dir).Any());
        Assert.False(ZkTecoSdkFiles.IsInstalled(_dir));
        Assert.StartsWith("https://raw.githubusercontent.com/ZKTeco/Standalone-SDK/", h.Urls[0]);
        Assert.Equal(13, ZkTecoSdkFiles.Files.Select(f => f.Name).Distinct().Count());
    }
}
