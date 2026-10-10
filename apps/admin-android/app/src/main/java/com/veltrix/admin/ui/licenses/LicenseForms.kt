package com.veltrix.admin.ui.licenses

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

    enum class Problem { NoTenant, BadSeats, BadDays, NoPlan, NoReason }

    fun validateDemo(tenantId: String?, days: String, seats: String): Problem? = when {
        tenantId.isNullOrBlank() -> Problem.NoTenant
        days.toIntOrNull()?.let { it in 1..MAX_DEMO_DAYS } != true -> Problem.BadDays
        seats.toIntOrNull()?.let { it in 1..MAX_SEATS } != true -> Problem.BadSeats
        else -> null
    }

    fun validateCommercial(tenantId: String?, planCode: String?, seats: String): Problem? = when {
        tenantId.isNullOrBlank() -> Problem.NoTenant
        planCode.isNullOrBlank() -> Problem.NoPlan
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
