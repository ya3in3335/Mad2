using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Veltrix.Contracts.Pos;
using Veltrix.Contracts.Sync;
using Veltrix.Desktop.Core.Api;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Sync;

namespace Veltrix.Desktop.Tests;

public sealed class OutboxTests : IAsyncLifetime
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly FakeTransport _transport = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero));

    private LocalDb Db() => new(new DbContextOptionsBuilder<LocalDb>().UseSqlite(_conn).Options);

    public async Task InitializeAsync()
    {
        await _conn.OpenAsync();
        await using var db = Db();
        await db.InitializeAsync();
    }

    public Task DisposeAsync() => _conn.DisposeAsync().AsTask();

    private async Task<List<string>> EnqueueShiftAndSalesAsync(int sales)
    {
        await using var db = Db();
        var shiftId = Guid.NewGuid();
        var ids = new List<string> { Outbox.Enqueue(db, SyncOperationTypes.ShiftOpen, new OpenShiftRequest(Guid.NewGuid(), 100, shiftId), _time.GetUtcNow()).OpId };
        for (var i = 0; i < sales; i++)
        {
            var opId = Outbox.NewOpId();
            Outbox.Enqueue(db, SyncOperationTypes.Sale, new CompleteSaleRequest(opId, Guid.NewGuid(), shiftId, null, [], [], Offline: true), _time.GetUtcNow(), opId: opId);
            ids.Add(opId);
        }
        await db.SaveChangesAsync();
        return ids;
    }

    [Fact]
    public async Task Pushes_in_sequence_order_with_op_id_as_idempotency_key()
    {
        var ids = await EnqueueShiftAndSalesAsync(3);
        var engine = new SyncEngine(Db, _transport, _time);
        var (pushed, _, _) = await engine.PushAsync(default);

        Assert.Equal(4, pushed);
        Assert.Equal(ids, _transport.Sent.Select(o => o.OpId));
        var sale = _transport.Sent[1];
        Assert.Equal(sale.OpId, sale.Payload.GetProperty("idempotencyKey").GetString());
        Assert.True(sale.Payload.GetProperty("offline").GetBoolean());
        Assert.Equal(0, await engine.PendingCountAsync(default));
    }

    [Fact]
    public async Task Transient_failure_pauses_later_operations_and_backs_off()
    {
        var ids = await EnqueueShiftAndSalesAsync(2);
        _transport.Statuses[ids[1]] = "retry";
        var engine = new SyncEngine(Db, _transport, _time);
        await engine.PushAsync(default);

        await using (var db = Db())
        {
            var all = await db.Outbox.OrderBy(e => e.Seq).ToListAsync();
            Assert.Equal(OutboxStatus.Done, all[0].Status);
            Assert.Equal(OutboxStatus.Pending, all[1].Status);
            Assert.Equal(1, all[1].Attempts);
            Assert.Equal(_time.GetUtcNow() + Backoff.For(1), all[1].NextAttemptAt);
            Assert.Equal(OutboxStatus.Pending, all[2].Status); // not applied ahead of the failed one
        }

        // Before the retry time nothing is sent; afterwards the remaining entries go in order.
        _transport.Sent.Clear();
        _transport.Statuses.Clear();
        await engine.PushAsync(default);
        Assert.Empty(_transport.Sent);
        _time.Advance(TimeSpan.FromSeconds(6));
        await engine.PushAsync(default);
        Assert.Equal(ids.Skip(1), _transport.Sent.Select(o => o.OpId));
    }

    [Fact]
    public async Task Rejected_operations_are_kept_for_review_and_do_not_block()
    {
        var ids = await EnqueueShiftAndSalesAsync(2);
        _transport.Statuses[ids[1]] = "rejected";
        var engine = new SyncEngine(Db, _transport, _time);
        var (pushed, rejected, _) = await engine.PushAsync(default);
        Assert.Equal(3, pushed);
        Assert.Equal(1, rejected);
        await using var db = Db();
        Assert.Equal(OutboxStatus.Rejected, (await db.Outbox.SingleAsync(e => e.OpId == ids[1])).Status);
        Assert.Equal(OutboxStatus.Done, (await db.Outbox.SingleAsync(e => e.OpId == ids[2])).Status);
    }

    [Fact]
    public void Backoff_grows_and_is_capped()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), Backoff.For(1));
        Assert.Equal(TimeSpan.FromSeconds(10), Backoff.For(2));
        Assert.Equal(TimeSpan.FromMinutes(15), Backoff.For(30));
    }

    [Fact]
    public async Task Pull_stores_cursor_and_merges_catalog()
    {
        var engine = new SyncEngine(Db, _transport, _time);
        var pulled = await engine.PullAsync(null, default);
        Assert.Equal(1, pulled);
        await using var db = Db();
        Assert.Equal("Water 1.5L", (await db.Products.SingleAsync()).Name);
        Assert.NotNull(await db.GetValueAsync(LocalDb.Keys.SyncCursor));
        Assert.Equal("true", await db.GetValueAsync(LocalDb.Keys.ServerSettingPrefix + "pos.pricesIncludeTax"));
    }

    private sealed class FakeTransport : ISyncTransport
    {
        public List<SyncOperation> Sent { get; } = [];
        public Dictionary<string, string> Statuses { get; } = [];

        public Task<SyncPushResponse> PushAsync(SyncPushRequest request, CancellationToken ct)
        {
            Sent.AddRange(request.Operations);
            var results = request.Operations.Select(o => new SyncOperationResult(o.OpId, Statuses.GetValueOrDefault(o.OpId, "applied"), null, null,
                Guid.NewGuid().ToString(), null)).ToList();
            return Task.FromResult(new SyncPushResponse(results, DateTimeOffset.UtcNow));
        }

        public Task<SyncPullResponse> PullAsync(DateTimeOffset? since, Guid? warehouseId, CancellationToken ct)
        {
            var product = new Veltrix.Contracts.Catalog.ProductDto(Guid.NewGuid(), "W15", "Water 1.5L", null, null, "Standard", "Active", null, null, null, null,
                19, 45, null, 30, 30, true, false, false, false, 10, 50, null, null, false, ["6130000000011"], [], DateTimeOffset.UtcNow);
            return Task.FromResult(new SyncPullResponse(DateTimeOffset.UtcNow, false, [product], [], [], [], [], [], [],
                new Dictionary<string, string> { ["pos.pricesIncludeTax"] = "true" }));
        }
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
