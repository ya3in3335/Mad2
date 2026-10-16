using System.Collections.Concurrent;
using Veltrix.Desktop.Core.Data;

namespace Veltrix.Desktop.Core.Workforce.Terminals;

/// <summary>
/// Single-writer terminal sync: every ingestion runs under the machine-wide <see cref="IIngestionLock"/> so the desktop app and the connector
/// service never ingest at the same time (SQLite WAL lets the other one keep reading). Idempotency keys make a double run harmless anyway.
/// </summary>
public sealed class TerminalSyncCoordinator(WorkforceService wf, IIngestionLock ingestionLock, string origin)
{
    public TimeSpan LockWait { get; init; } = TimeSpan.FromSeconds(3);
    public WorkforceService Workforce => wf;
    public string Origin => origin;

    public async Task<TerminalSyncResult> SyncAsync(Guid deviceId, CancellationToken ct = default)
    {
        var held = await ingestionLock.TryAcquireAsync(LockWait, ct);
        if (held is null) return TerminalSyncResult.Busy(deviceId);
        await using (held) return await wf.SyncTerminalAsync(deviceId, origin, ct);
    }

    /// <summary>"Sync now" from the UI (permission checked first).</summary>
    public async Task<TerminalSyncResult> SyncAsync(WorkforceActor actor, Guid deviceId, CancellationToken ct = default)
    {
        if (!actor.Has(WorkforcePermissions.DevicesManage) || WorkforcePermissions.IsScoped(actor.Role)) throw new WorkforceAccessDeniedException(WorkforcePermissions.DevicesManage);
        return await SyncAsync(deviceId, ct);
    }

    /// <summary>Buffers and ingests streamed events under the lock (waits for it: a stream event must not be lost).</summary>
    public async Task<bool> IngestStreamedAsync(WfDevice device, IReadOnlyList<TerminalEvent> events, CancellationToken ct = default)
    {
        var held = await ingestionLock.TryAcquireAsync(TimeSpan.FromSeconds(30), ct);
        if (held is null) return false; // stays on the terminal; the next poll imports it
        await using (held)
        {
            await wf.BufferTerminalEventsAsync(device, null, events, null, ct);
            await wf.ProcessBufferedTerminalEventsAsync(device.Id, ct);
            return true;
        }
    }

    /// <summary>True when the connector service wrote a heartbeat recently (the app then leaves ingestion to it).</summary>
    public async Task<bool> IsServiceActiveAsync(TimeProvider time, TimeSpan staleAfter, CancellationToken ct = default) =>
        await wf.ConnectorHeartbeatAsync(ct) is { Origin: ConnectorOrigins.Service } hb && time.GetUtcNow() - hb.At < staleAfter;
}

public static class ConnectorOrigins
{
    public const string Service = "Service";
    public const string App = "App";
    public const string Manual = "Manual";
}

public sealed record ConnectorOptions
{
    public string Origin { get; init; } = ConnectorOrigins.Service;
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan HeartbeatEvery { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>App mode: do nothing while the connector service is alive (heartbeat younger than this).</summary>
    public TimeSpan? DeferToServiceWhenFresherThan { get; init; }
    public bool EnableStreaming { get; init; } = true;
    public string Version { get; init; } = "0.0.0";
}

/// <summary>
/// Background ingestion loop shared by the Windows connector service and the desktop app (fallback when the service is not installed):
/// scheduled polling per terminal (its own interval), real-time streams when the adapter supports them, automatic reconnection with
/// exponential backoff, a heartbeat in the database. Events are buffered in the persisted device queue before ingestion (offline-safe).
/// </summary>
public sealed class TerminalConnectorLoop(TerminalSyncCoordinator coordinator, TimeProvider time, ConnectorOptions options, Action<string, string>? log = null)
{
    private sealed class DeviceState
    {
        public DateTimeOffset NextDue;
        public int Failures;
        public Task? Stream;
        public CancellationTokenSource? StreamCts;
    }

    private readonly ConcurrentDictionary<Guid, DeviceState> _states = new();
    private DateTimeOffset _nextHeartbeat = DateTimeOffset.MinValue;
    private WorkforceService Wf => coordinator.Workforce;

    public IReadOnlyDictionary<Guid, int> Failures => _states.ToDictionary(kv => kv.Key, kv => kv.Value.Failures);
    public DateTimeOffset? NextDue(Guid deviceId) => _states.TryGetValue(deviceId, out var s) ? s.NextDue : null;

    public async Task RunAsync(CancellationToken ct)
    {
        log?.Invoke("Info", $"Terminal connector started ({options.Origin}).");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await TickAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { log?.Invoke("Error", "Connector tick failed: " + ex.Message); }
                try { await Task.Delay(options.Tick, time, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            await StopStreamsAsync();
            log?.Invoke("Info", "Terminal connector stopped.");
        }
    }

    /// <summary>One scheduling pass; returns the number of sync attempts.</summary>
    public async Task<int> TickAsync(CancellationToken ct = default)
    {
        if (options.DeferToServiceWhenFresherThan is { } fresh && await coordinator.IsServiceActiveAsync(time, fresh, ct))
        {
            await StopStreamsAsync();
            return 0;
        }
        var now = time.GetUtcNow();
        var devices = await Wf.ActiveTerminalsAsync(ct);
        foreach (var gone in _states.Keys.Except(devices.Select(d => d.Id)).ToList())
            if (_states.TryRemove(gone, out var st)) await StopStreamAsync(st);
        var attempts = 0;
        foreach (var d in devices)
        {
            var state = _states.GetOrAdd(d.Id, _ => new DeviceState { NextDue = now });
            if (now < state.NextDue) continue;
            attempts++;
            TerminalSyncResult r;
            try { r = await coordinator.SyncAsync(d.Id, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { r = new TerminalSyncResult(d.Id, false, ex.Message, null, 0, 0, 0, 0, 0, null, [], null); }
            if (r.Error == "wf.term.busy") { state.NextDue = now + options.Tick; continue; }
            if (r.Ok)
            {
                if (state.Failures > 0) log?.Invoke("Info", $"{d.Name}: reconnected after {state.Failures} failure(s).");
                state.Failures = 0;
                state.NextDue = now + TimeSpan.FromSeconds(Math.Max(10, d.PollIntervalSeconds));
                if (r.Accepted + r.ToReview > 0) log?.Invoke("Info", $"{d.Name}: {r.Accepted} punch(es), {r.Duplicates} duplicate(s), {r.ToReview} to review.");
                if (options.EnableStreaming && (d.Capabilities ?? "").Contains(nameof(TerminalCapabilities.EventStream), StringComparison.Ordinal)) EnsureStream(d, state, ct);
            }
            else
            {
                state.Failures++;
                state.NextDue = now + Backoff(state.Failures);
                log?.Invoke(state.Failures == 1 ? "Warning" : "Info", $"{d.Name}: sync failed ({r.ErrorKind}) {r.Error}; retry in {Backoff(state.Failures).TotalSeconds:0}s.");
            }
        }
        if (now >= _nextHeartbeat)
        {
            _nextHeartbeat = now + options.HeartbeatEvery;
            await Wf.WriteConnectorHeartbeatAsync(new ConnectorHeartbeat(now, options.Origin, Environment.MachineName, Environment.ProcessId, options.Version, devices.Count,
                _states.Values.Count(s => s.Failures > 0)), ct);
        }
        return attempts;
    }

    public TimeSpan Backoff(int failures)
    {
        var seconds = options.InitialBackoff.TotalSeconds * Math.Pow(2, Math.Clamp(failures - 1, 0, 20));
        return TimeSpan.FromSeconds(Math.Min(seconds, options.MaxBackoff.TotalSeconds));
    }

    private void EnsureStream(WfDevice d, DeviceState state, CancellationToken ct)
    {
        if (state.Stream is { IsCompleted: false }) return;
        state.StreamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = state.StreamCts.Token;
        state.Stream = Task.Run(() => StreamLoopAsync(d, token), CancellationToken.None);
    }

    private async Task StreamLoopAsync(WfDevice d, CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var adapter = await Wf.OpenTerminalAsync(d.Id, ct);
                if (!adapter.Capabilities.HasFlag(TerminalCapabilities.EventStream)) return;
                await foreach (var e in adapter.StreamEventsAsync(ct))
                {
                    failures = 0;
                    await coordinator.IngestStreamedAsync(d, [e], ct);
                }
                failures++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (TerminalException ex) when (ex.Kind == TerminalErrorKind.Unsupported) { return; }
            catch (Exception ex)
            {
                failures++;
                log?.Invoke("Warning", $"{d.Name}: event stream lost ({ex.Message}); reconnecting.");
            }
            try { await Task.Delay(Backoff(Math.Max(1, failures)), time, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task StopStreamsAsync()
    {
        foreach (var s in _states.Values) await StopStreamAsync(s);
    }

    private static async Task StopStreamAsync(DeviceState s)
    {
        if (s.StreamCts is null) return;
        await s.StreamCts.CancelAsync();
        if (s.Stream is { } t)
            try { await t; } catch (Exception) { /* stopping */ }
        s.StreamCts.Dispose();
        (s.StreamCts, s.Stream) = (null, null);
    }
}
