package com.veltrix.admin.data.repo

import com.veltrix.admin.data.api.*
import javax.inject.Inject
import javax.inject.Singleton

/** Platform-administration API surface. Every call is authorized server-side by platform permissions. */
@Singleton
class AdminRepository @Inject constructor(private val api: ApiClient) {

    private fun stepUp(password: String) = mapOf(ApiClient.CONFIRM_PASSWORD to password)

    // ---- Dashboard ----
    suspend fun platformDashboard(): PlatformDashboard = api.get(ApiRoutes.PLATFORM_DASHBOARD)
    suspend fun ownerDashboard(): OwnerDashboard = api.get(ApiRoutes.OWNER_DASHBOARD)
    suspend fun health(): SystemHealth = api.get(ApiRoutes.HEALTH)

    // ---- Tenants ----
    suspend fun tenants(page: Int, pageSize: Int, search: String?, status: String?): Paged<TenantSummary> =
        api.get(ApiRoutes.TENANTS, mapOf("page" to page, "pageSize" to pageSize, "search" to search, "status" to status))
    suspend fun tenantOverview(id: String): TenantOverview = api.get(ApiRoutes.tenantOverview(id))
    suspend fun suspendTenant(id: String, reason: String, password: String) =
        api.postNoContent(ApiRoutes.tenantSuspend(id), ReasonRequest(reason), stepUp(password))
    suspend fun reactivateTenant(id: String, password: String) =
        api.postNoContent(ApiRoutes.tenantReactivate(id), emptyMap<String, String>(), stepUp(password))

    // ---- Devices ----
    suspend fun devices(page: Int, pageSize: Int, search: String?, platform: String?, online: Boolean?, status: String?, tenantId: String? = null): Paged<AdminDevice> =
        api.get(ApiRoutes.DEVICES, mapOf("page" to page, "pageSize" to pageSize, "search" to search, "platform" to platform, "online" to online,
            "status" to status, "tenantId" to tenantId))
    suspend fun device(id: String): AdminDeviceDetail = api.get(ApiRoutes.device(id))
    suspend fun deactivateDevice(id: String, reason: String, password: String) =
        api.postNoContent(ApiRoutes.deviceDeactivate(id), ReasonRequest(reason), stepUp(password))
    suspend fun reassignDevice(id: String, targetLicenseId: String, reason: String, password: String) =
        api.postNoContent(ApiRoutes.deviceReassign(id), ReassignDeviceRequest(targetLicenseId, reason), stepUp(password))
    suspend fun requestDiagnostics(deviceId: String, reason: String?) =
        api.postNoContent(ApiRoutes.deviceDiagnostics(deviceId), CreateDiagnosticRequestRequest(reason, 24))
    suspend fun diagnosticReport(requestId: String): DiagnosticReport = api.get(ApiRoutes.diagnosticReport(requestId))

    // ---- Licenses ----
    suspend fun licenses(page: Int, pageSize: Int, search: String?, tenantId: String? = null): Paged<License> =
        api.get(ApiRoutes.LICENSES, mapOf("page" to page, "pageSize" to pageSize, "search" to search, "tenantId" to tenantId))
    suspend fun license(id: String): License = api.get(ApiRoutes.license(id))
    suspend fun licenseEvents(id: String): List<LicenseEvent> = api.get(ApiRoutes.licenseEvents(id))
    suspend fun activationFailures(licenseId: String?, tenantId: String? = null): Paged<ActivationFailure> =
        api.get(ApiRoutes.ACTIVATION_FAILURES, mapOf("licenseId" to licenseId, "tenantId" to tenantId, "pageSize" to 50))
    suspend fun issueDemo(req: IssueDemoLicenseRequest): CreateLicenseResponse = api.post(ApiRoutes.LICENSE_DEMO, req)
    suspend fun issueCommercial(req: CreateLicenseRequest): CreateLicenseResponse = api.post(ApiRoutes.LICENSES, req)
    suspend fun renewLicense(id: String, days: Int, reason: String?) = api.postNoContent(ApiRoutes.licenseExtend(id), ExtendLicenseRequest(days, reason))
    suspend fun changeSeats(id: String, seats: Int, reason: String) = api.postNoContent(ApiRoutes.licenseSeats(id), ChangeLicenseSeatsRequest(seats, reason))
    suspend fun suspendLicense(id: String, reason: String, password: String) =
        api.postNoContent(ApiRoutes.licenseSuspend(id), ReasonRequest(reason), stepUp(password))
    suspend fun reactivateLicense(id: String, reason: String, password: String) = api.postNoContent(ApiRoutes.licenseReactivate(id), ReasonRequest(reason), stepUp(password))
    suspend fun revokeLicense(id: String, reason: String, password: String) =
        api.postNoContent(ApiRoutes.licenseRevoke(id), ReasonRequest(reason), stepUp(password))

    // ---- Subscriptions & payments ----
    suspend fun plans(): List<Plan> = api.get(ApiRoutes.PLANS)
    suspend fun subscriptions(page: Int, pageSize: Int, status: String?): Paged<Subscription> =
        api.get(ApiRoutes.SUBSCRIPTIONS, mapOf("page" to page, "pageSize" to pageSize, "status" to status))
    suspend fun invoices(page: Int, pageSize: Int, status: String?, tenantId: String? = null): Paged<BillingInvoice> =
        api.get(ApiRoutes.INVOICES, mapOf("page" to page, "pageSize" to pageSize, "status" to status, "tenantId" to tenantId))
    /** Manual payment confirmation: step-up + idempotency key so a retried tap can never double-confirm. */
    suspend fun confirmPayment(invoiceId: String, req: ConfirmPaymentRequest, password: String, idempotencyKey: String) =
        api.postNoContent(ApiRoutes.confirmPayment(invoiceId), req, stepUp(password) + (ApiClient.IDEMPOTENCY_KEY to idempotencyKey))

    // ---- Incidents ----
    suspend fun incidents(page: Int, pageSize: Int, status: String?, severity: String?, category: String?, search: String?): Paged<Incident> =
        api.get(ApiRoutes.INCIDENTS, mapOf("page" to page, "pageSize" to pageSize, "status" to status, "severity" to severity, "category" to category, "search" to search))
    suspend fun incidentGroups(status: String?): List<IncidentGroup> = api.get(ApiRoutes.INCIDENT_GROUPS, mapOf("status" to status))
    suspend fun incident(id: String): IncidentDetail = api.get(ApiRoutes.incident(id))
    suspend fun updateIncidentStatus(id: String, status: String, note: String?) =
        api.postNoContent(ApiRoutes.incidentStatus(id), UpdateIncidentStatusRequest(status, note))

    // ---- Support ----
    suspend fun supportCases(status: String?): List<SupportCase> = api.get(ApiRoutes.SUPPORT, mapOf("status" to status))
    suspend fun replySupport(id: String, body: String, status: String?) = api.postNoContent(ApiRoutes.supportReply(id), ReplySupportCaseRequest(body, status))

    // ---- Releases ----
    suspend fun releases(): List<AppRelease> = api.get(ApiRoutes.RELEASES)
    suspend fun createRelease(r: UpsertReleaseRequest) = api.postNoContent(ApiRoutes.RELEASES, r)
    suspend fun updateRelease(id: String, r: UpsertReleaseRequest) { api.send<Unit>("PUT", ApiRoutes.release(id), emptyMap(), api.json.encodeToString(UpsertReleaseRequest.serializer(), r), null) }

    // ---- Audit ----
    suspend fun audit(page: Int, pageSize: Int, action: String?, tenantId: String?, search: String?): Paged<AuditLog> =
        api.get(ApiRoutes.AUDIT, mapOf("page" to page, "pageSize" to pageSize, "action" to action, "tenantId" to tenantId, "search" to search))
    suspend fun securityAlerts(days: Int = 7): List<SecurityAlert> = api.get(ApiRoutes.SECURITY_ALERTS, mapOf("days" to days))

    // ---- Telegram ----
    suspend fun telegramStatus(): TelegramStatus = api.get(ApiRoutes.TELEGRAM_STATUS)
    suspend fun telegramDeliveries(): Paged<TelegramDelivery> = api.get(ApiRoutes.TELEGRAM_DELIVERIES, mapOf("pageSize" to 20))
    suspend fun telegramTest(text: String?): TelegramTestMessageResponse = api.post(ApiRoutes.TELEGRAM_TEST, TelegramTestMessageRequest(text))
}
