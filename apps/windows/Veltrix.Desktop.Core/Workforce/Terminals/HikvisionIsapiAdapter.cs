using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Veltrix.Desktop.Core.Workforce.Terminals;

/// <summary>
/// Hikvision access-control / time-attendance terminals over ISAPI (HTTP digest authentication).
/// STATUS: Implemented from public ISAPI docs — NOT verified on hardware.
/// Endpoints used (read-only): GET /ISAPI/System/deviceInfo (XML), GET /ISAPI/System/time (XML),
/// POST /ISAPI/AccessControl/AcsEvent?format=json with AcsEventCond {searchID, searchResultPosition, maxResults, major, minor, startTime, endTime}.
/// Responses are parsed defensively: unknown / missing fields never crash ingestion (unknown events become method/result "unknown").
/// </summary>
public sealed class HikvisionIsapiAdapter : ITerminalAdapter
{
    public const string Status = "Implemented from public ISAPI docs — NOT verified on hardware";
    /// <summary>Major event type 5 = "event" (authentication results etc.); minor 0 = every minor type of that major.</summary>
    public const int MajorEvent = 5;
    public const int MinorCardPass = 0x01;
    public const int MinorFingerprintPass = 0x26;
    public const int MinorFingerprintFail = 0x27;
    public const int MinorFacePass = 0x4b;
    public const int MinorFaceFail = 0x4c;

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly Uri _base;
    private readonly int _offsetMinutes;
    private readonly TimeProvider _time;

    /// <param name="handler">Test seam; production uses a digest-auth <see cref="HttpClientHandler"/>.</param>
    /// <param name="offsetMinutes">Terminal UTC offset used to format search times.</param>
    public HikvisionIsapiAdapter(TerminalConnection connection, TimeProvider time, int offsetMinutes = 0, HttpMessageHandler? handler = null)
    {
        _base = connection.BaseUri(80);
        _offsetMinutes = offsetMinutes;
        _time = time;
        handler ??= connection.CreateHandler(_base, digest: true);
        _http = new HttpClient(handler, disposeHandler: true) { BaseAddress = _base, Timeout = TimeSpan.FromSeconds(15) };
        _ownsClient = true;
    }

    public string Manufacturer => TerminalManufacturers.Hikvision;

    public TerminalCapabilities Capabilities => TerminalCapabilities.EventImport | TerminalCapabilities.DeviceInfo | TerminalCapabilities.ClockRead
        | TerminalCapabilities.Card | TerminalCapabilities.Fingerprint | TerminalCapabilities.Face | TerminalCapabilities.VendorEventIds;

    public async Task ValidateConnectionAsync(CancellationToken ct = default) => await GetStringAsync("/ISAPI/System/deviceInfo", ct);

    public async Task<TerminalDeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default)
    {
        var info = ParseDeviceInfo(await GetStringAsync("/ISAPI/System/deviceInfo", ct));
        DateTimeOffset? clock = null;
        try { clock = ParseTime(await GetStringAsync("/ISAPI/System/time", ct)); }
        catch (TerminalException e) when (e.Kind is TerminalErrorKind.Unsupported or TerminalErrorKind.ProtocolError) { /* clock optional */ }
        return info with { DeviceClock = clock };
    }

    public async Task<TerminalImportPage> ImportEventsAsync(string? cursor, DateTimeOffset? since, int maxEvents, CancellationToken ct = default)
    {
        var c = Cursor.Parse(cursor) ?? new Cursor(since ?? _time.GetUtcNow().AddDays(-31), 0, Guid.NewGuid().ToString("N"));
        var offset = TimeSpan.FromMinutes(_offsetMinutes);
        var body = BuildSearch(c.SearchId, c.Position, Math.Clamp(maxEvents, 1, 100), c.Start.ToOffset(offset), _time.GetUtcNow().AddDays(1).ToOffset(offset));
        var json = await TerminalErrors.GuardAsync(async () =>
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("/ISAPI/AccessControl/AcsEvent?format=json", content, ct);
            if (!resp.IsSuccessStatusCode) throw TerminalErrors.FromHttpStatus((int)resp.StatusCode, "ISAPI AcsEvent");
            return await resp.Content.ReadAsStringAsync(ct);
        }, "ISAPI AcsEvent", ct);
        var page = ParseAcsEvents(json);
        string next;
        if (page.More) next = new Cursor(c.Start, c.Position + page.Events.Count, c.SearchId).ToString();
        else
        {
            // Restart from the newest event time (overlap of that second is deduplicated by ingestion).
            var newest = page.Events.Count == 0 ? c.Start
                : page.Events.Max(e => new DateTimeOffset(e.DeviceLocalTime, TimeSpan.FromMinutes(e.DeviceOffsetMinutes ?? _offsetMinutes)));
            next = new Cursor(newest > c.Start ? newest : c.Start, 0, Guid.NewGuid().ToString("N")).ToString();
        }
        return new TerminalImportPage(page.Events, next, page.More && page.Events.Count > 0);
    }

    public IAsyncEnumerable<TerminalEvent> StreamEventsAsync(CancellationToken ct = default) =>
        throw TerminalException.Unsupported("Hikvision: real-time alert stream is not implemented; events are polled.");

    public async Task<TerminalHealth> HealthAsync(CancellationToken ct = default)
    {
        var start = _time.GetTimestamp();
        try
        {
            await ValidateConnectionAsync(ct);
            return new TerminalHealth(true, _time.GetElapsedTime(start), "ISAPI OK");
        }
        catch (TerminalException e) { return new TerminalHealth(e.Kind != TerminalErrorKind.Unreachable, null, e.Message); }
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsClient) _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private Task<string> GetStringAsync(string path, CancellationToken ct) => TerminalErrors.GuardAsync(async () =>
    {
        using var resp = await _http.GetAsync(path, ct);
        if (!resp.IsSuccessStatusCode) throw TerminalErrors.FromHttpStatus((int)resp.StatusCode, "ISAPI " + path);
        return await resp.Content.ReadAsStringAsync(ct);
    }, "ISAPI " + path, ct);

    // ------------------------------------------------------------------ documented payloads (pure, unit-tested)

    /// <summary>AcsEventCond request body (major 5 / minor 0 = all authentication events).</summary>
    public static string BuildSearch(string searchId, int position, int maxResults, DateTimeOffset start, DateTimeOffset end) =>
        new JsonObject
        {
            ["AcsEventCond"] = new JsonObject
            {
                ["searchID"] = searchId,
                ["searchResultPosition"] = position,
                ["maxResults"] = maxResults,
                ["major"] = MajorEvent,
                ["minor"] = 0,
                ["startTime"] = start.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
                ["endTime"] = end.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            },
        }.ToJsonString();

    /// <summary>Parses &lt;DeviceInfo&gt; (namespace-agnostic): model, serialNumber, firmwareVersion.</summary>
    public static TerminalDeviceInfo ParseDeviceInfo(string xml)
    {
        var root = XDocument.Parse(xml).Root ?? throw new InvalidDataException("empty deviceInfo");
        string? V(string name) => root.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;
        var fw = V("firmwareVersion") is { } f ? (V("firmwareReleasedDate") is { } d ? f + " " + d : f) : null;
        return new TerminalDeviceInfo(TerminalManufacturers.Hikvision, V("model"), V("serialNumber"), fw);
    }

    /// <summary>Parses &lt;Time&gt;&lt;localTime&gt;2019-02-28T10:50:44+08:00&lt;/localTime&gt;.</summary>
    public static DateTimeOffset? ParseTime(string xml)
    {
        var root = XDocument.Parse(xml).Root;
        var v = root?.Elements().FirstOrDefault(e => e.Name.LocalName == "localTime")?.Value.Trim();
        return v is not null && DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
    }

    public sealed record AcsEventPage(IReadOnlyList<TerminalEvent> Events, bool More, int? TotalMatches);

    /// <summary>Parses the AcsEvent search response: responseStatusStrg (OK / MORE / NO MATCH), InfoList[].</summary>
    public static AcsEventPage ParseAcsEvents(string json)
    {
        var root = JsonNode.Parse(json)?["AcsEvent"] ?? throw new InvalidDataException("AcsEvent missing");
        var status = Str(root["responseStatusStrg"]);
        int? total = Int(root["totalMatches"]);
        var events = new List<TerminalEvent>();
        if (root["InfoList"] is JsonArray list)
        {
            foreach (var item in list)
            {
                if (item is not JsonObject o) continue;
                var timeText = Str(o["time"]);
                if (timeText is null) continue;
                DateTime local;
                int? offset = null;
                if (DateTimeOffset.TryParse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto) && HasOffset(timeText))
                    (local, offset) = (dto.DateTime, (int)dto.Offset.TotalMinutes);
                else if (DateTime.TryParse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) local = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
                else continue;
                var minor = Int(o["minor"]) ?? -1;
                var user = Str(o["employeeNoString"]) ?? (Int(o["employeeNo"]) is { } n ? n.ToString(CultureInfo.InvariantCulture) : null);
                var card = Str(o["cardNo"]);
                var serial = Int(o["serialNo"]);
                var (method, result) = MapMinor(minor, Str(o["currentVerifyMode"]));
                var raw = new Dictionary<string, string> { ["major"] = Text(Int(o["major"])), ["minor"] = Text(minor), ["time"] = timeText };
                if (Str(o["attendanceStatus"]) is { } att) raw["attendanceStatus"] = att;
                if (Str(o["currentVerifyMode"]) is { } mode) raw["currentVerifyMode"] = mode;
                events.Add(new TerminalEvent(serial?.ToString(CultureInfo.InvariantCulture), user, card, local, offset, method, result, MapAttendance(Str(o["attendanceStatus"])), raw));
            }
        }
        return new AcsEventPage(events, string.Equals(status, "MORE", StringComparison.OrdinalIgnoreCase), total);
    }

    public static (string Method, string Result) MapMinor(int minor, string? verifyMode) => minor switch
    {
        MinorCardPass => (VerifyMethods.Card, VerifyResults.Success),
        MinorFingerprintPass => (VerifyMethods.Finger, VerifyResults.Success),
        MinorFingerprintFail => (VerifyMethods.Finger, VerifyResults.Failed),
        MinorFacePass => (VerifyMethods.Face, VerifyResults.Success),
        MinorFaceFail => (VerifyMethods.Face, VerifyResults.Failed),
        _ => (verifyMode?.ToLowerInvariant() switch
        {
            "card" => VerifyMethods.Card,
            "fp" or "fingerprint" => VerifyMethods.Finger,
            "face" => VerifyMethods.Face,
            _ => VerifyMethods.Unknown,
        }, VerifyResults.Unknown),
    };

    /// <summary>attendanceStatus: checkIn / checkOut / breakOut / breakIn (others / undefined = toggle).</summary>
    public static string? MapAttendance(string? status) => status switch
    {
        "checkIn" or "overtimeIn" => Data.PunchTypes.In,
        "checkOut" or "overtimeOut" => Data.PunchTypes.Out,
        "breakOut" => Data.PunchTypes.BreakStart,
        "breakIn" => Data.PunchTypes.BreakEnd,
        _ => null,
    };

    private static bool HasOffset(string s) => s.EndsWith('Z') || s.Length > 6 && (s[^6] == '+' || s[^6] == '-') && s[^3] == ':';
    private static string Text(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";

    internal static string? Str(JsonNode? n) => n is JsonValue v
        ? v.TryGetValue<string>(out var s) ? (s.Trim().Length > 0 ? s.Trim() : null) : v.TryGetValue<long>(out var l) ? l.ToString(CultureInfo.InvariantCulture) : null
        : null;

    internal static int? Int(JsonNode? n) => n is JsonValue v
        ? v.TryGetValue<int>(out var i) ? i : v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null
        : null;

    private sealed record Cursor(DateTimeOffset Start, int Position, string SearchId)
    {
        public override string ToString() =>
            string.Join('|', Start.ToString("O", CultureInfo.InvariantCulture), Position.ToString(CultureInfo.InvariantCulture), SearchId);

        public static Cursor? Parse(string? s)
        {
            var p = s?.Split('|');
            return p is { Length: 3 } && DateTimeOffset.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pos)
                ? new Cursor(t, pos, p[2]) : null;
        }
    }
}
