package com.veltrix.os.data.workforce

import java.security.MessageDigest
import java.time.DayOfWeek
import java.time.Duration
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.LocalTime
import kotlin.math.floor

// Faithful Kotlin port of apps/windows/Veltrix.Desktop.Core/Workforce (AttendanceCalculator, ShiftPlanner, WorkforcePolicy, punch rules).
// Pure JVM code (no Android / Room) so it is unit-tested against the same cases as the Windows engine.

object PunchTypes {
    const val IN = "In"
    const val OUT = "Out"
    const val BREAK_START = "BreakStart"
    const val BREAK_END = "BreakEnd"
    /** Compensating event that cancels `compensatesEventId` (raw events are never edited or deleted). */
    const val VOID = "Void"
    val PUNCHES = listOf(IN, OUT, BREAK_START, BREAK_END)
}

object PunchSources {
    const val CARD = "Card"
    const val QR = "QR"
    const val PIN = "PIN"
    const val MANUAL = "Manual"
    const val KIOSK = "Kiosk"
    const val CORRECTION = "Correction"
}

object CredentialKinds {
    const val CARD = "Card"
    const val QR = "Qr"
    const val PIN = "Pin"
}

object DayStatuses {
    const val PRESENT = "Present"
    const val INCOMPLETE = "Incomplete"
    const val ABSENT = "Absent"
    const val LEAVE = "Leave"
    const val HOLIDAY = "Holiday"
    const val REST = "Rest"
}

object EmployeeStatuses {
    const val ACTIVE = "Active"
    const val SUSPENDED = "Suspended"
    const val TERMINATED = "Terminated"
    val ALL = listOf(ACTIVE, SUSPENDED, TERMINATED)
}

object RequestStatuses {
    const val PENDING = "Pending"
    const val APPROVED = "Approved"
    const val REJECTED = "Rejected"
}

enum class RoundingMode { Down, Nearest, Up }
enum class OvertimeBasis { DailyThreshold, AfterShiftEnd }

data class AttendancePolicy(
    /** Same employee + same punch type within this window = duplicate scan (rejected). */
    val duplicateWindowSeconds: Int = 60,
    val earlyWindowMinutes: Int = 240,
    val lateWindowMinutes: Int = 360,
    val autoDeductBreak: Boolean = true,
)

data class OvertimePolicy(
    val basis: OvertimeBasis = OvertimeBasis.AfterShiftEnd,
    val dailyThresholdMinutes: Int = 480,
    val minimumMinutes: Int = 15,
    val roundingStepMinutes: Int = 15,
    val rounding: RoundingMode = RoundingMode.Down,
)

data class WorkforcePolicy(
    val attendance: AttendancePolicy = AttendancePolicy(),
    val overtime: OvertimePolicy = OvertimePolicy(),
    val nightStart: LocalTime = LocalTime.of(21, 0),
    val nightEnd: LocalTime = LocalTime.of(5, 0),
)

/** One working segment of a shift. End ≤ Start means the segment crosses midnight. */
data class ShiftSegment(val start: LocalTime, val end: LocalTime)

data class PlannedInterval(val start: LocalDateTime, val end: LocalDateTime) {
    val minutes: Int get() = minutesBetween(start, end)
}

data class ShiftDefinition(
    val id: String, val code: String, val name: String, val segments: List<ShiftSegment>, val breakMinutes: Int, val graceInMinutes: Int,
    val graceOutMinutes: Int, val isFlexible: Boolean = false, val coreStart: LocalTime? = null, val coreEnd: LocalTime? = null, val requiredMinutes: Int = 0,
) {
    /** Segments placed on the calendar for a shift starting on [date] (later segments follow earlier ones, crossing midnight as needed). */
    fun place(date: LocalDate): List<PlannedInterval> {
        val list = ArrayList<PlannedInterval>()
        var prevEnd: LocalDateTime? = null
        for (s in segments) {
            var start = date.atTime(s.start)
            while (prevEnd != null && start < prevEnd) start = start.plusDays(1)
            var end = start.toLocalDate().atTime(s.end)
            if (end <= start) end = end.plusDays(1)
            list += PlannedInterval(start, end)
            prevEnd = end
        }
        return list
    }

    fun isOvernight(date: LocalDate): Boolean = place(date).lastOrNull()?.let { it.end.minusNanos(1).toLocalDate() > date } == true

    /** Scheduled paid minutes: segment minutes − unpaid break, or the required minutes for a flexible shift. */
    fun scheduledMinutes(date: LocalDate): Int = if (isFlexible && requiredMinutes > 0) requiredMinutes
        else maxOf(0, place(date).sumOf { it.minutes } - breakMinutes)

    companion object {
        /** "HH:mm-HH:mm;HH:mm-HH:mm" (storage format). */
        fun parseSegments(text: String): List<ShiftSegment> = text.split(';').map { it.trim() }.filter { it.isNotEmpty() }.map {
            val (a, b) = it.split('-').map { p -> LocalTime.parse(p.trim()) }
            ShiftSegment(a, b)
        }
        fun formatSegments(segments: List<ShiftSegment>): String = segments.joinToString(";") { "${hhmm(it.start)}-${hhmm(it.end)}" }
        fun hhmm(t: LocalTime): String = "%02d:%02d".format(java.util.Locale.ROOT, t.hour, t.minute)
    }
}

/** An employee's plan for one day (local time). [shift] null = rest day. */
data class PlannedDay(val employeeId: String, val date: LocalDate, val shift: ShiftDefinition?, val segments: List<PlannedInterval>, val isHoliday: Boolean) {
    val isRestDay: Boolean get() = shift == null
    val isScheduledWorkday: Boolean get() = shift != null && !isHoliday
    val start: LocalDateTime? get() = segments.firstOrNull()?.start
    val end: LocalDateTime? get() = segments.lastOrNull()?.end
    val scheduledMinutes: Int get() = shift?.scheduledMinutes(date) ?: 0
}

/** Assignment of a shift to an employee over a date range; [daysMask] bit (DayOfWeek with Sunday = 0) set = works that weekday; 0 = every day. */
data class ShiftAssignmentRule(val employeeId: String, val shiftId: String, val from: LocalDate, val to: LocalDate?, val daysMask: Int = 0)

/** Pure shift planning (assignments + holidays → daily plans). Ported from ShiftPlanner (no rotations/overrides on Android). */
class ShiftPlanner(templates: List<ShiftDefinition>, assignments: List<ShiftAssignmentRule>, private val holidays: Set<LocalDate> = emptySet()) {
    private val templates = templates.associateBy { it.id }
    private val assignments = assignments.groupBy { it.employeeId }

    fun plan(employeeId: String, date: LocalDate): PlannedDay {
        val shift = resolve(employeeId, date)
        return PlannedDay(employeeId, date, shift, shift?.place(date) ?: emptyList(), date in holidays)
    }

    private fun resolve(employeeId: String, date: LocalDate): ShiftDefinition? {
        val a = assignments[employeeId].orEmpty().filter { it.from <= date && (it.to == null || it.to >= date) }.maxByOrNull { it.from } ?: return null
        if (a.daysMask != 0 && (a.daysMask and (1 shl dayBit(date.dayOfWeek))) == 0) return null
        return templates[a.shiftId]
    }

    companion object {
        /// .NET DayOfWeek numbering: Sunday = 0 … Saturday = 6.
        fun dayBit(d: DayOfWeek): Int = d.value % 7
        const val WEEKDAYS_MASK = 0b0111110
        const val ALL_DAYS = 0
    }
}

/** An effective punch (raw event not voided) in workplace local time. */
data class Punch(val id: String, val type: String, val local: LocalDateTime, val source: String = PunchSources.MANUAL)

data class LeaveDay(val typeCode: String, val isPaid: Boolean)

data class DayResult(
    val employeeId: String, val date: LocalDate, val status: String, val shiftCode: String?, val shiftId: String?, val isHoliday: Boolean, val isRestDay: Boolean,
    val firstIn: LocalDateTime?, val lastOut: LocalDateTime?, val scheduledMinutes: Int, val workedMinutes: Int, val breakMinutes: Int, val lateMinutes: Int,
    val earlyLeaveMinutes: Int, val overtimeMinutes: Int, val nightMinutes: Int, val shortfallMinutes: Int, val missedPunch: Boolean, val anomalies: List<String>,
    val leave: LeaveDay? = null,
) {
    val isScheduledWorkday: Boolean get() = !isRestDay && !isHoliday
}

internal fun minutesBetween(a: LocalDateTime, b: LocalDateTime): Int = Duration.between(a, b).toMinutes().toInt()

/** Pure attendance engine: attributes punches to shift days and computes worked time, lateness, early departure, overtime … */
object AttendanceCalculator {
    /**
     * Attributes punches to (shift start) days. Each punch goes to the planned shift of D−1, D or D+1 whose window [start − early window,
     * end + late window] contains it and whose span is nearest (0 inside the shift); ties go to the later day for an In punch and to the
     * earlier day otherwise. Punches near no shift stay on their calendar day.
     */
    fun attribute(punches: List<Punch>, plan: (LocalDate) -> PlannedDay, policy: AttendancePolicy): Map<LocalDate, List<Punch>> {
        val result = LinkedHashMap<LocalDate, MutableList<Punch>>()
        for (p in punches.sortedBy { it.local }) {
            val day = p.local.toLocalDate()
            var target = day
            var best = Double.MAX_VALUE
            for (d in listOf(day.minusDays(1), day, day.plusDays(1))) {
                val pl = plan(d)
                val s = pl.start ?: continue
                val e = pl.end ?: continue
                if (p.local < s.minusMinutes(policy.earlyWindowMinutes.toLong()) || p.local > e.plusMinutes(policy.lateWindowMinutes.toLong())) continue
                val dist = when {
                    p.local < s -> Duration.between(p.local, s).toMillis() / 60000.0
                    p.local > e -> Duration.between(e, p.local).toMillis() / 60000.0
                    else -> 0.0
                }
                if (dist < best || (dist == best && p.type == PunchTypes.IN && d > target)) {
                    best = dist
                    target = d
                }
            }
            result.getOrPut(target) { ArrayList() }.add(p)
        }
        return result
    }

    fun computeDay(plan: PlannedDay, punches: List<Punch>, policy: WorkforcePolicy, leave: LeaveDay? = null): DayResult {
        val sorted = punches.sortedBy { it.local }
        val anomalies = ArrayList<String>()
        val work = ArrayList<PlannedInterval>()
        val breaks = ArrayList<PlannedInterval>()
        var missed = false
        var workStart: LocalDateTime? = null
        var breakStart: LocalDateTime? = null
        for (p in sorted) {
            when (p.type) {
                PunchTypes.IN -> {
                    if (workStart != null) { missed = true; anomalies += "missing_out" }
                    breakStart?.let { breaks += PlannedInterval(it, p.local); breakStart = null; anomalies += "missing_break_end"; missed = true }
                    workStart = p.local
                }
                PunchTypes.OUT -> {
                    val ws = workStart
                    val bs = breakStart
                    if (ws != null) { work += PlannedInterval(ws, p.local); workStart = null }
                    else if (bs != null) { breaks += PlannedInterval(bs, p.local); breakStart = null; missed = true; anomalies += "missing_break_end" }
                    else { missed = true; anomalies += "out_without_in" }
                }
                PunchTypes.BREAK_START -> {
                    val ws = workStart
                    if (ws != null) { work += PlannedInterval(ws, p.local); workStart = null; breakStart = p.local }
                    else { missed = true; anomalies += "break_without_in"; breakStart = p.local }
                }
                PunchTypes.BREAK_END -> {
                    val bs = breakStart
                    if (bs != null) { breaks += PlannedInterval(bs, p.local); breakStart = null }
                    else { missed = true; anomalies += "break_end_without_start" }
                    workStart = p.local
                }
            }
        }
        if (workStart != null) { missed = true; anomalies += "missing_out" }
        if (breakStart != null) { missed = true; anomalies += "missing_break_end" }

        val shift = plan.shift
        var worked = work.sumOf { it.minutes }
        var breakMinutes = breaks.sumOf { it.minutes }
        if (shift != null && breaks.isEmpty() && policy.attendance.autoDeductBreak && shift.breakMinutes > 0 && worked > shift.breakMinutes) {
            worked -= shift.breakMinutes
            breakMinutes = shift.breakMinutes
        }
        val firstIn = sorted.firstOrNull { it.type == PunchTypes.IN }?.local
        val lastOut = sorted.lastOrNull { it.type == PunchTypes.OUT }?.local
        val scheduled = plan.scheduledMinutes

        var late = 0
        var early = 0
        var shortfall = 0
        if (shift != null && !plan.isHoliday && sorted.isNotEmpty()) {
            if (shift.isFlexible) {
                val anchor = plan.start ?: plan.date.atStartOfDay()
                val cs = shift.coreStart
                if (cs != null && firstIn != null) {
                    var coreStart = anchor.toLocalDate().atTime(cs)
                    if (coreStart < anchor) coreStart = coreStart.plusDays(1)
                    if (minutesExact(coreStart, firstIn) > shift.graceInMinutes) late = minutesBetween(coreStart, firstIn)
                    val ce = shift.coreEnd
                    if (ce != null && lastOut != null) {
                        var coreEnd = coreStart.toLocalDate().atTime(ce)
                        if (coreEnd <= coreStart) coreEnd = coreEnd.plusDays(1)
                        if (minutesExact(lastOut, coreEnd) > shift.graceOutMinutes) early = minutesBetween(lastOut, coreEnd)
                    }
                }
                shortfall = maxOf(0, shift.requiredMinutes - worked)
            } else {
                val segs = plan.segments
                for (i in segs.indices) {
                    val lo = if (i == 0) LocalDateTime.MIN else mid(segs[i - 1].end, segs[i].start)
                    val hi = if (i == segs.size - 1) LocalDateTime.MAX else mid(segs[i].end, segs[i + 1].start)
                    val inSeg = sorted.filter { it.local >= lo && it.local < hi }
                    val segIn = inSeg.firstOrNull { it.type == PunchTypes.IN }?.local
                    val segOut = inSeg.lastOrNull { it.type == PunchTypes.OUT }?.local
                    if (segIn == null && segOut == null) {
                        anomalies += "segment_absent"
                        shortfall += segs[i].minutes
                        continue
                    }
                    if (segIn != null && minutesExact(segs[i].start, segIn) > shift.graceInMinutes) late += minutesBetween(segs[i].start, segIn)
                    if (segOut != null && minutesExact(segOut, segs[i].end) > shift.graceOutMinutes) early += minutesBetween(segOut, segs[i].end)
                }
            }
        }

        var overtime = 0
        if (worked > 0) {
            val end = plan.end
            val raw = when {
                shift == null || plan.isHoliday -> worked
                shift.isFlexible -> worked - maxOf(shift.requiredMinutes, 0)
                policy.overtime.basis == OvertimeBasis.DailyThreshold -> worked - policy.overtime.dailyThresholdMinutes
                else -> if (end != null) work.sumOf { overlap(it.start, it.end, end, LocalDateTime.MAX) } else 0
            }
            overtime = applyOvertimeRules(raw, policy.overtime)
        }

        var night = 0
        if (work.isNotEmpty()) {
            var d = plan.date.minusDays(1)
            while (d <= plan.date.plusDays(1)) {
                val ns = d.atTime(policy.nightStart)
                var ne = d.atTime(policy.nightEnd)
                if (ne <= ns) ne = ne.plusDays(1)
                night += work.sumOf { overlap(it.start, it.end, ns, ne) }
                d = d.plusDays(1)
            }
        }

        val status: String
        if (sorted.isEmpty()) {
            status = when {
                plan.isHoliday -> DayStatuses.HOLIDAY
                plan.isRestDay -> DayStatuses.REST
                leave != null -> DayStatuses.LEAVE
                else -> DayStatuses.ABSENT
            }
        } else {
            status = if (missed) DayStatuses.INCOMPLETE else DayStatuses.PRESENT
            if (leave != null) anomalies += "worked_during_leave"
        }
        if (status == DayStatuses.ABSENT || status == DayStatuses.LEAVE) shortfall = 0

        return DayResult(plan.employeeId, plan.date, status, shift?.code, shift?.id, plan.isHoliday, plan.isRestDay, firstIn, lastOut, scheduled, worked,
            breakMinutes, late, early, overtime, night, shortfall, missed, anomalies, leave)
    }

    /** Rounding (step / mode) then the minimum: overtime below the minimum is ignored. */
    fun applyOvertimeRules(rawMinutes: Int, p: OvertimePolicy): Int {
        if (rawMinutes <= 0) return 0
        var v = rawMinutes
        if (p.roundingStepMinutes > 1) {
            val step = p.roundingStepMinutes
            v = when (p.rounding) {
                RoundingMode.Up -> (v + step - 1) / step * step
                RoundingMode.Nearest -> floor(v / step.toDouble() + 0.5).toInt() * step // AwayFromZero for positive values
                RoundingMode.Down -> v / step * step
            }
        }
        return if (v < p.minimumMinutes) 0 else v
    }

    private fun mid(a: LocalDateTime, b: LocalDateTime): LocalDateTime = a.plus(Duration.between(a, b).dividedBy(2))

    /** Exact (fractional) minutes from a to b, as .NET TimeSpan.TotalMinutes. */
    private fun minutesExact(a: LocalDateTime, b: LocalDateTime): Double = Duration.between(a, b).toNanos() / 60_000_000_000.0

    fun overlap(aStart: LocalDateTime, aEnd: LocalDateTime, bStart: LocalDateTime, bEnd: LocalDateTime): Int {
        val s = if (aStart > bStart) aStart else bStart
        val e = if (aEnd < bEnd) aEnd else bEnd
        return if (e > s) minutesBetween(s, e) else 0
    }

    /** Computes days [from]..[to] for one employee the way WorkforceService.ComputeCoreAsync does. */
    fun computeRange(employeeId: String, punches: List<Punch>, planner: ShiftPlanner, policy: WorkforcePolicy, from: LocalDate, to: LocalDate,
        hireDate: LocalDate? = null, leaveOn: (LocalDate) -> LeaveDay? = { null }): List<DayResult> {
        val byDay = attribute(punches, { planner.plan(employeeId, it) }, policy.attendance)
        val out = ArrayList<DayResult>()
        var d = from
        while (d <= to) {
            if (hireDate == null || d >= hireDate) out += computeDay(planner.plan(employeeId, d), byDay[d].orEmpty(), policy, leaveOn(d))
            d = d.plusDays(1)
        }
        return out
    }
}

/** Minimal view of a stored event for duplicate / toggle decisions and chain hashing. */
data class EventFacts(val id: String, val type: String, val occurredAt: Long)

sealed interface PunchDecision {
    data class Record(val type: String) : PunchDecision
    data class Duplicate(val existing: EventFacts) : PunchDecision
}

object PunchRules {
    /**
     * Ported from WorkforceService.PunchCoreAsync: [recent] = effective events of the employee in [at − 24 h, at + window], ordered by time.
     * A punch within the duplicate window of an existing one (same type, or any type when auto-toggling) is a duplicate. Type null toggles:
     * Out after In/BreakEnd less than 20 h ago, else In.
     */
    fun decide(recent: List<EventFacts>, atMillis: Long, requestedType: String?, windowSeconds: Int): PunchDecision {
        val window = windowSeconds * 1000L
        if (window > 0) recent.firstOrNull { kotlin.math.abs(it.occurredAt - atMillis) < window && (requestedType == null || it.type == requestedType) }
            ?.let { return PunchDecision.Duplicate(it) }
        if (requestedType != null) return PunchDecision.Record(requestedType)
        val last = recent.lastOrNull { it.occurredAt <= atMillis }
        val out = last != null && (last.type == PunchTypes.IN || last.type == PunchTypes.BREAK_END) && atMillis - last.occurredAt < 20 * 3_600_000L
        return PunchDecision.Record(if (out) PunchTypes.OUT else PunchTypes.IN)
    }

    /** Normalizes a card UID: upper-case alphanumerics without separators. */
    fun normalizeUid(uid: String): String = uid.filter { it.isLetterOrDigit() && it.code < 128 }.uppercase(java.util.Locale.ROOT)

    fun hashToken(token: String): String = sha256Hex(token)

    fun sha256Hex(text: String): String = MessageDigest.getInstance("SHA-256").digest(text.toByteArray(Charsets.UTF_8))
        .joinToString("") { "%02X".format(java.util.Locale.ROOT, it) }
}

/** Fields that are hashed into the append-only attendance chain. */
data class ChainFields(
    val id: String, val employeeId: String, val type: String, val source: String, val occurredAt: Long, val localOffsetMinutes: Int, val recordedAt: Long,
    val createdBy: String?, val reason: String?, val authorizedBy: String?, val correctionId: String?, val compensatesEventId: String?,
)

object AttendanceChain {
    const val GENESIS_HASH = "VELTRIX-ATTENDANCE-GENESIS"

    /** SHA-256(prev hash | canonical event) in upper-case hex. */
    fun computeHash(prevHash: String, e: ChainFields): String = PunchRules.sha256Hex(listOf(prevHash, e.id, e.employeeId, e.type, e.source, e.occurredAt.toString(),
        e.localOffsetMinutes.toString(), e.recordedAt.toString(), e.createdBy ?: "", e.reason ?: "", e.authorizedBy ?: "", e.correctionId ?: "",
        e.compensatesEventId ?: "").joinToString("|"))

    /** Returns the 0-based index of the first broken link, or null when the whole chain verifies. */
    fun verify(chain: List<Pair<ChainFields, Pair<String, String>>>): Int? {
        var prev = GENESIS_HASH
        chain.forEachIndexed { i, (f, h) ->
            val (prevHash, hash) = h
            if (prevHash != prev || hash != computeHash(prev, f)) return i
            prev = hash
        }
        return null
    }
}

object WorkforceCsv {
    /** Quotes a CSV cell and neutralises spreadsheet formulas (=, +, -, @, tab, CR prefixed with '). */
    fun cell(v: Any?): String {
        var s = v?.toString() ?: ""
        if (s.isNotEmpty() && s[0] in "=+-@\t\r") s = "'$s"
        return if (s.any { it == ',' || it == '"' || it == '\n' || it == '\r' }) "\"" + s.replace("\"", "\"\"") + "\"" else s
    }

    fun row(vararg cells: Any?): String = cells.joinToString(",") { cell(it) }
}

object LeaveMath {
    /** Leave days = planned working days in the range; an employee without any planned shift counts calendar days. */
    fun days(from: LocalDate, to: LocalDate, plan: (LocalDate) -> PlannedDay, hasAssignments: Boolean): Double {
        require(!to.isBefore(from)) { "wf_err_period" }
        var n = 0
        var d = from
        while (d <= to) { if (!hasAssignments || plan(d).isScheduledWorkday) n++; d = d.plusDays(1) }
        return n.toDouble()
    }
}
