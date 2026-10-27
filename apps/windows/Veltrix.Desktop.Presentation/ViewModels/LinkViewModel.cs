using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Replication;

namespace Veltrix.Desktop.Presentation.ViewModels;

public sealed record LinkNodeRow(LinkNode Node, string LastSeen, string State)
{
    public Guid Id => Node.Id;
    public string Code => Node.Code;
    public string Name => Node.Name;
    public string? Branch => Node.Branch;
    public string? Address => Node.LastAddress;
    public bool IsRevoked => Node.RevokedAt is not null;
}

/// <summary>
/// VELTRIX Link (company LAN): on the company PC the owner turns on the hub, issues one-time codes and revokes PCs; a new PC finds the hub
/// and joins with a code; a branch PC shows its connection status.
/// </summary>
public sealed partial class LinkViewModel(DesktopServices s) : PageViewModel(s)
{
    public override string TitleKey => "link.nav";
    private LinkRuntime Link => S.Link!;

    public ObservableCollection<LinkNodeRow> Nodes { get; } = [];
    public ObservableCollection<DiscoveredHub> Hubs { get; } = [];

    public bool IsBranch => Link.IsBranch;
    public bool IsHub => Link.IsHub;
    public bool IsStandaloneMode => !Link.IsBranch && !Link.IsHub;
    public bool CanManage => S.IsOwner;

    [ObservableProperty] private string _hubName = Environment.MachineName;
    [ObservableProperty] private string _branch = "";
    [ObservableProperty] private string? _pairingCode;
    [ObservableProperty] private string? _pairingExpires;
    [ObservableProperty] private DiscoveredHub? _selectedHub;
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _code = "";
    [ObservableProperty] private string _pcName = Environment.MachineName;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _connected;
    [ObservableProperty] private string? _formError;
    [ObservableProperty] private bool _searching;

    partial void OnSelectedHubChanged(DiscoveredHub? value)
    {
        if (value is not null) Host = value.Address;
    }

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        await using var db = S.Db();
        HubName = await db.GetValueAsync(LinkProtocol.HubNameKey, ct) ?? Environment.MachineName;
        if (IsHub)
        {
            var nodes = await db.LinkNodes.AsNoTracking().OrderBy(n => n.Code).ToListAsync(ct);
            Nodes.Clear();
            foreach (var n in nodes)
                Nodes.Add(new LinkNodeRow(n, n.LastSeenAt is { } at ? at.ToLocalTime().ToString("g", Loc.Culture) : "—",
                    n.RevokedAt is not null ? Loc.Get("link.revoked") : n.LastSeenAt is { } seen && S.Time.GetUtcNow() - seen < TimeSpan.FromMinutes(2) ? Loc.Get("link.online") : Loc.Get("link.offline")));
        }
        RefreshStatus();
        return true;
    }

    private void RefreshStatus()
    {
        if (Link.Node is not { } n) { StatusText = ""; return; }
        var st = Link.Client?.Current;
        Connected = st?.Connected == true;
        StatusText = Loc.Format("link.branchStatus", n.HubName, n.Host, n.NodeCode,
            st?.LastSyncAt is { } at ? at.ToLocalTime().ToString("T", Loc.Culture) : "—", st?.Pending ?? 0, st?.Rejected ?? 0)
            + (st?.Error is { } e ? Environment.NewLine + Loc.Get(e) : "");
    }

    [RelayCommand]
    public void Poll() => RefreshStatus();

    private Task<bool> Run(Func<Task> action, string? success = null) => TryActionAsync(async () =>
    {
        FormError = null;
        try { await action(); }
        catch (LinkException e) { FormError = Loc.Get(e.Code); throw; }
        catch (InvalidOperationException e) { FormError = Describe(e); throw; }
    }, success);

    [RelayCommand]
    public Task EnableHubAsync() => Run(async () =>
    {
        if (!CanManage) throw new InvalidOperationException("sec.err.ownerOnly");
        await Link.SetHubAsync(S.Db, true, HubName);
        await LinkRuntime.PublishUsersAsync(S.Db, S.Standalone.Auth);
        Changed();
        await ReloadAsync();
    }, "link.hubOnDone");

    [RelayCommand]
    public Task DisableHubAsync() => Run(async () =>
    {
        if (!CanManage) throw new InvalidOperationException("sec.err.ownerOnly");
        if (!S.Ui.Confirm(Loc.Get("link.hubOffConfirm"))) return;
        await Link.SetHubAsync(S.Db, false, null);
        Changed();
    });

    [RelayCommand]
    public Task NewCodeAsync() => Run(async () =>
    {
        if (!CanManage || !IsHub) throw new InvalidOperationException("sec.err.ownerOnly");
        await using var db = S.Db();
        PairingCode = await LinkHubService.CreatePairingAsync(db, LinkRoles.Employee, string.IsNullOrWhiteSpace(Branch) ? null : Branch.Trim(), S.Time);
        PairingExpires = Loc.Format("link.codeValid", (S.Time.GetUtcNow() + LinkProtocol.PairingLifetime).ToLocalTime().ToString("t", Loc.Culture));
    });

    [RelayCommand]
    public Task RevokeAsync(LinkNodeRow? row) => Run(async () =>
    {
        if (row is null || !CanManage) return;
        if (!S.Ui.Confirm(Loc.Format("link.revokeConfirm", row.Name))) return;
        await using (var db = S.Db())
        {
            var n = await db.LinkNodes.FirstAsync(x => x.Id == row.Id);
            n.RevokedAt = S.Time.GetUtcNow();
            await db.SaveChangesAsync();
        }
        await ReloadAsync();
    });

    [RelayCommand]
    public async Task DiscoverAsync()
    {
        Searching = true;
        try
        {
            Hubs.Clear();
            foreach (var h in await LinkDiscovery.FindAsync(TimeSpan.FromSeconds(2))) Hubs.Add(h);
            SelectedHub = Hubs.FirstOrDefault();
            StatusMessage = Hubs.Count == 0 ? Loc.Get("link.noHub") : null;
        }
        catch (System.Net.Sockets.SocketException e) { StatusMessage = e.Message; }
        finally { Searching = false; }
    }

    [RelayCommand]
    public Task JoinAsync() => Run(async () =>
    {
        if (!CanManage) throw new InvalidOperationException("sec.err.ownerOnly");
        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Code)) throw new InvalidOperationException("link.err.hostCode");
        var port = SelectedHub is { } h && h.Address == Host.Trim() ? h.Port : LinkProtocol.TcpPort;
        if (!S.Ui.Confirm(Loc.Get("link.joinConfirm"))) return;
        await Link.JoinAsync(S.Db, Host.Trim(), port, Code, PcName);
        Code = "";
    }, "link.joined");

    [RelayCommand]
    public Task LeaveAsync() => Run(async () =>
    {
        if (!CanManage) throw new InvalidOperationException("sec.err.ownerOnly");
        if (!S.Ui.Confirm(Loc.Get("link.leaveConfirm"))) return;
        await Link.LeaveAsync(S.Db);
        Changed();
    }, "link.left");

    private void Changed()
    {
        OnPropertyChanged(nameof(IsHub));
        OnPropertyChanged(nameof(IsBranch));
        OnPropertyChanged(nameof(IsStandaloneMode));
    }
}
