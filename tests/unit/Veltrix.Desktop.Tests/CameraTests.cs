using System.Diagnostics;
using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Cameras;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Security;
using Veltrix.Desktop.Core.Settings;

namespace Veltrix.Desktop.Tests;

/// <summary>CCTV: RTSP presets, LAN-only hosts, ONVIF discovery parsing, encrypted credentials, snapshot paths/retention, sale linking.</summary>
public sealed class CameraTests : IAsyncLifetime
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero));
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "veltrix-cam-" + Guid.NewGuid().ToString("N"));

    private LocalDb Db() => new(new DbContextOptionsBuilder<LocalDb>().UseSqlite(_conn).Options);

    public async Task InitializeAsync()
    {
        await _conn.OpenAsync();
        await using var db = Db();
        await db.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _conn.DisposeAsync();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static CameraConfig Cam(CameraBrand brand, int channel = 1) => new()
    {
        Name = "Till", Brand = brand, Host = "192.168.1.64", Port = 554, Username = "admin", Password = "Pa:ss@w/rd#1", Channel = channel,
    };

    // ------------------------------------------------------------------ URL builder

    [Theory]
    [InlineData(1, CameraStream.Main, "/Streaming/Channels/101")]
    [InlineData(1, CameraStream.Sub, "/Streaming/Channels/102")]
    [InlineData(4, CameraStream.Main, "/Streaming/Channels/401")]
    [InlineData(12, CameraStream.Sub, "/Streaming/Channels/1202")]
    public void Hikvision_url_uses_channel_and_stream(int channel, CameraStream stream, string path)
    {
        var url = RtspUrlBuilder.Build(Cam(CameraBrand.Hikvision, channel), stream);
        Assert.Equal("rtsp://admin:Pa%3Ass%40w%2Frd%231@192.168.1.64:554" + path, url);
    }

    [Theory]
    [InlineData(1, CameraStream.Main, "channel=1&subtype=0")]
    [InlineData(1, CameraStream.Sub, "channel=1&subtype=1")]
    [InlineData(7, CameraStream.Main, "channel=7&subtype=0")]
    public void Dahua_url_uses_channel_and_subtype(int channel, CameraStream stream, string query)
    {
        var url = RtspUrlBuilder.Build(Cam(CameraBrand.Dahua, channel) with { Port = 8554 }, stream);
        Assert.Equal("rtsp://admin:Pa%3Ass%40w%2Frd%231@192.168.1.64:8554/cam/realmonitor?" + query, url);
    }

    [Fact]
    public void Credentials_are_percent_encoded_and_can_be_left_out_or_redacted()
    {
        var cam = Cam(CameraBrand.Hikvision) with { Username = "caisse 1", Password = "é&?" };
        var url = RtspUrlBuilder.Build(cam, CameraStream.Main);
        Assert.StartsWith("rtsp://caisse%201:%C3%A9%26%3F@", url);
        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host == "192.168.1.64");
        Assert.Equal("rtsp://192.168.1.64:554/Streaming/Channels/101", RtspUrlBuilder.Build(cam, CameraStream.Main, includeCredentials: false));
        Assert.Equal("rtsp://***@192.168.1.64:554/Streaming/Channels/101", RtspUrlBuilder.Redact(url));
        Assert.Equal("rtsp://192.168.1.64:554/Streaming/Channels/102", RtspUrlBuilder.Build(cam with { Username = "" }, CameraStream.Sub));
    }

    [Fact]
    public void Generic_custom_url_gets_credentials_only_when_missing()
    {
        var cam = new CameraConfig { Brand = CameraBrand.Generic, CustomUrl = "rtsp://10.0.0.5:554/live/ch0", Username = "u", Password = "p w" };
        Assert.Equal("rtsp://u:p%20w@10.0.0.5:554/live/ch0", RtspUrlBuilder.Build(cam, CameraStream.Main));
        Assert.Equal("rtsp://10.0.0.5:554/live/ch0", RtspUrlBuilder.Build(cam, CameraStream.Main, includeCredentials: false));
        var withCreds = cam with { CustomUrl = "rtsp://x:y@10.0.0.5/live" };
        Assert.Equal("rtsp://x:y@10.0.0.5/live", RtspUrlBuilder.Build(withCreds, CameraStream.Sub));
        Assert.Equal("rtsp://10.0.0.5/live", RtspUrlBuilder.Build(withCreds, CameraStream.Sub, includeCredentials: false));
    }

    [Fact]
    public void Snapshot_urls_follow_brand_presets()
    {
        Assert.Equal("http://192.168.1.64:80/ISAPI/Streaming/channels/301/picture", RtspUrlBuilder.SnapshotUrl(Cam(CameraBrand.Hikvision, 3)));
        Assert.Equal("http://192.168.1.64:80/cgi-bin/snapshot.cgi?channel=2", RtspUrlBuilder.SnapshotUrl(Cam(CameraBrand.Dahua, 2)));
        Assert.Null(RtspUrlBuilder.SnapshotUrl(new CameraConfig { Brand = CameraBrand.Generic, CustomUrl = "rtsp://10.0.0.5/x" }));
    }

    // ------------------------------------------------------------------ LAN-only hosts

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.0.10", true)]
    [InlineData("169.254.10.20", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:192.168.1.2", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("172.15.255.255", false)]
    [InlineData("192.169.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("2001:4860:4860::8888", false)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void Only_private_and_link_local_addresses_are_local(string ip, bool local) => Assert.Equal(local, LanAddress.IsLocal(IPAddress.Parse(ip)));

    [Fact]
    public async Task Host_names_must_resolve_only_to_lan_addresses()
    {
        Task<IPAddress[]> Resolve(params string[] ips) => Task.FromResult(ips.Select(IPAddress.Parse).ToArray());
        Assert.Equal(IPAddress.Parse("192.168.1.20"), await LanAddress.ResolveLocalAsync("nvr.local", default, (_, _) => Resolve("192.168.1.20")));
        Assert.Null(await LanAddress.ResolveLocalAsync("cam.example.com", default, (_, _) => Resolve("93.184.216.34")));
        // Mixed answers (DNS rebinding style) are refused.
        Assert.Null(await LanAddress.ResolveLocalAsync("mixed.lan", default, (_, _) => Resolve("192.168.1.20", "1.1.1.1")));
        Assert.Null(await LanAddress.ResolveLocalAsync("8.8.8.8", default, (_, _) => throw new InvalidOperationException("no DNS for literals")));
        Assert.Null(await LanAddress.ResolveLocalAsync("bad host/x", default));
        Assert.True(LanAddress.IsAcceptableHostText("[fe80::1]"));
        Assert.False(LanAddress.IsAcceptableHostText("203.0.113.9"));
    }

    [Fact]
    public async Task Saving_a_camera_with_a_public_host_is_refused()
    {
        var repo = Repo();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => repo.SaveAsync(Cam(CameraBrand.Hikvision) with { Host = "8.8.8.8" }));
        Assert.Equal("cam.err.notLan", ex.Message);
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => repo.SaveAsync(new CameraConfig
        {
            Name = "x", Brand = CameraBrand.Generic, CustomUrl = "rtsp://203.0.113.5/live",
        }));
        Assert.Equal("cam.err.notLan", ex.Message);
        Assert.Equal(CameraTestStatus.NotLan, (await CameraConnectionTester.TestAsync(Cam(CameraBrand.Dahua) with { Host = "1.1.1.1" }, CameraStream.Sub)).Status);
    }

    // ------------------------------------------------------------------ discovery

    private const string HikMatch = """
        <?xml version="1.0" encoding="UTF-8"?>
        <env:Envelope xmlns:env="http://www.w3.org/2003/05/soap-envelope" xmlns:wsa="http://schemas.xmlsoap.org/ws/2004/08/addressing"
            xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery" xmlns:dn="http://www.onvif.org/ver10/network/wsdl">
          <env:Header><wsa:RelatesTo>uuid:7f0c0b1e-0000-0000-0000-000000000001</wsa:RelatesTo></env:Header>
          <env:Body><d:ProbeMatches><d:ProbeMatch>
            <wsa:EndpointReference><wsa:Address>urn:uuid:a1b2</wsa:Address></wsa:EndpointReference>
            <d:Types>dn:NetworkVideoTransmitter tds:Device</d:Types>
            <d:Scopes>onvif://www.onvif.org/type/video_encoder onvif://www.onvif.org/name/HIKVISION%20DS-2CD2143G2 onvif://www.onvif.org/hardware/DS-2CD2143G2-I onvif://www.onvif.org/location/city/hangzhou</d:Scopes>
            <d:XAddrs>http://192.168.1.64/onvif/device_service http://[fe80::4a:1]/onvif/device_service</d:XAddrs>
            <d:MetadataVersion>10</d:MetadataVersion>
          </d:ProbeMatch></d:ProbeMatches></env:Body>
        </env:Envelope>
        """;

    private const string DahuaMatch = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing" xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery">
          <s:Body><d:ProbeMatches>
            <d:ProbeMatch><a:EndpointReference><a:Address>uuid:x</a:Address></a:EndpointReference>
              <d:Scopes>onvif://www.onvif.org/Profile/Streaming onvif://www.onvif.org/name/Dahua onvif://www.onvif.org/hardware/DH-XVR5108HS</d:Scopes>
              <d:XAddrs>http://10.0.0.108:80/onvif/device_service</d:XAddrs></d:ProbeMatch>
            <d:ProbeMatch><d:Scopes>onvif://www.onvif.org/name/Cloud</d:Scopes><d:XAddrs>http://203.0.113.10/onvif/device_service</d:XAddrs></d:ProbeMatch>
          </d:ProbeMatches></s:Body>
        </s:Envelope>
        """;

    [Fact]
    public void Probe_message_asks_for_network_video_transmitters()
    {
        var id = Guid.NewGuid();
        var xml = OnvifDiscovery.BuildProbe(id);
        Assert.Contains("dn:NetworkVideoTransmitter", xml);
        Assert.Contains("uuid:" + id.ToString("D"), xml);
        Assert.Contains("http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe", xml);
        System.Xml.Linq.XDocument.Parse(xml); // well-formed
    }

    [Fact]
    public void Probe_matches_are_parsed_with_brand_name_and_xaddrs()
    {
        var hik = Assert.Single(OnvifDiscovery.ParseProbeMatches(HikMatch, "192.168.1.64"));
        Assert.Equal("192.168.1.64", hik.Address);
        Assert.Equal(CameraBrand.Hikvision, hik.BrandGuess);
        Assert.Equal("HIKVISION DS-2CD2143G2", hik.Name);
        Assert.Equal("DS-2CD2143G2-I", hik.Hardware);
        Assert.Equal(2, hik.XAddrs.Count);

        // The public-address device is dropped (LAN only); the DVR is recognised as Dahua.
        var dahua = Assert.Single(OnvifDiscovery.ParseProbeMatches(DahuaMatch, "10.0.0.108"));
        Assert.Equal(("10.0.0.108", CameraBrand.Dahua, "DH-XVR5108HS"), (dahua.Address, dahua.BrandGuess, dahua.Hardware));
    }

    [Fact]
    public void Discovery_ignores_garbage_and_merges_duplicates()
    {
        Assert.Empty(OnvifDiscovery.ParseProbeMatches("not xml"));
        Assert.Empty(OnvifDiscovery.ParseProbeMatches("<a><b/></a>"));
        var merged = OnvifDiscovery.Merge(OnvifDiscovery.ParseProbeMatches(DahuaMatch).Concat(OnvifDiscovery.ParseProbeMatches(HikMatch))
            .Concat(OnvifDiscovery.ParseProbeMatches(HikMatch)));
        Assert.Equal(["10.0.0.108", "192.168.1.64"], merged.Select(d => d.Address));
    }

    // ------------------------------------------------------------------ RTSP auth

    [Fact]
    public void Digest_response_matches_rfc2617_example()
    {
        var r = CameraConnectionTester.DigestResponse("Mufasa", "testrealm@host.com", "Circle Of Life", "GET", "/dir/index.html",
            "dcd98b7102dd2f0e8b11d0f600bfb0c093", "auth", "00000001", "0a4f113b");
        Assert.Equal("6629fae49393a05397450978507c4ef1", r);
        var header = CameraConnectionTester.Authorization("Basic realm=\"x\"\nDigest realm=\"IP Camera\", nonce=\"abc\"", "DESCRIBE", "rtsp://h/1", "admin", "pw");
        Assert.StartsWith("Digest username=\"admin\", realm=\"IP Camera\", nonce=\"abc\", uri=\"rtsp://h/1\"", header);
        Assert.StartsWith("Basic ", CameraConnectionTester.Authorization("Basic realm=\"x\"", "DESCRIBE", "rtsp://h/1", "a", "b"));
    }

    // ------------------------------------------------------------------ credentials at rest

    [Fact]
    public async Task Credentials_are_encrypted_in_the_database_and_round_trip()
    {
        var repo = Repo();
        var saved = await repo.SaveAsync(Cam(CameraBrand.Hikvision) with { Role = CameraRoles.Cashier });
        await using (var db = Db())
        {
            var row = await db.Cameras.SingleAsync();
            Assert.StartsWith("dp1:", row.PasswordProtected);
            Assert.DoesNotContain("Pa:ss", row.PasswordProtected!);
            Assert.DoesNotContain("admin", row.UsernameProtected!);
        }
        var back = Assert.Single(await repo.ListAsync());
        Assert.Equal((saved.Id, "admin", "Pa:ss@w/rd#1", 554, CameraRoles.Cashier), (back.Id, back.Username, back.Password, back.Port, back.Role));
        Assert.Equal(back.Id, (await repo.CashierCameraAsync())!.Id);
        // Another key (e.g. another Windows account) cannot read them: empty, never an exception.
        var other = new CameraRepository(Db, new CredentialProtector(new XorProtector(0x33)), _time);
        Assert.Equal("", (await other.ListAsync())[0].Password);
        await repo.DeleteAsync(back.Id);
        Assert.Empty(await repo.ListAsync());
    }

    [Fact]
    public async Task Upgrading_a_v3_database_adds_camera_tables_and_keeps_data()
    {
        await using (var db = Db())
        {
            db.Sales.Add(new LocalSale { Id = Guid.NewGuid(), ClientNumber = "POS1-1", OpId = "op1", Total = 50, CreatedAt = _time.GetUtcNow() });
            await db.SaveChangesAsync();
            foreach (var t in LocalDb.V4Tables) await db.Database.ExecuteSqlRawAsync($"DROP TABLE \"{t}\";");
            await db.SetValueAsync(LocalDb.Keys.SchemaVersion, "3");
        }
        await using (var db = Db()) await db.InitializeAsync();
        await using (var db = Db())
        {
            Assert.Equal(1, await db.Sales.CountAsync());
            Assert.Equal(LocalDb.SchemaVersion.ToString(), await db.GetValueAsync(LocalDb.Keys.SchemaVersion));
        }
        await Repo().SaveAsync(Cam(CameraBrand.Dahua));
        Assert.Single(await Repo().ListAsync());
    }

    // ------------------------------------------------------------------ snapshots

    [Fact]
    public void Snapshot_path_is_dated_under_the_app_data_folder()
    {
        var store = new SnapshotStore(_dir, _time);
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var rel = store.RelativePathFor(id, new DateTimeOffset(2026, 3, 5, 23, 0, 0, TimeSpan.Zero));
        Assert.Equal(Path.Combine("Snapshots", "2026", "03", "11111111-2222-3333-4444-555555555555.jpg"), rel);
        Assert.Equal(Path.Combine(_dir, rel), store.FullPath(rel));
    }

    [Fact]
    public void Retention_deletes_only_old_snapshots_and_empty_folders()
    {
        var store = new SnapshotStore(_dir, _time);
        var oldFile = store.FullPath(store.RelativePathFor(Guid.NewGuid(), _time.GetUtcNow().AddDays(-40)));
        var newFile = store.FullPath(store.RelativePathFor(Guid.NewGuid(), _time.GetUtcNow().AddDays(-2)));
        foreach (var f in new[] { oldFile, newFile }) { Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllBytes(f, [0xFF, 0xD8]); }
        File.SetLastWriteTimeUtc(oldFile, _time.GetUtcNow().UtcDateTime.AddDays(-31));
        File.SetLastWriteTimeUtc(newFile, _time.GetUtcNow().UtcDateTime.AddDays(-29));
        Assert.Equal(1, store.Cleanup(30));
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(newFile));
        Assert.False(Directory.Exists(Path.GetDirectoryName(oldFile)));
    }

    [Fact]
    public async Task Sale_snapshot_runs_in_background_and_is_linked_to_the_sale()
    {
        var gate = new TaskCompletionSource();
        var capturer = new FakeCapturer(async (path, ct) => { await gate.Task.WaitAsync(ct); await File.WriteAllBytesAsync(path, [0xFF, 0xD8, 0xFF], ct); return true; });
        var (svc, settings) = Snapshots(capturer);
        await Repo().SaveAsync(Cam(CameraBrand.Hikvision) with { Role = CameraRoles.Cashier });
        var saleId = Guid.NewGuid();

        var sw = Stopwatch.StartNew();
        var task = svc.CaptureForSaleInBackground(saleId);
        Assert.True(sw.ElapsedMilliseconds < 500); // returns at once: the sale is never held up by the camera
        Assert.False(task.IsCompleted);
        Assert.Null(await svc.FindAsync(saleId));

        gate.SetResult();
        await task.WaitAsync(TimeSpan.FromSeconds(10));
        var path = await svc.FindAsync(saleId);
        Assert.NotNull(path);
        Assert.EndsWith(saleId + ".jpg", path);
        Assert.Equal("cashier", capturer.LastCamera!.Role);

        // Retention removes the link with the file.
        _time.Advance(TimeSpan.FromDays(31));
        File.SetLastWriteTimeUtc(path, _time.GetUtcNow().UtcDateTime.AddDays(-31));
        Assert.Equal(1, await svc.CleanupAsync());
        Assert.Null(await svc.FindAsync(saleId));
        await using var db = Db();
        Assert.Empty(await db.SaleSnapshots.ToListAsync());
        _ = settings;
    }

    [Fact]
    public async Task Sale_snapshot_failures_never_surface_and_link_nothing()
    {
        await Repo().SaveAsync(Cam(CameraBrand.Dahua) with { Role = CameraRoles.Cashier });
        var (throwing, _) = Snapshots(new FakeCapturer((_, _) => throw new HttpRequestException("camera down")));
        var id = Guid.NewGuid();
        await throwing.CaptureForSaleInBackground(id).WaitAsync(TimeSpan.FromSeconds(10)); // completes, does not fault
        Assert.Null(await throwing.FindAsync(id));

        var (noImage, _) = Snapshots(new FakeCapturer((_, _) => Task.FromResult(false)));
        await noImage.CaptureForSaleInBackground(id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(await noImage.FindAsync(id));
    }

    [Fact]
    public async Task No_snapshot_without_cashier_camera_or_when_disabled()
    {
        var calls = 0;
        var capturer = new FakeCapturer((p, _) => { calls++; File.WriteAllBytes(p, [0xFF, 0xD8]); return Task.FromResult(true); });
        var (svc, settings) = Snapshots(capturer);
        await Repo().SaveAsync(Cam(CameraBrand.Hikvision)); // general role only
        await svc.CaptureForSaleInBackground(Guid.NewGuid());
        await Repo().SaveAsync(Cam(CameraBrand.Hikvision) with { Name = "Cash", Role = CameraRoles.Cashier, Enabled = false });
        await svc.CaptureForSaleInBackground(Guid.NewGuid());
        await Repo().SaveAsync(Cam(CameraBrand.Hikvision) with { Name = "Cash2", Role = CameraRoles.Cashier });
        settings.SnapshotOnSale = false;
        Assert.True(svc.CaptureForSaleInBackground(Guid.NewGuid()).IsCompleted);
        Assert.Equal(0, calls);
    }

    // ------------------------------------------------------------------ helpers

    private CameraRepository Repo() => new(Db, new CredentialProtector(new XorProtector(0x5A)), _time);

    private (SaleSnapshotService Service, CameraSettings Settings) Snapshots(ISnapshotCapturer capturer)
    {
        var settings = new CameraSettings();
        return (new SaleSnapshotService(Db, Repo(), new SnapshotStore(_dir, _time), capturer, _time, () => settings), settings);
    }

    private sealed class FakeCapturer(Func<string, CancellationToken, Task<bool>> capture) : ISnapshotCapturer
    {
        public CameraConfig? LastCamera { get; private set; }

        public Task<bool> CaptureAsync(CameraConfig camera, string destinationPath, CancellationToken ct)
        {
            LastCamera = camera;
            return capture(destinationPath, ct);
        }
    }

    /// <summary>Reversible test protector with an authenticity check, so a different key fails like DPAPI does.</summary>
    private sealed class XorProtector(byte key) : IDataProtector
    {
        public byte[] Protect(byte[] plaintext) => [key, .. plaintext.Select(b => (byte)(b ^ key))];

        public byte[] Unprotect(byte[] ciphertext) => ciphertext.Length > 0 && ciphertext[0] == key
            ? ciphertext.Skip(1).Select(b => (byte)(b ^ key)).ToArray()
            : throw new System.Security.Cryptography.CryptographicException("wrong key");
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
