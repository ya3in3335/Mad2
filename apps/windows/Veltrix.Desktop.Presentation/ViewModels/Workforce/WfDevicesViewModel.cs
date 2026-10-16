using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Workforce;
using Veltrix.Desktop.Core.Workforce.Devices;

namespace Veltrix.Desktop.Presentation.ViewModels;

public sealed record DeviceRow(WfDevice Device, string KindLabel, string Status, string LastSeen, bool IsVendor)
{
    public Guid Id => Device.Id;
    public string Name => Device.Name;
    public string Direction => Device.Direction;
    public bool IsEnabled => Device.IsEnabled;
    public int ErrorCount => Device.ErrorCount;
    public string? LastError => Device.LastError;
}

/// <summary>Device management: register virtual / keyboard-wedge (USB HID) / serial / TCP readers, test the connection, health and error counts.</summary>
public sealed partial class WfDevicesViewModel(DesktopServices s) : WorkforcePageViewModel(s)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public override string TitleKey => "wf.nav.devices";
    public static IReadOnlyList<string> Kinds { get; } = [DeviceKinds.Virtual, DeviceKinds.KeyboardWedge, DeviceKinds.Serial, DeviceKinds.TcpLine, DeviceKinds.Vendor];
    public static IReadOnlyList<string> Directions { get; } = ["Auto", PunchTypes.In, PunchTypes.Out];
    public static string VendorNotice => VendorTerminals.Notice;

    public ObservableCollection<DeviceRow> Items { get; } = [];
    public ObservableCollection<Choice<Guid?>> DoorChoices { get; } = [];

    [ObservableProperty] private DeviceRow? _selected;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _name = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWedge), nameof(IsSerial), nameof(IsTcp), nameof(IsVendor), nameof(IsVirtual))]
    private string _kind = DeviceKinds.Virtual;
    [ObservableProperty] private string _direction = "Auto";
    [ObservableProperty] private Guid? _doorId;
    [ObservableProperty] private bool _isEnabled = true;
    // Kind-specific settings
    [ObservableProperty] private string _wedgeMinLength = "4";
    [ObservableProperty] private string _wedgeInterKeyMs = "80";
    [ObservableProperty] private string _wedgeQrPrefix = "VQR:";
    [ObservableProperty] private string _serialPort = "COM3";
    [ObservableProperty] private string _serialBaud = "9600";
    [ObservableProperty] private string _tcpHost = "192.168.1.50";
    [ObservableProperty] private string _tcpPort = "5005";
    [ObservableProperty] private string _linePattern = new LineProtocol().Pattern;
    [ObservableProperty] private string _vendorName = "";
    [ObservableProperty] private string? _testResult;
    [ObservableProperty] private bool _testOk;

    public bool IsVirtual => Kind == DeviceKinds.Virtual;
    public bool IsWedge => Kind == DeviceKinds.KeyboardWedge;
    public bool IsSerial => Kind == DeviceKinds.Serial;
    public bool IsTcp => Kind == DeviceKinds.TcpLine;
    public bool IsVendor => Kind == DeviceKinds.Vendor;

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        var devices = await Wf.DevicesAsync(Actor, ct);
        Fill(Items, devices.Select(ToRow));
        Fill(DoorChoices, [new Choice<Guid?>(null, "—"), .. (await Wf.DoorsAsync(Actor, ct)).Select(d => new Choice<Guid?>(d.Id, d.Name))]);
        return Items.Count > 0;
    }

    private DeviceRow ToRow(WfDevice d)
    {
        var status = d.Kind == DeviceKinds.Vendor ? Loc.Get("wf.dev.requiresSdk")
            : !d.IsEnabled ? Loc.Get("wf.dev.disabled")
            : d.LastError is not null && d.ErrorCount > 0 ? Loc.Get("wf.dev.error")
            : d.LastSeenAt is not null ? Loc.Get("wf.dev.ok") : Loc.Get("wf.dev.never");
        return new DeviceRow(d, T("kind", d.Kind), status, d.LastSeenAt is { } at ? DateTimeText(at) : "", d.Kind == DeviceKinds.Vendor);
    }

    partial void OnSelectedChanged(DeviceRow? value)
    {
        TestResult = null;
        if (value is null) return;
        var d = value.Device;
        (Name, Kind, Direction, DoorId, IsEnabled) = (d.Name, d.Kind, d.Direction, d.DoorId, d.IsEnabled);
        switch (d.Kind)
        {
            case DeviceKinds.KeyboardWedge:
                var w = DeviceFactory.Config<KeyboardWedgeConfig>(d);
                (WedgeMinLength, WedgeInterKeyMs, WedgeQrPrefix) = (Text(w.MinLength), Text(w.MaxInterKeyMilliseconds), w.QrPrefix);
                break;
            case DeviceKinds.Serial:
                var sc = DeviceFactory.Config<SerialLineConfig>(d);
                (SerialPort, SerialBaud, LinePattern) = (sc.PortName, Text(sc.BaudRate), sc.Protocol.Pattern);
                break;
            case DeviceKinds.TcpLine:
                var tc = DeviceFactory.Config<TcpLineConfig>(d);
                (TcpHost, TcpPort, LinePattern) = (tc.Host, Text(tc.Port), tc.Protocol.Pattern);
                break;
            case DeviceKinds.Vendor:
                try { VendorName = JsonDocument.Parse(d.ConfigJson).RootElement.TryGetProperty("vendor", out var v) ? v.GetString() ?? "" : ""; }
                catch (JsonException) { VendorName = ""; }
                break;
        }
        IsEditing = true;
    }

    [RelayCommand]
    public void New()
    {
        Selected = null;
        (Name, Kind, Direction, DoorId, IsEnabled, TestResult) = ("", DeviceKinds.KeyboardWedge, "Auto", null, true, null);
        IsEditing = true;
    }

    /// <summary>Builds the adapter configuration JSON from the kind-specific fields.</summary>
    public string BuildConfigJson() => Kind switch
    {
        DeviceKinds.KeyboardWedge => JsonSerializer.Serialize(new KeyboardWedgeConfig
        {
            MinLength = ParseInt(WedgeMinLength, "wf.err.deviceConfig", 1, 64), MaxInterKeyMilliseconds = ParseInt(WedgeInterKeyMs, "wf.err.deviceConfig", 0, 5000),
            QrPrefix = WedgeQrPrefix ?? "",
        }, Json),
        DeviceKinds.Serial => JsonSerializer.Serialize(new SerialLineConfig
        {
            PortName = string.IsNullOrWhiteSpace(SerialPort) ? throw new InvalidOperationException("wf.err.deviceConfig") : SerialPort.Trim(),
            BaudRate = ParseInt(SerialBaud, "wf.err.deviceConfig", 300, 921600), Protocol = Protocol(),
        }, Json),
        DeviceKinds.TcpLine => JsonSerializer.Serialize(new TcpLineConfig
        {
            Host = string.IsNullOrWhiteSpace(TcpHost) ? throw new InvalidOperationException("wf.err.deviceConfig") : TcpHost.Trim(),
            Port = ParseInt(TcpPort, "wf.err.deviceConfig", 1, 65535), Protocol = Protocol(),
        }, Json),
        DeviceKinds.Vendor => JsonSerializer.Serialize(new { vendor = VendorName?.Trim() ?? "" }, Json),
        _ => "{}",
    };

    private LineProtocol Protocol()
    {
        var pattern = string.IsNullOrWhiteSpace(LinePattern) ? new LineProtocol().Pattern : LinePattern.Trim();
        try { _ = new System.Text.RegularExpressions.Regex(pattern); }
        catch (ArgumentException) { throw new InvalidOperationException("wf.err.deviceConfig"); }
        return new LineProtocol { Pattern = pattern };
    }

    [RelayCommand]
    public Task SaveAsync() => SubmitAsync(async () =>
    {
        var d = await Wf.UpsertDeviceAsync(Actor, Selected?.Id, new DeviceInput(Name, Kind, BuildConfigJson(), Direction, DoorId, IsEnabled));
        await ReloadAsync();
        Selected = Items.FirstOrDefault(i => i.Id == d.Id);
    });

    /// <summary>Connection test of the selected (saved) device; vendor terminals report that the vendor SDK is required.</summary>
    [RelayCommand]
    public Task TestAsync() => SubmitAsync(async () =>
    {
        if (Selected is not { } row) throw new InvalidOperationException("wf.err.unknownDevice");
        TestResult = null;
        await using var hub = new DeviceHub(Wf);
        var r = await hub.TestAsync(row.Device);
        var text = row.IsVendor ? Loc.Get("wf.dev.requiresSdk") + " — " + r.Message : r.Message;
        await ReloadAsync();
        Selected = Items.FirstOrDefault(i => i.Id == row.Id);
        TestOk = r.Ok;
        TestResult = text;
    }, null);

    [RelayCommand]
    public void Cancel()
    {
        Selected = null;
        IsEditing = false;
    }
}
