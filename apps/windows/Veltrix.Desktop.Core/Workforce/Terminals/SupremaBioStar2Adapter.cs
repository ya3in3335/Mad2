using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Veltrix.Desktop.Core.Workforce.Terminals;

/// <summary>
/// Suprema terminals through the BioStar 2 server REST API (the terminals themselves are managed by BioStar 2).
/// STATUS: Implemented from public BioStar 2 API docs — NOT verified on hardware.
/// Endpoints used (read-only): POST /api/login {"User":{"login_id","password"}} → session header "bs-session-id";
/// POST /api/events/search {"Query":{"limit","conditions":[{"column":"datetime","operator":3 (BETWEEN),"values":[from,to]}],"orders":[…]}}
/// → {"EventCollection":{"rows":[{id, datetime, user_id{user_id}, device_id{id}, event_type_id{code}}]}}.
/// The BioStar server does not expose a terminal clock here, so drift is not measured (capability ClockRead absent).
/// </summary>
public sealed class SupremaBioStar2Adapter : ITerminalAdapter
{
    public const string Status = "Implemented from public BioStar 2 API docs — NOT verified on hardware";
    public const string SessionHeader = "bs-session-id";

    private readonly HttpClient _http;
    private readonly TerminalConnection _connection;
    private readonly TimeProvider _time;
    private string? _session;

    public SupremaBioStar2Adapter(TerminalConnection connection, TimeProvider time, HttpMessageHandler? handler = null)
    {
        _connection = connection;
        _time = time;
        var baseUri = connection.BaseUri(443);
        _http = new HttpClient(handler ?? connection.CreateHandler(baseUri, digest: false), disposeHandler: true) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(20) };
    }

    public string Manufacturer => TerminalManufacturers.Suprema;

    public TerminalCapabilities Capabilities => TerminalCapabilities.EventImport | TerminalCapabilities.DeviceInfo | TerminalCapabilities.Fingerprint
        | TerminalCapabilities.Face | TerminalCapabilities.Card | TerminalCapabilities.Pin | TerminalCapabilities.VendorEventIds;

    public async Task ValidateConnectionAsync(CancellationToken ct = default)
    {
        _session = null;
        await LoginAsync(ct);
    }

    private async Task LoginAsync(CancellationToken ct)
    {
        if (_session is not null) return;
        _session = await TerminalErrors.GuardAsync(async () =>
        {
            using var content = new StringContent(BuildLogin(_connection.UserName ?? "", _connection.Password ?? ""), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("/api/login", content, ct);
            if (!resp.IsSuccessStatusCode) throw TerminalErrors.FromHttpStatus((int)resp.StatusCode, "BioStar login");
            return resp.Headers.TryGetValues(SessionHeader, out var v) && v.FirstOrDefault() is { Length: > 0 } s
                ? s : throw new TerminalException(TerminalErrorKind.ProtocolError, "BioStar login: no session header.");
        }, "BioStar login", ct);
    }

    public async Task<TerminalDeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default)
    {
        await LoginAsync(ct);
        return new TerminalDeviceInfo(Manufacturer, "BioStar 2", _connection.RemoteDeviceId, null);
    }

    public async Task<TerminalImportPage> ImportEventsAsync(string? cursor, DateTimeOffset? since, int maxEvents, CancellationToken ct = default)
    {
        await LoginAsync(ct);
        var start = DateTimeOffset.TryParse(cursor, CultureInfo.InvariantCulture, DateTimeStyles.None, out var c) ? c : since ?? _time.GetUtcNow().AddDays(-31);
        var limit = Math.Clamp(maxEvents, 1, 200);
        var body = BuildSearch(limit, start, _time.GetUtcNow().AddDays(1));
        var json = await TerminalErrors.GuardAsync(async () =>
        {
            for (var attempt = 0; ; attempt++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "/api/events/search") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                req.Headers.TryAddWithoutValidation(SessionHeader, _session);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var resp = await _http.SendAsync(req, ct);
                if ((int)resp.StatusCode == 401 && attempt == 0) { _session = null; await LoginAsync(ct); continue; } // session expired
                if (!resp.IsSuccessStatusCode) throw TerminalErrors.FromHttpStatus((int)resp.StatusCode, "BioStar events/search");
                return await resp.Content.ReadAsStringAsync(ct);
            }
        }, "BioStar events/search", ct);
        var (events, rowCount) = ParseEvents(json, _connection.RemoteDeviceId);
        var newest = events.Count == 0 ? start : events.Max(e => new DateTimeOffset(e.DeviceLocalTime, TimeSpan.FromMinutes(e.DeviceOffsetMinutes ?? 0)));
        var full = rowCount >= limit;
        // A full page whose newest event is still at the start second would loop: step one second (documented limitation).
        if (full && newest <= start) newest = start.AddSeconds(1);
        return new TerminalImportPage(events, newest.ToString("O", CultureInfo.InvariantCulture), full);
    }

    public IAsyncEnumerable<TerminalEvent> StreamEventsAsync(CancellationToken ct = default) =>
        throw TerminalException.Unsupported("BioStar 2: real-time WebSocket events are not implemented; events are polled.");

    public async Task<TerminalHealth> HealthAsync(CancellationToken ct = default)
    {
        var start = _time.GetTimestamp();
        try
        {
            await ValidateConnectionAsync(ct);
            return new TerminalHealth(true, _time.GetElapsedTime(start), "BioStar 2 OK");
        }
        catch (TerminalException e) { return new TerminalHealth(e.Kind != TerminalErrorKind.Unreachable, null, e.Message); }
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------ documented payloads (pure, unit-tested)

    public static string BuildLogin(string loginId, string password) =>
        new JsonObject { ["User"] = new JsonObject { ["login_id"] = loginId, ["password"] = password } }.ToJsonString();

    public static string BuildSearch(int limit, DateTimeOffset from, DateTimeOffset to) => new JsonObject
    {
        ["Query"] = new JsonObject
        {
            ["limit"] = limit,
            ["conditions"] = new JsonArray(new JsonObject
            {
                ["column"] = "datetime",
                ["operator"] = 3,
                ["values"] = new JsonArray(Iso(from), Iso(to)),
            }),
            ["orders"] = new JsonArray(new JsonObject { ["column"] = "datetime", ["descending"] = false }),
        },
    }.ToJsonString();

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Parses EventCollection.rows; rows of other terminals are skipped when <paramref name="remoteDeviceId"/> is set. Returns the raw row count too.</summary>
    public static (IReadOnlyList<TerminalEvent> Events, int RowCount) ParseEvents(string json, string? remoteDeviceId = null)
    {
        var rows = JsonNode.Parse(json)?["EventCollection"]?["rows"] as JsonArray;
        var events = new List<TerminalEvent>();
        if (rows is null) return (events, 0);
        foreach (var r in rows)
        {
            if (r is not JsonObject o) continue;
            var device = HikvisionIsapiAdapter.Str(o["device_id"]?["id"]);
            if (remoteDeviceId is { Length: > 0 } && device is not null && device != remoteDeviceId) continue;
            if (HikvisionIsapiAdapter.Str(o["datetime"]) is not { } dt
                || !DateTimeOffset.TryParse(dt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) continue;
            var code = HikvisionIsapiAdapter.Int(o["event_type_id"]?["code"]) ?? -1;
            var (method, result) = MapEventCode(code);
            var user = HikvisionIsapiAdapter.Str(o["user_id"]?["user_id"]);
            var raw = new Dictionary<string, string> { ["code"] = code.ToString(CultureInfo.InvariantCulture), ["datetime"] = dt };
            if (device is not null) raw["device_id"] = device;
            if (HikvisionIsapiAdapter.Str(o["tna_key"]) is { } tna) raw["tna_key"] = tna;
            events.Add(new TerminalEvent(HikvisionIsapiAdapter.Str(o["id"]), user, null, DateTime.SpecifyKind(at.UtcDateTime, DateTimeKind.Unspecified), 0, method, result, null, raw));
        }
        return (events, rows.Count);
    }

    /// <summary>
    /// BioStar 2 event codes: 0x10xx verify success, 0x11xx verify fail, 0x13xx identify success, 0x14xx identify fail
    /// (0x12xx / 0x15xx duress → unknown, i.e. reviewed). Sub-code gives the credential (identify: 1-2 finger, 3-4 face; verify: 1 PIN,
    /// 2-3 finger, 4-5 face, ≥6 card).
    /// </summary>
    public static (string Method, string Result) MapEventCode(int code)
    {
        var category = code & 0xFF00;
        var sub = code & 0xFF;
        var result = category switch
        {
            0x1000 or 0x1300 => VerifyResults.Success,
            0x1100 or 0x1400 => VerifyResults.Failed,
            _ => VerifyResults.Unknown,
        };
        var method = category switch
        {
            0x1300 or 0x1400 or 0x1500 => sub switch { 1 or 2 => VerifyMethods.Finger, 3 or 4 => VerifyMethods.Face, _ => VerifyMethods.Unknown },
            0x1000 or 0x1100 or 0x1200 => sub switch
            {
                1 => VerifyMethods.Pin, 2 or 3 => VerifyMethods.Finger, 4 or 5 => VerifyMethods.Face, >= 6 and <= 0x0F => VerifyMethods.Card, _ => VerifyMethods.Unknown,
            },
            _ => VerifyMethods.Unknown,
        };
        return (method, result);
    }
}

/// <summary>
/// ZKTeco terminals require the official ZKTeco Standalone SDK (zkemkeeper COM component, Windows, obtained from ZKTeco and registered with
/// regsvr32 — 32-bit by default). It is NOT bundled and no call is faked: every operation reports Unsupported until a real SDK-backed adapter
/// is added. See docs/HARDWARE_INTEGRATION.md.
/// </summary>
public sealed class ZkTecoSdkAdapter : ITerminalAdapter
{
    public const string Message = "ZKTeco SDK not installed";
    public string Manufacturer => TerminalManufacturers.ZKTeco;
    public TerminalCapabilities Capabilities => TerminalCapabilities.None;
    public Task ValidateConnectionAsync(CancellationToken ct = default) => throw TerminalException.Unsupported(Message);
    public Task<TerminalDeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default) => throw TerminalException.Unsupported(Message);
    public Task<TerminalImportPage> ImportEventsAsync(string? cursor, DateTimeOffset? since, int maxEvents, CancellationToken ct = default) =>
        throw TerminalException.Unsupported(Message);
    public IAsyncEnumerable<TerminalEvent> StreamEventsAsync(CancellationToken ct = default) => throw TerminalException.Unsupported(Message);
    public Task<TerminalHealth> HealthAsync(CancellationToken ct = default) => Task.FromResult(new TerminalHealth(false, null, Message));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
