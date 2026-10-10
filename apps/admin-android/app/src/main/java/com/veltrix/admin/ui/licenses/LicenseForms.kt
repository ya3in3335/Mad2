package com.veltrix.admin.ui.licenses

import com.veltrix.admin.data.api.Plan
import java.time.Instant
import java.time.ZoneOffset

/** Commercial license terms offered by the issue form (server enum LicenseType minus Trial, which is issued via the demo route). */
enum class CommercialTerm(val wireType: String, val months: Long?) {
    Monthly("Monthly", 1), Quarterly("Quarterly", 3), Annual("Annual", 12), Perpetual("Perpetual", null), Enterprise("Enterprise", 12),
}

/** Validation + request building for the issue/renew/seat forms. Pure so it can be unit-tested. */
object LicenseForms {
    const val MAX_DEMO_DAYS = 90
    const val MAX_SEATS = 1000

    /** VELTRIX OS is sold as a single plan that includes every feature; seats are the commercial lever. */
    const val FULL_PLAN = "full"

    enum class Problem { NoTenant, BadSeats, BadDays, NoReason }

    /** The single sellable plan: "full", falling back to the first active plan. */
    fun singlePlan(plans: List<Plan>): Plan? = plans.firstOrNull { it.code == FULL_PLAN } ?: plans.firstOrNull { it.isActive }

    fun validateDemo(tenantId: String?, days: String, seats: String): Problem? = when {
        tenantId.isNullOrBlank() -> Problem.NoTenant
        days.toIntOrNull()?.let { it in 1..MAX_DEMO_DAYS } != true -> Problem.BadDays
        seats.toIntOrNull()?.let { it in 1..MAX_SEATS } != true -> Problem.BadSeats
        else -> null
    }

    fun validateCommercial(tenantId: String?, seats: String): Problem? = when {
        tenantId.isNullOrBlank() -> Problem.NoTenant
        seats.toIntOrNull()?.let { it in 1..MAX_SEATS } != true -> Problem.BadSeats
        else -> null
    }

    fun validateSeats(seats: String, reason: String): Problem? = when {
        seats.toIntOrNull()?.let { it in 1..MAX_SEATS } != true -> Problem.BadSeats
        reason.isBlank() -> Problem.NoReason
        else -> null
    }

    fun validateRenew(days: String): Problem? = if (days.toIntOrNull()?.let { it in 1..3660 } == true) null else Problem.BadDays

    /** Expiry for a commercial term starting [start] (UTC calendar months); null for perpetual. */
    fun expiry(term: CommercialTerm, start: Instant): Instant? =
        term.months?.let { start.atOffset(ZoneOffset.UTC).plusMonths(it).toInstant() }
}
