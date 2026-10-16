using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Veltrix.Desktop.Core.Data;
using Veltrix.Desktop.Core.Erp;
using Veltrix.Desktop.Core.Standalone;
using Veltrix.Desktop.Core.Workforce;

namespace Veltrix.Desktop.Tests;

/// <summary>VELTRIX Workforce &amp; Attendance core engine: employees, punches, shifts, leave, corrections, payroll preparation, RBAC, migration, backup.</summary>
public sealed class WorkforceTests : IAsyncLifetime
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 12, 6, 0, 0, TimeSpan.Zero)); // Monday
    private static readonly WorkforceActor Owner = new("owner", LocalRoles.Owner);
    private static readonly DateOnly Mon = new(2026, 10, 12);
    private const int WeekdaysMask = 0b0111110; // Mon..Fri

    private LocalDb Db() => new(new DbContextOptionsBuilder<LocalDb>().UseSqlite(_conn).Options);
    private WorkforceService Wf() => new(Db, _time, PasswordHasher.MinimumIterations);

    public async Task InitializeAsync()
    {
        await _conn.OpenAsync();
        await using var db = Db();
        await db.InitializeAsync();
    }

    public async Task DisposeAsync() => await _conn.DisposeAsync();

    private async Task<LocalEmployee> EmployeeAsync(string name, Guid? dept = null, decimal salary = 30000, Guid? manager = null) =>
        await Wf().UpsertEmployeeAsync(Owner, null, new EmployeeProfileInput(name, salary, new DateOnly(2026, 1, 1)) { DepartmentId = dept, ManagerId = manager });

    private async Task<WfShiftTemplate> TemplateAsync(string code, int breakMin, int graceIn, int graceOut, params (int h1, int m1, int h2, int m2)[] segs) =>
        await Wf().UpsertShiftTemplateAsync(Owner, null, new ShiftTemplateInput(code, code, segs.Select(s => new ShiftSegment(new TimeOnly(s.h1, s.m1), new TimeOnly(s.h2, s.m2))).ToList(),
            breakMin, graceIn, graceOut));

    private Task<PunchResult> PunchAsync(Guid employee, string type, DateOnly day, int h, int m) =>
        Wf().RecordPunchAsync(Owner, new PunchInput(employee, type, PunchSources.Manual, new DateTimeOffset(day.ToDateTime(new TimeOnly(h, m)), TimeSpan.Zero), Reason: "test", AuthorizedBy: "boss"));

    private async Task<DayResult> DayAsync(Guid employee, DateOnly day) => (await Wf().ComputeAttendanceAsync(Owner, day, day, employee)).Single();

    // ------------------------------------------------------------------ employees

    [Fact]
    public async Task Employee_crud_records_full_audit_history()
    {
        var wf = Wf();
        var dept = await wf.UpsertDepartmentAsync(Owner, null, "Production");
        var e = await wf.UpsertEmployeeAsync(Owner, null, new EmployeeProfileInput("Amine", 40000, new DateOnly(2026, 2, 1))
        {
            DepartmentId = dept.Id, ContractType = "CDI", EmergencyContactName = "Sara", Workplace = "Oran",
        });
        Assert.Equal("E0001", e.Code);
        await wf.UpsertEmployeeAsync(Owner, e.Id, new EmployeeProfileInput("Amine B.", 42000, new DateOnly(2026, 2, 1)) { DepartmentId = dept.Id, Status = EmployeeStatuses.Suspended });
        await wf.AddDocumentAsync(Owner, e.Id, new EmployeeDocumentInput("IdCard", @"C:\docs\id.pdf", new DateOnly(2030, 1, 1)));

        var history = await wf.EmployeeHistoryAsync(Owner, e.Id);
        Assert.Equal(["create", "update", "document.add"], history.Select(h => h.Action));
        Assert.Contains("Amine", history[1].OldJson);
        Assert.Contains("Amine B.", history[1].NewJson);
        Assert.Contains("42000", history[1].NewJson);
        Assert.All(history, h => Assert.Equal("owner", h.Actor));
        var reloaded = await wf.EmployeeAsync(Owner, e.Id);
        Assert.Equal(EmployeeStatuses.Suspended, reloaded!.Status);
        Assert.False(reloaded.IsActive is false); // suspended employees stay on the books
        Assert.Single(await wf.DocumentsAsync(Owner, expiringBefore: new DateOnly(2031, 1, 1)));

        // Delete is allowed only without history; the audit trail survives.
        var temp = await EmployeeAsync("Temp");
        Assert.True(await wf.DeleteEmployeeAsync(Owner, temp.Id));
        Assert.Equal("delete", (await wf.EmployeeHistoryAsync(Owner, temp.Id)).Last().Action);
        Assert.Equal(PunchOutcomes.Rejected, (await PunchAsync(e.Id, PunchTypes.In, Mon, 8, 0)).Outcome); // suspended
        await wf.UpsertEmployeeAsync(Owner, e.Id, new EmployeeProfileInput("Amine B.", 42000, new DateOnly(2026, 2, 1)));
        await PunchAsync(e.Id, PunchTypes.In, Mon, 8, 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => wf.DeleteEmployeeAsync(Owner, e.Id));
    }

    [Fact]
    public async Task Suspended_employee_cannot_punch()
    {
        var e = await Wf().UpsertEmployeeAsync(Owner, null, new EmployeeProfileInput("S", 1, Mon) { Status = EmployeeStatuses.Suspended });
        var r = await PunchAsync(e.Id, PunchTypes.In, Mon, 8, 0);
        Assert.Equal(PunchOutcomes.Rejected, r.Outcome);
    }

    // ------------------------------------------------------------------ punches

    [Fact]
    public async Task Card_check_in_out_with_auto_toggle_and_duplicate_scan_protection()
    {
        var wf = Wf();
        var e = await EmployeeAsync("Karim");
        await wf.AssignCardAsync(Owner, e.Id, "04:a2:b3:c4");
        _time.Set(Mon, 7, 58);
        var first = await wf.PunchByCredentialAsync(CredentialKinds.Card, "04A2B3C4", source: PunchSources.Card);
        Assert.Equal((PunchOutcomes.Recorded, PunchTypes.In), (first.Outcome, first.Event!.Type));
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(PunchOutcomes.Duplicate, (await wf.PunchByCredentialAsync(CredentialKinds.Card, "04-A2-B3-C4")).Outcome);
        _time.Set(Mon, 16, 2);
        var last = await wf.PunchByCredentialAsync(CredentialKinds.Card, "04A2B3C4");
        Assert.Equal(PunchTypes.Out, last.Event!.Type);
        Assert.Equal(PunchOutcomes.Rejected, (await wf.PunchByCredentialAsync(CredentialKinds.Card, "FFFF0000")).Outcome);

        var t = await TemplateAsync("D", 60, 5, 5, (8, 0, 16, 0));
        await wf.AssignShiftAsync(Owner, new AssignmentInput(e.Id, Mon, null, t.Id));
        var day = await DayAsync(e.Id, Mon);
        Assert.Equal(DayStatuses.Present, day.Status);
        Assert.Equal(484 - 60, day.WorkedMinutes);
        Assert.Equal((0, 0, false), (day.LateMinutes, day.EarlyLeaveMinutes, day.MissedPunch));
    }

    [Fact]
    public async Task Pin_and_qr_credentials_are_hashed_and_rotatable()
    {
        var wf = Wf();
        var e = await EmployeeAsync("Nadia");
        await wf.SetPinAsync(Owner, e.Id, "4821");
        var token = await wf.IssueQrTokenAsync(Owner, e.Id);
        await using (var db = Db())
        {
            var creds = await db.WfCredentials.AsNoTracking().ToListAsync();
            Assert.DoesNotContain(creds, c => c.Value.Contains("4821") || c.Value == token);
            Assert.StartsWith("pbkdf2-sha256$", creds.Single(c => c.Kind == CredentialKinds.Pin).Value);
        }
        Assert.True((await wf.PunchByCredentialAsync(CredentialKinds.Pin, e.Code + ":4821")).IsRecorded);
        Assert.Equal(PunchOutcomes.Rejected, (await wf.PunchByCredentialAsync(CredentialKinds.Pin, e.Code + ":0000")).Outcome);
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.True((await wf.PunchByCredentialAsync(CredentialKinds.Qr, token)).IsRecorded);
        var rotated = await wf.IssueQrTokenAsync(Owner, e.Id);
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(PunchOutcomes.Rejected, (await wf.PunchByCredentialAsync(CredentialKinds.Qr, token)).Outcome);
        Assert.True((await wf.PunchByCredentialAsync(CredentialKinds.Qr, rotated)).IsRecorded);
        Assert.All(await wf.CredentialsAsync(Owner, e.Id), c => Assert.DoesNotContain("pbkdf2", c.Value));
    }

    [Fact]
    public async Task Manual_punch_requires_reason_and_authorizer()
    {
        var e = await EmployeeAsync("M");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Wf().RecordPunchAsync(Owner, new PunchInput(e.Id, PunchTypes.In, PunchSources.Manual, Reason: "x")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Wf().RecordPunchAsync(Owner, new PunchInput(e.Id, PunchTypes.In, PunchSources.Manual, AuthorizedBy: "x")));
        var ok = await Wf().RecordPunchAsync(Owner, new PunchInput(e.Id, PunchTypes.In, PunchSources.Manual, Reason: "forgot card", AuthorizedBy: "chief"));
        Assert.Equal(("owner", "forgot card", "chief"), (ok.Event!.CreatedBy, ok.Event.Reason, ok.Event.AuthorizedBy));
    }

    // ------------------------------------------------------------------ daily computation

    [Fact]
    public async Task Late_early_absence_missed_punch_holiday_and_rest_days()
    {
        var wf = Wf();
        var e = await EmployeeAsync("Yacine");
        var t = await TemplateAsync("D", 60, 5, 5, (8, 0, 16, 0));
        await wf.AssignShiftAsync(Owner, new AssignmentInput(e.Id, Mon, null, t.Id, DaysMask: WeekdaysMask));
        await wf.AddHolidayAsync(Owner, Mon.AddDays(3), "Local holiday");
        await PunchAsync(e.Id, PunchTypes.In, Mon, 8, 20);
        await PunchAsync(e.Id, PunchTypes.Out, Mon, 15, 30);
        await PunchAsync(e.Id, PunchTypes.In, Mon.AddDays(2), 8, 0);
        await PunchAsync(e.Id, PunchTypes.In, Mon.AddDays(4), 8, 3);
        await PunchAsync(e.Id, PunchTypes.Out, Mon.AddDays(4), 15, 57);

        var days = (await wf.ComputeAttendanceAsync(Owner, Mon, Mon.AddDays(6), e.Id)).ToDictionary(d => d.Date);
        Assert.Equal((DayStatuses.Present, 20, 30), (days[Mon].Status, days[Mon].LateMinutes, days[Mon].EarlyLeaveMinutes));
        Assert.Equal(430 - 60, days[Mon].WorkedMinutes);
        Assert.Equal(DayStatuses.Absent, days[Mon.AddDays(1)].Status);
        Assert.Equal(DayStatuses.Incomplete, days[Mon.AddDays(2)].Status);
        Assert.True(days[Mon.AddDays(2)].MissedPunch);
        Assert.Contains("missing_out", days[Mon.AddDays(2)].Anomalies);
        Assert.Equal(DayStatuses.Holiday, days[Mon.AddDays(3)].Status);
        Assert.Equal((DayStatuses.Present, 0, 0), (days[Mon.AddDays(4)].Status, days[Mon.AddDays(4)].LateMinutes, days[Mon.AddDays(4)].EarlyLeaveMinutes)); // within grace
        Assert.Equal(DayStatuses.Rest, days[Mon.AddDays(5)].Status);
        Assert.Equal(DayStatuses.Rest, days[Mon.AddDays(6)].Status);
    }

    [Fact]
    public async Task Overnight_shift_is_attributed_to_its_start_day()
    {
        var wf = Wf();
        var e = await EmployeeAsync("Night");
        var n = await TemplateAsync("N", 0, 5, 5, (22, 0, 6, 0));
        await wf.AssignShiftAsync(Owner, new AssignmentInput(e.Id, Mon, Mon, n.Id));
        await PunchAsync(e.Id, PunchTypes.In, Mon, 21, 55);
        await PunchAsync(e.Id, PunchTypes.Out, Mon.AddDays(1), 6, 40);

        var days = (await wf.ComputeAttendanceAsync(Owner, Mon, Mon.AddDays(1), e.Id)).ToDictionary(d => d.Date);
        var d = days[Mon];
        Assert.Equal(DayStatuses.Present, d.Status);
        Assert.Equal(525, d.WorkedMinutes);
        Assert.Equal(30, d.OvertimeMinutes); // 40 min after the 06:00 end, rounded down to the 15-min step
        Assert.Equal(425, d.NightMinutes); // 21:55 → 05:00 within the 21:00–05:00 night window
        Assert.Equal(0, d.LateMinutes);
        Assert.Equal(DayStatuses.Rest, days[Mon.AddDays(1)].Status); // the 06:40 Out does not leak to Tuesday
    }

    [Fact]
    public async Task Split_and_flexible_shifts()
    {
        var wf = Wf();
        var e = await EmployeeAsync("Split");
        var s = await TemplateAsync("S", 0, 5, 5, (8, 0, 12, 0), (14, 0, 18, 0));
        await wf.AssignShiftAsync(Owner, new AssignmentInput(e.Id, Mon, Mon.AddDays(1), s.Id));
        await PunchAsync(e.Id, PunchTypes.In, Mon, 8, 0);
        await PunchAsync(e.Id, PunchTypes.Out, Mon, 12, 0);
        await PunchAsync(e.Id, PunchTypes.In, Mon, 14, 10);
        await PunchAsync(e.Id, PunchTypes.Out, Mon, 18, 0);
        await PunchAsync(e.Id, PunchTypes.In, Mon.AddDays(1), 8, 0);
        await PunchAsync(e.Id, PunchTypes.Out, Mon.AddDays(1), 12, 0);
        var days = (await wf.ComputeAttendanceAsync(Owner, Mon, Mon.AddDays(1), e.Id)).ToDictionary(d => d.Date);
        Assert.Equal((DayStatuses.Present, 470, 10, 0), (days[Mon].Status, days[Mon].WorkedMinutes, days[Mon].LateMinutes, days[Mon].OvertimeMinutes));
        Assert.Contains("segment_absent", days[Mon.AddDays(1)].Anomalies);
        Assert.Equal(240, days[Mon.AddDays(1)].ShortfallMinutes);

        var f = await wf.UpsertShiftTemplateAsync(Owner, null, new ShiftTemplateInput("F", "Flex", [new ShiftSegment(new TimeOnly(7, 0), new TimeOnly(19, 0))], 0, 0, 0,
            IsFlexible: true, CoreStart: new TimeOnly(10, 0), CoreEnd: new TimeOnly(15, 0), RequiredMinutes: 480));
        var g = await EmployeeAsync("Flex");
        await wf.AssignShiftAsync(Owner, new AssignmentInput(g.Id, Mon, null, f.Id));
        await PunchAsync(g.Id, PunchTypes.In, Mon, 9, 0);
        await PunchAsync(g.Id, PunchTypes.Out, Mon, 18, 30);
        await PunchAsync(g.Id, PunchTypes.In, Mon.AddDays(1), 10, 30);
        await PunchAsync(g.Id, PunchTypes.Out, Mon.AddDays(1), 17, 0);
        var flex = (await wf.ComputeAttendanceAsync(Owner, Mon, Mon.AddDays(1), g.Id)).ToDictionary(d => d.Date);
        Assert.Equal((570, 0, 90), (flex[Mon].WorkedMinutes, flex[Mon].LateMinutes, flex[Mon].OvertimeMinutes));
        Assert.Equal((390, 30, 90, 0), (flex[Mon.AddDays(1)].WorkedMinutes, flex[Mon.AddDays(1)].LateMinutes, flex[Mon.AddDays(1)].ShortfallMinutes, flex[Mon.AddDays(1)].OvertimeMinutes));
    }

    [Fact]
    public void Overtime_rounding_minimum_and_threshold_policies()
    {
        var p = new OvertimePolicy { RoundingStepMinutes = 15, MinimumMinutes = 15 };
        Assert.Equal(45, AttendanceCalculator.ApplyOvertimeRules(52, p));
        Assert.Equal(60, AttendanceCalculator.ApplyOvertimeRules(52, p with { Rounding = RoundingMode.Up }));
        Assert.Equal(45, AttendanceCalculator.ApplyOvertimeRules(52, p with { Rounding = RoundingMode.Nearest }));
        Assert.Equal(60, AttendanceCalculator.ApplyOvertimeRules(53, p with { Rounding = RoundingMode.Nearest }));
        Assert.Equal(0, AttendanceCalculator.ApplyOvertimeRules(10, p));
        Assert.Equal(0, AttendanceCalculator.ApplyOvertimeRules(20, p with { MinimumMinutes = 30 }));

        var shift = new ShiftDefinition(Guid.NewGuid(), "D", "D", [new ShiftSegment(new TimeOnly(8, 0), new TimeOnly(16, 0))], 0, 0, 0);
        var plan = new PlannedDay(Guid.NewGuid(), Mon, shift, shift.Place(Mon), false);
        Punch P(string type, int h, int m) => new(Guid.NewGuid(), type, Mon.ToDateTime(new TimeOnly(h, m)));
        var policy = new WorkforcePolicy { Overtime = new OvertimePolicy { Basis = OvertimeBasis.DailyThreshold, DailyThresholdMinutes = 420, RoundingStepMinutes = 0, MinimumMinutes = 0 } };
        var r = AttendanceCalculator.ComputeDay(plan, [P(PunchTypes.In, 6, 0), P(PunchTypes.BreakStart, 12, 0), P(PunchTypes.BreakEnd, 12, 30), P(PunchTypes.Out, 16, 0)], policy);
        Assert.Equal((570, 30, 150), (r.WorkedMinutes, r.BreakMinutes, r.OvertimeMinutes));
        var restPlan = new PlannedDay(plan.EmployeeId, Mon, null, [], false);
        Assert.Equal(120, AttendanceCalculator.ComputeDay(restPlan, [P(PunchTypes.In, 9, 0), P(PunchTypes.Out, 11, 0)], policy).OvertimeMinutes); // rest day: all overtime
    }

    // ------------------------------------------------------------------ corrections & tamper evidence

    [Fact]
    public async Task Approved_corrections_append_compensating_events_and_hash_chain_detects_tampering()
    {
        var wf = Wf();
        var dept = await wf.UpsertDepartmentAsync(Owner, null, "Ops");
        var manager = await EmployeeAsync("Manager", dept.Id);
        var e = await EmployeeAsync("Worker", dept.Id);
        var t = await TemplateAsync("D", 60, 5, 5, (8, 0, 16, 0));
        await wf.AssignShiftAsync(Owner, new AssignmentInput(e.Id, Mon, null, t.Id));
        await PunchAsync(e.Id, PunchTypes.In, Mon, 8, 0);
        var wrong = (await PunchAsync(e.Id, PunchTypes.Out, Mon, 12, 0)).Event!;
        List<WfAttendanceEvent> before;
        await using (var db = Db()) before = await db.WfAttendanceEvents.AsNoTracking().OrderBy(x => x.Seq).ToListAsync();

        var supervisor = new WorkforceActor("sup", LocalRoles.AttendanceSupervisor);
        var mgr = new WorkforceActor("mgr", LocalRoles.DeptManager, manager.Id);
        var v = await wf.RequestCorrectionAsync(supervisor, new CorrectionInput(e.Id, CorrectionKinds.VoidPunch, "badge misread", TargetEventId: wrong.Id));
        var a = await wf.RequestCorrectionAsync(supervisor, new CorrectionInput(e.Id, CorrectionKinds.AddPunch, "left at 16:00", PunchTypes.Out,
            new DateTimeOffset(Mon.ToDateTime(new TimeOnly(16, 0)), TimeSpan.Zero)));
        await Assert.ThrowsAsync<WorkforceAccessDeniedException>(() => wf.DecideCorrectionAsync(supervisor, v.Id, true)); // supervisors cannot approve
        await wf.DecideCorrectionAsync(mgr, v.Id, true);
        await wf.DecideCorrectionAsync(mgr, a.Id, true, "ok");
        await Assert.ThrowsAsync<InvalidOperationException>(() => wf.DecideCorrectionAsync(mgr, a.Id, true));

        await using (var db = Db())
        {
            var all = await db.WfAttendanceEvents.AsNoTracking().OrderBy(x => x.Seq).ToListAsync();
            Assert.Equal(4, all.Count);
            foreach (var o in before) Assert.Equivalent(o, all.Single(x => x.Seq == o.Seq)); // raw events untouched
            Assert.Equal(PunchTypes.Void, all[2].Type);
            Assert.Equal((wrong.Id, "mgr", "sup"), (all[2].CompensatesEventId!.Value, all[2].AuthorizedBy, all[2].CreatedBy));
        }
        var day = await DayAsync(e.Id, Mon);
        Assert.Equal((DayStatuses.Present, 420, 0), (day.Status, day.WorkedMinutes, day.EarlyLeaveMinutes));
        Assert.True((await wf.VerifyChainAsync(Owner)).IsValid);

        // A manager cannot approve their own correction.
        await PunchAsync(manager.Id, PunchTypes.In, Mon, 9, 0);
        var own = await wf.RequestCorrectionAsync(mgr, new CorrectionInput(manager.Id, CorrectionKinds.AddPunch, "forgot", PunchTypes.Out, _time.GetUtcNow()));
        await Assert.ThrowsAsync<WorkforceAccessDeniedException>(() => wf.DecideCorrectionAsync(mgr, own.Id, true));

        await using (var db = Db()) await db.Database.ExecuteSqlRawAsync("UPDATE \"WfAttendanceEvents\" SET \"OccurredTicks\" = \"OccurredTicks\" + 600000000 WHERE \"Seq\" = 2;");
        var check = await wf.VerifyChainAsync(Owner);
        Assert.False(check.IsValid);
        Assert.Equal(2, check.BrokenAtSeq);
    }

    // ------------------------------------------------------------------ shifts

    [Fact]
    public async Task Rest_period_overlap_and_assignment_conflicts_are_detected()
    {
        var wf = Wf();
        var e = await EmployeeAsync("Conf");
        var n = await TemplateAsync("N", 0, 0, 0, (22, 0, 6, 0));
        var d = await TemplateAsync("D", 0, 0, 0, (8, 0, 16, 0));
        var early = await TemplateAsync("E", 0, 0, 0, (5, 0, 13, 0));
        await wf.SetShiftOverrideAsync(Owner, e.Id, Mon, n.Id);
        await wf.SetShiftOverrideAsync(Owner, e.Id, Mon.AddDays(1), d.Id);
        var c = await wf.DetectConflictsAsync(Owner, Mon, Mon.AddDays(1));
        Assert.Equal(ConflictKinds.RestViolation, Assert.Single(c).Kind);
        await wf.SetShiftOverrideAsync(Owner, e.Id, Mon.AddDays(1), early.Id);
        Assert.Equal(ConflictKinds.Overlap, Assert.Single(await wf.DetectConflictsAsync(Owner, Mon, Mon.AddDays(1))).Kind);

        var g = await EmployeeAsync("Assign");
        await wf.AssignShiftAsync(Owner, new AssignmentInput(g.Id, Mon, Mon.AddDays(30), n.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => wf.AssignShiftAsync(Owner, new AssignmentInput(g.Id, Mon.AddDays(10), null, d.Id)));
        var r = await wf.AssignShiftAsync(Owner, new AssignmentInput(g.Id, Mon.AddDays(10), null, d.Id), replaceOverlaps: true);
        Assert.DoesNotContain(r.Conflicts, x => x.Kind == ConflictKinds.AssignmentOverlap);
        var cal = (await wf.CalendarAsync(Owner, Mon.AddDays(8), Mon.AddDays(11), g.Id)).ToDictionary(p => p.Date);
        Assert.Equal(("N", "D"), (cal[Mon.AddDays(9)].Shift!.Code, cal[Mon.AddDays(10)].Shift!.Code));
        // Returned as a warning: the night shift ends at 06:00 and the day shift starts at 08:00.
        Assert.Equal((ConflictKinds.RestViolation, Mon.AddDays(10)), (Assert.Single(r.Conflicts).Kind, r.Conflicts[0].Date));
        Assert.Equal(ConflictKinds.RestViolation, Assert.Single(await wf.DetectConflictsAsync(Owner, Mon.AddDays(10), Mon.AddDays(10)), x => x.EmployeeId == g.Id).Kind);
    }

    [Fact]
    public async Task Rotation_and_weekly_monthly_calendar()
    {
        var wf = Wf();
        var e = await EmployeeAsync("Rot");
        var m = await TemplateAsync("M", 0, 0, 0, (6, 0, 14, 0));
        var a = await TemplateAsync("A", 0, 0, 0, (14, 0, 22, 0));
        var rot = await wf.UpsertRotationAsync(Owner, null, "2M-2A-2R", [m.Id, m.Id, a.Id, a.Id, null, null]);
        await wf.AssignShiftAsync(Owner, new AssignmentInput(e.Id, Mon, null, RotationId: rot.Id, RotationAnchor: Mon));
        var (wFrom, wTo) = ShiftPlanner.Week(Mon.AddDays(3));
        Assert.Equal((Mon, Mon.AddDays(6)), (wFrom, wTo));
        var week = await wf.CalendarAsync(Owner, wFrom, wTo, e.Id);
        Assert.Equal(["M", "M", "A", "A", null, null, "M"], week.Select(p => p.Shift?.Code));
        var (mFrom, mTo) = ShiftPlanner.Month(2026, 10);
        Assert.Equal(31, (await wf.CalendarAsync(Owner, mFrom, mTo, e.Id)).Count);
    }

    [Fact]
    public async Task Shift_swap_requires_approval_and_respects_rest_rules()
    {
        var wf = Wf();
        var d = await TemplateAsync("D", 0, 0, 0, (8, 0, 16, 0));
        var late = await TemplateAsync("L", 0, 0, 0, (9, 0, 17, 0));
        var n = await TemplateAsync("N", 0, 0, 0, (22, 0, 6, 0));
        var a = await EmployeeAsync("A");
        var b = await EmployeeAsync("B");
        var c = await EmployeeAsync("C");
        await wf.AssignShiftAsync(Owner, new AssignmentInput(a.Id, Mon.AddDays(-7), null, d.Id));
        await wf.AssignShiftAsync(Owner, new AssignmentInput(b.Id, Mon.AddDays(-7), null, late.Id));
        await wf.AssignShiftAsync(Owner, new AssignmentInput(c.Id, Mon.AddDays(-7), null, n.Id));
        var employee = new WorkforceActor("a", LocalRoles.Employee, a.Id);
        var s = await wf.RequestSwapAsync(employee, a.Id, Mon, b.Id, Mon, "appointment");
        await Assert.ThrowsAsync<WorkforceAccessDeniedException>(() => wf.DecideSwapAsync(employee, s.Id, true));
        await wf.DecideSwapAsync(Owner, s.Id, true);
        var cal = (await wf.CalendarAsync(Owner, Mon, Mon)).ToDictionary(p => p.EmployeeId);
        Assert.Equal(("L", "D"), (cal[a.Id].Shift!.Code, cal[b.Id].Shift!.Code));

        var bad = await wf.RequestSwapAsync(Owner, a.Id, Mon.AddDays(2), c.Id, Mon.AddDays(2));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => wf.DecideSwapAsync(Owner, bad.Id, true));
        Assert.StartsWith("wf.err.swapConflict", ex.Message);
        Assert.Equal(RequestStatuses.Pending, (await wf.SwapsAsync(Owner)).Single(x => x.Id == bad.Id).Status); // rolled back
    }

    // ------------------------------------------------------------------ leave

    [Fact]
    public async Task Leave_accrual_approval_balances_and_absence_exclusion()
    {
        var wf = Wf();
        await wf.EnsureDefaultLeaveTypesAsync();
        var types = (await wf.LeaveTypesAsync()).ToDictionary(t => t.Code);
        Assert.Equal(["annual", "exceptional", "sick", "unpaid"], types.Keys.Order());
        Assert.All(types.Values, t => Assert.Equal(0m, t.AccrualDaysPerMonth)); // entitlements are configured by the user
        var annual = await wf.UpsertLeaveTypeAsync(Owner, types["annual"].Id, new LeaveTypeInput("annual", "Annual leave", true, 2.5m, TracksBalance: true));
        var e = await EmployeeAsync("Leave");
        var dt = await TemplateAsync("D", 60, 5, 5, (8, 0, 16, 0));
        await wf.AssignShiftAsync(Owner, new AssignmentInput(e.Id, Mon.AddDays(-30), null, dt.Id, DaysMask: WeekdaysMask));
        await wf.AddHolidayAsync(Owner, Mon.AddDays(9), "Holiday");
        Assert.True(await wf.AccrueLeaveAsync(Owner, 2026, 9) > 0);
        await wf.AccrueLeaveAsync(Owner, 2026, 10);
        Assert.Equal(0, await wf.AccrueLeaveAsync(Owner, 2026, 10)); // idempotent

        var self = new WorkforceActor("leave", LocalRoles.Employee, e.Id);
        var req = await wf.RequestLeaveAsync(self, new LeaveRequestInput(e.Id, annual.Id, Mon.AddDays(7), Mon.AddDays(11), "family", @"C:\docs\note.pdf"));
        Assert.Equal(4m, req.Days); // Mon–Fri minus the Wednesday holiday
        await Assert.ThrowsAsync<WorkforceAccessDeniedException>(() => wf.DecideLeaveAsync(self, req.Id, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => wf.RequestLeaveAsync(self, new LeaveRequestInput(e.Id, annual.Id, Mon.AddDays(8), Mon.AddDays(8))));
        await wf.DecideLeaveAsync(Owner, req.Id, true);
        var bal = (await wf.LeaveBalancesAsync(Owner, e.Id)).Single(b => b.LeaveType == "annual");
        Assert.Equal((5m, 4m, 1m), (bal.Accrued, bal.Taken, bal.Balance));

        var tooMuch = await wf.RequestLeaveAsync(self, new LeaveRequestInput(e.Id, annual.Id, Mon.AddDays(14), Mon.AddDays(15)));
        Assert.Equal("wf.err.insufficientBalance", (await Assert.ThrowsAsync<InvalidOperationException>(() => wf.DecideLeaveAsync(Owner, tooMuch.Id, true))).Message);
        await wf.DecideLeaveAsync(Owner, tooMuch.Id, false);
        var unpaid = await wf.RequestLeaveAsync(self, new LeaveRequestInput(e.Id, types["unpaid"].Id, Mon.AddDays(14), Mon.AddDays(15)));
        await wf.DecideLeaveAsync(Owner, unpaid.Id, true); // not balance-tracked

        var days = (await wf.ComputeAttendanceAsync(Owner, Mon.AddDays(7), Mon.AddDays(15), e.Id)).ToDictionary(d => d.Date);
        Assert.Equal(DayStatuses.Leave, days[Mon.AddDays(7)].Status);
        Assert.Equal(DayStatuses.Holiday, days[Mon.AddDays(9)].Status);
        Assert.Equal(DayStatuses.Rest, days[Mon.AddDays(12)].Status); // Saturday
        Assert.Equal(DayStatuses.Leave, days[Mon.AddDays(14)].Status);
        Assert.False(days[Mon.AddDays(14)].Leave!.IsPaid);
        Assert.DoesNotContain(days.Values, d => d.Date >= Mon.AddDays(7) && d.Date <= Mon.AddDays(11) && d.Status == DayStatuses.Absent);

        await wf.CancelLeaveAsync(Owner, req.Id);
        Assert.Equal(5m, (await wf.LeaveBalancesAsync(Owner, e.Id)).Single(b => b.LeaveType == "annual").Balance);
    }

    // ------------------------------------------------------------------ payroll preparation

    private static DayResult Day(DateOnly date, string status, int worked, int ot = 0, int late = 0, bool rest = false, bool holiday = false, Guid? shift = null, int scheduled = 480,
        LeaveDay? leave = null) =>
        new(Guid.Empty, date, status, rest ? null : "D", rest ? null : shift, holiday, rest, null, null, rest ? 0 : scheduled, worked, 0, late, 0, ot, 0, 0, false, [], leave);

    [Fact]
    public void Payroll_preparation_math_with_tiers_items_and_user_statutory_rules()
    {
        var e = new LocalEmployee { Id = Guid.NewGuid(), Name = "P", Salary = 32000 };
        var policy = new PayrollPolicy { StandardMonthlyHours = 160, Tier1MinutesPerDay = 120, Tier1Multiplier = 1.5m, Tier2Multiplier = 1.75m, RestDayMultiplier = 2m,
            AbsenceMethod = AbsenceDeductionMethod.PerWorkingDay, LateMethod = LateAdjustmentMethod.PerMinute, LateToleranceMinutesPerMonth = 10 };
        var days = new List<DayResult>
        {
            Day(Mon, DayStatuses.Present, 660, ot: 180),
            Day(Mon.AddDays(1), DayStatuses.Present, 480, late: 30),
            Day(Mon.AddDays(2), DayStatuses.Present, 480),
            Day(Mon.AddDays(3), DayStatuses.Absent, 0),
            Day(Mon.AddDays(5), DayStatuses.Present, 60, ot: 60, rest: true),
        };
        var items = new List<WfPayItem>
        {
            new() { Code = "TR", Kind = PayItemKinds.Allowance, Amount = 1000, From = Mon, IsActive = true },
            new() { Code = "B10", Kind = PayItemKinds.Bonus, Amount = 10, IsPercentOfBase = true, From = Mon, IsActive = true },
            new() { Code = "SP", Kind = PayItemKinds.ShiftPremium, Amount = 50, From = Mon, IsActive = true },
            new() { Code = "OLD", Kind = PayItemKinds.Bonus, Amount = 999, From = Mon.AddYears(-2), To = Mon.AddYears(-1), IsActive = true },
        };
        var rules = new List<WfStatutoryRule>
        {
            new() { Code = "SS", Order = 1, EmployeeRatePercent = 9, EmployerRatePercent = 25, IsEnabled = true },
            new() { Code = "TAX", Order = 2, BracketsJson = "[{\"from\":0,\"to\":10000,\"ratePercent\":0},{\"from\":10000,\"to\":null,\"ratePercent\":10}]", BaseAfterPreviousRules = true, IsEnabled = true },
            new() { Code = "OFF", Order = 3, EmployeeRatePercent = 50, IsEnabled = false },
        };
        var line = PayrollCalculator.Compute(e, days, policy, items, rules, Mon, Mon.AddDays(30));
        Assert.Equal(32000m, line.BaseSalary);
        Assert.Equal(950m + 400m, line.OvertimeAmount); // 120 min ×1.5 + 60 min ×1.75 at 200/h, + rest day 60 min ×2
        Assert.Equal((1m, 8000m), (line.AbsenceDays, line.AbsenceDeduction)); // 32000 / 4 planned workdays
        Assert.Equal(66.67m, line.LateDeduction); // (30 − 10) min at 200/h
        Assert.Equal((3200m, 1000m, 200m), (line.Bonuses, line.Allowances, line.ShiftPremiums));
        Assert.Equal(29683.33m, line.Gross);
        Assert.Equal(29683.33m, line.NetBeforeStatutory);
        Assert.Equal(2671.50m + 1701.18m, line.StatutoryEmployee);
        Assert.Equal(7420.83m, line.StatutoryEmployer);
        Assert.Equal(25310.65m, line.Net);
        Assert.Equal((0m, 0m), PayrollCalculator.Statutory(50000, [])); // nothing shipped → nothing withheld

        var hourly = new LocalEmployee { Id = Guid.NewGuid(), Name = "H", PayBasis = "Hourly", HourlyRate = 300 };
        var h = PayrollCalculator.Compute(hourly, [Day(Mon, DayStatuses.Present, 540, ot: 60), Day(Mon.AddDays(1), DayStatuses.Absent, 0)], policy, [], [], Mon, Mon.AddDays(30));
        Assert.Equal((2400m, 450m, 0m), (h.BaseSalary, h.OvertimeAmount, h.AbsenceDeduction));
    }

    [Fact]
    public async Task Payroll_preparation_posts_through_existing_payroll_with_statutory_deductions()
    {
        var wf = Wf();
        Assert.Empty(await wf.StatutoryRulesAsync(Owner));
        Assert.Contains("official sources", StatutoryNotice.Text);
        var e = await EmployeeAsync("Pay", salary: 30000);
        await new ErpService(Db, _time).GiveAdvanceAsync(e.Id, 2000, PayMethods.Cash, Mon);
        await wf.UpsertStatutoryRuleAsync(Owner, null, new StatutoryRuleInput("SS", "Social security (user-entered)", 1, 10, IsEnabled: true));
        var prep = await wf.PreparePayrollAsync(Owner, 2026, 10);
        var line = Assert.Single(prep.Lines);
        Assert.Equal((30000m, 3000m, 27000m), (line.Gross, line.StatutoryEmployee, line.Net));
        await wf.PreparePayrollAsync(Owner, 2026, 10); // re-prepare a draft
        var run = await wf.PostPayrollPrepAsync(Owner, 2026, 10);
        Assert.Equal((30000m, 3000m, 2000m, 25000m), (run.TotalGross, run.TotalDeductions, run.TotalAdvances, run.TotalNet));
        await Assert.ThrowsAsync<InvalidOperationException>(() => wf.PreparePayrollAsync(Owner, 2026, 10));
        await using var db = Db();
        var lines = await db.JournalLines.AsNoTracking().ToListAsync();
        Assert.Equal(0m, lines.Sum(l => l.Debit - l.Credit));
        Assert.Equal(-3000m, lines.Where(l => l.AccountCode == "431").Sum(l => l.Debit - l.Credit));
        Assert.Equal(3000m, (await db.Payslips.SingleAsync()).Deductions);
        var csv = WorkforceReports.ToCsv(prep.Lines);
        Assert.Contains("Gross", csv.Split('\n')[0]);
    }

    // ------------------------------------------------------------------ reports

    [Fact]
    public async Task Attendance_reports_return_rows_and_safe_csv()
    {
        var wf = Wf();
        await wf.EnsureDefaultLeaveTypesAsync();
        var sick = (await wf.LeaveTypesAsync()).Single(t => t.Code == "sick");
        var dept = await wf.UpsertDepartmentAsync(Owner, null, "Sales");
        var e = await EmployeeAsync("=Formula", dept.Id);
        var t = await TemplateAsync("D", 60, 5, 5, (8, 0, 16, 0));
        await wf.AssignShiftAsync(Owner, new AssignmentInput(e.Id, Mon, null, t.Id, DaysMask: WeekdaysMask));
        await PunchAsync(e.Id, PunchTypes.In, Mon, 8, 20);
        await PunchAsync(e.Id, PunchTypes.Out, Mon, 17, 30);
        var leave = await wf.RequestLeaveAsync(Owner, new LeaveRequestInput(e.Id, sick.Id, Mon.AddDays(2), Mon.AddDays(2), "flu"));
        await wf.DecideLeaveAsync(Owner, leave.Id, true);
        var reports = new WorkforceReports(wf, Db);

        var daily = await reports.DailyAsync(Owner, Mon, Mon.AddDays(2));
        Assert.Equal([DayStatuses.Present, DayStatuses.Absent, DayStatuses.Leave], daily.Select(r => r.Status));
        Assert.Equal(("Sales", 20, 90), (daily[0].Department, daily[0].LateMinutes, daily[0].OvertimeMinutes));
        Assert.Single(await reports.LateAsync(Owner, Mon, Mon.AddDays(2)));
        Assert.Single(await reports.AbsenceAsync(Owner, Mon, Mon.AddDays(2)));
        Assert.Single(await reports.OvertimeAsync(Owner, Mon, Mon.AddDays(2)));
        var month = Assert.Single(await reports.MonthlyAsync(Owner, 2026, 10));
        Assert.Equal((15, 1, 1), (month.ScheduledDays, month.PresentDays, month.LeaveDays));
        Assert.Equal(("Sales", 1), ((await reports.DepartmentAsync(Owner, Mon, Mon.AddDays(2))).Single().Department, (await reports.DepartmentAsync(Owner, Mon, Mon.AddDays(2))).Single().Employees));
        var coverage = await reports.ShiftCoverageAsync(Owner, Mon, Mon.AddDays(2));
        Assert.Equal([(1, 1, 0, 0), (1, 0, 1, 0), (1, 0, 0, 1)], coverage.Select(c => (c.Planned, c.Present, c.Absent, c.OnLeave)));
        Assert.Equal("sick", Assert.Single(await reports.LeaveAsync(Owner, Mon, Mon.AddDays(6))).LeaveType);
        Assert.NotEmpty(await reports.LeaveBalancesAsync(Owner));

        var csv = WorkforceReports.ToCsv(daily);
        var lines = csv.TrimEnd().Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("Date,EmployeeNo,Employee,Department", lines[0]);
        Assert.Contains("'=Formula", lines[1]); // spreadsheet formula injection neutralised
        Assert.Contains("2026-10-12 08:20", lines[1]);
        await Assert.ThrowsAsync<WorkforceAccessDeniedException>(() => reports.DailyAsync(new WorkforceActor("x", LocalRoles.Employee, e.Id), Mon, Mon));
    }

    // ------------------------------------------------------------------ RBAC

    [Fact]
    public async Task Department_manager_is_scoped_auditor_is_read_only_and_employee_is_self_only()
    {
        var wf = Wf();
        var a = await wf.UpsertDepartmentAsync(Owner, null, "A");
        var b = await wf.UpsertDepartmentAsync(Owner, null, "B");
        var boss = await EmployeeAsync("Boss A", a.Id);
        var inA = await EmployeeAsync("Worker A", a.Id);
        var inB = await EmployeeAsync("Worker B", b.Id);
        var mgr = new WorkforceActor("bossA", LocalRoles.DeptManager, boss.Id);

        Assert.Equal(["Boss A", "Worker A"], (await wf.EmployeesAsync(mgr)).Select(e => e.Name));
        Assert.True((await wf.RecordPunchAsync(mgr, new PunchInput(inA.Id, PunchTypes.In, PunchSources.Manual, Reason: "r", AuthorizedBy: "bossA"))).IsRecorded);
        await Assert.ThrowsAsync<WorkforceAccessDeniedException>(() => wf.RecordPunchAsync(mgr, new PunchInput(inB.Id, PunchTypes.In, PunchSources.Manual, Reason: "r", AuthorizedBy: "x")));
        await Assert.ThrowsAsync<WorkforceAccessDeniedException>(() => wf.ComputeAttendanceAsync(mgr, Mon, Mon, inB.Id));
        Assert.All(await wf.ComputeAttendanceAsync(mgr, Mon, Mon), d => Assert.NotEqual(inB.Id, d.EmployeeId));
        await Assert.ThrowsAsync<WorkforceAccessDeniedException>(() => wf.UpsertEmployeeAsync(mgr, inA.Id, new EmployeeProfileInput("x", 1, Mon)));
        await Assert.ThrowsAsync<WorkforceAccessDeniedException>(() => wf.PreparePayrollAsync(mgr, 2026, 10));

        var auditor = new WorkforceActor("audit", LocalRoles.Auditor);
        Assert.Equal(3, (await wf.EmployeesAsync(auditor)).Count);
        Assert.True((await wf.VerifyChainAsync(auditor)).IsValid);
        Assert.NotEmpty(await wf.EmployeeHistoryAsync(auditor, inA.Id));
        await Assert.ThrowsAsync<WorkforceAccessDeniedException>(() => wf.UpsertEmployeeAsync(auditor, null, new EmployeeProfileInput("x", 1, Mon)));
        await Assert.ThrowsAsync<WorkforceAccessDeniedException>(() => wf.RecordPunchAsync(auditor, new PunchInput(inA.Id, PunchTypes.Out, PunchSources.Manual, Reason: "r", AuthorizedBy: "x")));
        await Assert.ThrowsAsync<WorkforceAccessDeniedException>(() => wf.SavePolicyAsync(auditor, new WorkforcePolicy()));
        await Assert.ThrowsAsync<WorkforceAccessDeniedException>(() => wf.AssignCardAsync(auditor, inA.Id, "AABBCCDD"));
        await Assert.ThrowsAsync<WorkforceAccessDeniedException>(() => wf.EmergencyUnlockAllAsync(auditor));
        Assert.All(WorkforcePermissions.ForRole(LocalRoles.Auditor), p => Assert.Contains(p, WorkforcePermissions.ReadOnly));

        var me = new WorkforceActor("workerB", LocalRoles.Employee, inB.Id);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.True((await wf.RecordPunchAsync(me, new PunchInput(inB.Id, null, PunchSources.Kiosk))).IsRecorded);
        await Assert.ThrowsAsync<WorkforceAccessDeniedException>(() => wf.RecordPunchAsync(me, new PunchInput(inA.Id, null, PunchSources.Kiosk)));
        Assert.Equal([inB.Id], (await wf.EmployeesAsync(new WorkforceActor("hr", LocalRoles.HrAdmin))).Where(e => e.Id == inB.Id).Select(e => e.Id));
        await Assert.ThrowsAsync<WorkforceAccessDeniedException>(() => wf.EmployeesAsync(me)); // employees have no directory access
    }

    [Fact]
    public void Local_accounts_support_workforce_roles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vlx-wf-" + Guid.NewGuid().ToString("N"));
        try
        {
            var auth = new LocalAuthService(new Veltrix.Desktop.Core.Security.SecureStore(dir, new PlainProtector()), _time, PasswordHasher.MinimumIterations);
            auth.CreateOwner("Owner", "owner-pass-1");
            var hr = auth.AddUser("Hr", "hr-pass-12", LocalRoles.HrAdmin);
            Assert.Contains(WorkforcePermissions.EmployeesManage, hr.ToProfile().Permissions);
            Assert.Throws<ArgumentException>(() => auth.AddUser("Mgr", "mgr-pass-12", LocalRoles.DeptManager));
            var empId = Guid.NewGuid();
            var mgr = auth.AddUser("Mgr", "mgr-pass-12", LocalRoles.DeptManager, empId);
            Assert.Equal(empId, auth.SignIn("Mgr", "mgr-pass-12").User!.EmployeeId);
            var actor = WorkforceActor.From(mgr);
            Assert.True(actor.Has(WorkforcePermissions.CorrectionsApprove));
            Assert.False(actor.Has(WorkforcePermissions.PayrollPost));
            Assert.Equal(LocalRoles.CashierPermissions, LocalRoles.PermissionsFor(LocalRoles.Cashier));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------------ migration & backup

    [Fact]
    public async Task Schema_v4_database_is_upgraded_to_v5_keeping_employees_and_payroll()
    {
        var erp = new ErpService(Db, _time);
        var active = await erp.UpsertEmployeeAsync(null, new EmployeeInput("Old active", "Cashier", "0550", 25000, new DateOnly(2025, 1, 1)));
        var gone = await erp.UpsertEmployeeAsync(null, new EmployeeInput("Old gone", null, null, 20000, new DateOnly(2025, 1, 1), IsActive: false));
        await erp.RunPayrollAsync(2026, 9);
        await using (var db = Db())
        {
            foreach (var t in LocalDb.V5Tables) await db.Database.ExecuteSqlRawAsync($"DROP TABLE \"{t}\";");
            foreach (var (table, column, _) in LocalDb.V5Columns) await db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"{table}\" DROP COLUMN \"{column}\";");
            await db.SetValueAsync(LocalDb.Keys.SchemaVersion, "4");
        }
        await using (var db = Db()) await db.InitializeAsync();
        await using (var db = Db())
        {
            var emps = await db.Employees.AsNoTracking().ToListAsync();
            Assert.Equal(("Old active", 25000m, EmployeeStatuses.Active, "Monthly"), (emps.Single(e => e.Id == active.Id).Name, emps.Single(e => e.Id == active.Id).Salary,
                emps.Single(e => e.Id == active.Id).Status, emps.Single(e => e.Id == active.Id).PayBasis));
            Assert.Equal(EmployeeStatuses.Terminated, emps.Single(e => e.Id == gone.Id).Status);
            Assert.Equal(25000m, (await db.Payslips.SingleAsync()).Gross);
            Assert.Equal(0m, (await db.PayrollRuns.SingleAsync()).TotalDeductions);
            Assert.Equal(LocalDb.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), await db.GetValueAsync(LocalDb.Keys.SchemaVersion));
        }
        var wf = Wf();
        await wf.UpsertEmployeeAsync(Owner, active.Id, new EmployeeProfileInput("Old active", 25000, new DateOnly(2025, 1, 1)) { ContractType = "CDI" });
        Assert.True((await PunchAsync(active.Id, PunchTypes.In, Mon, 8, 0)).IsRecorded);
    }

    [Fact]
    public async Task Encrypted_backup_includes_every_workforce_table()
    {
        Assert.Equal(LocalDb.V5Tables.Concat(LocalDb.V6Tables).Order(), LocalBackup.WorkforceTableNames.Order());
        var wf = Wf();
        var e = await EmployeeAsync("Backed");
        await wf.AssignCardAsync(Owner, e.Id, "11223344");
        await wf.EnsureDefaultLeaveTypesAsync();
        var t = await TemplateAsync("D", 60, 5, 5, (8, 0, 16, 0));
        await wf.AssignShiftAsync(Owner, new AssignmentInput(e.Id, Mon, null, t.Id));
        await PunchAsync(e.Id, PunchTypes.In, Mon, 8, 0);
        await PunchAsync(e.Id, PunchTypes.Out, Mon, 16, 0);
        await wf.UpsertDeviceAsync(Owner, null, new DeviceInput("Gate", "Virtual"));
        await wf.SavePolicyAsync(Owner, new WorkforcePolicy { Attendance = new AttendancePolicy { DuplicateWindowSeconds = 90 } });

        BackupPayload payload;
        await using (var db = Db()) payload = await LocalBackup.CaptureAsync(db, "[]", null, "Shop", _time.GetUtcNow());
        var restored = LocalBackup.Decrypt(LocalBackup.Encrypt(payload, "pw-123456", PasswordHasher.MinimumIterations), "pw-123456");
        await using (var db = Db())
        {
            db.WfAttendanceEvents.RemoveRange(db.WfAttendanceEvents);
            await db.SaveChangesAsync();
        }
        await using (var db = Db()) await LocalBackup.RestoreAsync(db, restored);

        await using (var db = Db())
        {
            Assert.Equal(2, await db.WfAttendanceEvents.CountAsync());
            Assert.Equal("11223344", (await db.WfCredentials.SingleAsync()).Value);
            Assert.Equal(4, await db.WfLeaveTypes.CountAsync());
            Assert.Single(await db.WfDevices.ToListAsync());
            Assert.NotEmpty(await db.WfEmployeeAudits.ToListAsync());
        }
        Assert.True((await wf.VerifyChainAsync(Owner)).IsValid);
        Assert.Equal(90, (await wf.PolicyAsync()).Attendance.DuplicateWindowSeconds);
        Assert.Equal(420, (await DayAsync(e.Id, Mon)).WorkedMinutes);
    }

    private sealed class PlainProtector : Veltrix.Desktop.Core.Security.IDataProtector
    {
        public byte[] Protect(byte[] data) => data;
        public byte[] Unprotect(byte[] data) => data;
    }

    internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public void Set(DateOnly day, int h, int m) => _now = new DateTimeOffset(day.ToDateTime(new TimeOnly(h, m)), TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
