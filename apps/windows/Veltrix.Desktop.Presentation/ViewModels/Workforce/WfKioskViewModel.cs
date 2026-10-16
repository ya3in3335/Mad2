using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Workforce;
using Veltrix.Desktop.Core.Workforce.Devices;

namespace Veltrix.Desktop.Presentation.ViewModels;

/// <summary>Result of one kiosk scan, shown big on the terminal (with a success / failure animation and sound).</summary>
public sealed record KioskFeedback(bool Success, string Title, string Detail, string? EmployeeName, string? PunchType, string Time);

/// <summary>
/// Attendance terminal (kiosk): big clock, card / QR scans from keyboard-wedge readers (they type the UID then Enter into the focused capture
/// box), a PIN keypad (employee number + PIN) and any attached <see cref="IAttendanceDevice"/> (virtual terminal, serial, TCP…).
/// </summary>
public sealed partial class WfKioskViewModel : WorkforcePageViewModel, IDisposable
{
    /// <summary>Default prefix of QR tokens typed by 2D scanners in keyboard mode (see <see cref="KeyboardWedgeConfig.QrPrefix"/>).</summary>
    public const string QrPrefix = "VQR:";
    private readonly List<IAttendanceDevice> _devices = [];
    /// <summary>Readers started from the device registry while the kiosk is open (owned: disposed on close).</summary>
    private readonly List<IAttendanceDevice> _owned = [];
    private readonly SemaphoreSlim _deviceGate = new(1, 1);
    private int _openCount;

    public WfKioskViewModel(DesktopServices s) : base(s) => Tick();

    public override string TitleKey => "wf.nav.kiosk";

    [ObservableProperty] private string _clock = "";
    [ObservableProperty] private string _dateLine = "";
    /// <summary>Capture box of keyboard-wedge readers (and manual UID entry).</summary>
    [ObservableProperty] private string _scanText = "";
    [ObservableProperty] private string _pinEmployeeNo = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PinMask))] private string _pin = "";
    /// <summary>The keypad types into the employee number (true) or the PIN (false).</summary>
    [ObservableProperty] private bool _keypadOnEmployee = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasFeedback))] private KioskFeedback? _feedback;
    [ObservableProperty] private bool _isFullScreen;
    [ObservableProperty] private bool _isProcessing;

    public string PinMask => new('•', Pin.Length);
    public bool HasFeedback => Feedback is not null;
    public bool ReducedMotion => S.ReducedMotion;
    public static IReadOnlyList<string> KeypadDigits { get; } = ["1", "2", "3", "4", "5", "6", "7", "8", "9"];

    /// <summary>Raised after every scan (the view animates and plays the sound).</summary>
    public event EventHandler<KioskFeedback>? FeedbackShown;

    /// <summary>The scan being processed for the last device event (tests and the view await it).</summary>
    public Task<KioskFeedback>? LastScan { get; private set; }

    protected override Task<bool> LoadAsync(CancellationToken ct)
    {
        Tick();
        return Task.FromResult(true);
    }

    /// <summary>Updates the clock (the view calls it every second).</summary>
    public void Tick()
    {
        var now = LocalNow;
        Clock = now.ToString("T", Loc.Culture);
        DateLine = now.ToString("D", Loc.Culture);
    }

    // ------------------------------------------------------------------ inputs

    /// <summary>Enter in the capture box: a card UID, or a QR token (prefixed "VQR:" by 2D scanners, or a raw "VQ1." token).</summary>
    [RelayCommand]
    public async Task SubmitScanAsync()
    {
        var text = (ScanText ?? "").Trim();
        ScanText = "";
        if (text.Length == 0) return;
        var (kind, value) = Classify(text);
        await ProcessAsync(kind, value);
    }

    public static (string Kind, string Value) Classify(string text) =>
        text.StartsWith(QrPrefix, StringComparison.Ordinal) ? (CredentialKinds.Qr, text[QrPrefix.Length..])
        : text.StartsWith("VQ1.", StringComparison.Ordinal) ? (CredentialKinds.Qr, text)
        : (CredentialKinds.Card, text);

    [RelayCommand]
    public void KeypadDigit(string digit)
    {
        if (digit is not { Length: 1 } || !char.IsAsciiDigit(digit[0])) return;
        if (KeypadOnEmployee) { if (PinEmployeeNo.Length < 16) PinEmployeeNo += digit; }
        else if (Pin.Length < 8) Pin += digit;
    }

    [RelayCommand]
    public void KeypadBackspace()
    {
        if (KeypadOnEmployee) { if (PinEmployeeNo.Length > 0) PinEmployeeNo = PinEmployeeNo[..^1]; }
        else if (Pin.Length > 0) Pin = Pin[..^1];
    }

    [RelayCommand]
    public void KeypadClear()
    {
        PinEmployeeNo = "";
        Pin = "";
        KeypadOnEmployee = true;
    }

    [RelayCommand]
    public void FocusEmployee() => KeypadOnEmployee = true;

    [RelayCommand]
    public void FocusPin() => KeypadOnEmployee = false;

    /// <summary>OK on the keypad: moves to the PIN after the employee number, then punches.</summary>
    [RelayCommand]
    public async Task KeypadOkAsync()
    {
        if (KeypadOnEmployee && Pin.Length == 0) { KeypadOnEmployee = false; return; }
        var no = PinEmployeeNo.Trim();
        var pin = Pin;
        KeypadClear();
        if (no.Length == 0 || pin.Length == 0) { Show(Fail(Loc.Get("wf.kiosk.pinIncomplete"))); return; }
        // Employee numbers are typed digits-only on the keypad: "12" matches "E0012" when no exact code exists.
        await ProcessAsync(CredentialKinds.Pin, $"{await ResolveEmployeeNoAsync(no)}:{pin}");
    }

    private async Task<string> ResolveEmployeeNoAsync(string typed)
    {
        if (!typed.All(char.IsAsciiDigit)) return typed;
        try
        {
            await using var db = S.Db();
            var codes = db.Employees.Select(e => e.Code).ToList();
            if (codes.Contains(typed)) return typed;
            var n = int.Parse(typed, System.Globalization.CultureInfo.InvariantCulture);
            return codes.FirstOrDefault(c => c.Length > 1 && char.IsAsciiLetter(c[0]) && int.TryParse(c[1..], out var k) && k == n) ?? typed;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            S.Log.Warn("Kiosk employee lookup failed", e);
            return typed;
        }
    }

    [RelayCommand]
    public void EnterFullScreen() => IsFullScreen = true;

    /// <summary>Leaving kiosk mode asks for confirmation (the terminal is unattended).</summary>
    [RelayCommand]
    public void ExitFullScreen()
    {
        if (IsFullScreen && S.Ui.Confirm(Loc.Get("wf.kiosk.exitConfirm"))) IsFullScreen = false;
    }

    [RelayCommand]
    public void DismissFeedback() => Feedback = null;

    // ------------------------------------------------------------------ devices

    /// <summary>Listens to a reader / terminal: each scan punches the identified employee.</summary>
    public void AttachDevice(IAttendanceDevice device)
    {
        if (_devices.Contains(device)) return;
        _devices.Add(device);
        device.Scanned += OnDeviceScanned;
    }

    public void DetachDevice(IAttendanceDevice device)
    {
        if (_devices.Remove(device)) device.Scanned -= OnDeviceScanned;
    }

    /// <summary>Devices of the registry currently running for this kiosk.</summary>
    public IReadOnlyList<IAttendanceDevice> RegistryDevices => _owned.ToList();

    /// <summary>Kinds the kiosk starts itself (keyboard-wedge readers type into the scan box; vendor terminals need an SDK).</summary>
    public static bool AutoStarts(WfDevice d) => d.IsEnabled && d.Kind is DeviceKinds.Virtual or DeviceKinds.Serial or DeviceKinds.TcpLine;

    /// <summary>
    /// Kiosk opened (view shown): starts and attaches every enabled virtual / serial / TCP device of the registry. Reference-counted, so the
    /// page and the full-screen window can both open it; the devices stop when the last one closes.
    /// </summary>
    public async Task OpenDevicesAsync()
    {
        await _deviceGate.WaitAsync();
        try
        {
            if (_openCount++ > 0) return;
            List<WfDevice> registry;
            await using (var db = S.Db()) registry = db.WfDevices.AsEnumerable().Where(AutoStarts).OrderBy(d => d.Name).ToList();
            await Task.WhenAll(registry.Select(StartAsync));
        }
        catch (Exception e) when (e is not OperationCanceledException) { S.Log.Warn("Kiosk devices failed to start", e); }
        finally { _deviceGate.Release(); }
    }

    private async Task StartAsync(WfDevice config)
    {
        IAttendanceDevice device;
        try { device = DeviceFactory.Create(config, S.Time); }
        catch (Exception e) when (e is NotSupportedException or System.Text.Json.JsonException)
        {
            await Health(config.Id, false, e.Message);
            return;
        }
        lock (_owned) _owned.Add(device);
        device.StateChanged += (_, st) => { if (st == DeviceConnectionState.Connected) _ = Health(device.DeviceId, true, null); };
        device.Faulted += (_, ex) => _ = Health(device.DeviceId, false, ex.Message);
        AttachDevice(device);
        try { await device.ConnectAsync(); }
        catch (Exception e) when (e is not OperationCanceledException) { await Health(config.Id, false, e.Message); }
    }

    private async Task Health(Guid deviceId, bool ok, string? error)
    {
        try { await Wf.RecordDeviceHealthAsync(deviceId, ok, error); }
        catch (Exception e) { S.Log.Warn("Device health update failed", e); }
    }

    /// <summary>Kiosk closed: detaches and disposes the registry devices once nobody has the kiosk open.</summary>
    public async Task CloseDevicesAsync()
    {
        await _deviceGate.WaitAsync();
        try
        {
            if (_openCount == 0 || --_openCount > 0) return;
            await StopOwnedAsync();
        }
        finally { _deviceGate.Release(); }
    }

    private async Task StopOwnedAsync()
    {
        List<IAttendanceDevice> owned;
        lock (_owned) { owned = _owned.ToList(); _owned.Clear(); }
        foreach (var d in owned)
        {
            DetachDevice(d);
            try { await d.DisposeAsync(); }
            catch (Exception e) { S.Log.Warn("Device stop failed", e); }
        }
    }

    private void OnDeviceScanned(object? sender, DeviceScan scan) =>
        LastScan = ProcessAsync(scan.CredentialKind, scan.CredentialValue, scan.PunchType, scan.DeviceId, scan.DeviceEventId, scan.OccurredAtUtc);

    // ------------------------------------------------------------------ processing

    public async Task<KioskFeedback> ProcessAsync(string kind, string value, string? punchType = null, Guid? deviceId = null, string? deviceEventId = null,
        DateTimeOffset? occurredAtUtc = null)
    {
        IsProcessing = true;
        KioskFeedback fb;
        try
        {
            var source = kind switch
            {
                CredentialKinds.Card => PunchSources.Card,
                CredentialKinds.Qr => PunchSources.Qr,
                CredentialKinds.Pin => PunchSources.Pin,
                _ => deviceId is null ? PunchSources.Kiosk : PunchSources.Terminal,
            };
            var r = await Wf.PunchByCredentialAsync(kind, value, punchType, source, deviceId, deviceEventId, occurredAtUtc);
            var name = r.EmployeeId is { } id ? (await Wf.EmployeeAsync(WorkforceActor.System, id))?.Name : null;
            var at = r.Event is { } ev ? Time(ev.OccurredAtUtc) : Time(Now);
            fb = r.Outcome switch
            {
                PunchOutcomes.Recorded => new KioskFeedback(true, T("punch", r.Event!.Type), Loc.Get("wf.kiosk.recorded"), name, r.Event.Type, at),
                PunchOutcomes.Duplicate => new KioskFeedback(true, T("punch", r.Event?.Type), Loc.Get("wf.kiosk.duplicate"), name, r.Event?.Type, at),
                _ => Fail(T("reason", r.Reason), name),
            };
        }
        catch (Exception e)
        {
            S.Log.Warn("Kiosk punch failed", e);
            fb = Fail(Describe(e));
        }
        finally
        {
            IsProcessing = false;
        }
        Show(fb);
        return fb;
    }

    private KioskFeedback Fail(string detail, string? name = null) => new(false, Loc.Get("wf.kiosk.failed"), detail, name, null, Time(Now));

    private void Show(KioskFeedback fb)
    {
        Feedback = fb;
        FeedbackShown?.Invoke(this, fb);
    }

    public void Dispose()
    {
        _openCount = 0;
        StopOwnedAsync().GetAwaiter().GetResult();
        foreach (var d in _devices.ToList()) DetachDevice(d);
    }
}
