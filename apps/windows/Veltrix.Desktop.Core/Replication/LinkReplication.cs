using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Erp;

namespace Veltrix.Desktop.Core.Replication;

/// <summary>
/// One replicated row change. On the hub (company PC) the log is the ordered feed every branch PC pulls from; on a branch PC it holds the
/// local changes still to push. Rows are identified by their primary key; stock rows travel as quantity deltas so concurrent sales on
/// several PCs add up instead of overwriting each other.
/// </summary>
public sealed class LinkChange
{
    public long Seq { get; set; }
    /// <summary>Entity CLR name (LocalSale, LocalStock…).</summary>
    public string Table { get; set; } = "";
    /// <summary>Primary key values joined with '|'.</summary>
    public string Key { get; set; } = "";
    public string Op { get; set; } = LinkOps.Upsert;
    /// <summary>Row JSON (for a delta: the row with Quantity / Available replaced by their change).</summary>
    public string? Data { get; set; }
    /// <summary>Node code that made the change ("HUB" for the company PC).</summary>
    public string Origin { get; set; } = "";
    /// <summary>Sequence of the change on its origin (idempotent replays).</summary>
    public long OriginSeq { get; set; }
    public DateTimeOffset At { get; set; }
    public string Status { get; set; } = LinkStatuses.Pending;
    public string? Error { get; set; }
}

/// <summary>A branch PC paired with the hub.</summary>
public sealed class LinkNode
{
    public Guid Id { get; set; }
    /// <summary>Short code (B1, B2…), also the prefix of the documents numbered on that PC.</summary>
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = LinkRoles.Employee;
    public string? Branch { get; set; }
    /// <summary>SHA-256 of the node's bearer token (the token itself is never stored on the hub).</summary>
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    /// <summary>Highest origin sequence applied from this node.</summary>
    public long LastOriginSeq { get; set; }
    public string? LastAddress { get; set; }
}

public static class LinkOps
{
    public const string Upsert = "U";
    public const string Delete = "D";
    public const string Delta = "Δ";
}

public static class LinkStatuses
{
    public const string Pending = "Pending";
    public const string Done = "Done";
    public const string Rejected = "Rejected";
}

public static class LinkRoles
{
    /// <summary>Employee / branch PC: sales, stock, customers — never HR, payroll, workforce, cameras or the company's accounting.</summary>
    public const string Employee = "Employee";
}

public static class LinkOrigins
{
    public const string Hub = "HUB";
}

/// <summary>What replicates where. Everything else stays on the PC where it was written (HR, workforce, cameras, settings, queues…).</summary>
public static class LinkScopes
{
    /// <summary>Tables a branch PC receives (nothing confidential: no employees, salaries, attendance, accounting books).</summary>
    public static readonly IReadOnlySet<string> EmployeeRead = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(LocalProduct), nameof(LocalCustomer), nameof(LocalStock), nameof(LocalRegister), nameof(LocalShift), nameof(LocalSale),
        nameof(LocalSaleLine), nameof(LocalSaleReturn), nameof(LocalSaleReturnLine), nameof(LocalCashMovement), nameof(LocalClosedPeriod),
    };

    /// <summary>Tables a branch PC may send (its sales post their own journal entries, numbered with the PC's prefix).</summary>
    public static readonly IReadOnlySet<string> EmployeeWrite = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(LocalProduct), nameof(LocalCustomer), nameof(LocalStock), nameof(LocalRegister), nameof(LocalShift), nameof(LocalSale),
        nameof(LocalSaleLine), nameof(LocalSaleReturn), nameof(LocalSaleReturnLine), nameof(LocalCashMovement), nameof(LocalJournalEntry),
        nameof(LocalJournalLine),
    };

    /// <summary>Tables captured in the log (anything some role reads or writes).</summary>
    public static readonly IReadOnlySet<string> Captured = new HashSet<string>(EmployeeRead.Concat(EmployeeWrite), StringComparer.Ordinal);

    public static bool CanRead(string role, string table) => role == LinkRoles.Employee && EmployeeRead.Contains(table);

    /// <summary>Branch PCs never delete replicated rows (voids and returns are new documents).</summary>
    public static bool CanWrite(string role, string table, string op) => role == LinkRoles.Employee && op != LinkOps.Delete && EmployeeWrite.Contains(table);
}

/// <summary>Records the replicated changes of a unit of work into <see cref="LocalDb.LinkChanges"/>, inside the same transaction.</summary>
public sealed class LinkCapture(string origin, bool isHub, TimeProvider? time = null)
{
    public string Origin { get; } = origin;
    public bool IsHub { get; } = isHub;
    /// <summary>Hub applying a node's change: the node's own sequence (idempotency).</summary>
    public long OriginSeq { get; init; }

    public void Capture(LocalDb db)
    {
        db.ChangeTracker.DetectChanges();
        var now = (time ?? TimeProvider.System).GetUtcNow();
        foreach (var e in db.ChangeTracker.Entries().ToList())
        {
            if (e.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            var table = e.Metadata.ClrType.Name;
            if (!LinkScopes.Captured.Contains(table)) continue;
            var (op, data) = Describe(e);
            db.LinkChanges.Add(new LinkChange
            {
                Table = table, Key = LinkRows.KeyOf(e.Metadata, e.Entity), Op = op, Data = data, Origin = Origin, OriginSeq = OriginSeq, At = now,
                Status = IsHub ? LinkStatuses.Done : LinkStatuses.Pending,
            });
        }
    }

    private static (string Op, string Data) Describe(EntityEntry e)
    {
        var json = LinkRows.Serialize(e.Entity);
        if (e.State == EntityState.Deleted) return (LinkOps.Delete, json);
        if (e.Entity is not LocalStock s) return (LinkOps.Upsert, json);
        decimal Orig(string p) => e.State == EntityState.Added ? 0m : (decimal)e.Property(p).OriginalValue!;
        var node = JsonNode.Parse(json)!.AsObject();
        node[LinkRows.JsonName(nameof(LocalStock.Quantity))] = s.Quantity - Orig(nameof(LocalStock.Quantity));
        node[LinkRows.JsonName(nameof(LocalStock.Available))] = s.Available - Orig(nameof(LocalStock.Available));
        return (LinkOps.Delta, node.ToJsonString());
    }
}

/// <summary>Wire form of a change.</summary>
public sealed record LinkChangeDto(long Seq, string Table, string Key, string Op, string? Data, string Origin, long OriginSeq);

public sealed record LinkPushResult(long OriginSeq, string Status, string? Error = null);

public sealed record LinkPullPage(IReadOnlyList<LinkChangeDto> Changes, long Cursor, bool HasMore, bool IsSnapshot);

/// <summary>Generic row (de)serialization and apply on top of the EF model.</summary>
public static class LinkRows
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string JsonName(string property) => Json.PropertyNamingPolicy!.ConvertName(property);

    public static string Serialize(object entity) => JsonSerializer.Serialize(entity, entity.GetType(), Json);

    public static string KeyOf(IEntityType type, object entity) =>
        string.Join('|', type.FindPrimaryKey()!.Properties.Select(p => Convert.ToString(p.PropertyInfo!.GetValue(entity), CultureInfo.InvariantCulture)));

    public static IEntityType EntityType(LocalDb db, string table) =>
        db.Model.GetEntityTypes().FirstOrDefault(t => t.ClrType.Name == table) ?? throw new InvalidDataException("Unknown table " + table);

    /// <summary>Applies one change to the tracked context (the caller saves). Deltas add to the stored quantities.</summary>
    public static async Task ApplyAsync(LocalDb db, string table, string op, string? data, CancellationToken ct)
    {
        var type = EntityType(db, table);
        var incoming = JsonSerializer.Deserialize(data ?? throw new InvalidDataException("Missing row data."), type.ClrType, Json)
            ?? throw new InvalidDataException("Empty row.");
        var keys = type.FindPrimaryKey()!.Properties.Select(p => p.PropertyInfo!.GetValue(incoming)).ToArray();
        var existing = await db.FindAsync(type.ClrType, keys, ct);
        switch (op)
        {
            case LinkOps.Delete:
                if (existing is not null) db.Remove(existing);
                break;
            case LinkOps.Upsert:
                if (existing is null) db.Add(incoming);
                else db.Entry(existing).CurrentValues.SetValues(incoming);
                break;
            case LinkOps.Delta when incoming is LocalStock d:
                if (existing is LocalStock s)
                {
                    (s.Quantity, s.Available) = (s.Quantity + d.Quantity, s.Available + d.Available);
                    (s.WarehouseName, s.LowStockThreshold, s.UpdatedAt) = (d.WarehouseName, d.LowStockThreshold, d.UpdatedAt > s.UpdatedAt ? d.UpdatedAt : s.UpdatedAt);
                }
                else db.Add(d);
                break;
            default:
                throw new InvalidDataException($"Unsupported operation {op} on {table}.");
        }
    }
}

/// <summary>Hub side: applies what branch PCs push, serves the ordered feed and the first-sync snapshot.</summary>
public sealed class LinkHub(Func<LinkCapture?, LocalDb> dbFactory)
{
    public const int MaxPage = 500;

    /// <summary>Applies a node's changes in order; each one in its own transaction so one bad row never blocks the others. Replays are skipped.</summary>
    public async Task<IReadOnlyList<LinkPushResult>> ApplyPushAsync(LinkNode node, IReadOnlyList<LinkChangeDto> changes, CancellationToken ct = default)
    {
        var results = new List<LinkPushResult>();
        foreach (var c in changes.OrderBy(c => c.OriginSeq))
        {
            if (c.OriginSeq <= node.LastOriginSeq) { results.Add(new(c.OriginSeq, LinkStatuses.Done)); continue; }
            string status = LinkStatuses.Done;
            string? error = null;
            if (!LinkScopes.CanWrite(node.Role, c.Table, c.Op)) (status, error) = (LinkStatuses.Rejected, "Not allowed for this PC: " + c.Op + " " + c.Table);
            else
            {
                try
                {
                    await using var db = dbFactory(new LinkCapture(node.Code, isHub: true) { OriginSeq = c.OriginSeq });
                    await LinkRows.ApplyAsync(db, c.Table, c.Op, c.Data, ct);
                    await db.SaveChangesAsync(ct);
                }
                catch (Exception e) when (e is DbUpdateException or InvalidDataException or JsonException or InvalidOperationException)
                {
                    (status, error) = (LinkStatuses.Rejected, e.GetBaseException().Message);
                }
            }
            node.LastOriginSeq = c.OriginSeq;
            results.Add(new(c.OriginSeq, status, error));
        }
        await using (var db = dbFactory(null))
        {
            var stored = await db.LinkNodes.FirstAsync(n => n.Id == node.Id, ct);
            stored.LastOriginSeq = Math.Max(stored.LastOriginSeq, node.LastOriginSeq);
            await db.SaveChangesAsync(ct);
        }
        return results;
    }

    /// <summary>Changes after <paramref name="cursor"/> the node may read; a node without a cursor first receives a snapshot of its tables.</summary>
    public async Task<LinkPullPage> PullAsync(LinkNode node, long cursor, CancellationToken ct = default)
    {
        await using var db = dbFactory(null);
        if (cursor <= 0) return await SnapshotAsync(db, node, ct);
        var rows = await db.LinkChanges.AsNoTracking().Where(c => c.Seq > cursor).OrderBy(c => c.Seq).Take(MaxPage + 1).ToListAsync(ct);
        var more = rows.Count > MaxPage;
        if (more) rows.RemoveAt(rows.Count - 1);
        var next = rows.Count > 0 ? rows[^1].Seq : cursor;
        var visible = rows.Where(r => LinkScopes.CanRead(node.Role, r.Table)).Select(ToDto).ToList();
        return new LinkPullPage(visible, next, more, false);
    }

    private static async Task<LinkPullPage> SnapshotAsync(LocalDb db, LinkNode node, CancellationToken ct)
    {
        var cursor = await db.LinkChanges.MaxAsync(c => (long?)c.Seq, ct) ?? 0;
        var changes = new List<LinkChangeDto>();
        foreach (var table in LinkScopes.EmployeeRead.Where(t => LinkScopes.CanRead(node.Role, t)))
        {
            var type = LinkRows.EntityType(db, table);
            var rows = (IQueryable<object>)typeof(LinkHub).GetMethod(nameof(Rows), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(type.ClrType).Invoke(null, [db])!;
            foreach (var row in await rows.ToListAsync(ct))
                changes.Add(new LinkChangeDto(0, table, LinkRows.KeyOf(type, row), LinkOps.Upsert, LinkRows.Serialize(row), LinkOrigins.Hub, 0));
        }
        return new LinkPullPage(changes, Math.Max(cursor, 1), false, true);
    }

    private static IQueryable<object> Rows<T>(LocalDb db) where T : class => db.Set<T>().AsNoTracking();

    private static LinkChangeDto ToDto(LinkChange c) => new(c.Seq, c.Table, c.Key, c.Op, c.Data, c.Origin, c.OriginSeq);
}

/// <summary>Branch side: pending local changes to push, and apply of the hub feed.</summary>
public sealed class LinkNodeReplica(Func<LinkCapture?, LocalDb> dbFactory, string selfCode)
{
    public const string CursorKey = "link.cursor";

    public async Task<IReadOnlyList<LinkChangeDto>> PendingAsync(int max = LinkHub.MaxPage, CancellationToken ct = default)
    {
        await using var db = dbFactory(null);
        return await db.LinkChanges.AsNoTracking().Where(c => c.Status == LinkStatuses.Pending).OrderBy(c => c.Seq).Take(max)
            .Select(c => new LinkChangeDto(c.Seq, c.Table, c.Key, c.Op, c.Data, c.Origin, c.Seq)).ToListAsync(ct);
    }

    /// <summary>Marks pushed changes (rejected ones are kept with their reason, never silently dropped).</summary>
    public async Task MarkPushedAsync(IReadOnlyList<LinkPushResult> results, CancellationToken ct = default)
    {
        await using var db = dbFactory(null);
        var seqs = results.Select(r => r.OriginSeq).ToList();
        var rows = await db.LinkChanges.Where(c => seqs.Contains(c.Seq)).ToListAsync(ct);
        foreach (var r in results)
            if (rows.FirstOrDefault(x => x.Seq == r.OriginSeq) is { } row) (row.Status, row.Error) = (r.Status, r.Error);
        await db.SaveChangesAsync(ct);
    }

    public async Task<long> CursorAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory(null);
        return long.TryParse(await db.GetValueAsync(CursorKey, ct), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 0;
    }

    /// <summary>
    /// Applies a page from the hub (not captured again). Own deltas are skipped (already counted here); rows with a local change still to
    /// push keep the local version (the hub will echo it back). The cursor advances in the same transaction.
    /// </summary>
    public async Task<int> ApplyPageAsync(LinkPullPage page, CancellationToken ct = default)
    {
        await using var db = dbFactory(null);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var pending = (await db.LinkChanges.AsNoTracking().Where(c => c.Status == LinkStatuses.Pending).Select(c => new { c.Table, c.Key }).ToListAsync(ct))
            .Select(x => x.Table + "/" + x.Key).ToHashSet(StringComparer.Ordinal);
        if (page.IsSnapshot)
        {
            // Joining (or re-joining) the company: this PC's shop tables are replaced by the hub's copy.
            foreach (var table in LinkScopes.EmployeeRead)
            {
                var type = LinkRows.EntityType(db, table);
#pragma warning disable EF1002 // table names come from the EF model, not from input
                await db.Database.ExecuteSqlRawAsync($"DELETE FROM \"{type.GetTableName()}\";", ct);
#pragma warning restore EF1002
            }
            await db.LinkChanges.Where(c => c.Status == LinkStatuses.Pending).ExecuteDeleteAsync(ct);
            pending.Clear();
        }
        var applied = 0;
        foreach (var c in page.Changes)
        {
            if (c.Op == LinkOps.Delta && c.Origin == selfCode) continue;
            if (c.Op != LinkOps.Delta && pending.Contains(c.Table + "/" + c.Key)) continue;
            await LinkRows.ApplyAsync(db, c.Table, c.Op, c.Data, ct);
            await db.SaveChangesAsync(ct);
            applied++;
        }
        var kv = await db.Values.FindAsync([CursorKey], ct);
        var value = page.Cursor.ToString(CultureInfo.InvariantCulture);
        if (kv is null) db.Values.Add(new KeyValue { Key = CursorKey, Value = value });
        else kv.Value = value;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return applied;
    }
}
