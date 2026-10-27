using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Security;
using Veltrix.Desktop.Core.Standalone;

namespace Veltrix.Desktop.Core.Replication;

/// <summary>
/// This PC's place in the company network: standalone (no VELTRIX Link), company hub, or branch PC. Decides the change capture used by every
/// unit of work and, on a branch PC, runs the sync loop with the hub.
/// </summary>
public sealed class LinkRuntime(SecureStore secrets, TimeProvider time)
{
    public const string TokenKey = "link.token";

    public LinkCapture? Capture { get; private set; }
    public bool IsHub { get; private set; }
    public LinkNodeSettings? Node { get; private set; }
    public bool IsBranch => Node is not null;
    public LinkClient? Client { get; private set; }
    private CancellationTokenSource? _loop;

    public async Task LoadAsync(Func<LocalDb> db, CancellationToken ct = default)
    {
        await using var ctx = db();
        Node = await LinkClient.SettingsAsync(ctx, ct);
        IsHub = Node is null && await ctx.GetValueAsync(LinkProtocol.HubFlagKey, ct) == "1";
        Capture = Node is { } n ? new LinkCapture(n.NodeCode, isHub: false, time) : IsHub ? new LinkCapture(LinkOrigins.Hub, isHub: true, time) : null;
    }

    /// <summary>Company PC: turns the hub on / off (the connector service starts serving within seconds).</summary>
    public async Task SetHubAsync(Func<LocalDb> db, bool enabled, string? hubName, CancellationToken ct = default)
    {
        if (IsBranch) throw new InvalidOperationException("link.err.isBranch");
        await using var ctx = db();
        await ctx.SetValueAsync(LinkProtocol.HubFlagKey, enabled ? "1" : "0", ct);
        if (!string.IsNullOrWhiteSpace(hubName)) await ctx.SetValueAsync(LinkProtocol.HubNameKey, hubName.Trim(), ct);
        IsHub = enabled;
        Capture = enabled ? new LinkCapture(LinkOrigins.Hub, isHub: true, time) : null;
    }

    /// <summary>Hub: publishes the sign-in accounts (PBKDF2 hashes only) for the branch PCs.</summary>
    public static async Task PublishUsersAsync(Func<LocalDb> db, LocalAuthService auth, CancellationToken ct = default)
    {
        await using var ctx = db();
        var json = auth.ExportUsers();
        if (await ctx.GetValueAsync(LinkProtocol.UsersKey, ct) != json) await ctx.SetValueAsync(LinkProtocol.UsersKey, json, ct);
    }

    /// <summary>Branch PC: joins with a code (pairing, register moved to the hub's shop, token stored encrypted). Restart to switch mode.</summary>
    public async Task<JoinResponse> JoinAsync(Func<LocalDb> db, string host, int port, string code, string pcName, CancellationToken ct = default)
    {
        if (IsHub) throw new InvalidOperationException("link.err.isHub");
        using var t = new HttpLinkTransport(host, port, null, null);
        var (settings, token, join) = await LinkClient.PairAsync(t, host, port, code, pcName, ct);
        secrets.Set(TokenKey, token);
        await using var ctx = db();
        await LinkClient.ApplyJoinAsync(ctx, settings, join, ct);
        return join;
    }

    /// <summary>Branch PC: leaves the company network (local data stays; the hub should also revoke the PC).</summary>
    public async Task LeaveAsync(Func<LocalDb> db, CancellationToken ct = default)
    {
        Stop();
        secrets.Set(TokenKey, null);
        await using var ctx = db();
        foreach (var key in new[] { LinkClient.SettingsKey, LocalDb.Keys.LinkNodeCode, LinkNodeReplica.CursorKey })
            if (await ctx.Values.FindAsync([key], ct) is { } kv) ctx.Values.Remove(kv);
        await ctx.SaveChangesAsync(ct);
        (Node, Capture) = (null, null);
    }

    /// <summary>Branch PC: starts the background sync (every 3 s; accounts refreshed every minute).</summary>
    public void Start(Func<LinkCapture?, LocalDb> db, LocalAuthService auth, Action<string>? log = null)
    {
        if (Node is not { } n || _loop is not null) return;
        var token = secrets.Get(TokenKey);
        if (token is null) { log?.Invoke("VELTRIX Link: no pairing token on this PC; join the company network again."); return; }
        Client = new LinkClient(db, time);
        _loop = new CancellationTokenSource();
        var ct = _loop.Token;
        _ = Task.Run(() => Client.RunAsync(() => new HttpLinkTransport(n.Host, n.Port, n.CertSha256, token), n.NodeCode, json =>
        {
            try { auth.ImportUsers(json, resetLockout: false); }
            catch (Exception e) when (e is InvalidOperationException or System.Text.Json.JsonException) { log?.Invoke("VELTRIX Link: accounts not updated: " + e.Message); }
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(3), ct), CancellationToken.None);
    }

    public void Stop()
    {
        _loop?.Cancel();
        _loop = null;
    }
}
