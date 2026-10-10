using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Veltrix.Contracts.Sync;
using Veltrix.Desktop.Core.Api;
using Veltrix.Desktop.Core.Data;

namespace Veltrix.Desktop.Core.Sync;

public interface ISyncTransport
{
    Task<SyncPushResponse> PushAsync(SyncPushRequest request, CancellationToken ct);
    Task<SyncPullResponse> PullAsync(DateTimeOffset? since, Guid? warehouseId, CancellationToken ct);
}

public sealed class ApiSyncTransport(ApiClient api) : ISyncTransport
{
    public Task<SyncPushResponse> PushAsync(SyncPushRequest request, CancellationToken ct) => api.PostAsync<SyncPushResponse>("/api/v1/sync/push", request, ct);

    public Task<SyncPullResponse> PullAsync(DateTimeOffset? since, Guid? warehouseId, CancellationToken ct) =>
        api.GetAsync<SyncPullResponse>("/api/v1/sync/pull" + ApiClient.Query(("since", since), ("warehouseId", warehouseId), ("limit", 1000)), ct);
}

public static class Backoff
{
    /// <summary>Exponential retry delay: 5 s, 10 s, 20 s … capped at 15 minutes.</summary>
    public static TimeSpan For(int attempts) => TimeSpan.FromSeconds(Math.Min(900, 5 * Math.Pow(2, Math.Max(0, attempts - 1))));
}

public static class Outbox
{
    /// <summary>Queues an operation. The OpId doubles as the server idempotency key, so replays can never double-book.</summary>
    public static OutboxEntry Enqueue(LocalDb db, string type, object payload, DateTimeOffset now, DateTimeOffset? baseVersion = null, string? opId = null)
    {
        var entry = new OutboxEntry
        {
            OpId = opId ?? NewOpId(), Type = type, PayloadJson = JsonSerializer.Serialize(payload, payload.GetType(), ApiClient.Json),
            ClientTime = now, BaseVersion = baseVersion, NextAttemptAt = now,
        };
        db.Outbox.Add(entry);
        return entry;
    }

    public static string NewOpId() => "w-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// The next batch to send: pending entries in sequence order, stopping at the first entry still waiting for its retry
    /// time so that later operations (e.g. a sale) never overtake earlier ones (e.g. the shift it belongs to).
    /// </summary>
    public static IReadOnlyList<OutboxEntry> NextBatch(IEnumerable<OutboxEntry> pendingOrdered, DateTimeOffset now, int max)
    {
        var batch = new List<OutboxEntry>();
        foreach (var e in pendingOrdered)
        {
            if (e.NextAttemptAt > now || batch.Count >= max) break;
            batch.Add(e);
        }
        return batch;
    }

    /// <summary>Applies one server result to its entry. Returns false when processing must pause (transient failure).</summary>
    public static bool Apply(OutboxEntry entry, SyncOperationResult result, DateTimeOffset now)
    {
        switch (result.Status)
        {
            case "applied" or "duplicate":
                entry.Status = OutboxStatus.Done; break;
            case "conflict":
                entry.Status = OutboxStatus.Conflict; entry.LastError = result.Message ?? "Server kept its version."; break;
            case "rejected":
                entry.Status = OutboxStatus.Rejected; entry.LastError = $"{result.Code}: {result.Message}"; break;
            default:
                entry.Attempts++;
                entry.LastError = result.Message ?? "Temporary failure";
                entry.NextAttemptAt = now + Backoff.For(entry.Attempts);
                return false;
        }
        entry.EntityId = result.EntityId;
        entry.ResultJson = result.Result?.GetRawText();
        entry.CompletedAt = now;
        return true;
    }
}

public sealed record SyncRunResult(int Pushed, int Rejected, int Conflicts, int Pending, int PulledProducts, bool Offline, string? Error);

/// <summary>Push-then-pull synchronisation of the local database with the server.</summary>
public sealed class SyncEngine(Func<LocalDb> dbFactory, ISyncTransport transport, TimeProvider time)
{
    public const int BatchSize = 100;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Raised after each pushed operation result is stored (e.g. to update local sale numbers).</summary>
    public event Action<OutboxEntry>? OperationCompleted;

    public async Task<SyncRunResult> RunOnceAsync(Guid? warehouseId, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return new SyncRunResult(0, 0, 0, await PendingCountAsync(ct), 0, false, null);
        try
        {
            var (pushed, rejected, conflicts) = await PushAsync(ct);
            var pulled = await PullAsync(warehouseId, ct);
            await using var db = dbFactory();
            await db.SetValueAsync(LocalDb.Keys.LastSyncAt, time.GetUtcNow().ToString("O"), ct);
            return new SyncRunResult(pushed, rejected, conflicts, await PendingCountAsync(ct), pulled, false, null);
        }
        catch (ApiOfflineException)
        {
            return new SyncRunResult(0, 0, 0, await PendingCountAsync(ct), 0, true, null);
        }
        catch (ApiException e)
        {
            return new SyncRunResult(0, 0, 0, await PendingCountAsync(ct), 0, false, e.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> PendingCountAsync(CancellationToken ct)
    {
        await using var db = dbFactory();
        return await db.Outbox.CountAsync(e => e.Status == OutboxStatus.Pending, ct);
    }

    public async Task<(int Pushed, int Rejected, int Conflicts)> PushAsync(CancellationToken ct)
    {
        int pushed = 0, rejected = 0, conflicts = 0;
        while (true)
        {
            await using var db = dbFactory();
            var pending = await db.Outbox.Where(e => e.Status == OutboxStatus.Pending).OrderBy(e => e.Seq).Take(BatchSize).ToListAsync(ct);
            var now = time.GetUtcNow();
            var batch = Outbox.NextBatch(pending, now, BatchSize);
            if (batch.Count == 0) break;

            var request = new SyncPushRequest(batch.Select(e => new SyncOperation(e.OpId, e.Type,
                JsonDocument.Parse(e.PayloadJson).RootElement.Clone(), e.ClientTime, e.BaseVersion)).ToList());
            SyncPushResponse response;
            try
            {
                response = await transport.PushAsync(request, ct);
            }
            catch (ApiException e) when (e.Status >= 500)
            {
                foreach (var entry in batch) { entry.Attempts++; entry.LastError = e.Message; entry.NextAttemptAt = now + Backoff.For(entry.Attempts); }
                await db.SaveChangesAsync(ct);
                break;
            }

            var byId = response.Results.ToDictionary(r => r.OpId);
            var paused = false;
            foreach (var entry in batch)
            {
                if (paused || !byId.TryGetValue(entry.OpId, out var result))
                {
                    paused = true;
                    continue;
                }
                if (!Outbox.Apply(entry, result, now)) { paused = true; continue; }
                pushed++;
                if (entry.Status == OutboxStatus.Rejected) rejected++;
                if (entry.Status == OutboxStatus.Conflict) conflicts++;
                ApplySideEffects(db, entry);
            }
            await db.SaveChangesAsync(ct);
            foreach (var entry in batch.Where(e => e.Status != OutboxStatus.Pending)) OperationCompleted?.Invoke(entry);
            if (paused || batch.Count < BatchSize) break;
        }
        return (pushed, rejected, conflicts);
    }

    private static void ApplySideEffects(LocalDb db, OutboxEntry entry)
    {
        if (entry.Type == SyncOperationTypes.Sale)
        {
            var sale = db.Sales.Local.FirstOrDefault(s => s.OpId == entry.OpId) ?? db.Sales.FirstOrDefault(s => s.OpId == entry.OpId);
            if (sale is null) return;
            sale.SyncStatus = entry.Status;
            if (entry.ResultJson is { } json && JsonDocument.Parse(json).RootElement.TryGetProperty("number", out var n)) sale.ServerNumber = n.GetString();
        }
        else if (entry.Type == SyncOperationTypes.CustomerUpsert && entry.EntityId is { } id && Guid.TryParse(id, out var cid))
        {
            var c = db.Customers.Find(cid);
            if (c is not null) c.PendingSync = false;
        }
    }

    public async Task<int> PullAsync(Guid? warehouseId, CancellationToken ct)
    {
        var total = 0;
        for (var page = 0; page < 50; page++)
        {
            await using var db = dbFactory();
            var cursorText = await db.GetValueAsync(LocalDb.Keys.SyncCursor, ct);
            DateTimeOffset? since = cursorText is null ? null : DateTimeOffset.Parse(cursorText, System.Globalization.CultureInfo.InvariantCulture);
            var r = await transport.PullAsync(since, warehouseId, ct);
            await LocalDbMerge.MergeAsync(db, r, ct);
            await db.SetValueAsync(LocalDb.Keys.SyncCursor, r.Cursor.ToString("O"), ct);
            total += r.Products.Count;
            if (!r.HasMore) break;
        }
        return total;
    }
}

public static class LocalDbMerge
{
    public static async Task MergeAsync(LocalDb db, SyncPullResponse r, CancellationToken ct)
    {
        var ids = r.Products.Select(p => p.Id).ToList();
        var existing = await db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        foreach (var p in r.Products)
        {
            if (!existing.TryGetValue(p.Id, out var lp)) db.Products.Add(lp = new LocalProduct { Id = p.Id });
            lp.Sku = p.Sku; lp.Name = p.Name; lp.NameAr = p.NameAr; lp.Barcodes = string.Join('\n', p.Barcodes); lp.Type = p.Type; lp.Status = p.Status;
            lp.CategoryId = p.CategoryId; lp.SalePrice = p.SalePrice; lp.TaxRatePercent = p.TaxRatePercent; lp.TrackStock = p.TrackStock;
            lp.LowStockThreshold = p.LowStockThreshold; lp.UpdatedAt = p.UpdatedAt;
        }

        var cids = r.Customers.Select(c => c.Id).ToList();
        var customers = await db.Customers.Where(c => cids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        foreach (var c in r.Customers)
        {
            if (!customers.TryGetValue(c.Id, out var lc)) db.Customers.Add(lc = new LocalCustomer { Id = c.Id });
            else if (lc.PendingSync) continue; // the local edit is pushed first; the next pull refreshes it
            lc.Code = c.Code; lc.Type = c.Type; lc.Name = c.Name; lc.Phone = c.Phone; lc.Email = c.Email; lc.Address = c.Address; lc.City = c.City;
            lc.Balance = c.Balance; lc.CreditLimit = c.CreditLimit; lc.IsActive = c.IsActive; lc.ServerVersion = c.UpdatedAt;
        }

        foreach (var s in r.StockBalances)
        {
            var ls = await db.Stock.FindAsync([s.WarehouseId, s.ProductId], ct);
            if (ls is null) db.Stock.Add(ls = new LocalStock { WarehouseId = s.WarehouseId, ProductId = s.ProductId });
            ls.WarehouseName = s.WarehouseName; ls.Quantity = s.Quantity; ls.Available = s.Available; ls.LowStockThreshold = s.LowStockThreshold; ls.UpdatedAt = s.UpdatedAt;
        }

        foreach (var reg in r.Registers)
        {
            var lr = await db.Registers.FindAsync([reg.Id], ct);
            if (lr is null) db.Registers.Add(lr = new LocalRegister { Id = reg.Id });
            lr.BranchId = reg.BranchId; lr.WarehouseId = reg.WarehouseId; lr.Code = reg.Code; lr.Name = reg.Name; lr.IsActive = reg.IsActive;
            lr.OpenShiftId = reg.OpenShiftId;
        }

        foreach (var (key, value) in r.Settings)
        {
            var k = LocalDb.Keys.ServerSettingPrefix + key;
            var kv = await db.Values.FindAsync([k], ct);
            if (kv is null) db.Values.Add(new KeyValue { Key = k, Value = value });
            else kv.Value = value;
        }
        await db.SaveChangesAsync(ct);
    }
}
