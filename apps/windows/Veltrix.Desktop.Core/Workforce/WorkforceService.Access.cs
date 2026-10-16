using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Workforce.Devices;

namespace Veltrix.Desktop.Core.Workforce;

public sealed record DeviceInput(string Name, string Kind, string ConfigJson = "{}", string Direction = "Auto", Guid? DoorId = null, bool IsEnabled = true);

public sealed record AccessResult(AccessDecision Decision, Guid? EmployeeId, WfAccessLog Log);

public sealed record DeviceProcessingResult(int Processed, int Rejected);

public sealed partial class WorkforceService
{
    private readonly ConcurrentDictionary<Guid, IAccessController> _controllers = new();

    /// <summary>Registers a live door controller (relay) by its device id.</summary>
    public void RegisterController(IAccessController controller) => _controllers[controller.DeviceId] = controller;
    public void UnregisterController(Guid deviceId) => _controllers.TryRemove(deviceId, out _);

    // ------------------------------------------------------------------ zones, doors, access groups

    public Task<WfZone> UpsertZoneAsync(WorkforceActor actor, Guid? id, string name, string antiPassback = AntiPassbackModes.None, CancellationToken ct = default) =>
        InTransactionAsync(async (db, _) =>
        {
            AuthorizeGlobal(actor, WorkforcePermissions.AccessManage);
            Require(!string.IsNullOrWhiteSpace(name), "wf.err.nameRequired");
            Require(antiPassback is AntiPassbackModes.None or AntiPassbackModes.Soft or AntiPassbackModes.Hard, "wf.err.antiPassback");
            var z = id is { } x ? await db.WfZones.FindAsync([x], ct) ?? throw new InvalidOperationException("wf.err.unknown") : null;
            if (z is null) db.WfZones.Add(z = new WfZone { Id = Guid.NewGuid() });
            z.Name = name.Trim();
            z.AntiPassback = antiPassback;
            return z;
        }, ct);

    public Task<WfDoor> UpsertDoorAsync(WorkforceActor actor, Guid? id, string name, Guid zoneId, string direction = "In", Guid? controllerDeviceId = null,
        int unlockSeconds = 5, CancellationToken ct = default) => InTransactionAsync(async (db, _) =>
    {
        AuthorizeGlobal(actor, WorkforcePermissions.AccessManage);
        Require(!string.IsNullOrWhiteSpace(name) && direction is "In" or "Out" && unlockSeconds is > 0 and <= 300, "wf.err.door");
        Require(await db.WfZones.AnyAsync(z => z.Id == zoneId, ct), "wf.err.unknownZone");
        var d = id is { } x ? await db.WfDoors.FindAsync([x], ct) ?? throw new InvalidOperationException("wf.err.unknown") : null;
        if (d is null) db.WfDoors.Add(d = new WfDoor { Id = Guid.NewGuid() });
        (d.Name, d.ZoneId, d.Direction, d.ControllerDeviceId, d.UnlockSeconds) = (name.Trim(), zoneId, direction, controllerDeviceId, unlockSeconds);
        return d;
    }, ct);

    public Task<WfAccessGroup> UpsertAccessGroupAsync(WorkforceActor actor, Guid? id, string name, IReadOnlyList<Guid> doorIds, IReadOnlyList<AccessWindow> schedule,
        bool allowOnHolidays = false, IReadOnlyList<Guid>? memberIds = null, CancellationToken ct = default) => InTransactionAsync(async (db, _) =>
    {
        AuthorizeGlobal(actor, WorkforcePermissions.AccessManage);
        Require(!string.IsNullOrWhiteSpace(name), "wf.err.nameRequired");
        Require(schedule.All(w => w.Day is >= 0 and <= 6 && TimeOnly.TryParse(w.Start, CultureInfo.InvariantCulture, out var s1) && TimeOnly.TryParse(w.End, CultureInfo.InvariantCulture, out var e1)), "wf.err.schedule");
        var g = id is { } x ? await db.WfAccessGroups.FindAsync([x], ct) ?? throw new InvalidOperationException("wf.err.unknown") : null;
        if (g is null) db.WfAccessGroups.Add(g = new WfAccessGroup { Id = Guid.NewGuid() });
        g.Name = name.Trim();
        g.DoorIdsJson = JsonSerializer.Serialize(doorIds);
        g.ScheduleJson = JsonSerializer.Serialize(schedule, Json);
        g.AllowOnHolidays = allowOnHolidays;
        if (memberIds is not null)
        {
            db.WfAccessGroupMembers.RemoveRange(db.WfAccessGroupMembers.Where(m => m.GroupId == g.Id));
            db.WfAccessGroupMembers.AddRange(memberIds.Distinct().Select(e => new WfAccessGroupMember { GroupId = g.Id, EmployeeId = e }));
        }
        return g;
    }, ct);

    /// <summary>
    /// Access decision for a credential presented at a door: identity → status → access group → weekly schedule → holidays → anti-passback.
    /// Logs the result, updates the zone presence and pulses the door relay when granted.
    /// </summary>
    public async Task<AccessResult> RequestAccessAsync(Guid doorId, string credentialKind, string credentialValue, DateTimeOffset? atUtc = null, Guid? deviceId = null,
        CancellationToken ct = default)
    {
        WfDoor door;
        AccessResult result;
        await using (var db = dbFactory())
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var now = atUtc ?? time.GetUtcNow();
            door = await db.WfDoors.AsNoTracking().FirstOrDefaultAsync(d => d.Id == doorId, ct) ?? throw new InvalidOperationException("wf.err.unknownDoor");
            var zone = await db.WfZones.AsNoTracking().FirstAsync(z => z.Id == door.ZoneId, ct);
            var (employee, _) = await ResolveCredentialAsync(db, credentialKind, credentialValue, deviceId, ct);
            var policy = WorkforcePolicy.FromJson(await db.GetValueAsync(WorkforcePolicy.Key, ct));
            var local = TimeZoneInfo.ConvertTime(now, Zone(policy)).DateTime;
            var groups = new List<WfAccessGroup>();
            WfZonePresence? presence = null;
            if (employee is not null)
            {
                var gids = await db.WfAccessGroupMembers.Where(m => m.EmployeeId == employee.Id).Select(m => m.GroupId).ToListAsync(ct);
                groups = await db.WfAccessGroups.AsNoTracking().Where(g => gids.Contains(g.Id)).ToListAsync(ct);
                presence = await db.WfZonePresence.FirstOrDefaultAsync(p => p.EmployeeId == employee.Id && p.ZoneId == zone.Id, ct);
            }
            var isHoliday = await db.WfHolidays.AnyAsync(h => h.Date == DateOnly.FromDateTime(local), ct);
            var decision = AccessEngine.Decide(new AccessRequest(door, zone, employee, groups, presence?.IsInside, local, isHoliday));
            if (decision.Granted && employee is not null)
            {
                if (presence is null) db.WfZonePresence.Add(presence = new WfZonePresence { EmployeeId = employee.Id, ZoneId = zone.Id });
                presence.IsInside = door.Direction == "In";
                presence.At = now;
            }
            var log = new WfAccessLog
            {
                At = now, DoorId = door.Id, EmployeeId = employee?.Id, CredentialKind = credentialKind,
                CredentialHint = credentialKind == CredentialKinds.Pin ? "****" : AccessEngine.Mask(credentialValue), Granted = decision.Granted, Reason = decision.Reason,
            };
            db.WfAccessLogs.Add(log);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            result = new AccessResult(decision, employee?.Id, log);
        }
        if (result.Decision.Granted && door.ControllerDeviceId is { } cid && _controllers.TryGetValue(cid, out var controller))
            await controller.UnlockAsync(door.Id, TimeSpan.FromSeconds(door.UnlockSeconds), ct);
        return result;
    }

    /// <summary>
    /// EMERGENCY: sends "unlock all" to every registered controller and logs it. The software never sends a lock command during an alarm;
    /// life-safety egress must not depend on it (fail-safe hardware wiring is mandatory).
    /// </summary>
    public async Task<int> EmergencyUnlockAllAsync(WorkforceActor actor, CancellationToken ct = default)
    {
        Authorize(actor, WorkforcePermissions.EmergencyUnlock);
        var n = 0;
        var errors = new List<Exception>();
        foreach (var c in _controllers.Values)
        {
            try { await c.EmergencyUnlockAllAsync(ct); n++; }
            catch (Exception ex) { errors.Add(ex); }
        }
        await using var db = dbFactory();
        db.WfAccessLogs.Add(new WfAccessLog
        {
            At = time.GetUtcNow(), CredentialKind = "emergency", Granted = true, Reason = AccessReasons.EmergencyUnlock, Actor = actor.UserName,
            CredentialHint = $"{n}/{_controllers.Count} controllers",
        });
        await db.SaveChangesAsync(ct);
        if (errors.Count > 0) throw new AggregateException("Some controllers did not acknowledge the emergency unlock.", errors);
        return n;
    }

    public async Task<IReadOnlyList<WfAccessLog>> AccessLogsAsync(WorkforceActor actor, DateTimeOffset fromUtc, DateTimeOffset toUtc, bool deniedOnly = false,
        CancellationToken ct = default)
    {
        AuthorizeGlobal(actor, WorkforcePermissions.AccessView);
        await using var db = dbFactory();
        return (await db.WfAccessLogs.AsNoTracking().ToListAsync(ct)).Where(l => l.At >= fromUtc && l.At <= toUtc && (!deniedOnly || !l.Granted)).OrderBy(l => l.Seq).ToList();
    }

    // ------------------------------------------------------------------ device registry & persisted event queue

    public Task<WfDevice> UpsertDeviceAsync(WorkforceActor actor, Guid? id, DeviceInput input, CancellationToken ct = default) => InTransactionAsync(async (db, now) =>
    {
        AuthorizeGlobal(actor, WorkforcePermissions.DevicesManage);
        Require(!string.IsNullOrWhiteSpace(input.Name), "wf.err.nameRequired");
        Require(input.Kind is DeviceKinds.Virtual or DeviceKinds.KeyboardWedge or DeviceKinds.Serial or DeviceKinds.TcpLine or DeviceKinds.Vendor, "wf.err.deviceKind");
        Require(input.Direction is "Auto" or PunchTypes.In or PunchTypes.Out, "wf.err.direction");
        try { using var _ = JsonDocument.Parse(string.IsNullOrWhiteSpace(input.ConfigJson) ? "{}" : input.ConfigJson); }
        catch (JsonException) { throw new InvalidOperationException("wf.err.deviceConfig"); }
        var d = id is { } x ? await db.WfDevices.FindAsync([x], ct) ?? throw new InvalidOperationException("wf.err.unknown") : null;
        if (d is null) db.WfDevices.Add(d = new WfDevice { Id = Guid.NewGuid(), CreatedAt = now });
        (d.Name, d.Kind, d.ConfigJson, d.Direction, d.DoorId, d.IsEnabled) = (input.Name.Trim(), input.Kind, input.ConfigJson, input.Direction, input.DoorId, input.IsEnabled);
        return d;
    }, ct);

    public async Task<IReadOnlyList<WfDevice>> DevicesAsync(WorkforceActor actor, CancellationToken ct = default)
    {
        AuthorizeGlobal(actor, WorkforcePermissions.AccessView);
        await using var db = dbFactory();
        return (await db.WfDevices.AsNoTracking().ToListAsync(ct)).OrderBy(d => d.Name).ToList();
    }

    /// <summary>Health monitoring: last seen on success, error counter + last error on failure.</summary>
    public async Task RecordDeviceHealthAsync(Guid deviceId, bool ok, string? error = null, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var d = await db.WfDevices.FindAsync([deviceId], ct);
        if (d is null) return;
        if (ok) d.LastSeenAt = time.GetUtcNow();
        else
        {
            d.ErrorCount++;
            d.LastError = error is { Length: > 300 } ? error[..300] : error;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Persists a device event BEFORE any processing (power-failure safe). Returns false when (device, event id) was already received.</summary>
    public async Task<bool> EnqueueDeviceEventAsync(DeviceScan scan, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        if (await db.WfDeviceInbox.AnyAsync(i => i.DeviceId == scan.DeviceId && i.DeviceEventId == scan.DeviceEventId, ct)) return false;
        db.WfDeviceInbox.Add(new WfDeviceInbox
        {
            DeviceId = scan.DeviceId, DeviceEventId = scan.DeviceEventId, CredentialKind = scan.CredentialKind, CredentialValue = scan.CredentialValue,
            PunchType = scan.PunchType, OccurredAtUtc = scan.OccurredAtUtc, ReceivedAt = time.GetUtcNow(),
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return false; } // unique (DeviceId, DeviceEventId): concurrent duplicate
        var dev = await db.WfDevices.FindAsync([scan.DeviceId], ct);
        if (dev is not null) { dev.LastSeenAt = time.GetUtcNow(); await db.SaveChangesAsync(ct); }
        return true;
    }

    /// <summary>
    /// Processes queued device events in arrival order: door devices → access decision, others → attendance punch. Each item is marked in the
    /// same unit of work as its effect; punches are also idempotent by (device, event id), so a crash between the two never doubles a punch.
    /// </summary>
    public async Task<DeviceProcessingResult> ProcessPendingDeviceEventsAsync(CancellationToken ct = default)
    {
        int processed = 0, rejected = 0;
        List<WfDeviceInbox> pending;
        Dictionary<Guid, WfDevice> devices;
        await using (var db = dbFactory())
        {
            pending = await db.WfDeviceInbox.AsNoTracking().Where(i => i.Status == "Pending").OrderBy(i => i.Seq).ToListAsync(ct);
            devices = (await db.WfDevices.AsNoTracking().ToListAsync(ct)).ToDictionary(d => d.Id);
        }
        foreach (var item in pending)
        {
            devices.TryGetValue(item.DeviceId, out var dev);
            string result;
            bool ok;
            if (dev?.DoorId is { } doorId)
            {
                var a = await RequestAccessAsync(doorId, item.CredentialKind, item.CredentialValue, item.OccurredAtUtc, item.DeviceId, ct);
                (ok, result) = (a.Decision.Granted, a.Decision.Reason);
            }
            else
            {
                var type = item.PunchType ?? (dev?.Direction is PunchTypes.In or PunchTypes.Out ? dev.Direction : null);
                var source = item.CredentialKind switch
                {
                    CredentialKinds.Card => PunchSources.Card,
                    CredentialKinds.Qr => PunchSources.Qr,
                    CredentialKinds.Pin => PunchSources.Pin,
                    _ => PunchSources.Terminal,
                };
                var r = await PunchByCredentialAsync(item.CredentialKind, item.CredentialValue, type, source, item.DeviceId, item.DeviceEventId, item.OccurredAtUtc, ct);
                (ok, result) = (r.Outcome != PunchOutcomes.Rejected, r.Outcome + (r.Reason is null ? "" : ":" + r.Reason));
            }
            await using var db = dbFactory();
            var row = await db.WfDeviceInbox.FirstAsync(i => i.Seq == item.Seq, ct);
            row.Status = ok ? "Processed" : "Rejected";
            row.Result = result;
            row.ProcessedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
            if (ok) processed++; else rejected++;
        }
        return new DeviceProcessingResult(processed, rejected);
    }

    public async Task<IReadOnlyList<WfDeviceInbox>> DeviceInboxAsync(WorkforceActor actor, string? status = null, CancellationToken ct = default)
    {
        AuthorizeGlobal(actor, WorkforcePermissions.AccessView);
        await using var db = dbFactory();
        return await db.WfDeviceInbox.AsNoTracking().Where(i => status == null || i.Status == status).OrderBy(i => i.Seq).ToListAsync(ct);
    }
}
