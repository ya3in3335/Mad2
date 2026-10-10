package com.veltrix.admin

import com.veltrix.admin.ui.common.ChartMath
import com.veltrix.admin.ui.common.Formatters
import com.veltrix.admin.ui.common.StatusTone
import com.veltrix.admin.ui.common.Tone
import com.veltrix.admin.ui.licenses.CommercialTerm
import com.veltrix.admin.ui.licenses.LicenseForms
import com.veltrix.admin.ui.licenses.LicenseForms.Problem
import java.time.Instant
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class LogicTest {
    @Test fun normalizeScalesToMaxAndClampsNegatives() =
        assertEquals(listOf(0.5f, 1f, 0f), ChartMath.normalize(listOf(5.0, 10.0, -3.0)))

    @Test fun normalizeHandlesEmptyAndZero() {
        assertEquals(emptyList<Float>(), ChartMath.normalize(emptyList()))
        assertEquals(listOf(0f, 0f), ChartMath.normalize(listOf(0.0, 0.0)))
    }

    @Test fun sharesSumToOne() = assertEquals(listOf(0.25f, 0.75f), ChartMath.shares(listOf(1.0, 3.0)))

    @Test fun niceCeiling() {
        assertEquals(1.0, ChartMath.niceCeiling(0.0), 0.0)
        assertEquals(250.0, ChartMath.niceCeiling(210.0), 1e-9)
        assertEquals(5000.0, ChartMath.niceCeiling(4100.0), 1e-9)
        assertEquals(10.0, ChartMath.niceCeiling(7.0), 1e-9)
    }

    @Test fun compactNumbers() {
        assertEquals("950", Formatters.compact(950.0))
        assertEquals("12.4K", Formatters.compact(12_400.0))
        assertEquals("3.2M", Formatters.compact(3_200_000.0))
        assertEquals("2K", Formatters.compact(2_000.0))
    }

    @Test fun statusTones() {
        assertEquals(Tone.Positive, StatusTone.of("Active"))
        assertEquals(Tone.Warning, StatusTone.of("past_due"))
        assertEquals(Tone.Negative, StatusTone.of("REVOKED"))
        assertEquals(Tone.Neutral, StatusTone.of(null))
    }

    @Test fun demoLicenseValidation() {
        assertEquals(Problem.NoTenant, LicenseForms.validateDemo(null, "14", "1"))
        assertEquals(Problem.BadDays, LicenseForms.validateDemo("t", "91", "1"))
        assertEquals(Problem.BadSeats, LicenseForms.validateDemo("t", "14", "0"))
        assertNull(LicenseForms.validateDemo("t", "14", "3"))
    }

    @Test fun seatChangeRequiresReason() {
        assertEquals(Problem.NoReason, LicenseForms.validateSeats("5", " "))
        assertNull(LicenseForms.validateSeats("5", "customer upgrade"))
    }

    @Test fun commercialExpiryUsesCalendarMonths() {
        val start = Instant.parse("2026-01-31T00:00:00Z")
        assertEquals(Instant.parse("2026-02-28T00:00:00Z"), LicenseForms.expiry(CommercialTerm.Monthly, start))
        assertEquals(Instant.parse("2027-01-31T00:00:00Z"), LicenseForms.expiry(CommercialTerm.Annual, start))
        assertNull(LicenseForms.expiry(CommercialTerm.Perpetual, start))
    }
}
