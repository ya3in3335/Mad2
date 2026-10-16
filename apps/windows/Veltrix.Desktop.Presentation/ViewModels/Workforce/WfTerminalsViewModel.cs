using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Workforce;
using Veltrix.Desktop.Core.Workforce.Terminals;

namespace Veltrix.Desktop.Presentation.ViewModels;

public sealed record TerminalRow(WfDevice Device, string Status, string LastConnection, string LastSync, string Drift)
{
    public Guid Id => Device.Id;
    public string Name => Device.Name;
    public string? Manufacturer => Device.Manufacturer;
    public string? Model => Device.Model;
    public string? SerialNumber => Device.SerialNumber;
    public string? Address => Device.Host is { } h ? Device.Port is { } p ? $"{h}:{p}" : h : null;
    public string? Branch => Device.Branch;
    public string? Firmware => Device.Firmware;
    public string? Capabilities => Device.Capabilities;
    public bool IsEnabled => Device.IsEnabled;
    public int ErrorCount => Device.ErrorCount;
    public string? LastError => Device.LastError;
}

public sealed record TerminalEventRow(string Time, string? User, string? Card, string? Method, string? Verify, string Status, string? Reason, string? Result, string Provenance);

/// <summary>
/// Physical attendance terminals (pointeuses): register, test / diagnose, sync now, events and logs, disable, remove (history kept).
/// Polling itself runs in the VELTRIX connector service (or in the app when the service is not installed).
/// </summary>
public sealed partial class WfTerminalsViewModel(DesktopServices s) : WorkforcePageViewModel(s)
{
    public override string TitleKey => "wf.nav.terminals";
    public static IReadOnlyList<string> Manufacturers => TerminalManufacturers.All;
    public static IReadOnlyList<string> Directions { get; } = ["Auto", PunchTypes.In, PunchTypes.Out];
    public static IReadOnlyList<string> Protocols { get; } = ["http", "https"];

    public ObservableCollection<TerminalRow> Items { get; } = [];
    public ObservableCollection<TerminalEventRow> Events { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];

    [ObservableProperty] private TerminalRow? _selected;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _name = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZkTeco), nameof(IsSimulated), nameof(IsSuprema))]
    private string _manufacturer = TerminalManufacturers.Hikvision;
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _port = "";
    [ObservableProperty] private string _protocol = "http";
    [ObservableProperty] private string _userName = "admin";
    /// <summary>Empty = keep the stored password. Never read back from the database.</summary>
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _clearPassword;
    [ObservableProperty] private string _branch = "";
    [ObservableProperty] private string _location = "";
    [ObservableProperty] private string _offsetMinutes = "";
    [ObservableProperty] private string _pollSeconds = "60";
    [ObservableProperty] private bool _applyDrift = true;
    [ObservableProperty] private string _driftTolerance = "300";
    [ObservableProperty] private string _direction = "Auto";
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private string _certificatePin = "";
    [ObservableProperty] private string _remoteDeviceId = "";
    [ObservableProperty] private string? _testResult;
    [ObservableProperty] private bool _testOk;
    [ObservableProperty] private string _connectorStatus = "";
    [ObservableProperty] private bool _showEvents;

    public bool IsZkTeco => Manufacturer == TerminalManufacturers.ZKTeco;
    public bool IsSimulated => Manufacturer == TerminalManufacturers.Simulated;
    public bool IsSuprema => Manufacturer == TerminalManufacturers.Suprema;

    private TerminalSyncCoordinator Coordinator => new(Wf, S.IngestionLock, ConnectorOrigins.Manual);

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        var devices = (await Wf.DevicesAsync(Actor, ct: ct)).Where(d => d.Kind == DeviceKinds.Terminal);
        Fill(Items, devices.Select(ToRow));
        var hb = await Wf.ConnectorHeartbeatAsync(ct);
        ConnectorStatus = hb is null ? Loc.Get("wf.term.svcNone")
            : string.Format(Loc.Culture, Loc.Get(Now - hb.At < TimeSpan.FromMinutes(2) ? "wf.term.svcOk" : "wf.term.svcStale"),
                T("origin", hb.Origin), DateTimeText(hb.At), hb.Devices, hb.Faulted);
        return Items.Count > 0;
    }

    private TerminalRow ToRow(WfDevice d)
    {
        var status = !d.IsEnabled ? Loc.Get("wf.dev.disabled")
            : d.LastSyncStatus == "Error" ? Loc.Get("wf.dev.error")
            : d.LastSyncAt is not null ? Loc.Get("wf.dev.ok") : Loc.Get("wf.dev.never");
        var drift = d.ClockDriftSeconds is { } s ? s.ToString("+0;-0;0", CultureInfo.InvariantCulture) + " s" : "";
        return new TerminalRow(d, status, d.LastConnectionAt is { } c ? DateTimeText(c) : "", d.LastSyncAt is { } at ? DateTimeText(at) : "", drift);
    }

    partial void OnSelectedChanged(TerminalRow? value)
    {
        (TestResult, ShowEvents) = (null, false);
        if (value is null) return;
        var d = value.Device;
        var o = TerminalAdapterFactory.Options(d);
        (Name, Manufacturer, Host, Port, Protocol, UserName) = (d.Name, d.Manufacturer ?? TerminalManufacturers.Hikvision, d.Host ?? "", Opt(d.Port), d.Protocol ?? "http", d.UserName ?? "");
        (Password, ClearPassword, Branch, Location, OffsetMinutes) = ("", false, d.Branch ?? "", d.Location ?? "", Opt(d.TimeZoneOffsetMinutes));
        (PollSeconds, ApplyDrift, DriftTolerance, Direction, IsEnabled) = (Text(d.PollIntervalSeconds), d.ApplyDriftCorrection, Text(d.DriftToleranceSeconds), d.Direction, d.IsEnabled);
        (CertificatePin, RemoteDeviceId) = (o.CertificateSha256 ?? "", o.RemoteDeviceId ?? "");
        IsEditing = true;
    }

    private static string Opt(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";

    [RelayCommand]
    public void New()
    {
        Selected = null;
        (Name, Manufacturer, Host, Port, Protocol, UserName, Password, ClearPassword) = ("", TerminalManufacturers.Hikvision, "", "", "http", "admin", "", false);
        (Branch, Location, OffsetMinutes, PollSeconds, ApplyDrift, DriftTolerance, Direction, IsEnabled) = ("", "", "", "60", true, "300", "Auto", true);
        (CertificatePin, RemoteDeviceId, TestResult, ShowEvents) = ("", "", null, false);
        IsEditing = true;
    }

    public TerminalInput BuildInput() => new(Name, Manufacturer)
    {
        Host = Host, Port = Port.Trim().Length == 0 ? null : ParseInt(Port, "wf.err.deviceConfig", 1, 65535), Protocol = Protocol, UserName = UserName,
        Password = ClearPassword ? "" : Password.Length == 0 ? null : Password,
        Branch = Branch, Location = Location,
        TimeZoneOffsetMinutes = OffsetMinutes.Trim().Length == 0 ? null : ParseSigned(OffsetMinutes, -840, 840),
        PollIntervalSeconds = ParseInt(PollSeconds, "wf.err.pollInterval", 10, 86400), ApplyDriftCorrection = ApplyDrift,
        DriftToleranceSeconds = ParseInt(DriftTolerance, "wf.err.deviceConfig", 0, 86400), Direction = Direction, IsEnabled = IsEnabled,
        CertificateSha256 = CertificatePin, RemoteDeviceId = RemoteDeviceId,
    };

    private static int ParseSigned(string text, int min, int max) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max ? v : throw new InvalidOperationException("wf.err.deviceConfig");

    [RelayCommand]
    public Task SaveAsync() => SubmitAsync(async () =>
    {
        var d = await Wf.UpsertTerminalAsync(Actor, Selected?.Id, BuildInput());
        Password = "";
        await ReloadAsync();
        Selected = Items.FirstOrDefault(i => i.Id == d.Id);
    });

    /// <summary>Step-by-step connection test (reachability, authentication, identity, clock drift). Nothing is imported.</summary>
    [RelayCommand]
    public Task DiagnoseAsync() => SubmitAsync(async () =>
    {
        if (Selected is not { } row) throw new InvalidOperationException("wf.err.unknownDevice");
        TestResult = null;
        var steps = await Wf.DiagnoseTerminalAsync(Actor, row.Id);
        TestOk = steps.Count > 0 && steps.All(x => x.Ok);
        TestResult = string.Join(Environment.NewLine, steps.Select(x => (x.Ok ? "✔ " : "✖ ") + x.Step + " — " + x.Message));
        await RefreshSelectedAsync(row.Id);
    }, null);

    [RelayCommand]
    public Task SyncNowAsync() => SubmitAsync(async () =>
    {
        if (Selected is not { } row) throw new InvalidOperationException("wf.err.unknownDevice");
        var r = await Coordinator.SyncAsync(Actor, row.Id);
        TestOk = r.Ok;
        TestResult = r.Error == "wf.term.busy" ? Loc.Get("wf.term.busy")
            : string.Format(Loc.Culture, Loc.Get("wf.term.syncResult"), r.Received, r.Accepted, r.Duplicates, r.ToReview, r.Ignored)
              + (r.Ok ? "" : Environment.NewLine + r.ErrorKind + ": " + r.Error);
        await RefreshSelectedAsync(row.Id);
    }, null);

    [RelayCommand]
    public Task ToggleEnabledAsync() => SubmitAsync(async () =>
    {
        if (Selected is not { } row) throw new InvalidOperationException("wf.err.unknownDevice");
        await Wf.SetDeviceEnabledAsync(Actor, row.Id, !row.IsEnabled);
        await RefreshSelectedAsync(row.Id);
    });

    /// <summary>Takes the terminal out of operation; its events, batches and logs stay for audit.</summary>
    [RelayCommand]
    public Task RemoveAsync() => SubmitAsync(async () =>
    {
        if (Selected is not { } row) throw new InvalidOperationException("wf.err.unknownDevice");
        if (!S.Ui.Confirm(Loc.Get("wf.term.removeConfirm"))) return;
        await Wf.RemoveDeviceAsync(Actor, row.Id);
        Selected = null;
        IsEditing = false;
        await ReloadAsync();
    });

    [RelayCommand]
    public Task LoadEventsAsync() => SubmitAsync(async () =>
    {
        if (Selected is not { } row) throw new InvalidOperationException("wf.err.unknownDevice");
        var events = await Wf.TerminalEventsAsync(Actor, row.Id, 300);
        Fill(Events, events.Select(e => new TerminalEventRow(DateTimeText(e.OccurredAtUtc), e.DeviceUserId, e.CardNo, e.Method, e.VerifyResult,
            T("inbox", e.Status), e.ReviewReason is { } r ? T("review", r) : null, e.Result,
            $"{e.DeviceLocalTime} {(e.DeviceOffsetMinutes is { } o ? "UTC" + (o >= 0 ? "+" : "") + (o / 60.0).ToString("0.##", CultureInfo.InvariantCulture) : "")}"
            + (e.DriftAppliedSeconds is { } d ? $" (drift {d:+0;-0}s)" : "") + (e.VendorEventId is { } v ? " #" + v : ""))));
        Fill(Logs, (await Wf.DeviceLogsAsync(Actor, row.Id, 100)).Select(l => $"{DateTimeText(l.At)}  [{l.Level}]  {l.Message}"));
        ShowEvents = true;
    }, null);

    private async Task RefreshSelectedAsync(Guid id)
    {
        var (result, ok) = (TestResult, TestOk);
        await ReloadAsync();
        Selected = Items.FirstOrDefault(i => i.Id == id);
        (TestResult, TestOk) = (result, ok);
    }

    [RelayCommand]
    public void Cancel()
    {
        Selected = null;
        IsEditing = false;
    }
}

public sealed record ReviewRow(WfDeviceInbox Item, string Device, string Time, string Reason, string? Suggested)
{
    public long Seq => Item.Seq;
    public string? User => Item.DeviceUserId;
    public string? Card => Item.CardNo;
    public string? Method => Item.Method;
    public string? Result => Item.Result;
    public bool CanMap => Item.ReviewReason == ReviewReasons.UnknownUser;
}

/// <summary>Terminal events that need a decision (unknown user, clock drift, conflicting duplicate, branch mismatch…). Nothing is ever dropped silently.</summary>
public sealed partial class WfReviewViewModel(DesktopServices s) : WorkforcePageViewModel(s)
{
    public override string TitleKey => "wf.nav.review";

    public ObservableCollection<ReviewRow> Items { get; } = [];
    public ObservableCollection<Choice<Guid?>> EmployeeChoices { get; } = [];

    [ObservableProperty] private ReviewRow? _selected;
    [ObservableProperty] private bool _includeClosed;
    [ObservableProperty] private Guid? _employeeId;
    [ObservableProperty] private DateTime? _correctedDate;
    [ObservableProperty] private string _correctedTime = "";
    [ObservableProperty] private string _note = "";

    partial void OnIncludeClosedChanged(bool value) => _ = ReloadAsync();

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        var devices = (await Wf.DevicesAsync(Actor, includeRemoved: true, ct: ct)).ToDictionary(d => d.Id, d => d.Name);
        var items = await Wf.ReviewQueueAsync(Actor, IncludeClosed, ct);
        Fill(Items, items.Select(i => new ReviewRow(i, devices.GetValueOrDefault(i.DeviceId, "?"), DateTimeText(i.OccurredAtUtc), T("review", i.ReviewReason),
            i.SuggestedUtc is { } sug ? DateTimeText(sug) : null)));
        Fill(EmployeeChoices, [new Choice<Guid?>(null, "—"), .. (await Wf.EmployeesAsync(Actor, ct: ct)).Select(e => new Choice<Guid?>(e.Id, e.Code + " — " + e.Name))]);
        return Items.Count > 0;
    }

    partial void OnSelectedChanged(ReviewRow? value)
    {
        (EmployeeId, Note) = (value?.Item.EmployeeId, "");
        var t = value?.Item.SuggestedUtc ?? value?.Item.OccurredAtUtc;
        if (t is { } at)
        {
            var local = TimeZoneInfo.ConvertTime(at, S.Time.LocalTimeZone).DateTime;
            (CorrectedDate, CorrectedTime) = (local.Date, local.ToString("HH:mm", CultureInfo.InvariantCulture));
        }
    }

    private DateTimeOffset? Corrected()
    {
        if (CorrectedDate is not { } d || CorrectedTime.Trim().Length == 0) return null;
        if (!TimeOnly.TryParseExact(CorrectedTime.Trim(), ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            throw new InvalidOperationException("wf.err.time");
        var local = DateOnly.FromDateTime(d).ToDateTime(t);
        return new DateTimeOffset(local, S.Time.LocalTimeZone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>Links the terminal user / card to the chosen employee (future events resolve automatically) and accepts all of their pending events.</summary>
    [RelayCommand]
    public Task MapAsync() => SubmitAsync(async () =>
    {
        if (Selected is not { } row || EmployeeId is not { } eid) throw new InvalidOperationException("wf.err.employeeRequired");
        await Wf.ResolveReviewByMappingAsync(Actor, row.Seq, eid, NoteOrNull());
        await ReloadAsync();
    });

    [RelayCommand]
    public Task AcceptAsync() => SubmitAsync(async () =>
    {
        if (Selected is not { } row) throw new InvalidOperationException("wf.err.unknown");
        await Wf.AcceptReviewAsync(Actor, row.Seq, Corrected(), EmployeeId, NoteOrNull());
        await ReloadAsync();
    });

    [RelayCommand]
    public Task RejectAsync() => SubmitAsync(async () =>
    {
        if (Selected is not { } row) throw new InvalidOperationException("wf.err.unknown");
        if (Note.Trim().Length == 0) throw new InvalidOperationException("wf.err.reasonRequired");
        await Wf.RejectReviewAsync(Actor, row.Seq, Note.Trim());
        await ReloadAsync();
    });

    private string? NoteOrNull() => Note.Trim().Length == 0 ? null : Note.Trim();
}
