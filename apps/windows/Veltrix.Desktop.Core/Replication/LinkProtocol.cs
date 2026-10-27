using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;

namespace Veltrix.Desktop.Core.Replication;

public static class LinkProtocol
{
    public const int TcpPort = 47800;
    public const int DiscoveryPort = 47801;
    public const string DiscoveryRequest = "VELTRIX-LINK?1";
    public const string HubFlagKey = "link.hub";
    public const string HubNameKey = "link.hubName";
    public const string PairingKey = "link.pairing";
    public const string UsersKey = "link.users";
    public static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(15);
    public const int MaxPairingAttempts = 5;
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O, 1/I

    /// <summary>One-time pairing code such as K7Q2M-9XDHA (10 symbols of 32 = 50 bits; valid 15 minutes, 5 attempts).</summary>
    public static string NewPairingCode()
    {
        var chars = Enumerable.Range(0, 10).Select(_ => CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)]).ToArray();
        return new string(chars, 0, 5) + "-" + new string(chars, 5, 5);
    }

    public static string NormalizeCode(string code) => new(code.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Proof that the sender knows the pairing code, bound to both nonces and to the hub's TLS certificate: a man in the middle (other
    /// certificate) or someone without the code cannot produce it.
    /// </summary>
    public static string Proof(string code, string role, string nonceA, string nonceB, string certSha256) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(NormalizeCode(code)),
            Encoding.UTF8.GetBytes(string.Join('|', "veltrix-link-v1", role, nonceA, nonceB, certSha256.ToLowerInvariant()))));

    public static bool ProofEquals(string a, string b) =>
        a.Length == b.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}

public sealed record PairingOffer(string CodeHash, DateTimeOffset ExpiresAt, string Role, string? Branch, int Attempts);
public sealed record HelloRequest(string NodeNonce);
public sealed record HelloResponse(string HubNonce, string HubName, string Proof);
public sealed record JoinRequest(string NodeNonce, string HubNonce, string Name, string Proof);
public sealed record JoinResponse(string Token, string NodeCode, string Role, string HubName, Guid? WarehouseId, Guid? BranchId, string? WarehouseName);
public sealed record DiscoveredHub(string Name, string Address, int Port);

public sealed class LinkException(string code, string message) : Exception(message)
{
    /// <summary>link.err.* key: wrongCode, expired, revoked, unauthorized, unreachable, mitm, protocol.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// Hub request handling, independent of the web host: pairing, authentication of paired PCs, push / pull / users. The TLS certificate
/// fingerprint is the one the hub serves (bound into the pairing proofs).
/// </summary>
public sealed class LinkHubService(Func<LinkCapture?, LocalDb> dbFactory, TimeProvider time, string certSha256)
{
    private readonly ConcurrentDictionary<string, (string HubNonce, DateTimeOffset Expires)> _hellos = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    public LinkHub Hub { get; } = new(dbFactory);
    public string CertSha256 => certSha256;

    /// <summary>Admin (hub PC): issues a one-time code. Returns the code to show on screen; only its hash is stored.</summary>
    public static async Task<string> CreatePairingAsync(LocalDb db, string role, string? branch, TimeProvider time, CancellationToken ct = default)
    {
        var code = LinkProtocol.NewPairingCode();
        var offer = new PairingOffer(LinkProtocol.Sha256Hex(LinkProtocol.NormalizeCode(code)), time.GetUtcNow() + LinkProtocol.PairingLifetime, role, branch, 0);
        await db.SetValueAsync(LinkProtocol.PairingKey, JsonSerializer.Serialize(offer, LinkRows.Json), ct);
        return code;
    }

    private async Task<(LocalDb Db, PairingOffer Offer)> OfferAsync(CancellationToken ct)
    {
        var db = dbFactory(null);
        var json = await db.GetValueAsync(LinkProtocol.PairingKey, ct);
        var offer = json is null ? null : JsonSerializer.Deserialize<PairingOffer>(json, LinkRows.Json);
        if (offer is null || offer.ExpiresAt < time.GetUtcNow() || offer.Attempts >= LinkProtocol.MaxPairingAttempts)
        {
            await db.DisposeAsync();
            throw new LinkException("link.err.expired", "No valid pairing code on the hub.");
        }
        return (db, offer);
    }

    /// <summary>Step 1: the hub proves it knows the code (for this certificate) before the node reveals anything.</summary>
    public async Task<HelloResponse> HelloAsync(HelloRequest r, CancellationToken ct = default)
    {
        if (r.NodeNonce is not { Length: >= 16 and <= 64 }) throw new LinkException("link.err.protocol", "Bad nonce.");
        var (db, offer) = await OfferAsync(ct);
        await using (db)
        {
            var hubNonce = LinkProtocol.NewNonce();
            foreach (var old in _hellos.Where(h => h.Value.Expires < time.GetUtcNow()).Select(h => h.Key).ToList()) _hellos.TryRemove(old, out _);
            _hellos[r.NodeNonce] = (hubNonce, time.GetUtcNow() + TimeSpan.FromMinutes(2));
            // The code itself is unknown here (only its hash): the proof is keyed by the normalized code hash on both sides.
            return new HelloResponse(hubNonce, await HubNameAsync(db, ct), LinkProtocol.Proof(offer.CodeHash, "hub", r.NodeNonce, hubNonce, certSha256));
        }
    }

    /// <summary>Step 2: the node proves it knows the code; the hub registers it and returns its bearer token (single-use code).</summary>
    public async Task<JoinResponse> JoinAsync(JoinRequest r, string? address, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var (db, offer) = await OfferAsync(ct);
            await using (db)
            {
                if (!_hellos.TryRemove(r.NodeNonce, out var hello) || hello.HubNonce != r.HubNonce || hello.Expires < time.GetUtcNow()
                    || !LinkProtocol.ProofEquals(LinkProtocol.Proof(offer.CodeHash, "node", r.HubNonce, r.NodeNonce, certSha256), r.Proof))
                {
                    await db.SetValueAsync(LinkProtocol.PairingKey, JsonSerializer.Serialize(offer with { Attempts = offer.Attempts + 1 }, LinkRows.Json), ct);
                    throw new LinkException("link.err.wrongCode", "Wrong pairing code.");
                }
                var token = LinkProtocol.NewToken();
                var n = await db.LinkNodes.CountAsync(ct) + 1;
                string code;
                do code = "B" + n++.ToString(CultureInfo.InvariantCulture);
                while (await db.LinkNodes.AnyAsync(x => x.Code == code, ct));
                var name = string.IsNullOrWhiteSpace(r.Name) ? code : r.Name.Trim()[..Math.Min(r.Name.Trim().Length, 60)];
                db.LinkNodes.Add(new LinkNode
                {
                    Id = Guid.NewGuid(), Code = code, Name = name, Role = offer.Role, Branch = offer.Branch, TokenHash = LinkProtocol.Sha256Hex(token),
                    CreatedAt = time.GetUtcNow(), LastSeenAt = time.GetUtcNow(), LastAddress = address,
                });
                db.Values.Remove((await db.Values.FindAsync([LinkProtocol.PairingKey], ct))!); // single use
                await db.SaveChangesAsync(ct);
                var reg = await db.Registers.AsNoTracking().OrderBy(x => x.Code).FirstOrDefaultAsync(ct);
                var wh = reg is null ? null : await db.Stock.AsNoTracking().Where(s => s.WarehouseId == reg.WarehouseId).Select(s => s.WarehouseName).FirstOrDefaultAsync(ct);
                return new JoinResponse(token, code, offer.Role, await HubNameAsync(db, ct), reg?.WarehouseId, reg?.BranchId, wh);
            }
        }
        finally { _gate.Release(); }
    }

    private static async Task<string> HubNameAsync(LocalDb db, CancellationToken ct) => await db.GetValueAsync(LinkProtocol.HubNameKey, ct) ?? Environment.MachineName;

    /// <summary>Paired, not revoked node for a bearer token (null = refuse).</summary>
    public async Task<LinkNode?> AuthenticateAsync(string? bearer, string? address, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(bearer)) return null;
        var hash = LinkProtocol.Sha256Hex(bearer);
        await using var db = dbFactory(null);
        var node = await db.LinkNodes.FirstOrDefaultAsync(n => n.TokenHash == hash, ct);
        if (node is null || node.RevokedAt is not null) return null;
        (node.LastSeenAt, node.LastAddress) = (time.GetUtcNow(), address);
        await db.SaveChangesAsync(ct);
        db.Entry(node).State = EntityState.Detached;
        return node;
    }

    public async Task<string> UsersAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory(null);
        return await db.GetValueAsync(LinkProtocol.UsersKey, ct) ?? "[]";
    }
}

/// <summary>Node side of the wire protocol (HTTPS, hub certificate pinned after pairing).</summary>
public interface ILinkTransport
{
    Task<HelloResponse> HelloAsync(HelloRequest r, CancellationToken ct);
    Task<JoinResponse> JoinAsync(JoinRequest r, CancellationToken ct);
    Task<IReadOnlyList<LinkPushResult>> PushAsync(IReadOnlyList<LinkChangeDto> changes, CancellationToken ct);
    Task<LinkPullPage> PullAsync(long cursor, CancellationToken ct);
    Task<string> UsersAsync(CancellationToken ct);
    /// <summary>SHA-256 of the certificate the hub presented (for the pairing proofs).</summary>
    string? ObservedCertSha256 { get; }
}

public sealed class HttpLinkTransport : ILinkTransport, IDisposable
{
    private readonly HttpClient _http;
    private string? _observed;
    public string? ObservedCertSha256 => _observed;

    /// <param name="pinnedCertSha256">Null only while pairing (the proofs then authenticate the hub); afterwards any other certificate is refused.</param>
    public HttpLinkTransport(string host, int port, string? pinnedCertSha256, string? token, HttpMessageHandler? inner = null)
    {
        var handler = inner ?? new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            {
                if (cert is null) return false;
                _observed = LinkProtocol.Sha256Hex(cert.RawData);
                return pinnedCertSha256 is null || string.Equals(_observed, pinnedCertSha256, StringComparison.OrdinalIgnoreCase);
            },
        };
        _http = new HttpClient(handler) { BaseAddress = new UriBuilder("https", host, port).Uri, Timeout = TimeSpan.FromSeconds(20) };
        if (token is not null) _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (inner is not null) _observed = pinnedCertSha256;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        HttpResponseMessage res;
        try
        {
            using var req = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body, body.GetType(), options: LinkRows.Json) };
            res = await _http.SendAsync(req, ct);
        }
        catch (HttpRequestException e) when (e.InnerException is System.Security.Authentication.AuthenticationException)
        {
            throw new LinkException("link.err.mitm", "The hub's certificate does not match the paired hub.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new LinkException("link.err.unreachable", e.Message);
        }
        using (res)
        {
            if (res.StatusCode == HttpStatusCode.Unauthorized) throw new LinkException("link.err.unauthorized", "This PC is not (or no longer) paired.");
            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync(ct);
                var code = err.StartsWith("link.err.", StringComparison.Ordinal) ? err.Trim() : "link.err.protocol";
                throw new LinkException(code, $"HTTP {(int)res.StatusCode}: {err}");
            }
            return await res.Content.ReadFromJsonAsync<T>(LinkRows.Json, ct) ?? throw new LinkException("link.err.protocol", "Empty reply.");
        }
    }

    public Task<HelloResponse> HelloAsync(HelloRequest r, CancellationToken ct) => SendAsync<HelloResponse>(HttpMethod.Post, "/link/pair/hello", r, ct);
    public Task<JoinResponse> JoinAsync(JoinRequest r, CancellationToken ct) => SendAsync<JoinResponse>(HttpMethod.Post, "/link/pair/join", r, ct);
    public async Task<IReadOnlyList<LinkPushResult>> PushAsync(IReadOnlyList<LinkChangeDto> changes, CancellationToken ct) =>
        await SendAsync<List<LinkPushResult>>(HttpMethod.Post, "/link/push", changes, ct);
    public Task<LinkPullPage> PullAsync(long cursor, CancellationToken ct) =>
        SendAsync<LinkPullPage>(HttpMethod.Get, "/link/pull?cursor=" + cursor.ToString(CultureInfo.InvariantCulture), null, ct);
    public async Task<string> UsersAsync(CancellationToken ct) => (await SendAsync<JsonElement>(HttpMethod.Get, "/link/users", null, ct)).GetRawText();

    public void Dispose() => _http.Dispose();
}

/// <summary>Finds hubs on the LAN with one UDP broadcast (the hub answers with its name and HTTPS port).</summary>
public static class LinkDiscovery
{
    public static async Task<IReadOnlyList<DiscoveredHub>> FindAsync(TimeSpan wait, CancellationToken ct = default)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        var request = Encoding.UTF8.GetBytes(LinkProtocol.DiscoveryRequest);
        await udp.SendAsync(request, new IPEndPoint(IPAddress.Broadcast, LinkProtocol.DiscoveryPort), ct);
        var found = new Dictionary<string, DiscoveredHub>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(wait);
        try
        {
            while (true)
            {
                var r = await udp.ReceiveAsync(cts.Token);
                try
                {
                    var hub = JsonSerializer.Deserialize<DiscoveredHub>(r.Buffer, LinkRows.Json);
                    if (hub is not null) found[r.RemoteEndPoint.Address.ToString()] = hub with { Address = r.RemoteEndPoint.Address.ToString() };
                }
                catch (JsonException) { /* not a hub */ }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return found.Values.ToList();
    }
}

/// <summary>Stored on a branch PC after pairing (the token is kept in the encrypted store, not here).</summary>
public sealed record LinkNodeSettings(string Host, int Port, string CertSha256, string NodeCode, string HubName, string Role);

/// <summary>Branch PC: pairing, then the continuous push / pull loop (every few seconds; backs off while the hub is unreachable).</summary>
public sealed class LinkClient(Func<LinkCapture?, LocalDb> dbFactory, TimeProvider time)
{
    public const string SettingsKey = "link.node";

    public sealed record Status(bool Connected, DateTimeOffset? LastSyncAt, int Pending, int Rejected, string? Error);

    /// <summary>Runs the two-step pairing with a code typed on this PC. Returns the settings and the token to store.</summary>
    public static async Task<(LinkNodeSettings Settings, string Token, JoinResponse Join)> PairAsync(ILinkTransport transport, string host, int port, string code,
        string pcName, CancellationToken ct = default)
    {
        var nodeNonce = LinkProtocol.NewNonce();
        var hello = await transport.HelloAsync(new HelloRequest(nodeNonce), ct);
        var cert = transport.ObservedCertSha256 ?? throw new LinkException("link.err.protocol", "No TLS certificate observed.");
        var codeHash = LinkProtocol.Sha256Hex(LinkProtocol.NormalizeCode(code));
        if (!LinkProtocol.ProofEquals(LinkProtocol.Proof(codeHash, "hub", nodeNonce, hello.HubNonce, cert), hello.Proof))
            throw new LinkException("link.err.wrongCode", "Wrong code, or the answering PC is not the hub that issued it.");
        var join = await transport.JoinAsync(new JoinRequest(nodeNonce, hello.HubNonce, pcName, LinkProtocol.Proof(codeHash, "node", hello.HubNonce, nodeNonce, cert)), ct);
        return (new LinkNodeSettings(host, port, cert, join.NodeCode, join.HubName, join.Role), join.Token, join);
    }

    /// <summary>Records the pairing in the local database and moves this PC's register into the hub's shop (warehouse / branch).</summary>
    public static async Task ApplyJoinAsync(LocalDb db, LinkNodeSettings settings, JoinResponse join, CancellationToken ct = default)
    {
        await db.SetValueAsync(SettingsKey, JsonSerializer.Serialize(settings, LinkRows.Json), ct);
        await db.SetValueAsync(LocalDb.Keys.LinkNodeCode, settings.NodeCode, ct);
        await db.SetValueAsync(LinkNodeReplica.CursorKey, "0", ct);
        if (await db.GetValueAsync(LocalDb.Keys.StandaloneRegister, ct) is { } rid && Guid.TryParse(rid, out var regId)
            && await db.Registers.FindAsync([regId], ct) is { } reg)
        {
            if (join.WarehouseId is { } wh) reg.WarehouseId = wh;
            if (join.BranchId is { } br) reg.BranchId = br;
            reg.Code = settings.NodeCode + "-POS";
            reg.Name = "POS " + settings.NodeCode;
            await db.SaveChangesAsync(ct);
        }
    }

    public static async Task<LinkNodeSettings?> SettingsAsync(LocalDb db, CancellationToken ct = default) =>
        await db.GetValueAsync(SettingsKey, ct) is { } json ? JsonSerializer.Deserialize<LinkNodeSettings>(json, LinkRows.Json) : null;

    private Status _status = new(false, null, 0, 0, null);
    public Status Current => _status;
    public event Action<Status>? Changed;

    /// <summary>One push + pull round. Returns the number of hub changes applied.</summary>
    public async Task<int> SyncOnceAsync(ILinkTransport transport, string nodeCode, CancellationToken ct = default)
    {
        var replica = new LinkNodeReplica(dbFactory, nodeCode);
        try
        {
            while (true)
            {
                var pending = await replica.PendingAsync(ct: ct);
                if (pending.Count == 0) break;
                await replica.MarkPushedAsync(await transport.PushAsync(pending, ct), ct);
                if (pending.Count < LinkHub.MaxPage) break;
            }
            var applied = 0;
            for (var i = 0; i < 100; i++)
            {
                var page = await transport.PullAsync(await replica.CursorAsync(ct), ct);
                applied += await replica.ApplyPageAsync(page, ct);
                if (!page.HasMore) break;
            }
            await SetStatusAsync(true, null, ct);
            return applied;
        }
        catch (LinkException e)
        {
            await SetStatusAsync(false, e.Code, ct);
            throw;
        }
    }

    private async Task SetStatusAsync(bool ok, string? error, CancellationToken ct)
    {
        await using var db = dbFactory(null);
        var pending = await db.LinkChanges.CountAsync(c => c.Status == LinkStatuses.Pending, ct);
        var rejected = await db.LinkChanges.CountAsync(c => c.Status == LinkStatuses.Rejected, ct);
        _status = new Status(ok, ok ? time.GetUtcNow() : _status.LastSyncAt, pending, rejected, error);
        Changed?.Invoke(_status);
    }

    /// <summary>Background loop: sync every <paramref name="every"/>, users every minute, exponential backoff (max 1 min) while unreachable.</summary>
    public async Task RunAsync(Func<ILinkTransport> transport, string nodeCode, Func<string, Task>? onUsers, TimeSpan every, CancellationToken ct)
    {
        var failures = 0;
        var nextUsers = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var t = transport() as IDisposable;
                var tr = (ILinkTransport)t!;
                await SyncOnceAsync(tr, nodeCode, ct);
                if (onUsers is not null && time.GetUtcNow() >= nextUsers)
                {
                    await onUsers(await tr.UsersAsync(ct));
                    nextUsers = time.GetUtcNow() + TimeSpan.FromMinutes(1);
                }
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception) { failures++; }
            var delay = failures == 0 ? every : TimeSpan.FromSeconds(Math.Min(60, 2 * Math.Pow(2, Math.Min(failures, 6))));
            try { await Task.Delay(delay, time, ct); } catch (OperationCanceledException) { return; }
        }
    }
}
