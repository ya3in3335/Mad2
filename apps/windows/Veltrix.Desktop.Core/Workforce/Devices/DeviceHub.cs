using System.Collections.Concurrent;
using Veltrix.Desktop.Core.Data;

namespace Veltrix.Desktop.Core.Workforce.Devices;

/// <summary>
/// Runtime host for live devices: every scan is first persisted to the local queue (<see cref="WorkforceService.EnqueueDeviceEventAsync"/>),
/// then processed; state changes and faults feed the device health record. Processing is serialized.
/// </summary>
public sealed class DeviceHub(WorkforceService service) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, IAttendanceDevice> _devices = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentBag<Task> _inflight = [];

    public IReadOnlyCollection<IAttendanceDevice> Devices => _devices.Values.ToList();

    /// <summary>Creates the adapter for a registered device and connects it.</summary>
    public async Task<IAttendanceDevice> StartAsync(WfDevice config, CancellationToken ct = default)
    {
        var device = DeviceFactory.Create(config, service.Time);
        await AttachAsync(device, ct);
        return device;
    }

    public async Task AttachAsync(IAttendanceDevice device, CancellationToken ct = default)
    {
        _devices[device.DeviceId] = device;
        device.Scanned += (_, scan) => Track(HandleScanAsync(scan));
        device.StateChanged += (_, state) =>
        {
            if (state == DeviceConnectionState.Connected) Track(service.RecordDeviceHealthAsync(device.DeviceId, true));
        };
        device.Faulted += (_, ex) => Track(service.RecordDeviceHealthAsync(device.DeviceId, false, ex.Message));
        if (device is IAccessController controller) service.RegisterController(controller);
        try { await device.ConnectAsync(ct); }
        catch (Exception ex) { await service.RecordDeviceHealthAsync(device.DeviceId, false, ex.Message, ct); }
    }

    /// <summary>Connection test for a configured device (vendor terminals report that an SDK adapter is required).</summary>
    public async Task<DeviceTestResult> TestAsync(WfDevice config, CancellationToken ct = default)
    {
        try
        {
            await using var device = DeviceFactory.Create(config, service.Time);
            var r = await device.TestConnectionAsync(ct);
            await service.RecordDeviceHealthAsync(config.Id, r.Ok, r.Ok ? null : r.Message, ct);
            return r;
        }
        catch (NotSupportedException ex) { return new DeviceTestResult(false, ex.Message); }
    }

    public async Task HandleScanAsync(DeviceScan scan)
    {
        await _gate.WaitAsync();
        try
        {
            await service.EnqueueDeviceEventAsync(scan);
            await service.ProcessPendingDeviceEventsAsync();
        }
        finally { _gate.Release(); }
    }

    private void Track(Task t) => _inflight.Add(t);

    /// <summary>Waits for every scan / health update received so far.</summary>
    public async Task DrainAsync()
    {
        while (_inflight.TryTake(out var t)) await t;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var d in _devices.Values)
        {
            service.UnregisterController(d.DeviceId);
            await d.DisposeAsync();
        }
        _devices.Clear();
        await DrainAsync();
        _gate.Dispose();
    }
}
