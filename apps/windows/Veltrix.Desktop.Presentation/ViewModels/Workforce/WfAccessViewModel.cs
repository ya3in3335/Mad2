using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Workforce;

namespace Veltrix.Desktop.Presentation.ViewModels;

public sealed record AccessLogRow(string At, string Door, string Employee, string Credential, bool Granted, string Result, string Reason, string? Actor);

/// <summary>A checkable item (doors / members of an access group).</summary>
public sealed partial class CheckItem(Guid id, string text, bool isChecked = false) : ObservableObject
{
    public Guid Id { get; } = id;
    public string Text { get; } = text;
    [ObservableProperty] private bool _isChecked = isChecked;
}

/// <summary>Access control: zones (anti-passback), doors, access groups (doors, weekly schedule, members), logs and the emergency unlock.</summary>
public sealed partial class WfAccessViewModel : WorkforcePageViewModel
{
    public WfAccessViewModel(DesktopServices s) : base(s)
    {
        _logFrom = FromDate(Today);
        _logTo = FromDate(Today);
    }

    public override string TitleKey => "wf.nav.access";
    public static IReadOnlyList<string> AntiPassbackModesList { get; } = [AntiPassbackModes.None, AntiPassbackModes.Soft, AntiPassbackModes.Hard];
    public static IReadOnlyList<string> Directions { get; } = ["In", "Out"];

    public ObservableCollection<WfZone> Zones { get; } = [];
    public ObservableCollection<WfDoor> Doors { get; } = [];
    public ObservableCollection<WfAccessGroup> Groups { get; } = [];
    public ObservableCollection<AccessLogRow> Logs { get; } = [];
    public ObservableCollection<Choice<Guid?>> Controllers { get; } = [];
    public ObservableCollection<CheckItem> GroupDoors { get; } = [];
    public ObservableCollection<CheckItem> GroupMembers { get; } = [];

    /// <summary>0 zones &amp; doors, 1 groups, 2 logs.</summary>
    [ObservableProperty] private int _tabIndex;

    [ObservableProperty] private WfZone? _selectedZone;
    [ObservableProperty] private string _zoneName = "";
    [ObservableProperty] private string _zoneAntiPassback = AntiPassbackModes.None;

    [ObservableProperty] private WfDoor? _selectedDoor;
    [ObservableProperty] private string _doorName = "";
    [ObservableProperty] private WfZone? _doorZone;
    [ObservableProperty] private string _doorDirection = "In";
    [ObservableProperty] private Guid? _doorController;
    [ObservableProperty] private string _doorUnlockSeconds = "5";

    [ObservableProperty] private WfAccessGroup? _selectedGroup;
    [ObservableProperty] private string _groupName = "";
    [ObservableProperty] private string _groupSchedule = "";
    [ObservableProperty] private bool _groupAllowHolidays;

    [ObservableProperty] private DateTime? _logFrom;
    [ObservableProperty] private DateTime? _logTo;
    [ObservableProperty] private bool _deniedOnly;
    [ObservableProperty] private string? _emergencyResult;

    public bool CanView => HasGlobal(WorkforcePermissions.AccessView);
    public bool CanManage => HasGlobal(WorkforcePermissions.AccessManage);
    public bool CanEmergency => Has(WorkforcePermissions.EmergencyUnlock);

    private IReadOnlyList<EmployeeRef> _people = [];

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        if (!CanView) return CanEmergency; // scoped managers: emergency unlock only
        Fill(Zones, await Wf.ZonesAsync(Actor, ct));
        Fill(Doors, await Wf.DoorsAsync(Actor, ct));
        Fill(Groups, await Wf.AccessGroupsAsync(Actor, ct));
        Fill(Controllers, [new Choice<Guid?>(null, "—"), .. (await Wf.DevicesAsync(Actor, ct: ct)).Select(d => new Choice<Guid?>(d.Id, d.Name))]);
        _people = await Wf.DirectoryAsync(Actor, ct);
        await LoadLogsAsync(ct);
        if (SelectedGroup is null) FillGroupChecks(null, []);
        return true;
    }

    private async Task LoadLogsAsync(CancellationToken ct)
    {
        var from = LocalStartUtc(ToDate(LogFrom, Today));
        var to = LocalStartUtc(ToDate(LogTo, Today).AddDays(1));
        var logs = await Wf.AccessLogsAsync(Actor, from, to, DeniedOnly, ct);
        var doors = Doors.ToDictionary(d => d.Id, d => d.Name);
        var people = _people.ToDictionary(p => p.Id);
        Fill(Logs, logs.OrderByDescending(l => l.Seq).Take(1000).Select(l => new AccessLogRow(DateTimeText(l.At), l.DoorId is { } d ? doors.GetValueOrDefault(d) ?? "" : "",
            l.EmployeeId is { } e && people.GetValueOrDefault(e) is { } p ? $"{p.Code} — {p.Name}" : "", $"{l.CredentialKind} {l.CredentialHint}".Trim(), l.Granted,
            Loc.Get(l.Granted ? "wf.access.granted" : "wf.access.denied"), T("reason", l.Reason), l.Actor)));
    }

    [RelayCommand] public Task SearchLogsAsync() => TryActionAsync(() => LoadLogsAsync(CancellationToken.None));
    partial void OnDeniedOnlyChanged(bool value) { if (IsReady) _ = SearchLogsAsync(); }

    // ---- zones

    partial void OnSelectedZoneChanged(WfZone? value)
    {
        if (value is null) return;
        ZoneName = value.Name;
        ZoneAntiPassback = value.AntiPassback;
    }

    [RelayCommand]
    public void NewZone()
    {
        SelectedZone = null;
        ZoneName = "";
        ZoneAntiPassback = AntiPassbackModes.None;
    }

    [RelayCommand]
    public Task SaveZoneAsync() => SubmitAsync(async () =>
    {
        var z = await Wf.UpsertZoneAsync(Actor, SelectedZone?.Id, ZoneName, ZoneAntiPassback);
        await ReloadAsync();
        SelectedZone = Zones.FirstOrDefault(x => x.Id == z.Id);
    });

    // ---- doors

    partial void OnSelectedDoorChanged(WfDoor? value)
    {
        if (value is null) return;
        DoorName = value.Name;
        DoorZone = Zones.FirstOrDefault(z => z.Id == value.ZoneId);
        DoorDirection = value.Direction;
        DoorController = value.ControllerDeviceId;
        DoorUnlockSeconds = Text(value.UnlockSeconds);
    }

    [RelayCommand]
    public void NewDoor()
    {
        SelectedDoor = null;
        (DoorName, DoorZone, DoorDirection, DoorController, DoorUnlockSeconds) = ("", Zones.FirstOrDefault(), "In", null, "5");
    }

    [RelayCommand]
    public Task SaveDoorAsync() => SubmitAsync(async () =>
    {
        var zone = DoorZone ?? throw new InvalidOperationException("wf.err.unknownZone");
        var d = await Wf.UpsertDoorAsync(Actor, SelectedDoor?.Id, DoorName, zone.Id, DoorDirection, DoorController, ParseInt(DoorUnlockSeconds, "wf.err.door", 1, 300));
        await ReloadAsync();
        SelectedDoor = Doors.FirstOrDefault(x => x.Id == d.Id);
    });

    // ---- groups

    partial void OnSelectedGroupChanged(WfAccessGroup? value)
    {
        if (value is null) return;
        GroupName = value.Name;
        GroupAllowHolidays = value.AllowOnHolidays;
        GroupSchedule = FormatSchedule(AccessEngine.ParseSchedule(value.ScheduleJson));
        _ = LoadMembersAsync(value);
    }

    private async Task LoadMembersAsync(WfAccessGroup g)
    {
        try
        {
            var members = await Wf.AccessGroupMembersAsync(Actor, g.Id);
            if (SelectedGroup?.Id == g.Id) FillGroupChecks(AccessEngine.ParseDoors(g.DoorIdsJson), members);
        }
        catch (Exception e) { S.Log.Warn("Access group members failed", e); }
    }

    private void FillGroupChecks(IReadOnlyList<Guid>? doors, IReadOnlyList<Guid> members)
    {
        Fill(GroupDoors, Doors.Select(d => new CheckItem(d.Id, d.Name, doors?.Contains(d.Id) == true)));
        Fill(GroupMembers, _people.Where(p => p.Status != EmployeeStatuses.Terminated).Select(p => new CheckItem(p.Id, $"{p.Code} — {p.Name}", members.Contains(p.Id))));
    }

    [RelayCommand]
    public void NewGroup()
    {
        SelectedGroup = null;
        GroupName = "";
        GroupSchedule = "1-5 08:00-18:00";
        GroupAllowHolidays = false;
        FillGroupChecks(null, []);
    }

    [RelayCommand]
    public Task SaveGroupAsync() => SubmitAsync(async () =>
    {
        var g = await Wf.UpsertAccessGroupAsync(Actor, SelectedGroup?.Id, GroupName, GroupDoors.Where(d => d.IsChecked).Select(d => d.Id).ToList(),
            ParseSchedule(GroupSchedule), GroupAllowHolidays, GroupMembers.Where(m => m.IsChecked).Select(m => m.Id).ToList());
        await ReloadAsync();
        SelectedGroup = Groups.FirstOrDefault(x => x.Id == g.Id);
    });

    /// <summary>One window per line: "day[-day] HH:mm-HH:mm", days 0 = Sunday … 6 = Saturday (e.g. "1-5 08:00-18:00"). Empty = always.</summary>
    public static IReadOnlyList<AccessWindow> ParseSchedule(string text)
    {
        var list = new List<AccessWindow>();
        foreach (var line in text.Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) throw new InvalidOperationException("wf.err.schedule");
            var days = parts[0].Split('-');
            var times = parts[1].Split('-');
            if (times.Length != 2 || !int.TryParse(days[0], out var d1) || !int.TryParse(days[^1], out var d2) || d1 is < 0 or > 6 || d2 is < 0 or > 6 || d2 < d1
                || !TimeOnly.TryParse(times[0], CultureInfo.InvariantCulture, out var s) || !TimeOnly.TryParse(times[1], CultureInfo.InvariantCulture, out var e))
                throw new InvalidOperationException("wf.err.schedule");
            for (var d = d1; d <= d2; d++) list.Add(new AccessWindow(d, s.ToString("HH:mm", CultureInfo.InvariantCulture), e.ToString("HH:mm", CultureInfo.InvariantCulture)));
        }
        return list;
    }

    public static string FormatSchedule(IReadOnlyList<AccessWindow> windows)
    {
        var sb = new StringBuilder();
        foreach (var g in windows.GroupBy(w => (w.Start, w.End)))
        {
            var days = g.Select(w => w.Day).Distinct().Order().ToList();
            var i = 0;
            while (i < days.Count)
            {
                var j = i;
                while (j + 1 < days.Count && days[j + 1] == days[j] + 1) j++;
                sb.Append(days[i] == days[j] ? $"{days[i]}" : $"{days[i]}-{days[j]}").Append(' ').Append(g.Key.Start).Append('-').Append(g.Key.End).Append('\n');
                i = j + 1;
            }
        }
        return sb.ToString().TrimEnd();
    }

    // ---- emergency

    /// <summary>EMERGENCY: unlocks every door on every registered controller after an explicit confirmation.</summary>
    [RelayCommand]
    public Task EmergencyUnlockAllAsync() => SubmitAsync(async () =>
    {
        if (!S.Ui.Confirm(Loc.Get("wf.access.emergencyConfirm"))) return;
        EmergencyResult = null;
        try
        {
            var n = await Wf.EmergencyUnlockAllAsync(Actor);
            EmergencyResult = Loc.Format("wf.access.emergencyDone", n);
        }
        catch (AggregateException e)
        {
            EmergencyResult = Loc.Get("wf.access.emergencyPartial");
            S.Log.Warn("Emergency unlock partially failed", e);
        }
        if (CanView) await LoadLogsAsync(CancellationToken.None);
    }, null);

    [RelayCommand]
    public Task ExportLogsAsync() => SubmitAsync(() => ExportCsvAsync("access-log", Logs), null);
}
