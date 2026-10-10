package com.veltrix.os

import com.veltrix.os.data.api.PaymentInput
import com.veltrix.os.data.db.ProductEntity
import com.veltrix.os.data.db.StockEntity
import com.veltrix.os.data.repo.CartLine
import com.veltrix.os.data.standalone.BackupCodec
import com.veltrix.os.data.standalone.BackupException
import com.veltrix.os.data.standalone.BackupPayload
import com.veltrix.os.data.standalone.LocalAuth
import com.veltrix.os.data.standalone.LocalAuthException
import com.veltrix.os.data.standalone.LocalRoles
import com.veltrix.os.data.standalone.LocalSignInStatus
import com.veltrix.os.data.standalone.ModePolicy
import com.veltrix.os.data.standalone.PasswordHasher
import com.veltrix.os.data.standalone.SecretStore
import com.veltrix.os.data.standalone.StandaloneLedger
import java.time.Instant
import java.time.ZoneOffset
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.double
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class StandaloneTest {
    private class MemoryStore : SecretStore {
        val map = HashMap<String, String>()
        override fun get(name: String) = map[name]
        override fun put(name: String, value: String?) { if (value == null) map.remove(name) else map[name] = value }
    }

    private var now = 1_791_000_000_000L
    private val fast = PasswordHasher.MIN_ITERATIONS // production: 210 000

    // ---------------------------------------------------------------- local auth
    @Test fun hashIsSaltedPbkdf2With210kIterations() {
        val a = PasswordHasher.hash("correct horse")
        assertTrue(a.startsWith("pbkdf2-sha256$210000$"))
        assertFalse(a == PasswordHasher.hash("correct horse"))
        assertTrue(PasswordHasher.verify("correct horse", a))
        assertFalse(PasswordHasher.verify("correct horsE", a))
        assertFalse(PasswordHasher.verify("x", "pbkdf2-sha256$1000\$AAAA\$AAAA"))
    }

    @Test fun secretPolicy() {
        assertEquals("password_short", LocalAuth.validateSecret("abc1234"))
        assertEquals("pin_short", LocalAuth.validateSecret("12345"))
        assertNull(LocalAuth.validateSecret("123456"))
        assertNull(LocalAuth.validateSecret("s3cret-pass"))
    }

    @Test fun noDefaultAccountAndRoles() {
        val auth = LocalAuth(MemoryStore(), { now }, fast)
        assertEquals(LocalSignInStatus.NoUsers, auth.signIn("admin", "admin").status)
        assertThrows(LocalAuthException::class.java) { auth.addUser("Karim", "12345678", LocalRoles.CASHIER) }
        val owner = auth.createOwner("Amina", "owner-pass-1")
        assertThrows(LocalAuthException::class.java) { auth.createOwner("Other", "owner-pass-2") }
        assertEquals(owner.id, auth.signIn(" amina ", "owner-pass-1").user!!.id)
        assertTrue(owner.toProfile().can("pos.discount"))
        val cashier = auth.addUser("Karim", "246810", LocalRoles.CASHIER)
        val p = auth.signIn("karim", "246810").user!!.toProfile()
        assertTrue(p.can("pos.sell")); assertFalse(p.can("pos.discount"))
        auth.removeUser(cashier.id)
        assertThrows(LocalAuthException::class.java) { auth.removeUser(owner.id) }
        assertFalse(auth.exportUsers().contains("owner-pass-1"))
    }

    @Test fun lockoutAfterFiveFailuresWithBackoffSurvivingRestart() {
        val store = MemoryStore()
        val auth = LocalAuth(store, { now }, fast)
        auth.createOwner("Amina", "owner-pass-1")
        repeat(4) { assertEquals(LocalSignInStatus.Invalid, auth.signIn("Amina", "nope-nope").status) }
        val fifth = auth.signIn("Amina", "nope-nope")
        assertEquals(LocalSignInStatus.LockedOut, fifth.status)
        assertEquals(30_000L, fifth.retryAfterMs)
        val restarted = LocalAuth(store, { now }, fast)
        assertEquals(LocalSignInStatus.LockedOut, restarted.signIn("Amina", "owner-pass-1").status)
        now += 31_000
        assertEquals(60_000L, restarted.signIn("Amina", "nope-nope").retryAfterMs)
        now += 61_000
        assertEquals(LocalSignInStatus.SignedIn, restarted.signIn("Amina", "owner-pass-1").status)
        assertEquals(LocalSignInStatus.Invalid, restarted.signIn("Amina", "nope-nope").status)
    }

    // ---------------------------------------------------------------- routing
    @Test fun offlineLicenseSelectsStandaloneAndHidesServerFeatures() {
        assertTrue(ModePolicy.isStandalone("Offline"))
        assertFalse(ModePolicy.isStandalone("Subscription"))
        assertFalse(ModePolicy.syncAllowed(true)); assertTrue(ModePolicy.syncAllowed(false))
        for (r in listOf("sync", "notifications", "employees", "expenses", "branch")) {
            assertFalse(ModePolicy.isVisible(r, standalone = true)); assertTrue(ModePolicy.isVisible(r, standalone = false))
        }
        for (r in listOf("users", "backup")) { assertTrue(ModePolicy.isVisible(r, true)); assertFalse(ModePolicy.isVisible(r, false)) }
        for (r in listOf("inventory", "orders", "reports", "license", "settings")) { assertTrue(ModePolicy.isVisible(r, true)); assertTrue(ModePolicy.isVisible(r, false)) }
    }

    // ---------------------------------------------------------------- sale → stock → report
    private val coffee = ProductEntity("p1", "COF", "Coffee", null, "Standard", "Active", null, 19.0, 120.0, true, "", "", costPrice = 60.0, lowStockThreshold = 9.0)
    private val cake = ProductEntity("p2", "CAK", "Cake", null, "Standard", "Active", null, 0.0, 200.0, true, "", "", costPrice = 120.0)

    @Test fun saleDecrementsStockAndFeedsDashboardAndReports() {
        val zone = ZoneOffset.UTC
        val sale = StandaloneLedger.buildSale("s1", "POS1-1", listOf(CartLine(coffee, 2.0), CartLine(cake, 1.0, 10.0)), listOf(PaymentInput("Cash", 500.0)), now, "Amina")
        assertEquals(420.0, sale.sale.total, 0.001) // 240 + 180
        assertEquals(80.0, sale.receipt.change, 0.001)
        assertEquals(mapOf("p1" to -2.0, "p2" to -1.0), sale.stockDeltas)
        assertThrows(IllegalArgumentException::class.java) {
            StandaloneLedger.buildSale("s2", "POS1-2", listOf(CartLine(cake, 1.0)), listOf(PaymentInput("Cash", 100.0)), now, null)
        }

        // Apply the deltas exactly like StandaloneDao.recordSale.
        val stock = mutableMapOf("p1" to 10.0, "p2" to 3.0)
        sale.stockDeltas.forEach { (k, d) -> stock[k] = stock.getValue(k) + d }
        val rows = stock.map { (k, q) -> StockEntity(k, StandaloneLedger.WAREHOUSE_ID, q, q) }
        val balances = StandaloneLedger.stockBalances(listOf(coffee, cake), rows).associateBy { it.productId }
        assertEquals(8.0, balances.getValue("p1").quantity, 0.0)
        assertEquals(2.0, balances.getValue("p2").quantity, 0.0)

        val dash = StandaloneLedger.dashboard(listOf(sale.sale), sale.lines, listOf(coffee, cake), rows, emptyList(), Instant.ofEpochMilli(now), zone)
        assertEquals(420.0, dash.salesToday, 0.001)
        assertEquals(1, dash.transactionsToday)
        assertEquals(1, dash.lowStockItems) // coffee 8 ≤ 9
        assertEquals("Coffee", dash.topProducts.first().name)
        assertEquals(42.86, dash.grossMarginMonth, 0.001) // (420 − 240) / 420
        assertEquals(420.0, dash.last30Days.last().value, 0.001)

        val day = Instant.ofEpochMilli(now).atZone(zone).toLocalDate()
        val byDay = StandaloneLedger.report("sales-by-day", listOf(sale.sale), sale.lines, listOf(coffee, cake), rows, day, day, zone)
        assertEquals(420.0, (byDay.summary.getValue("Total") as JsonPrimitive).double, 0.001)
        val byProduct = StandaloneLedger.report("sales-by-product", listOf(sale.sale), sale.lines, listOf(coffee, cake), rows, day, day, zone)
        assertEquals(2, byProduct.rows.size)
        val empty = StandaloneLedger.report("sales-by-day", listOf(sale.sale), sale.lines, listOf(coffee, cake), rows, day.plusDays(1), day.plusDays(2), zone)
        assertTrue(empty.rows.isEmpty())
        assertEquals("POS1-1", StandaloneLedger.salesHistory(listOf(sale.sale), zone).single().number)
        assertEquals("Amina", StandaloneLedger.receiptOf(sale.sale)!!.cashier)
    }

    // ---------------------------------------------------------------- backup
    @Test fun backupRoundTripWrongPasswordAndTamper() {
        val auth = LocalAuth(MemoryStore(), { now }, fast)
        auth.createOwner("Amina", "owner-pass-1")
        val sale = StandaloneLedger.buildSale("s1", "POS1-1", listOf(CartLine(coffee, 1.0)), listOf(PaymentInput("Cash", 120.0)), now, "Amina")
        val payload = BackupPayload(BackupCodec.VERSION, now, "t1", "Shop", auth.exportUsers(), listOf(coffee, cake), emptyList(),
            listOf(StockEntity("p1", StandaloneLedger.WAREHOUSE_ID, 9.0, 9.0)), emptyList(), listOf(sale.sale), sale.lines, 1)
        val file = BackupCodec.encrypt(payload, "owner-pass-1", fast)
        assertEquals("VLXBAK01", String(file.copyOfRange(0, 8), Charsets.US_ASCII))
        assertFalse(String(file, Charsets.ISO_8859_1).contains("Coffee"))

        assertEquals("backup_password", assertThrows(BackupException::class.java) { BackupCodec.decrypt(file, "owner-pass-2") }.key)
        val tampered = file.copyOf().also { it[it.size - 1] = (it[it.size - 1].toInt() xor 1).toByte() }
        assertThrows(BackupException::class.java) { BackupCodec.decrypt(tampered, "owner-pass-1") }
        assertEquals("backup_file", assertThrows(BackupException::class.java) { BackupCodec.decrypt(ByteArray(100), "x") }.key)

        val restored = BackupCodec.decrypt(file, "owner-pass-1")
        assertEquals(payload, restored)
        val newDevice = LocalAuth(MemoryStore(), { now }, fast)
        newDevice.importUsers(restored.usersJson)
        assertEquals(LocalSignInStatus.SignedIn, newDevice.signIn("Amina", "owner-pass-1").status)
    }
}
