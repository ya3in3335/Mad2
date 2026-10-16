package com.veltrix.os.data.workforce

import androidx.room.Dao
import androidx.room.Entity
import androidx.room.Index
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.Transaction
import androidx.room.Upsert
import kotlinx.coroutines.flow.Flow
import kotlinx.serialization.Serializable

// Workforce & Attendance tables (schema v3). Dates are ISO yyyy-MM-dd text, instants are epoch milliseconds (UTC).

@Serializable
@Entity(tableName = "wf_departments")
data class WfDepartmentEntity(@PrimaryKey val id: String, val name: String, val createdAt: Long)

@Serializable
@Entity(tableName = "wf_employees", indices = [Index(value = ["number"], unique = true), Index("departmentId")])
data class WfEmployeeEntity(
    @PrimaryKey val id: String, val number: String, val name: String, val departmentId: String?, val position: String?, val status: String,
    val photoUri: String?, val hireDate: String?, val createdAt: Long, val updatedAt: Long,
)

/** Card UID (normalized), SHA-256 of the QR token, or PBKDF2 hash of the PIN — never the secret itself. */
@Serializable
@Entity(tableName = "wf_credentials", indices = [Index(value = ["kind", "value"]), Index("employeeId")])
data class WfCredentialEntity(
    @PrimaryKey val id: String, val employeeId: String, val kind: String, val value: String, val active: Boolean, val createdAt: Long, val revokedAt: Long?,
)

/** Shift template; [segments] = "HH:mm-HH:mm[;HH:mm-HH:mm]" (end ≤ start crosses midnight). */
@Serializable
@Entity(tableName = "wf_shifts", indices = [Index(value = ["code"], unique = true)])
data class WfShiftEntity(
    @PrimaryKey val id: String, val code: String, val name: String, val segments: String, val breakMinutes: Int, val graceInMinutes: Int,
    val graceOutMinutes: Int, val isFlexible: Boolean, val coreStart: String?, val coreEnd: String?, val requiredMinutes: Int, val active: Boolean,
)

@Serializable
@Entity(tableName = "wf_assignments", indices = [Index("employeeId")])
data class WfAssignmentEntity(
    @PrimaryKey val id: String, val employeeId: String, val shiftId: String, val fromDate: String, val toDate: String?, val daysMask: Int, val createdAt: Long,
)

/** Raw attendance event. Append-only and hash-chained: hash = SHA-256(prevHash | canonical event). */
@Serializable
@Entity(tableName = "wf_attendance_events", indices = [Index(value = ["id"], unique = true), Index(value = ["employeeId", "occurredAt"])])
data class WfAttendanceEventEntity(
    @PrimaryKey(autoGenerate = true) val seq: Long = 0, val id: String, val employeeId: String, val type: String, val source: String, val occurredAt: Long,
    val localOffsetMinutes: Int, val recordedAt: Long, val createdBy: String?, val reason: String?, val authorizedBy: String?, val correctionId: String?,
    val compensatesEventId: String?, val prevHash: String, val hash: String,
) {
    fun fields() = ChainFields(id, employeeId, type, source, occurredAt, localOffsetMinutes, recordedAt, createdBy, reason, authorizedBy, correctionId, compensatesEventId)
}

@Serializable
@Entity(tableName = "wf_leave_types", indices = [Index(value = ["code"], unique = true)])
data class WfLeaveTypeEntity(@PrimaryKey val id: String, val code: String, val name: String, val isPaid: Boolean, val annualDays: Double)

@Serializable
@Entity(tableName = "wf_leave_requests", indices = [Index("employeeId")])
data class WfLeaveRequestEntity(
    @PrimaryKey val id: String, val employeeId: String, val leaveTypeId: String, val fromDate: String, val toDate: String, val days: Double, val reason: String?,
    val status: String, val requestedBy: String, val decidedBy: String?, val decidedAt: Long?, val createdAt: Long,
)

/** Entitlement of an employee for a leave type and year (taken days are derived from approved requests). */
@Serializable
@Entity(tableName = "wf_leave_balances", primaryKeys = ["employeeId", "leaveTypeId", "year"])
data class WfLeaveBalanceEntity(val employeeId: String, val leaveTypeId: String, val year: Int, val entitledDays: Double)

/** Correction request: kind "add" (new punch [punchType] at [occurredAt]) or "void" (cancel [eventId]). Approval appends compensating events. */
@Serializable
@Entity(tableName = "wf_corrections", indices = [Index("employeeId")])
data class WfCorrectionEntity(
    @PrimaryKey val id: String, val employeeId: String, val kind: String, val eventId: String?, val punchType: String?, val occurredAt: Long?, val reason: String,
    val status: String, val requestedBy: String, val decidedBy: String?, val decidedAt: Long?, val createdAt: Long,
)

@Serializable
@Entity(tableName = "wf_settings")
data class WfSettingEntity(@PrimaryKey val key: String, val value: String)

@Serializable
data class WorkforceSnapshot(
    val departments: List<WfDepartmentEntity> = emptyList(), val employees: List<WfEmployeeEntity> = emptyList(),
    val credentials: List<WfCredentialEntity> = emptyList(), val shifts: List<WfShiftEntity> = emptyList(),
    val assignments: List<WfAssignmentEntity> = emptyList(), val events: List<WfAttendanceEventEntity> = emptyList(),
    val leaveTypes: List<WfLeaveTypeEntity> = emptyList(), val leaveRequests: List<WfLeaveRequestEntity> = emptyList(),
    val leaveBalances: List<WfLeaveBalanceEntity> = emptyList(), val corrections: List<WfCorrectionEntity> = emptyList(),
    val settings: List<WfSettingEntity> = emptyList(),
)

@Dao
abstract class WorkforceDao {
    @Query("SELECT * FROM wf_departments ORDER BY name") abstract fun departmentsFlow(): Flow<List<WfDepartmentEntity>>
    @Query("SELECT * FROM wf_departments") abstract suspend fun departments(): List<WfDepartmentEntity>
    @Upsert abstract suspend fun upsertDepartment(d: WfDepartmentEntity)

    @Query("SELECT * FROM wf_employees ORDER BY name") abstract fun employeesFlow(): Flow<List<WfEmployeeEntity>>
    @Query("SELECT * FROM wf_employees ORDER BY name") abstract suspend fun employees(): List<WfEmployeeEntity>
    @Query("SELECT * FROM wf_employees WHERE id = :id") abstract suspend fun employee(id: String): WfEmployeeEntity?
    @Query("SELECT * FROM wf_employees WHERE number = :number COLLATE NOCASE") abstract suspend fun employeeByNumber(number: String): WfEmployeeEntity?
    @Query("SELECT COUNT(*) FROM wf_employees") abstract suspend fun employeeCount(): Int
    @Upsert abstract suspend fun upsertEmployee(e: WfEmployeeEntity)

    @Query("SELECT * FROM wf_credentials WHERE employeeId = :employeeId AND active = 1") abstract suspend fun credentialsOf(employeeId: String): List<WfCredentialEntity>
    @Query("SELECT * FROM wf_credentials WHERE kind = :kind AND value = :value AND active = 1 LIMIT 1") abstract suspend fun credential(kind: String, value: String): WfCredentialEntity?
    @Query("UPDATE wf_credentials SET active = 0, revokedAt = :at WHERE employeeId = :employeeId AND kind = :kind AND active = 1")
    abstract suspend fun revoke(employeeId: String, kind: String, at: Long)
    @Query("UPDATE wf_credentials SET active = 0, revokedAt = :at WHERE kind = :kind AND value = :value AND active = 1")
    abstract suspend fun revokeValue(kind: String, value: String, at: Long)
    @Insert abstract suspend fun insertCredential(c: WfCredentialEntity)

    /** Rotates a credential: previous credentials of the same kind (and any other holder of a card UID) are revoked, never deleted. */
    @Transaction
    open suspend fun replaceCredential(c: WfCredentialEntity, revokeSameValue: Boolean) {
        revoke(c.employeeId, c.kind, c.createdAt)
        if (revokeSameValue) revokeValue(c.kind, c.value, c.createdAt)
        insertCredential(c)
    }

    @Query("SELECT * FROM wf_shifts ORDER BY code") abstract fun shiftsFlow(): Flow<List<WfShiftEntity>>
    @Query("SELECT * FROM wf_shifts") abstract suspend fun shifts(): List<WfShiftEntity>
    @Upsert abstract suspend fun upsertShift(s: WfShiftEntity)

    @Query("SELECT * FROM wf_assignments") abstract fun assignmentsFlow(): Flow<List<WfAssignmentEntity>>
    @Query("SELECT * FROM wf_assignments") abstract suspend fun assignments(): List<WfAssignmentEntity>
    @Upsert abstract suspend fun upsertAssignment(a: WfAssignmentEntity)
    @Query("DELETE FROM wf_assignments WHERE id = :id") abstract suspend fun deleteAssignment(id: String)

    @Query("SELECT * FROM wf_attendance_events ORDER BY seq") abstract suspend fun allEvents(): List<WfAttendanceEventEntity>
    @Query("SELECT * FROM wf_attendance_events ORDER BY seq DESC LIMIT 1") abstract suspend fun lastEvent(): WfAttendanceEventEntity?
    @Query("SELECT * FROM wf_attendance_events WHERE occurredAt >= :from AND occurredAt <= :to ORDER BY occurredAt, seq")
    abstract suspend fun eventsBetween(from: Long, to: Long): List<WfAttendanceEventEntity>
    @Query("SELECT * FROM wf_attendance_events WHERE employeeId = :employeeId AND occurredAt >= :from AND occurredAt <= :to ORDER BY occurredAt, seq")
    abstract suspend fun eventsOf(employeeId: String, from: Long, to: Long): List<WfAttendanceEventEntity>
    @Query("SELECT compensatesEventId FROM wf_attendance_events WHERE type = 'Void' AND compensatesEventId IS NOT NULL")
    abstract suspend fun voidedIds(): List<String>
    @Query("SELECT * FROM wf_attendance_events WHERE id = :id") abstract suspend fun event(id: String): WfAttendanceEventEntity?
    @Query("SELECT COUNT(*) FROM wf_attendance_events") abstract fun eventCount(): Flow<Int>
    @Insert(onConflict = OnConflictStrategy.ABORT) protected abstract suspend fun insertEvent(e: WfAttendanceEventEntity): Long

    /** Appends one event to the hash chain (the previous hash is read inside the same transaction). */
    @Transaction
    open suspend fun appendEvent(build: (prevHash: String) -> WfAttendanceEventEntity): WfAttendanceEventEntity {
        val e = build(lastEvent()?.hash ?: AttendanceChain.GENESIS_HASH)
        return e.copy(seq = insertEvent(e))
    }

    @Query("SELECT * FROM wf_leave_types ORDER BY code") abstract fun leaveTypesFlow(): Flow<List<WfLeaveTypeEntity>>
    @Query("SELECT * FROM wf_leave_types") abstract suspend fun leaveTypes(): List<WfLeaveTypeEntity>
    @Upsert abstract suspend fun upsertLeaveType(t: WfLeaveTypeEntity)
    @Query("SELECT * FROM wf_leave_requests ORDER BY createdAt DESC") abstract fun leaveRequestsFlow(): Flow<List<WfLeaveRequestEntity>>
    @Query("SELECT * FROM wf_leave_requests") abstract suspend fun leaveRequests(): List<WfLeaveRequestEntity>
    @Query("SELECT * FROM wf_leave_requests WHERE id = :id") abstract suspend fun leaveRequest(id: String): WfLeaveRequestEntity?
    @Upsert abstract suspend fun upsertLeaveRequest(r: WfLeaveRequestEntity)
    @Query("SELECT * FROM wf_leave_balances") abstract suspend fun leaveBalances(): List<WfLeaveBalanceEntity>
    @Upsert abstract suspend fun upsertLeaveBalance(b: WfLeaveBalanceEntity)

    @Query("SELECT * FROM wf_corrections ORDER BY createdAt DESC") abstract fun correctionsFlow(): Flow<List<WfCorrectionEntity>>
    @Query("SELECT * FROM wf_corrections") abstract suspend fun corrections(): List<WfCorrectionEntity>
    @Query("SELECT * FROM wf_corrections WHERE id = :id") abstract suspend fun correction(id: String): WfCorrectionEntity?
    @Upsert abstract suspend fun upsertCorrection(c: WfCorrectionEntity)

    @Query("SELECT * FROM wf_settings") abstract suspend fun settings(): List<WfSettingEntity>
    @Query("SELECT value FROM wf_settings WHERE `key` = :key") abstract suspend fun setting(key: String): String?
    @Upsert abstract suspend fun putSetting(s: WfSettingEntity)

    @Query("DELETE FROM wf_departments") protected abstract suspend fun clearDepartments()
    @Query("DELETE FROM wf_employees") protected abstract suspend fun clearEmployees()
    @Query("DELETE FROM wf_credentials") protected abstract suspend fun clearCredentials()
    @Query("DELETE FROM wf_shifts") protected abstract suspend fun clearShifts()
    @Query("DELETE FROM wf_assignments") protected abstract suspend fun clearAssignments()
    @Query("DELETE FROM wf_attendance_events") protected abstract suspend fun clearEvents()
    @Query("DELETE FROM wf_leave_types") protected abstract suspend fun clearLeaveTypes()
    @Query("DELETE FROM wf_leave_requests") protected abstract suspend fun clearLeaveRequests()
    @Query("DELETE FROM wf_leave_balances") protected abstract suspend fun clearLeaveBalances()
    @Query("DELETE FROM wf_corrections") protected abstract suspend fun clearCorrections()
    @Query("DELETE FROM wf_settings") protected abstract suspend fun clearSettings()
    @Insert(onConflict = OnConflictStrategy.REPLACE) protected abstract suspend fun insDepartments(x: List<WfDepartmentEntity>)
    @Insert(onConflict = OnConflictStrategy.REPLACE) protected abstract suspend fun insEmployees(x: List<WfEmployeeEntity>)
    @Insert(onConflict = OnConflictStrategy.REPLACE) protected abstract suspend fun insCredentials(x: List<WfCredentialEntity>)
    @Insert(onConflict = OnConflictStrategy.REPLACE) protected abstract suspend fun insShifts(x: List<WfShiftEntity>)
    @Insert(onConflict = OnConflictStrategy.REPLACE) protected abstract suspend fun insAssignments(x: List<WfAssignmentEntity>)
    @Insert(onConflict = OnConflictStrategy.REPLACE) protected abstract suspend fun insEvents(x: List<WfAttendanceEventEntity>)
    @Insert(onConflict = OnConflictStrategy.REPLACE) protected abstract suspend fun insLeaveTypes(x: List<WfLeaveTypeEntity>)
    @Insert(onConflict = OnConflictStrategy.REPLACE) protected abstract suspend fun insLeaveRequests(x: List<WfLeaveRequestEntity>)
    @Insert(onConflict = OnConflictStrategy.REPLACE) protected abstract suspend fun insLeaveBalances(x: List<WfLeaveBalanceEntity>)
    @Insert(onConflict = OnConflictStrategy.REPLACE) protected abstract suspend fun insCorrections(x: List<WfCorrectionEntity>)
    @Insert(onConflict = OnConflictStrategy.REPLACE) protected abstract suspend fun insSettings(x: List<WfSettingEntity>)

    @Transaction
    open suspend fun snapshot(): WorkforceSnapshot = WorkforceSnapshot(departments(), employees(), allCredentials(), shifts(), assignments(), allEvents(),
        leaveTypes(), leaveRequests(), leaveBalances(), corrections(), settings())

    @Query("SELECT * FROM wf_credentials") abstract suspend fun allCredentials(): List<WfCredentialEntity>

    /** Replaces all workforce data with a restored backup, atomically (the restored chain keeps its original seq numbers and hashes). */
    @Transaction
    open suspend fun replaceAll(s: WorkforceSnapshot) {
        clearDepartments(); clearEmployees(); clearCredentials(); clearShifts(); clearAssignments(); clearEvents()
        clearLeaveTypes(); clearLeaveRequests(); clearLeaveBalances(); clearCorrections(); clearSettings()
        insDepartments(s.departments); insEmployees(s.employees); insCredentials(s.credentials); insShifts(s.shifts); insAssignments(s.assignments)
        insEvents(s.events); insLeaveTypes(s.leaveTypes); insLeaveRequests(s.leaveRequests); insLeaveBalances(s.leaveBalances)
        insCorrections(s.corrections); insSettings(s.settings)
    }
}
