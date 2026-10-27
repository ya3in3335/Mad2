using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Replication;

namespace Veltrix.Desktop.Tests;

/// <summary>VELTRIX Link replication between a hub database and a branch database (in-process, no network).</summary>
public sealed class LinkReplicationTests : IAsyncLifetime
{
    private readonly SqliteConnection _hubConn = new("Data Source=:memory:");
    private readonly SqliteConnection _nodeConn = new("Data Source=:memory:");
    private static readonly Guid Wh = Guid.NewGuid();
    private static readonly Guid Prod = Guid.NewGuid();
    private LinkNode _node = null!;

    private LocalDb HubDb(LinkCapture? c = null) => new(new DbContextOptionsBuilder<LocalDb>().UseSqlite(_hubConn).Options) { Link = c };
    private LocalDb NodeDb(LinkCapture? c = null) => new(new DbContextOptionsBuilder<LocalDb>().UseSqlite(_nodeConn).Options) { Link = c };
    /// <summary>Normal work on the hub (captured as HUB) and on the branch PC (captured as pending changes of B1).</summary>
    private LocalDb HubWork() => HubDb(new LinkCapture(LinkOrigins.Hub, isHub: true));
    private LocalDb NodeWork() => NodeDb(new LinkCapture("B1", isHub: false));
    private LinkHub Hub => new(HubDb);
    private LinkNodeReplica Replica => new(NodeDb, "B1");

    public async Task InitializeAsync()
    {
        await _hubConn.OpenAsync();
        await _nodeConn.OpenAsync();
        await using (var h = HubDb()) await h.InitializeAsync();
        await using (var n = NodeDb())
        {
            await n.InitializeAsync();
            await n.SetValueAsync(LocalDb.Keys.LinkNodeCode, "B1");
        }
        await using var db = HubDb();
        db.LinkNodes.Add(_node = new LinkNode { Id = Guid.NewGuid(), Code = "B1", Name = "Caisse 2", TokenHash = "x", CreatedAt = DateTimeOffset.UnixEpoch });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _hubConn.DisposeAsync();
        await _nodeConn.DisposeAsync();
    }

    private async Task SyncAsync()
    {
        var pending = await Replica.PendingAsync();
        if (pending.Count > 0) await Replica.MarkPushedAsync(await Hub.ApplyPushAsync(_node, pending));
        while (true)
        {
            var page = await Hub.PullAsync(_node, await Replica.CursorAsync());
            await Replica.ApplyPageAsync(page);
            if (!page.HasMore) break;
        }
    }

    private static async Task<decimal> StockAsync(LocalDb db) => (await db.Stock.SingleAsync(s => s.ProductId == Prod)).Quantity;

    private async Task SeedHubAsync()
    {
        await using var db = HubWork();
        db.Products.Add(new LocalProduct { Id = Prod, Name = "Café", Sku = "CAF", SalePrice = 100m });
        db.Stock.Add(new LocalStock { WarehouseId = Wh, ProductId = Prod, WarehouseName = "Main", Quantity = 10, Available = 10 });
        db.Employees.Add(new LocalEmployee { Id = Guid.NewGuid(), Name = "Secret salary", Salary = 99999 });
        await db.SaveChangesAsync();
    }

    private async Task SellAsync(Func<LinkCapture?, LocalDb> _, LocalDb db, decimal qty)
    {
        var stock = await db.Stock.SingleAsync(s => s.ProductId == Prod);
        stock.Quantity -= qty;
        stock.Available -= qty;
        var sale = Guid.NewGuid();
        db.Sales.Add(new LocalSale { Id = sale, OpId = "op-" + sale.ToString("N"), ClientNumber = "S", CreatedAt = DateTimeOffset.UnixEpoch, Total = qty * 100 });
        db.SaleLines.Add(new LocalSaleLine { Id = Guid.NewGuid(), SaleId = sale, ProductId = Prod, Name = "Café", Quantity = qty, CreatedAt = DateTimeOffset.UnixEpoch });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Join_snapshot_brings_shop_data_but_never_hr()
    {
        await SeedHubAsync();
        await SyncAsync();
        await using var n = NodeDb();
        Assert.Equal("Café", (await n.Products.SingleAsync()).Name);
        Assert.Equal(10, await StockAsync(n));
        Assert.Empty(await n.Employees.ToListAsync());
        Assert.True(await Replica.CursorAsync() > 0);
    }

    [Fact]
    public async Task Concurrent_sales_on_hub_and_branch_add_up_and_replays_do_not_double_count()
    {
        await SeedHubAsync();
        await SyncAsync();
        await using (var n = NodeWork()) await SellAsync(NodeDb, n, 2);
        await using (var h = HubWork()) await SellAsync(HubDb, h, 3);

        var pending = await Replica.PendingAsync();
        var first = await Hub.ApplyPushAsync(_node, pending);
        Assert.All(first, r => Assert.Equal(LinkStatuses.Done, r.Status));
        await Hub.ApplyPushAsync(_node, pending); // network retry: same changes again
        await Replica.MarkPushedAsync(first);
        await SyncAsync();

        await using var hub = HubDb();
        await using var node = NodeDb();
        Assert.Equal(5, await StockAsync(hub));
        Assert.Equal(5, await StockAsync(node));
        Assert.Equal(2, await hub.Sales.CountAsync());
        Assert.Equal(2, await node.Sales.CountAsync());
    }

    [Fact]
    public async Task Hub_edits_reach_the_branch_and_local_unpushed_edits_are_kept()
    {
        await SeedHubAsync();
        await SyncAsync();
        await using (var h = HubWork()) { (await h.Products.SingleAsync()).SalePrice = 120m; await h.SaveChangesAsync(); }
        var customer = Guid.NewGuid();
        await using (var n = NodeWork()) { n.Customers.Add(new LocalCustomer { Id = customer, Name = "Local" }); await n.SaveChangesAsync(); }

        // Pull before push: the local customer (pending) must survive, the hub's price arrives.
        var page = await Hub.PullAsync(_node, await Replica.CursorAsync());
        await Replica.ApplyPageAsync(page);
        await using (var n = NodeDb())
        {
            Assert.Equal(120m, (await n.Products.SingleAsync()).SalePrice);
            Assert.Equal("Local", (await n.Customers.SingleAsync()).Name);
        }
        await SyncAsync();
        await using var hub = HubDb();
        Assert.Equal("Local", (await hub.Customers.SingleAsync(c => c.Id == customer)).Name);
    }

    [Fact]
    public async Task Branch_cannot_write_confidential_tables_or_delete_and_does_not_receive_the_books()
    {
        await SeedHubAsync();
        await using (var h = HubWork())
        {
            h.JournalEntries.Add(new LocalJournalEntry { Id = Guid.NewGuid(), Number = "JE-000001", Description = "Payroll", Source = "Payroll" });
            await h.SaveChangesAsync();
        }
        await SyncAsync();
        await using (var n = NodeDb()) Assert.Empty(await n.JournalEntries.ToListAsync());

        var forged = new[]
        {
            new LinkChangeDto(1, nameof(LocalEmployee), "x", LinkOps.Upsert, "{}", "B1", 9001),
            new LinkChangeDto(2, nameof(LocalProduct), Prod.ToString(), LinkOps.Delete, "{}", "B1", 9002),
        };
        var r = await Hub.ApplyPushAsync(_node, forged);
        Assert.All(r, x => Assert.Equal(LinkStatuses.Rejected, x.Status));
        await using var hub = HubDb();
        Assert.Single(await hub.Products.ToListAsync());
    }

    [Fact]
    public async Task Branch_documents_are_numbered_with_its_code()
    {
        await using var n = NodeWork();
        Assert.Equal("JE-B1-000001", await Veltrix.Desktop.Core.Erp.Ledger.NextNumberAsync(n, "JE", default));
        await using var h = HubWork();
        Assert.Equal("JE-000001", await Veltrix.Desktop.Core.Erp.Ledger.NextNumberAsync(h, "JE", default));
    }
}
