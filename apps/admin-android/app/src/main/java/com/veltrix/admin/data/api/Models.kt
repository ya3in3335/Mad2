package com.veltrix.admin.data.api

import kotlinx.serialization.Serializable

// Wire contracts mirroring packages/contracts/Veltrix.Contracts (System.Text.Json web defaults: camelCase, enums as strings).
// Only fields the admin app uses are required; everything else is optional so the server can evolve compatibly.
// Dates are kept as ISO-8601 strings and formatted in the UI (see ui/common/Formatters.kt).

@Serializable data class ProblemResponse(val type: String? = null, val title: String? = null, val status: Int? = null, val detail: String? = null,
    val code: String? = null, val correlationId: String? = null, val errors: Map<String, List<String>>? = null)

@Serializable data class Paged<T>(val items: List<T>, val page: Int = 1, val pageSize: Int = 50, val totalCount: Int = 0)

// ---------- Identity ----------
@Serializable data class LoginRequest(val email: String, val password: String, val mfaCode: String? = null, val deviceId: String? = null)
@Serializable data class RefreshRequest(val refreshToken: String)
@Serializable data class LogoutRequest(val refreshToken: String)
@Serializable data class UserProfile(val id: String, val email: String, val fullName: String, val tenantId: String? = null, val isPlatformAdmin: Boolean = false,
    val roles: List<String> = emptyList(), val permissions: List<String> = emptyList(), val preferredLanguage: String? = null, val mfaEnabled: Boolean = false) {
    /** Client-side hint only — the server enforces every platform permission. */
    val hasPlatformAccess: Boolean get() = isPlatformAdmin || permissions.any { it == "*" || it.startsWith("platform.") }
}
/** Login answers either a token response or `{ "mfaRequired": true }`; both shapes decode into this. */
@Serializable data class LoginResponse(val accessToken: String? = null, val accessTokenExpiresAt: String? = null, val refreshToken: String? = null,
    val refreshTokenExpiresAt: String? = null, val user: UserProfile? = null, val mfaRequired: Boolean = false)
@Serializable data class TokenResponse(val accessToken: String, val accessTokenExpiresAt: String? = null, val refreshToken: String,
    val refreshTokenExpiresAt: String? = null, val user: UserProfile? = null)

// ---------- Platform ----------
@Serializable data class MonthValue(val month: String, val value: Double)
@Serializable data class PlatformDashboard(val tenants: Int = 0, val activeTenants: Int = 0, val suspendedTenants: Int = 0, val pendingSetup: Int = 0,
    val activeLicenses: Int = 0, val expiringLicenses14d: Int = 0, val activeDevices: Int = 0, val activations30d: Int = 0,
    val monthlyRecurringRevenue: Double = 0.0, val revenueThisMonth: Double = 0.0, val revenueByMonth: List<MonthValue> = emptyList(),
    val activationsByMonth: List<MonthValue> = emptyList(), val openSupportCases: Int = 0, val securityAlerts7d: Int = 0)

@Serializable data class OwnerDashboard(
    val totalTenants: Int = 0, val activeTenants: Int = 0, val suspendedTenants: Int = 0, val totalCustomers: Int = 0,
    val activeSubscriptions: Int = 0, val trialingSubscriptions: Int = 0, val pastDueSubscriptions: Int = 0, val expiredSubscriptions: Int = 0,
    val activeLicenses: Int = 0, val suspendedLicenses: Int = 0, val expiringLicenses14d: Int = 0,
    val registeredDevices: Int = 0, val onlineDevices: Int = 0, val onlineWindowMinutes: Int = 0, val androidInstallations: Int = 0, val windowsInstallations: Int = 0,
    val monthlyRecurringRevenue: Double = 0.0, val pendingManualPayments: Int = 0, val pendingManualPaymentsAmount: Double = 0.0,
    val newSupportTickets: Int = 0, val openSupportTickets: Int = 0, val unresolvedErrors: Int = 0, val criticalUnresolvedErrors: Int = 0,
    val failedSyncOperations24h: Int = 0, val deviceReportedFailedSyncOperations: Int = 0, val backendHealth: String = "unknown",
    val latestReleases: List<AppRelease> = emptyList(), val generatedAt: String? = null)

@Serializable data class WorkerStatus(val name: String, val lastRunAt: String? = null, val lastStatus: String = "", val lastError: String? = null, val stale: Boolean = false)
@Serializable data class SystemHealth(val status: String, val databaseReachable: Boolean = false, val pendingMigrations: List<String> = emptyList(),
    val workers: List<WorkerStatus> = emptyList(), val version: String = "", val serverTime: String? = null)

@Serializable data class AppRelease(val id: String, val platform: String, val version: String, val channel: String, val minSupportedVersion: String = "",
    val releaseNotes: String? = null, val downloadUrl: String? = null, val sha256: String? = null, val isActive: Boolean = true, val publishedAt: String? = null)
@Serializable data class UpsertReleaseRequest(val platform: String, val version: String, val channel: String, val minSupportedVersion: String,
    val releaseNotes: String?, val downloadUrl: String?, val sha256: String?, val isActive: Boolean)

@Serializable data class SupportMessage(val id: String, val authorId: String? = null, val fromVendor: Boolean = false, val body: String, val createdAt: String)
@Serializable data class SupportCase(val id: String, val tenantId: String? = null, val tenantName: String? = null, val subject: String, val status: String,
    val priority: String, val createdAt: String, val updatedAt: String, val messages: List<SupportMessage> = emptyList())
@Serializable data class ReplySupportCaseRequest(val body: String, val status: String?)

@Serializable data class AuditLog(val id: String, val tenantId: String? = null, val userId: String? = null, val deviceId: String? = null, val action: String,
    val entityType: String = "", val entityId: String? = null, val details: String? = null, val ipAddress: String? = null, val correlationId: String? = null,
    val occurredAt: String)
@Serializable data class SecurityAlert(val kind: String, val severity: String, val tenantId: String? = null, val userId: String? = null,
    val description: String, val occurredAt: String)

// ---------- Tenants ----------
@Serializable data class TenantSummary(val id: String, val name: String, val status: String, val setupCompleted: Boolean = false, val email: String? = null,
    val phone: String? = null, val wilaya: String? = null, val createdAt: String? = null, val planCode: String? = null, val subscriptionStatus: String? = null,
    val subscriptionEndsAt: String? = null, val activeDevices: Int = 0, val users: Int = 0)
@Serializable data class Company(val id: String, val name: String, val legalName: String? = null, val taxId: String? = null, val address: String? = null,
    val city: String? = null, val wilaya: String? = null, val country: String? = null, val phone: String? = null, val email: String? = null,
    val currency: String = "DZD", val locale: String? = null, val status: String = "", val setupCompleted: Boolean = false)
@Serializable data class TenantSupportCaseSummary(val id: String, val subject: String, val status: String, val priority: String, val createdAt: String, val updatedAt: String)
@Serializable data class TenantOverview(val company: Company, val suspendedReason: String? = null, val subscription: Subscription? = null,
    val licenses: List<License> = emptyList(), val devices: List<AdminDevice> = emptyList(), val activationHistory: List<LicenseEvent> = emptyList(),
    val invoices: List<BillingInvoice> = emptyList(), val supportCases: List<TenantSupportCaseSummary> = emptyList(), val users: Int = 0,
    val customers: Int = 0, val openIncidents: Int = 0, val diagnosticsConsent: Boolean = false)
@Serializable data class ReasonRequest(val reason: String)

// ---------- Licensing ----------
@Serializable data class License(val id: String, val tenantId: String, val tenantName: String? = null, val keyHint: String = "", val type: String,
    val status: String, val effectiveStatus: String = status, val planCode: String = "", val maxDevices: Int = 0, val activeDevices: Int = 0,
    val startsAt: String? = null, val expiresAt: String? = null, val offlineGraceDays: Int = 0, val entitlements: List<String> = emptyList(),
    val notes: String? = null, val createdAt: String? = null)
@Serializable data class LicenseEvent(val id: String, val licenseId: String? = null, val deviceId: String? = null, val type: String, val details: String? = null,
    val occurredAt: String)
@Serializable data class CreateLicenseRequest(val tenantId: String, val type: String, val planCode: String, val maxDevices: Int, val startsAt: String?,
    val expiresAt: String?, val offlineGraceDays: Int?, val entitlements: List<String>?, val notes: String?)
/** The full license key is shown exactly once, right after issuance. It is never persisted on the phone. */
@Serializable data class CreateLicenseResponse(val license: License, val licenseKey: String)
@Serializable data class IssueDemoLicenseRequest(val tenantId: String, val days: Int, val maxDevices: Int, val notes: String?)
@Serializable data class ExtendLicenseRequest(val days: Int, val reason: String?)
@Serializable data class ChangeLicenseSeatsRequest(val maxDevices: Int, val reason: String)
@Serializable data class ActivationFailure(val id: String, val tenantId: String? = null, val licenseId: String? = null, val code: String, val platform: String? = null,
    val installationId: String? = null, val ipAddress: String? = null, val occurredAt: String)

// ---------- Devices ----------
@Serializable data class AdminDevice(val id: String, val tenantId: String, val tenantName: String? = null, val licenseId: String, val platform: String,
    val deviceName: String, val appVersion: String = "", val osVersion: String? = null, val status: String, val online: Boolean = false,
    val activatedAt: String? = null, val lastContactAt: String? = null, val lastHeartbeatAt: String? = null, val lastSyncAt: String? = null,
    val pendingSyncOperations: Int? = null, val failedSyncOperations: Int? = null)
@Serializable data class DiagnosticRequest(val id: String, val deviceId: String, val tenantId: String? = null, val status: String, val reason: String? = null,
    val requestedAt: String, val expiresAt: String? = null, val deliveredAt: String? = null, val uploadedAt: String? = null, val consentGranted: Boolean = false)
@Serializable data class AdminDeviceDetail(val device: AdminDevice, val licensePlan: String? = null, val licenseType: String? = null, val licenseStatus: String? = null,
    val revokedAt: String? = null, val revokedReason: String? = null, val replacedByDeviceId: String? = null, val reportedErrors: Int = 0, val openIncidents: Int = 0,
    val history: List<LicenseEvent> = emptyList(), val diagnosticRequests: List<DiagnosticRequest> = emptyList(), val tenantDiagnosticsConsent: Boolean = false)
@Serializable data class ReassignDeviceRequest(val targetLicenseId: String, val reason: String)
@Serializable data class CreateDiagnosticRequestRequest(val reason: String?, val expiresInHours: Int?)
@Serializable data class DiagnosticReport(val request: DiagnosticRequest, val appVersion: String? = null, val osVersion: String? = null,
    val facts: Map<String, String> = emptyMap(), val recentLogLines: List<String> = emptyList())

// ---------- Incidents ----------
@Serializable data class Incident(val id: String, val fingerprint: String = "", val tenantId: String? = null, val tenantName: String? = null, val source: String = "",
    val app: String = "", val appVersion: String = "", val platform: String = "", val category: String, val errorType: String = "", val severity: String,
    val status: String, val title: String, val occurrenceCount: Int = 1, val firstSeenAt: String? = null, val lastSeenAt: String? = null, val lastDeviceId: String? = null)
@Serializable data class ErrorEvent(val id: String, val deviceId: String? = null, val osVersion: String? = null, val message: String, val context: String? = null,
    val occurredAt: String, val receivedAt: String? = null)
@Serializable data class IncidentDetail(val incident: Incident, val sampleStackTrace: String? = null, val statusNote: String? = null, val acknowledgedAt: String? = null,
    val resolvedAt: String? = null, val recentEvents: List<ErrorEvent> = emptyList(), val affectedDevices: Int = 0)
@Serializable data class IncidentGroup(val fingerprint: String, val category: String, val errorType: String = "", val title: String, val app: String = "",
    val maxSeverity: String, val incidents: Int = 0, val tenants: Int = 0, val occurrences: Int = 0, val openIncidents: Int = 0,
    val firstSeenAt: String? = null, val lastSeenAt: String? = null)
@Serializable data class UpdateIncidentStatusRequest(val status: String, val note: String?)

// ---------- Subscriptions & billing ----------
@Serializable data class Plan(val id: String, val code: String, val name: String, val description: String? = null, val monthlyPrice: Double = 0.0,
    val annualPrice: Double = 0.0, val currency: String = "DZD", val features: List<String> = emptyList(), val maxDevices: Int = 0, val maxUsers: Int = 0,
    val maxBranches: Int = 0, val trialDays: Int = 0, val isActive: Boolean = true, val isPublic: Boolean = true)
@Serializable data class Subscription(val id: String, val tenantId: String, val tenantName: String? = null, val planCode: String, val billingCycle: String = "",
    val status: String, val currentPeriodStart: String? = null, val currentPeriodEnd: String? = null, val seats: Int = 0, val cancelAtPeriodEnd: Boolean = false,
    val licenseId: String? = null, val periodPrice: Double = 0.0, val currency: String = "DZD", val createdAt: String? = null)
@Serializable data class BillingInvoice(val id: String, val number: String, val tenantId: String, val tenantName: String? = null, val subscriptionId: String? = null,
    val periodStart: String? = null, val periodEnd: String? = null, val amount: Double = 0.0, val tax: Double = 0.0, val total: Double = 0.0,
    val currency: String = "DZD", val status: String, val issuedAt: String? = null, val dueDate: String? = null, val paidAt: String? = null,
    val paymentMethod: String? = null, val paymentReference: String? = null, val description: String? = null)
@Serializable data class ConfirmPaymentRequest(val method: String, val reference: String?, val paidAt: String?)

// ---------- Telegram ----------
@Serializable data class TelegramStatus(val enabled: Boolean = false, val botTokenConfigured: Boolean = false, val webhookSecretConfigured: Boolean = false,
    val allowedChats: Int = 0, val pending: Int = 0, val failed: Int = 0, val sent24h: Int = 0, val skipped24h: Int = 0, val lastSentAt: String? = null,
    val lastError: String? = null)
@Serializable data class TelegramDelivery(val id: String, val type: String, val severity: String = "", val text: String = "", val status: String,
    val attempts: Int = 0, val createdAt: String, val sentAt: String? = null, val lastError: String? = null)
@Serializable data class TelegramTestMessageRequest(val text: String?)
@Serializable data class TelegramTestMessageResponse(val queued: Int = 0)
