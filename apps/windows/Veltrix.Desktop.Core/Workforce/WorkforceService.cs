using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Erp;
using Veltrix.Desktop.Core.Security;
using Veltrix.Desktop.Core.Standalone;

namespace Veltrix.Desktop.Core.Workforce;

public sealed record EmployeeProfileInput(string Name, decimal Salary, DateOnly HireDate)
{
    /// <summary>Employee number; null = next automatic number (E0001 …).</summary>
    public string? Code { get; init; }
    public string? Role { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? PhotoPath { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? PositionId { get; init; }
    public string? Workplace { get; init; }
    public Guid? ManagerId { get; init; }
    public string? ContractType { get; init; }
    public DateOnly? ContractStart { get; init; }
    public DateOnly? ContractEnd { get; init; }
    public DateOnly? TerminationDate { get; init; }
    public string Status { get; init; } = EmployeeStatuses.Active;
    public string? EmergencyContactName { get; init; }
    public string? EmergencyContactPhone { get; init; }
    public string? Notes { get; init; }
    public string PayBasis { get; init; } = "Monthly";
    public decimal HourlyRate { get; init; }
}

public sealed record EmployeeDocumentInput(string DocType, string Path, DateOnly? Expiry = null, string? Notes = null);

/// <summary>
/// VELTRIX Workforce &amp; Attendance (local-first, standalone): employees, credentials, attendance, shifts, leave, payroll preparation,
/// devices and access control. Every public operation takes the acting <see cref="WorkforceActor"/> and enforces RBAC here, in the service layer:
/// department managers are limited to their department (and direct reports), employees to themselves, auditors are read-only.
/// </summary>
public sealed partial class WorkforceService(Func<LocalDb> dbFactory, TimeProvider time, int pinIterations = PasswordHasher.DefaultIterations)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public TimeProvider Time => time;

    // ------------------------------------------------------------------ plumbing

    private async Task<T> InTransactionAsync<T>(Func<LocalDb, DateTimeOffset, Task<T>> work, CancellationToken ct)
    {
        await using var db = dbFactory();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var result = await work(db, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private static void Require(bool condition, string key)
    {
        if (!condition) throw new InvalidOperationException(key);
    }

    private static string? O(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>Permission check without employee scope (organisation-wide operations: scoped roles are refused).</summary>
    private static void AuthorizeGlobal(WorkforceActor actor, string permission)
    {
        if (!actor.Has(permission) || WorkforcePermissions.IsScoped(actor.Role)) throw new WorkforceAccessDeniedException(permission);
    }

    private static void Authorize(WorkforceActor actor, string permission)
    {
        if (!actor.Has(permission)) throw new WorkforceAccessDeniedException(permission);
    }

    /// <summary>Permission + scope check for an operation on one employee.</summary>
    private async Task AuthorizeAsync(LocalDb db, WorkforceActor actor, string permission, Guid employeeId, CancellationToken ct)
    {
        Authorize(actor, permission);
        if (!WorkforcePermissions.IsScoped(actor.Role)) return;
        var scope = await ScopeAsync(db, actor, ct);
        if (!scope(await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, ct)))
            throw new WorkforceAccessDeniedException(permission);
    }

    /// <summary>Which employees the actor may see / act on.</summary>
    private static async Task<Func<LocalEmployee?, bool>> ScopeAsync(LocalDb db, WorkforceActor actor, CancellationToken ct)
    {
        if (!WorkforcePermissions.IsScoped(actor.Role)) return e => e is not null;
        var self = actor.EmployeeId;
        if (actor.Role == LocalRoles.Employee) return e => e is not null && self is not null && e.Id == self;
        var dept = actor.DepartmentId;
        if (dept is null && self is { } sid) dept = (await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == sid, ct))?.DepartmentId;
        return e => e is not null && (e.Id == self || (dept is not null && e.DepartmentId == dept) || (self is not null && e.ManagerId == self));
    }

    private async Task<List<LocalEmployee>> ScopedEmployeesAsync(LocalDb db, WorkforceActor actor, CancellationToken ct)
    {
        var scope = await ScopeAsync(db, actor, ct);
        return (await db.Employees.AsNoTracking().ToListAsync(ct)).Where(e => scope(e)).OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void Audit(LocalDb db, Guid employeeId, WorkforceActor actor, string action, object? oldValue, object? newValue, DateTimeOffset now) =>
        db.WfEmployeeAudits.Add(new WfEmployeeAudit
        {
            EmployeeId = employeeId, At = now, Actor = actor.UserName, Action = action,
            OldJson = oldValue is null ? null : JsonSerializer.Serialize(oldValue, Json), NewJson = newValue is null ? null : JsonSerializer.Serialize(newValue, Json),
        });

    // ------------------------------------------------------------------ policy

    public async Task<WorkforcePolicy> PolicyAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory();
        return WorkforcePolicy.FromJson(await db.GetValueAsync(WorkforcePolicy.Key, ct));
    }

    public async Task SavePolicyAsync(WorkforceActor actor, WorkforcePolicy policy, CancellationToken ct = default)
    {
        AuthorizeGlobal(actor, WorkforcePermissions.PayrollConfigure);
        await using var db = dbFactory();
        await db.SetValueAsync(WorkforcePolicy.Key, policy.ToJson(), ct);
    }

    private TimeZoneInfo Zone(WorkforcePolicy p)
    {
        if (p.Attendance.TimeZoneId is { Length: > 0 } id)
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
        return time.LocalTimeZone;
    }

    // ------------------------------------------------------------------ departments & positions

    public Task<WfDepartment> UpsertDepartmentAsync(WorkforceActor actor, Guid? id, string name, string? code = null, Guid? parentId = null, bool isActive = true,
        CancellationToken ct = default) => InTransactionAsync(async (db, _) =>
    {
        AuthorizeGlobal(actor, WorkforcePermissions.EmployeesManage);
        Require(!string.IsNullOrWhiteSpace(name), "wf.err.nameRequired");
        var d = id is { } x ? await db.WfDepartments.FindAsync([x], ct) ?? throw new InvalidOperationException("wf.err.unknown") : null;
        if (d is null) db.WfDepartments.Add(d = new WfDepartment { Id = Guid.NewGuid() });
        d.Name = name.Trim();
        d.Code = O(code) ?? d.Name.ToUpperInvariant()[..Math.Min(6, d.Name.Length)];
        d.ParentId = parentId;
        d.IsActive = isActive;
        return d;
    }, ct);

    public async Task<IReadOnlyList<WfDepartment>> DepartmentsAsync(WorkforceActor actor, CancellationToken ct = default)
    {
        Authorize(actor, WorkforcePermissions.EmployeesView);
        await using var db = dbFactory();
        return (await db.WfDepartments.AsNoTracking().ToListAsync(ct)).OrderBy(d => d.Name).ToList();
    }

    public Task<WfPosition> UpsertPositionAsync(WorkforceActor actor, Guid? id, string title, Guid? departmentId = null, bool isActive = true,
        CancellationToken ct = default) => InTransactionAsync(async (db, _) =>
    {
        AuthorizeGlobal(actor, WorkforcePermissions.EmployeesManage);
        Require(!string.IsNullOrWhiteSpace(title), "wf.err.nameRequired");
        var p = id is { } x ? await db.WfPositions.FindAsync([x], ct) ?? throw new InvalidOperationException("wf.err.unknown") : null;
        if (p is null) db.WfPositions.Add(p = new WfPosition { Id = Guid.NewGuid() });
        p.Title = title.Trim();
        p.DepartmentId = departmentId;
        p.IsActive = isActive;
        return p;
    }, ct);

    public async Task<IReadOnlyList<WfPosition>> PositionsAsync(WorkforceActor actor, CancellationToken ct = default)
    {
        Authorize(actor, WorkforcePermissions.EmployeesView);
        await using var db = dbFactory();
        return (await db.WfPositions.AsNoTracking().ToListAsync(ct)).OrderBy(d => d.Title).ToList();
    }

    // ------------------------------------------------------------------ employees (extends the ERP employee record)

    public async Task<IReadOnlyList<LocalEmployee>> EmployeesAsync(WorkforceActor actor, string? search = null, Guid? departmentId = null,
        string? status = null, CancellationToken ct = default)
    {
        Authorize(actor, WorkforcePermissions.EmployeesView);
        await using var db = dbFactory();
        var s = search?.Trim() ?? "";
        return (await ScopedEmployeesAsync(db, actor, ct))
            .Where(e => (departmentId is null || e.DepartmentId == departmentId) && (status is null || e.Status == status)
                && (s.Length == 0 || Has(e.Name, s) || Has(e.Code, s) || Has(e.Phone, s) || Has(e.Email, s) || Has(e.Workplace, s)))
            .ToList();
        static bool Has(string? v, string s) => v?.Contains(s, StringComparison.OrdinalIgnoreCase) == true;
    }

    public async Task<LocalEmployee?> EmployeeAsync(WorkforceActor actor, Guid id, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        await AuthorizeAsync(db, actor, WorkforcePermissions.EmployeesView, id, ct);
        return await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
    }

    /// <summary>Creates or updates an employee; every change is written to the audit history (who / when / old / new).</summary>
    public Task<LocalEmployee> UpsertEmployeeAsync(WorkforceActor actor, Guid? id, EmployeeProfileInput input, CancellationToken ct = default) =>
        InTransactionAsync(async (db, now) =>
        {
            AuthorizeGlobal(actor, WorkforcePermissions.EmployeesManage);
            Require(!string.IsNullOrWhiteSpace(input.Name), "wf.err.nameRequired");
            Require(input.Salary >= 0 && input.HourlyRate >= 0, "wf.err.salary");
            Require(EmployeeStatuses.All.Contains(input.Status), "wf.err.status");
            Require(input.PayBasis is "Monthly" or "Hourly", "wf.err.payBasis");
            Require(input.ContractEnd is null || input.ContractStart is null || input.ContractEnd >= input.ContractStart, "wf.err.contractDates");
            var e = id is { } x ? await db.Employees.FindAsync([x], ct) ?? throw new InvalidOperationException("erp.err.unknownEmployee") : null;
            Require(input.ManagerId is null || input.ManagerId != e?.Id, "wf.err.selfManager");
            if (input.ManagerId is { } m) Require(await db.Employees.AnyAsync(z => z.Id == m, ct), "wf.err.unknownManager");
            if (input.DepartmentId is { } dep) Require(await db.WfDepartments.AnyAsync(z => z.Id == dep, ct), "wf.err.unknownDepartment");
            var code = O(input.Code);
            if (code is not null)
                Require(!(await db.Employees.Where(z => z.Code == code).Select(z => z.Id).ToListAsync(ct)).Any(z => z != e?.Id), "wf.err.codeTaken");
            var old = e is null ? null : Snapshot(e);
            if (e is null) db.Employees.Add(e = new LocalEmployee { Id = Guid.NewGuid(), Code = code ?? await ErpService.NextEmployeeCodeAsync(db, ct) });
            else if (code is not null) e.Code = code;
            e.Name = input.Name.Trim();
            e.Role = O(input.Role);
            e.Phone = O(input.Phone);
            e.Email = O(input.Email);
            e.PhotoPath = O(input.PhotoPath);
            e.Salary = Money.Round(input.Salary);
            e.HourlyRate = Money.Round(input.HourlyRate, 4);
            e.PayBasis = input.PayBasis;
            e.HireDate = input.HireDate;
            e.DepartmentId = input.DepartmentId;
            e.PositionId = input.PositionId;
            e.Workplace = O(input.Workplace);
            e.ManagerId = input.ManagerId;
            e.ContractType = O(input.ContractType);
            e.ContractStart = input.ContractStart;
            e.ContractEnd = input.ContractEnd;
            e.TerminationDate = input.TerminationDate;
            e.Status = input.Status;
            e.IsActive = input.Status != EmployeeStatuses.Terminated;
            e.EmergencyContactName = O(input.EmergencyContactName);
            e.EmergencyContactPhone = O(input.EmergencyContactPhone);
            e.Notes = O(input.Notes);
            Audit(db, e.Id, actor, old is null ? "create" : "update", old, Snapshot(e), now);
            return e;
        }, ct);

    /// <summary>Deletes an employee without history (no punches, payslips or requests); otherwise terminate instead. The audit trail is kept.</summary>
    public Task<bool> DeleteEmployeeAsync(WorkforceActor actor, Guid id, CancellationToken ct = default) => InTransactionAsync(async (db, now) =>
    {
        AuthorizeGlobal(actor, WorkforcePermissions.EmployeesManage);
        var e = await db.Employees.FindAsync([id], ct);
        if (e is null) return false;
        Require(!await db.WfAttendanceEvents.AnyAsync(x => x.EmployeeId == id, ct) && !await db.Payslips.AnyAsync(x => x.EmployeeId == id, ct)
            && !await db.SalaryAdvances.AnyAsync(x => x.EmployeeId == id, ct) && !await db.WfLeaveRequests.AnyAsync(x => x.EmployeeId == id, ct), "wf.err.employeeHasHistory");
        Audit(db, id, actor, "delete", Snapshot(e), null, now);
        db.WfCredentials.RemoveRange(db.WfCredentials.Where(c => c.EmployeeId == id));
        db.WfShiftAssignments.RemoveRange(db.WfShiftAssignments.Where(c => c.EmployeeId == id));
        db.WfAccessGroupMembers.RemoveRange(db.WfAccessGroupMembers.Where(c => c.EmployeeId == id));
        db.Employees.Remove(e);
        return true;
    }, ct);

    public async Task<IReadOnlyList<WfEmployeeAudit>> EmployeeHistoryAsync(WorkforceActor actor, Guid employeeId, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        await AuthorizeAsync(db, actor, WorkforcePermissions.AuditView, employeeId, ct);
        return await db.WfEmployeeAudits.AsNoTracking().Where(a => a.EmployeeId == employeeId).OrderBy(a => a.Seq).ToListAsync(ct);
    }

    private static object Snapshot(LocalEmployee e) => new
    {
        e.Code, e.Name, e.Role, e.Phone, e.Email, e.PhotoPath, e.Salary, e.HourlyRate, e.PayBasis, e.HireDate, e.DepartmentId, e.PositionId, e.Workplace,
        e.ManagerId, e.ContractType, e.ContractStart, e.ContractEnd, e.TerminationDate, e.Status, e.IsActive, e.EmergencyContactName,
        e.EmergencyContactPhone, e.Notes,
    };

    public Task<WfEmployeeDocument> AddDocumentAsync(WorkforceActor actor, Guid employeeId, EmployeeDocumentInput input, CancellationToken ct = default) =>
        InTransactionAsync(async (db, now) =>
        {
            AuthorizeGlobal(actor, WorkforcePermissions.EmployeesManage);
            Require(await db.Employees.AnyAsync(e => e.Id == employeeId, ct), "erp.err.unknownEmployee");
            Require(!string.IsNullOrWhiteSpace(input.DocType) && !string.IsNullOrWhiteSpace(input.Path), "wf.err.document");
            var d = new WfEmployeeDocument
            {
                Id = Guid.NewGuid(), EmployeeId = employeeId, DocType = input.DocType.Trim(), Path = input.Path.Trim(), Expiry = input.Expiry, Notes = O(input.Notes), AddedAt = now,
            };
            db.WfEmployeeDocuments.Add(d);
            Audit(db, employeeId, actor, "document.add", null, new { d.DocType, d.Path, d.Expiry }, now);
            return d;
        }, ct);

    public Task<bool> RemoveDocumentAsync(WorkforceActor actor, Guid documentId, CancellationToken ct = default) => InTransactionAsync(async (db, now) =>
    {
        AuthorizeGlobal(actor, WorkforcePermissions.EmployeesManage);
        var d = await db.WfEmployeeDocuments.FindAsync([documentId], ct);
        if (d is null) return false;
        Audit(db, d.EmployeeId, actor, "document.remove", new { d.DocType, d.Path, d.Expiry }, null, now);
        db.WfEmployeeDocuments.Remove(d);
        return true;
    }, ct);

    public async Task<IReadOnlyList<WfEmployeeDocument>> DocumentsAsync(WorkforceActor actor, Guid? employeeId = null, DateOnly? expiringBefore = null,
        CancellationToken ct = default)
    {
        Authorize(actor, WorkforcePermissions.EmployeesView);
        await using var db = dbFactory();
        var scope = await ScopeAsync(db, actor, ct);
        var employees = (await db.Employees.AsNoTracking().ToListAsync(ct)).Where(e => scope(e)).Select(e => e.Id).ToHashSet();
        return (await db.WfEmployeeDocuments.AsNoTracking().ToListAsync(ct))
            .Where(d => employees.Contains(d.EmployeeId) && (employeeId is null || d.EmployeeId == employeeId) && (expiringBefore is null || d.Expiry <= expiringBefore))
            .OrderBy(d => d.Expiry ?? DateOnly.MaxValue).ToList();
    }

    // ------------------------------------------------------------------ credentials

    /// <summary>Normalizes a card UID: upper-case hex/alphanumerics without separators.</summary>
    public static string NormalizeUid(string uid) => new(uid.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private async Task<LocalEmployee> CredentialTargetAsync(LocalDb db, WorkforceActor actor, Guid employeeId, CancellationToken ct)
    {
        AuthorizeGlobal(actor, WorkforcePermissions.CredentialsManage);
        return await db.Employees.FindAsync([employeeId], ct) ?? throw new InvalidOperationException("erp.err.unknownEmployee");
    }

    private static void RevokeAll(LocalDb db, IEnumerable<WfCredential> creds, DateTimeOffset now)
    {
        foreach (var c in creds) { c.IsActive = false; c.RevokedAt = now; }
    }

    public Task<WfCredential> AssignCardAsync(WorkforceActor actor, Guid employeeId, string uid, string? label = null, CancellationToken ct = default) =>
        InTransactionAsync(async (db, now) =>
        {
            await CredentialTargetAsync(db, actor, employeeId, ct);
            var value = NormalizeUid(uid);
            Require(value.Length >= 4, "wf.err.cardUid");
            Require(!await db.WfCredentials.AnyAsync(c => c.Kind == CredentialKinds.Card && c.Value == value && c.IsActive, ct), "wf.err.cardInUse");
            var c = new WfCredential { Id = Guid.NewGuid(), EmployeeId = employeeId, Kind = CredentialKinds.Card, Value = value, Label = O(label), CreatedAt = now };
            db.WfCredentials.Add(c);
            Audit(db, employeeId, actor, "credential.card", null, new { Uid = AccessEngine.Mask(value) }, now);
            return c;
        }, ct);

    /// <summary>Issues (or rotates) the employee's QR token. The plain token is returned once for printing; only its SHA-256 is stored.</summary>
    public Task<string> IssueQrTokenAsync(WorkforceActor actor, Guid employeeId, CancellationToken ct = default) => InTransactionAsync(async (db, now) =>
    {
        await CredentialTargetAsync(db, actor, employeeId, ct);
        RevokeAll(db, await db.WfCredentials.Where(c => c.EmployeeId == employeeId && c.Kind == CredentialKinds.Qr && c.IsActive).ToListAsync(ct), now);
        var token = "VQ1." + Base64Url.Encode(RandomNumberGenerator.GetBytes(24));
        db.WfCredentials.Add(new WfCredential { Id = Guid.NewGuid(), EmployeeId = employeeId, Kind = CredentialKinds.Qr, Value = HashToken(token), CreatedAt = now });
        Audit(db, employeeId, actor, "credential.qr.rotate", null, null, now);
        return token;
    }, ct);

    /// <summary>Sets the kiosk PIN (4–8 digits). Stored as a salted PBKDF2 hash only.</summary>
    public Task<bool> SetPinAsync(WorkforceActor actor, Guid employeeId, string pin, CancellationToken ct = default) => InTransactionAsync(async (db, now) =>
    {
        await CredentialTargetAsync(db, actor, employeeId, ct);
        Require(pin is { Length: >= 4 and <= 8 } && pin.All(char.IsAsciiDigit), "wf.err.pin");
        RevokeAll(db, await db.WfCredentials.Where(c => c.EmployeeId == employeeId && c.Kind == CredentialKinds.Pin && c.IsActive).ToListAsync(ct), now);
        db.WfCredentials.Add(new WfCredential { Id = Guid.NewGuid(), EmployeeId = employeeId, Kind = CredentialKinds.Pin, Value = PasswordHasher.Hash(pin, pinIterations), CreatedAt = now });
        Audit(db, employeeId, actor, "credential.pin", null, null, now);
        return true;
    }, ct);

    /// <summary>
    /// Maps the employee to a user id on an external terminal. <paramref name="biometricEnrolled"/> records only that a fingerprint/face is
    /// enrolled ON that device — biometric templates are never stored or transferred by VELTRIX.
    /// </summary>
    public Task<WfCredential> MapTerminalUserAsync(WorkforceActor actor, Guid employeeId, Guid deviceId, string terminalUserId, bool biometricEnrolled = false,
        CancellationToken ct = default) => InTransactionAsync(async (db, now) =>
    {
        await CredentialTargetAsync(db, actor, employeeId, ct);
        var value = terminalUserId.Trim();
        Require(value.Length > 0, "wf.err.terminalUser");
        Require(await db.WfDevices.AnyAsync(d => d.Id == deviceId, ct), "wf.err.unknownDevice");
        Require(!await db.WfCredentials.AnyAsync(c => c.Kind == CredentialKinds.TerminalUser && c.DeviceId == deviceId && c.Value == value && c.IsActive && c.EmployeeId != employeeId, ct),
            "wf.err.terminalUserInUse");
        RevokeAll(db, await db.WfCredentials.Where(c => c.EmployeeId == employeeId && c.DeviceId == deviceId && c.IsActive
            && (c.Kind == CredentialKinds.TerminalUser || c.Kind == CredentialKinds.BiometricEnrollment)).ToListAsync(ct), now);
        var c = new WfCredential { Id = Guid.NewGuid(), EmployeeId = employeeId, Kind = CredentialKinds.TerminalUser, Value = value, DeviceId = deviceId, CreatedAt = now };
        db.WfCredentials.Add(c);
        if (biometricEnrolled)
            db.WfCredentials.Add(new WfCredential { Id = Guid.NewGuid(), EmployeeId = employeeId, Kind = CredentialKinds.BiometricEnrollment, Value = "enrolled", DeviceId = deviceId, CreatedAt = now });
        Audit(db, employeeId, actor, "credential.terminal", null, new { deviceId, terminalUserId = value, biometricEnrolled }, now);
        return c;
    }, ct);

    public Task<bool> RevokeCredentialAsync(WorkforceActor actor, Guid credentialId, CancellationToken ct = default) => InTransactionAsync(async (db, now) =>
    {
        AuthorizeGlobal(actor, WorkforcePermissions.CredentialsManage);
        var c = await db.WfCredentials.FindAsync([credentialId], ct);
        if (c is null || !c.IsActive) return false;
        c.IsActive = false;
        c.RevokedAt = now;
        Audit(db, c.EmployeeId, actor, "credential.revoke", new { c.Kind }, null, now);
        return true;
    }, ct);

    /// <summary>Credentials of an employee (hashes are never returned: Value is masked).</summary>
    public async Task<IReadOnlyList<WfCredential>> CredentialsAsync(WorkforceActor actor, Guid employeeId, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        await AuthorizeAsync(db, actor, WorkforcePermissions.EmployeesView, employeeId, ct);
        return (await db.WfCredentials.AsNoTracking().Where(c => c.EmployeeId == employeeId).ToListAsync(ct))
            .Select(c => { c.Value = c.Kind is CredentialKinds.Pin or CredentialKinds.Qr ? "********" : AccessEngine.Mask(c.Value); return c; }).ToList();
    }

    /// <summary>Resolves a presented credential to an employee (null when unknown or revoked).</summary>
    internal async Task<(LocalEmployee? Employee, WfCredential? Credential)> ResolveCredentialAsync(LocalDb db, string kind, string value, Guid? deviceId, CancellationToken ct)
    {
        WfCredential? cred = null;
        switch (kind)
        {
            case CredentialKinds.Card:
                var uid = NormalizeUid(value);
                cred = await db.WfCredentials.FirstOrDefaultAsync(c => c.Kind == CredentialKinds.Card && c.Value == uid && c.IsActive, ct);
                break;
            case CredentialKinds.Qr:
                var h = HashToken(value.Trim());
                cred = await db.WfCredentials.FirstOrDefaultAsync(c => c.Kind == CredentialKinds.Qr && c.Value == h && c.IsActive, ct);
                break;
            case CredentialKinds.TerminalUser:
                var tu = value.Trim();
                cred = await db.WfCredentials.FirstOrDefaultAsync(c => c.Kind == CredentialKinds.TerminalUser && c.Value == tu && c.IsActive && (deviceId == null || c.DeviceId == deviceId), ct);
                break;
            case CredentialKinds.Pin:
                var parts = value.Split(':', 2);
                if (parts.Length != 2) break;
                var code = parts[0].Trim();
                var emp = await db.Employees.FirstOrDefaultAsync(e => e.Code == code, ct);
                if (emp is null) { PasswordHasher.Verify(parts[1], PasswordHasher.Hash("0000", PasswordHasher.MinimumIterations)); break; }
                var pin = await db.WfCredentials.FirstOrDefaultAsync(c => c.EmployeeId == emp.Id && c.Kind == CredentialKinds.Pin && c.IsActive, ct);
                if (pin is not null && PasswordHasher.Verify(parts[1], pin.Value)) cred = pin;
                break;
        }
        if (cred is null) return (null, null);
        return (await db.Employees.FirstOrDefaultAsync(e => e.Id == cred.EmployeeId, ct), cred);
    }
}
