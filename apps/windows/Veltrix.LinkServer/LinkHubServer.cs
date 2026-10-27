using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Replication;
using Veltrix.Desktop.Core.Security;

namespace Veltrix.LinkServer;

/// <summary>
/// HTTPS endpoints of the hub. Every call except the two pairing steps needs the bearer token of a paired, non-revoked PC.
/// Requests are size-limited; pairing is single-use, time-limited and attempt-limited (see <see cref="LinkHubService"/>).
/// </summary>
public sealed class LinkHubServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public int Port { get; }
    public LinkHubService Service { get; }

    private LinkHubServer(WebApplication app, int port, LinkHubService service) => (_app, Port, Service) = (app, port, service);

    public static async Task<LinkHubServer> StartAsync(Func<LinkCapture?, LocalDb> db, X509Certificate2 cert, TimeProvider time, IPAddress bind, int port,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var service = new LinkHubService(db, time, LinkProtocol.Sha256Hex(cert.RawData));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = 32 * 1024 * 1024;
            k.Listen(bind, port, o => o.UseHttps(cert));
        });
        var app = builder.Build();
        string? Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString();

        async Task<IResult> Guard(Func<Task<IResult>> run)
        {
            try { return await run(); }
            catch (LinkException e) { return Results.Text(e.Code, statusCode: e.Code == "link.err.unauthorized" ? 401 : 400); }
            catch (JsonException) { return Results.Text("link.err.protocol", statusCode: 400); }
        }

        async Task<LinkNode?> Auth(HttpContext c)
        {
            var h = c.Request.Headers.Authorization.ToString();
            return await service.AuthenticateAsync(h.StartsWith("Bearer ", StringComparison.Ordinal) ? h[7..] : null, Ip(c), c.RequestAborted);
        }

        app.MapPost("/link/pair/hello", (HelloRequest r, HttpContext c) => Guard(async () => Results.Json(await service.HelloAsync(r, c.RequestAborted), LinkRows.Json)));
        app.MapPost("/link/pair/join", (JoinRequest r, HttpContext c) => Guard(async () =>
        {
            var j = await service.JoinAsync(r, Ip(c), c.RequestAborted);
            log?.Invoke($"Paired {j.NodeCode} ({r.Name}) from {Ip(c)}");
            return Results.Json(j, LinkRows.Json);
        }));
        app.MapPost("/link/push", (List<LinkChangeDto> changes, HttpContext c) => Guard(async () =>
            await Auth(c) is { } n ? Results.Json(await service.Hub.ApplyPushAsync(n, changes, c.RequestAborted), LinkRows.Json) : Results.Text("link.err.unauthorized", statusCode: 401)));
        app.MapGet("/link/pull", (long cursor, HttpContext c) => Guard(async () =>
            await Auth(c) is { } n ? Results.Json(await service.Hub.PullAsync(n, cursor, c.RequestAborted), LinkRows.Json) : Results.Text("link.err.unauthorized", statusCode: 401)));
        // (a CancellationToken parameter keeps this from binding as a raw RequestDelegate, whose result would be ignored)
        app.MapGet("/link/users", (HttpContext c, CancellationToken _) => Guard(async () =>
            await Auth(c) is not null ? Results.Text(await service.UsersAsync(c.RequestAborted), "application/json") : Results.Text("link.err.unauthorized", statusCode: 401)));

        await app.StartAsync(ct);
        var actual = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Select(a => new Uri(a.Replace("[::]", "localhost").Replace("0.0.0.0", "localhost")).Port).First();
        return new LinkHubServer(app, actual, service);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>The hub's self-signed TLS certificate (ECDSA P-256), created once and kept encrypted (DPAPI machine scope on Windows).</summary>
public static class LinkCertificate
{
    public static X509Certificate2 CreateSelfSigned(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        // Re-import so the private key is usable by SslStream on every platform (not an ephemeral key).
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    public static X509Certificate2 LoadOrCreate(string path, IDataProtector protector, string name)
    {
        if (File.Exists(path))
        {
            try { return X509CertificateLoader.LoadPkcs12(protector.Unprotect(File.ReadAllBytes(path)), null, X509KeyStorageFlags.Exportable); }
            catch (CryptographicException) { /* unreadable: recreate (paired PCs must pair again) */ }
        }
        var cert = CreateSelfSigned(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, protector.Protect(cert.Export(X509ContentType.Pfx)));
        return cert;
    }
}

/// <summary>Answers LAN discovery broadcasts with the hub's name and HTTPS port.</summary>
public static class LinkDiscoveryResponder
{
    public static async Task RunAsync(Func<string> hubName, int httpsPort, int udpPort, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, udpPort));
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }
            if (Encoding.UTF8.GetString(r.Buffer) != LinkProtocol.DiscoveryRequest) continue;
            var reply = JsonSerializer.SerializeToUtf8Bytes(new DiscoveredHub(hubName(), "", httpsPort), LinkRows.Json);
            try { await udp.SendAsync(reply, r.RemoteEndPoint, ct); } catch (SocketException) { }
        }
    }
}
