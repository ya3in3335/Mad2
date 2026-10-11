package com.veltrix.admin

import com.veltrix.admin.data.history.HistorySearch
import com.veltrix.admin.data.history.IssuedRecord
import com.veltrix.admin.data.license.DeviceCode
import com.veltrix.admin.data.license.OfflineIssuer
import com.veltrix.admin.ui.IssueForm
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class DeviceCodeAndExpiryTest {
    private val iid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301"
    private val kth = (0 until 32).joinToString("") { "%02x".format(it * 7) }
    private val code = DeviceCode.encode(iid, kth)

    @Test fun roundTrip() {
        assertEquals(64, code.length)
        val d = DeviceCode.decode(code)!!
        assertEquals(iid, d.installationId)
        assertEquals(kth, d.keyThumbprint)
    }

    @Test fun uuidIsBigEndianInFirst16Bytes() {
        val bytes = java.util.Base64.getUrlDecoder().decode(code)
        assertEquals(0x3f.toByte(), bytes[0]); assertEquals(0x25.toByte(), bytes[1]); assertEquals(0x01.toByte(), bytes[15])
        assertEquals(0x07.toByte(), bytes[17])
    }

    @Test fun groupedAndWrappedInputAccepted() {
        val grouped = DeviceCode.format(code)
        assertEquals(8, grouped.split(' ').size)
        assertEquals(iid, DeviceCode.decode(grouped)?.installationId)
        assertEquals(iid, DeviceCode.decode("  " + grouped.replace(" ", "\n") + "\t")?.installationId)
    }

    @Test fun invalidCodesRejected() {
        assertNull(DeviceCode.decode(null))
        assertNull(DeviceCode.decode(""))
        assertNull(DeviceCode.decode(code.dropLast(1)))
        assertNull(DeviceCode.decode(code + "A"))
        assertNull(DeviceCode.decode(code.dropLast(1) + "+"))
        assertNull(DeviceCode.decode(code.dropLast(1) + "="))
        assertFalse(DeviceCode.isValid("not a device code"))
        assertTrue(DeviceCode.isValid(code))
    }

    @Test fun shortForm() = assertEquals(code.take(6) + "…" + code.takeLast(6), DeviceCode.short(DeviceCode.format(code)))

    @Test fun expiryMath() {
        val iat = 1_760_000_000L
        assertEquals(iat + 30 * 86_400L, OfflineIssuer.expiry(iat, 30))
        assertEquals(iat + 365 * 86_400L, OfflineIssuer.expiry(iat, 365))
        assertEquals(iat + 36_500 * 86_400L, OfflineIssuer.expiry(iat, 36_500))
        assertTrue(runCatching { OfflineIssuer.expiry(iat, 0) }.isFailure)
        assertTrue(runCatching { OfflineIssuer.expiry(iat, 36_501) }.isFailure)
    }

    @Test fun daysLeftRoundsUpAndStopsAtZero() {
        val exp = 1_000_000L
        assertEquals(1, OfflineIssuer.daysLeft(exp, exp - 1))
        assertEquals(30, OfflineIssuer.daysLeft(exp, exp - 30 * 86_400L))
        assertEquals(0, OfflineIssuer.daysLeft(exp, exp))
        assertEquals(0, OfflineIssuer.daysLeft(exp, exp + 5))
    }

    @Test fun parseDays() {
        assertEquals(45, OfflineIssuer.parseDays(" 45 "))
        assertNull(OfflineIssuer.parseDays("0"))
        assertNull(OfflineIssuer.parseDays("36501"))
        assertNull(OfflineIssuer.parseDays("abc"))
        assertNull(OfflineIssuer.parseDays(null))
    }

    @Test fun formValidation() {
        assertEquals(IssueForm.Problem.BadDevice, IssueForm(deviceCode = "x", shop = "S").problem())
        assertEquals(IssueForm.Problem.NoShop, IssueForm(deviceCode = code, shop = " ").problem())
        assertEquals(IssueForm.Problem.BadDays, IssueForm(deviceCode = code, shop = "S", preset = null, customDays = "").problem())
        assertNull(IssueForm(deviceCode = code, shop = "S", preset = null, customDays = "120").problem())
        assertEquals(120, IssueForm(preset = null, customDays = "120").days())
        assertEquals(90, IssueForm(preset = 90).days())
    }

    @Test fun historySearch() {
        val a = IssuedRecord("l1", "Boutique Amine", code, 1, 2, 30, "paid cash", "c1")
        val b = IssuedRecord("l2", "Pharmacie Nour", "Z".repeat(64), 1, 2, 30, null, "c2")
        assertEquals(listOf(a, b), HistorySearch.filter(listOf(a, b), "  "))
        assertEquals(listOf(b), HistorySearch.filter(listOf(a, b), "nour"))
        assertEquals(listOf(a), HistorySearch.filter(listOf(a, b), "CASH"))
        assertEquals(listOf(a), HistorySearch.filter(listOf(a, b), DeviceCode.format(code).take(17)))
    }
}
