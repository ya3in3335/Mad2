namespace Veltrix.Desktop.Core.Data;

// Schema v5: VELTRIX Workforce & Attendance. Every table is local-first (SQLite) and included in encrypted backups.

public static class EmployeeStatuses
{
    public const string Active = "Active";
    public const string Suspended = "Suspended";
    public const string OnLeave = "OnLeave";
    public const string Terminated = "Terminated";
    public static readonly IReadOnlyList<string> All = [Active, Suspended, OnLeave, Terminated];
}

public sealed class WfDepartment
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public Guid? ParentId { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class WfPosition
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public Guid? DepartmentId { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class WfEmployeeDocument
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    /// <summary>Contract, IdCard, Diploma, Medical, Permit … (free text).</summary>
    public string DocType { get; set; } = "";
    /// <summary>Local file path (the file itself is not copied into the database).</summary>
    public string Path { get; set; } = "";
    public DateOnly? Expiry { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>Append-only history of every change to an employee (profile, credentials, documents).</summary>
public sealed class WfEmployeeAudit
{
    public long Seq { get; set; }
    public Guid EmployeeId { get; set; }
    public DateTimeOffset At { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string? OldJson { get; set; }
    public string? NewJson { get; set; }
}

public static class CredentialKinds
{
    /// <summary>NFC / RFID card UID (normalized upper-case hex).</summary>
    public const string Card = "Card";
    /// <summary>QR badge: only the SHA-256 of the random token is stored.</summary>
    public const string Qr = "Qr";
    /// <summary>PIN: salted PBKDF2 hash, never the PIN itself.</summary>
    public const string Pin = "Pin";
    /// <summary>User id of the employee on an external terminal (DeviceId set).</summary>
    public const string TerminalUser = "TerminalUser";
    /// <summary>"Biometric enrolled on device X" flag. No template is ever stored: matching happens on the device.</summary>
    public const string BiometricEnrollment = "BiometricEnrollment";
}

public sealed class WfCredential
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public string Kind { get; set; } = CredentialKinds.Card;
    /// <summary>Card UID, SHA-256(QR token), PIN hash or terminal user id — depending on <see cref="Kind"/>.</summary>
    public string Value { get; set; } = "";
    public Guid? DeviceId { get; set; }
    public string? Label { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public static class PunchTypes
{
    public const string In = "In";
    public const string Out = "Out";
    public const string BreakStart = "BreakStart";
    public const string BreakEnd = "BreakEnd";
    /// <summary>Compensating event that cancels <c>CompensatesEventId</c> (raw events are never edited or deleted).</summary>
    public const string Void = "Void";
    public static readonly IReadOnlyList<string> Punches = [In, Out, BreakStart, BreakEnd];
}

public static class PunchSources
{
    public const string Card = "Card";
    public const string Qr = "QR";
    public const string Pin = "PIN";
    public const string Manual = "Manual";
    public const string Terminal = "Terminal";
    public const string Kiosk = "Kiosk";
    public const string Correction = "Correction";
}

/// <summary>Raw attendance event. Append-only and hash-chained: Hash = SHA-256(PrevHash + canonical event).</summary>
public sealed class WfAttendanceEvent
{
    public long Seq { get; set; }
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public string Type { get; set; } = PunchTypes.In;
    public string Source { get; set; } = PunchSources.Manual;
    public Guid? DeviceId { get; set; }
    /// <summary>Device-side event id (idempotency for terminal imports).</summary>
    public string? DeviceEventId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    /// <summary>UTC ticks of <see cref="OccurredAtUtc"/> (indexed range queries).</summary>
    public long OccurredTicks { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    /// <summary>UTC offset of the workplace when the event happened (local = UTC + offset).</summary>
    public int LocalOffsetMinutes { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? Reason { get; set; }
    public string? AuthorizedBy { get; set; }
    public Guid? CorrectionId { get; set; }
    public Guid? CompensatesEventId { get; set; }
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";
}

public static class RequestStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";
}

public static class CorrectionKinds
{
    public const string AddPunch = "AddPunch";
    public const string VoidPunch = "VoidPunch";
}

public sealed class WfCorrectionRequest
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public string Kind { get; set; } = CorrectionKinds.AddPunch;
    public Guid? TargetEventId { get; set; }
    public string? PunchType { get; set; }
    public DateTimeOffset? OccurredAtUtc { get; set; }
    public string Reason { get; set; } = "";
    public string RequestedBy { get; set; } = "";
    public DateTimeOffset RequestedAt { get; set; }
    public string Status { get; set; } = RequestStatuses.Pending;
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
}

public sealed class WfShiftTemplate
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>JSON list of segments <c>[{"start":"08:00","end":"12:00"},…]</c>. An end ≤ start crosses midnight.</summary>
    public string SegmentsJson { get; set; } = "[]";
    /// <summary>Unpaid break minutes deducted when the employee did not punch breaks.</summary>
    public int BreakMinutes { get; set; }
    public int GraceInMinutes { get; set; }
    public int GraceOutMinutes { get; set; }
    public bool IsFlexible { get; set; }
    public TimeOnly? CoreStart { get; set; }
    public TimeOnly? CoreEnd { get; set; }
    /// <summary>Flexible shifts: minutes required per day.</summary>
    public int RequiredMinutes { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class WfRotation
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>JSON list of template ids (null = rest day), one per day of the cycle.</summary>
    public string PatternJson { get; set; } = "[]";
}

public sealed class WfShiftAssignment
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public DateOnly From { get; set; }
    public DateOnly? To { get; set; }
    public Guid? TemplateId { get; set; }
    public Guid? RotationId { get; set; }
    /// <summary>Day 0 of the rotation cycle.</summary>
    public DateOnly? RotationAnchor { get; set; }
    /// <summary>Day-of-week mask for fixed templates (bit 0 = Sunday); 0 = every day.</summary>
    public int DaysMask { get; set; }
}

/// <summary>Single-day override (shift swaps, ad-hoc changes). TemplateId null = rest day.</summary>
public sealed class WfShiftOverride
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public DateOnly Date { get; set; }
    public Guid? TemplateId { get; set; }
    public string Source { get; set; } = "Manual";
    public Guid? SourceId { get; set; }
}

/// <summary>User-entered holiday calendar (no official dates are shipped).</summary>
public sealed class WfHoliday
{
    public Guid Id { get; set; }
    public DateOnly Date { get; set; }
    public string Name { get; set; } = "";
    public bool IsPaid { get; set; } = true;
}

public sealed class WfSwapRequest
{
    public Guid Id { get; set; }
    public Guid RequesterId { get; set; }
    public DateOnly RequesterDate { get; set; }
    public Guid CounterpartId { get; set; }
    public DateOnly CounterpartDate { get; set; }
    public string? Reason { get; set; }
    public string RequestedBy { get; set; } = "";
    public DateTimeOffset RequestedAt { get; set; }
    public string Status { get; set; } = RequestStatuses.Pending;
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

public sealed class WfLeaveType
{
    public Guid Id { get; set; }
    /// <summary>annual, sick, unpaid, exceptional or any custom code.</summary>
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsPaid { get; set; } = true;
    /// <summary>Days credited per month by <c>AccrueLeaveAsync</c>; 0 = not accrued (no balance check).</summary>
    public decimal AccrualDaysPerMonth { get; set; }
    /// <summary>Requests are checked against the balance (accruals − taken ± adjustments).</summary>
    public bool TracksBalance { get; set; }
    public bool AllowNegative { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class WfLeaveRequest
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid LeaveTypeId { get; set; }
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public decimal Days { get; set; }
    public string? Reason { get; set; }
    public string? AttachmentPath { get; set; }
    public string RequestedBy { get; set; } = "";
    public DateTimeOffset RequestedAt { get; set; }
    public string Status { get; set; } = RequestStatuses.Pending;
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>Leave balance movements: accruals (+), approved leave (−), manual adjustments (±). Balance = sum.</summary>
public sealed class WfLeaveLedger
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid LeaveTypeId { get; set; }
    public DateOnly Date { get; set; }
    public decimal Days { get; set; }
    /// <summary>Accrual, Taken, Adjustment.</summary>
    public string Kind { get; set; } = "";
    /// <summary>Idempotency key (e.g. accrual:2026-10, leave:{id}).</summary>
    public string RefKey { get; set; } = "";
    public string? Note { get; set; }
}

public static class PayItemKinds
{
    public const string Bonus = "Bonus";
    public const string Allowance = "Allowance";
    /// <summary>Paid per day worked on <c>TemplateId</c> (or per day worked when null).</summary>
    public const string ShiftPremium = "ShiftPremium";
    public const string Deduction = "Deduction";
}

public sealed class WfPayItem
{
    public Guid Id { get; set; }
    /// <summary>Null = applies to every employee.</summary>
    public Guid? EmployeeId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = PayItemKinds.Allowance;
    public decimal Amount { get; set; }
    /// <summary>Amount is a percentage of the base salary.</summary>
    public bool IsPercentOfBase { get; set; }
    public Guid? TemplateId { get; set; }
    public DateOnly From { get; set; }
    public DateOnly? To { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// User-entered statutory rule (social security, income tax …). VELTRIX ships NO rates: rows are created by the user, disabled by default,
/// and must be verified against official sources before being enabled.
/// </summary>
public sealed class WfStatutoryRule
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int Order { get; set; }
    /// <summary>Flat employee rate (%) on the base; ignored when <see cref="BracketsJson"/> is set.</summary>
    public decimal EmployeeRatePercent { get; set; }
    public decimal EmployerRatePercent { get; set; }
    /// <summary>Optional progressive table <c>[{"from":0,"to":30000,"ratePercent":0},…]</c> applied to the base.</summary>
    public string? BracketsJson { get; set; }
    /// <summary>When true the base is gross minus the employee deductions of the rules ordered before this one.</summary>
    public bool BaseAfterPreviousRules { get; set; }
    public bool IsEnabled { get; set; }
    public string? Notes { get; set; }
}

public sealed class WfPayrollPrep
{
    public Guid Id { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    /// <summary>Draft or Posted.</summary>
    public string Status { get; set; } = "Draft";
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public Guid? PayrollRunId { get; set; }
}

public sealed class WfPayrollPrepLine
{
    public Guid Id { get; set; }
    public Guid PrepId { get; set; }
    public Guid EmployeeId { get; set; }
    public string EmployeeName { get; set; } = "";
    public decimal BaseSalary { get; set; }
    public int WorkedMinutes { get; set; }
    public int OvertimeMinutes { get; set; }
    public decimal OvertimeAmount { get; set; }
    public decimal NightPremium { get; set; }
    public decimal AbsenceDays { get; set; }
    public decimal AbsenceDeduction { get; set; }
    public int LateMinutes { get; set; }
    public decimal LateDeduction { get; set; }
    public decimal Bonuses { get; set; }
    public decimal Allowances { get; set; }
    public decimal ShiftPremiums { get; set; }
    public decimal OtherDeductions { get; set; }
    public decimal Gross { get; set; }
    public decimal NetBeforeStatutory { get; set; }
    public decimal StatutoryEmployee { get; set; }
    public decimal StatutoryEmployer { get; set; }
    public decimal Net { get; set; }
}

public static class DeviceKinds
{
    public const string Virtual = "Virtual";
    public const string KeyboardWedge = "KeyboardWedge";
    public const string Serial = "Serial";
    public const string TcpLine = "TcpLine";
    /// <summary>Vendor terminal (ZKTeco, Hikvision, Suprema, Anviz …): needs the vendor SDK / protocol; no adapter yet.</summary>
    public const string Vendor = "Vendor";
}

public sealed class WfDevice
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = DeviceKinds.Virtual;
    /// <summary>Adapter settings (port, baud, host, regex …) as JSON.</summary>
    public string ConfigJson { get; set; } = "{}";
    /// <summary>Punch type forced by the device: In, Out or Auto (toggle).</summary>
    public string Direction { get; set; } = "Auto";
    /// <summary>Door controlled / guarded by this device (access-control mode).</summary>
    public Guid? DoorId { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset? LastSeenAt { get; set; }
    public int ErrorCount { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Device events persisted before processing (power-failure safe); unique per (DeviceId, DeviceEventId).</summary>
public sealed class WfDeviceInbox
{
    public long Seq { get; set; }
    public Guid DeviceId { get; set; }
    public string DeviceEventId { get; set; } = "";
    public string CredentialKind { get; set; } = "";
    public string CredentialValue { get; set; } = "";
    public string? PunchType { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    /// <summary>Pending, Processed, Rejected.</summary>
    public string Status { get; set; } = "Pending";
    public string? Result { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}

public static class AntiPassbackModes
{
    public const string None = "None";
    /// <summary>Violation is logged but access granted.</summary>
    public const string Soft = "Soft";
    /// <summary>Violation denies access.</summary>
    public const string Hard = "Hard";
}

public sealed class WfZone
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string AntiPassback { get; set; } = AntiPassbackModes.None;
}

public sealed class WfDoor
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Guid ZoneId { get; set; }
    /// <summary>In = entering the zone, Out = leaving it.</summary>
    public string Direction { get; set; } = "In";
    public Guid? ControllerDeviceId { get; set; }
    public int UnlockSeconds { get; set; } = 5;
}

public sealed class WfAccessGroup
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Weekly windows <c>[{"day":1,"start":"08:00","end":"18:00"}]</c> (day 0 = Sunday). Empty = always.</summary>
    public string ScheduleJson { get; set; } = "[]";
    /// <summary>JSON list of door ids this group opens.</summary>
    public string DoorIdsJson { get; set; } = "[]";
    public bool AllowOnHolidays { get; set; }
}

public sealed class WfAccessGroupMember
{
    public Guid GroupId { get; set; }
    public Guid EmployeeId { get; set; }
}

public sealed class WfZonePresence
{
    public Guid EmployeeId { get; set; }
    public Guid ZoneId { get; set; }
    public bool IsInside { get; set; }
    public DateTimeOffset At { get; set; }
}

public sealed class WfAccessLog
{
    public long Seq { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid? DoorId { get; set; }
    public Guid? EmployeeId { get; set; }
    public string CredentialKind { get; set; } = "";
    /// <summary>Masked credential (last 4 characters only).</summary>
    public string CredentialHint { get; set; } = "";
    public bool Granted { get; set; }
    public string Reason { get; set; } = "";
    public string? Actor { get; set; }
}
