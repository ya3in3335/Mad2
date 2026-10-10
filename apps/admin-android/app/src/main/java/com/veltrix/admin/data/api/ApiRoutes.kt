package com.veltrix.admin.data.api

/**
 * Every server route used by the admin app, in one place (relative to the configured server URL).
 * Source of truth: docs/API_ADMIN.md and the module endpoint maps under modules/**.
 */
object ApiRoutes {
    // Identity (modules/identity)
    const val AUTH_LOGIN = "api/v1/auth/login"
    const val AUTH_REFRESH = "api/v1/auth/refresh"
    const val AUTH_LOGOUT = "api/v1/auth/logout"
    const val ME = "api/v1/me"
    /** Anonymous liveness probe used to verify the server address on first launch. */
    const val HEALTH_LIVE = "health/live"

    private const val P = "api/v1/platform"

    // Dashboards & health (modules/platform, modules/operations)
    const val PLATFORM_DASHBOARD = "$P/dashboard"
    const val OWNER_DASHBOARD = "$P/owner/dashboard"
    const val HEALTH = "$P/health"

    // Tenants (modules/platform, modules/tenants, modules/operations)
    const val TENANTS = "$P/tenants"
    fun tenant(id: String) = "$P/tenants/$id"
    fun tenantOverview(id: String) = "$P/tenants/$id/overview"
    fun tenantSuspend(id: String) = "$P/tenants/$id/suspend"
    fun tenantReactivate(id: String) = "$P/tenants/$id/reactivate"

    // Devices (modules/licensing, modules/operations)
    const val DEVICES = "$P/devices"
    fun device(id: String) = "$P/devices/$id"
    fun deviceDeactivate(id: String) = "$P/devices/$id/deactivate"
    fun deviceReassign(id: String) = "$P/devices/$id/reassign"
    fun deviceDiagnostics(id: String) = "$P/devices/$id/diagnostics"
    fun diagnosticReport(requestId: String) = "$P/diagnostics/$requestId/report"

    // Licenses (modules/licensing)
    const val LICENSES = "$P/licenses"
    const val LICENSE_DEMO = "$P/licenses/demo"
    const val ACTIVATION_FAILURES = "$P/licenses/activation-failures"
    fun license(id: String) = "$P/licenses/$id"
    fun licenseExtend(id: String) = "$P/licenses/$id/extend"
    fun licenseSeats(id: String) = "$P/licenses/$id/seats"
    fun licenseSuspend(id: String) = "$P/licenses/$id/suspend"
    fun licenseReactivate(id: String) = "$P/licenses/$id/reactivate"
    fun licenseRevoke(id: String) = "$P/licenses/$id/revoke"
    fun licenseEvents(id: String) = "$P/licenses/$id/events"

    // Subscriptions & billing (modules/subscriptions)
    const val PLANS = "$P/plans"
    const val SUBSCRIPTIONS = "$P/subscriptions"
    const val INVOICES = "$P/invoices"
    fun confirmPayment(invoiceId: String) = "$P/invoices/$invoiceId/confirm-payment"

    // Incidents (modules/operations)
    const val INCIDENTS = "$P/incidents"
    const val INCIDENT_GROUPS = "$P/incidents/groups"
    fun incident(id: String) = "$P/incidents/$id"
    fun incidentStatus(id: String) = "$P/incidents/$id/status"

    // Support, releases, audit (modules/platform)
    const val SUPPORT = "$P/support"
    fun supportReply(id: String) = "$P/support/$id/reply"
    const val RELEASES = "$P/releases"
    fun release(id: String) = "$P/releases/$id"
    const val AUDIT = "$P/audit"
    const val SECURITY_ALERTS = "$P/security-alerts"

    // Telegram (modules/notifications / operations)
    const val TELEGRAM_STATUS = "$P/telegram/status"
    const val TELEGRAM_DELIVERIES = "$P/telegram/deliveries"
    const val TELEGRAM_TEST = "$P/telegram/test"
}
