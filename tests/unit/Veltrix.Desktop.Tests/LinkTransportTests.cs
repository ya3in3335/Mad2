using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Replication;
using Veltrix.LinkServer;

namespace Veltrix.Desktop.Tests;

/// <summary>VELTRIX Link over real HTTPS on the loopback interface: pairing, pinned certificate, push / pull, revocation.</summary>
public sealed class LinkTransportTests : IAsyncLifetime
{
    private readonly SqliteConnection _hubConn = new("Data Source=:memory:");
    private readonly SqliteConnection _nodeConn = new("Data Source=:memory:");
    private LinkHubServer _server = null!;
    private LocalDb HubDb(LinkCapture? c = null) => new(new DbContextOptionsBuilder<LocalDb>().UseSqlite(_hubConn).Options) { Link = c };
    private LocalDb NodeDb(LinkCapture? c = null) => new(new DbContextOptionsBuilder<LocalDb>().UseSqlite(_nodeConn).Options) { Link = c };

    public async Task InitializeAsync()
    {
        await _hubConn.OpenAsync();
        await _nodeConn.OpenAsync();
        await using (var h = HubDb())
        {
            await h.InitializeAsync();
            await h.SetValueAsync(LinkProtocol.HubNameKey, "Siège");
            await h.SetValueAsync(LinkProtocol.UsersKey, "[{\"name\":\"amine\"}]");
        }
        await using (var hw = HubDb(new LinkCapture(LinkOrigins.Hub, true)))
        {
            hw.Products.Add(new LocalProduct { Id = Guid.NewGuid(), Name = "Thé", Sku = "THE", SalePrice = 80m });
            await hw.SaveChangesAsync();
        }
        await using (var n = NodeDb()) await n.InitializeAsync();
        _server = await LinkHubServer.StartAsync(HubDb, LinkCertificate.CreateSelfSigned("VELTRIX Hub test"), TimeProvider.System, IPAddress.Loopback, 0);
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _hubConn.DisposeAsync();
        await _nodeConn.DisposeAsync();
    }

    private async Task<string> CodeAsync()
    {
        await using var h = HubDb();
        return await LinkHubService.CreatePairingAsync(h, LinkRoles.Employee, "Oran", TimeProvider.System);
    }

    private async Task<(LinkNodeSettings Settings, string Token)> PairAsync()
    {
        var code = await CodeAsync();
        using var t = new HttpLinkTransport("127.0.0.1", _server.Port, null, null);
        var (settings, token, join) = await LinkClient.PairAsync(t, "127.0.0.1", _server.Port, code.ToLowerInvariant(), "Caisse 2");
        await using var n = NodeDb();
        await LinkClient.ApplyJoinAsync(n, settings, join);
        return (settings, token);
    }

    [Fact]
    public async Task Pair_then_sync_both_ways_over_https()
    {
        var (settings, token) = await PairAsync();
        Assert.Equal(("B1", "Siège", _server.Service.CertSha256), (settings.NodeCode, settings.HubName, settings.CertSha256));

        var client = new LinkClient(NodeDb, TimeProvider.System);
        using (var t = new HttpLinkTransport("127.0.0.1", _server.Port, settings.CertSha256, token))
        {
            await client.SyncOnceAsync(t, "B1");
            await using (var n = NodeDb()) Assert.Equal("Thé", (await n.Products.SingleAsync()).Name);
            var customer = Guid.NewGuid();
            await using (var nw = NodeDb(new LinkCapture("B1", false)))
            {
                nw.Customers.Add(new LocalCustomer { Id = customer, Name = "Client branche" });
                await nw.SaveChangesAsync();
            }
            await client.SyncOnceAsync(t, "B1");
            Assert.True(client.Current.Connected);
            Assert.Equal(0, client.Current.Pending);
            await using var h = HubDb();
            Assert.Equal("Client branche", (await h.Customers.SingleAsync(c => c.Id == customer)).Name);
            var users = await t.UsersAsync(default);
            Assert.Contains("amine", users);
        }

        // Revoked PC: refused.
        await using (var h = HubDb()) { (await h.LinkNodes.SingleAsync()).RevokedAt = DateTimeOffset.UtcNow; await h.SaveChangesAsync(); }
        using var revoked = new HttpLinkTransport("127.0.0.1", _server.Port, settings.CertSha256, token);
        Assert.Equal("link.err.unauthorized", (await Assert.ThrowsAsync<LinkException>(() => client.SyncOnceAsync(revoked, "B1"))).Code);
    }

    [Fact]
    public async Task Wrong_code_is_refused_and_the_code_is_single_use()
    {
        var code = await CodeAsync();
        using (var t = new HttpLinkTransport("127.0.0.1", _server.Port, null, null))
            Assert.Equal("link.err.wrongCode", (await Assert.ThrowsAsync<LinkException>(() => LinkClient.PairAsync(t, "127.0.0.1", _server.Port, "AAAAA-AAAAA", "x"))).Code);
        using (var t = new HttpLinkTransport("127.0.0.1", _server.Port, null, null))
            await LinkClient.PairAsync(t, "127.0.0.1", _server.Port, code, "first");
        using (var t = new HttpLinkTransport("127.0.0.1", _server.Port, null, null))
            Assert.Equal("link.err.expired", (await Assert.ThrowsAsync<LinkException>(() => LinkClient.PairAsync(t, "127.0.0.1", _server.Port, code, "second"))).Code);
    }

    [Fact]
    public async Task Another_certificate_than_the_paired_one_is_refused()
    {
        var (_, token) = await PairAsync();
        using var t = new HttpLinkTransport("127.0.0.1", _server.Port, new string('0', 64), token);
        Assert.Equal("link.err.mitm", (await Assert.ThrowsAsync<LinkException>(() => t.PullAsync(1, default))).Code);
    }
}
