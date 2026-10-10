package com.veltrix.os.data.repo

import com.veltrix.os.data.api.AppSettings
import com.veltrix.os.data.api.Dashboard
import com.veltrix.os.data.api.Paged
import com.veltrix.os.data.api.PaymentInput
import com.veltrix.os.data.api.ReportInfo
import com.veltrix.os.data.api.ReportTable
import com.veltrix.os.data.api.SalesDocument
import com.veltrix.os.data.api.StockBalance
import com.veltrix.os.data.db.CustomerEntity
import com.veltrix.os.data.db.LocalSaleEntity
import com.veltrix.os.data.db.ProductEntity
import com.veltrix.os.data.db.RegisterEntity
import com.veltrix.os.data.db.VeltrixDatabase
import com.veltrix.os.data.security.SecureStore
import com.veltrix.os.data.standalone.BackupCodec
import com.veltrix.os.data.standalone.BackupException
import com.veltrix.os.data.standalone.BackupPayload
import com.veltrix.os.data.standalone.LocalAuth
import com.veltrix.os.data.standalone.LocalAuthException
import com.veltrix.os.data.standalone.LocalUser
import com.veltrix.os.data.standalone.StandaloneLedger
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.UUID
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.withContext

/**
 * Local repository for standalone (offline-licensed) installations: every read and write goes to the Room database on this device,
 * nothing is sent to a server and nothing is queued in the sync outbox. Screens get the same models as from the API.
 */
@Singleton
class StandaloneRepository @Inject constructor(private val db: VeltrixDatabase, store: SecureStore, private val settings: AppSettings) {
    val auth = LocalAuth(store)
    private val dao get() = db.standalone()
    private val zone: ZoneId get() = ZoneId.systemDefault()

    /** The single local register/warehouse; selected automatically so the POS works without a branch setup. */
    suspend fun ensureRegister() {
        if (dao.allRegisters().none { it.id == StandaloneLedger.REGISTER_ID })
            dao.upsertRegister(RegisterEntity(StandaloneLedger.REGISTER_ID, StandaloneLedger.BRANCH_ID, StandaloneLedger.WAREHOUSE_ID, "POS1", "POS 1", null))
        settings.registerId = StandaloneLedger.REGISTER_ID
        settings.warehouseId = StandaloneLedger.WAREHOUSE_ID
        settings.branchId = StandaloneLedger.BRANCH_ID
    }

    // ---- catalog / customers / stock
    fun products(q: String): Flow<List<ProductEntity>> = dao.products(q)

    suspend fun saveProduct(id: String?, sku: String, name: String, price: Double, cost: Double, taxRate: Double, trackStock: Boolean, lowStock: Double,
        barcodes: String): ProductEntity {
        require(sku.isNotBlank() && name.isNotBlank()) { "SKU / name" }
        require(price >= 0 && cost >= 0) { "Price" }
        val pid = id ?: UUID.randomUUID().toString()
        if (dao.skuTaken(sku.trim(), pid) > 0) throw IllegalArgumentException("SKU ${sku.trim()}")
        val p = ProductEntity(pid, sku.trim(), name.trim(), null, "Standard", "Active", null, taxRate, price, trackStock,
            barcodes.split(',', ';', '\n').map { it.trim() }.filter { it.isNotEmpty() }.distinct().joinToString(","), Instant.now().toString(), cost, lowStock)
        dao.upsertProduct(p)
        return p
    }

    suspend fun addCustomer(name: String, phone: String?): CustomerEntity {
        require(name.isNotBlank()) { "Name" }
        val c = CustomerEntity(UUID.randomUUID().toString(), "C%05d".format(dao.customerCount() + 1), name.trim(), phone?.ifBlank { null }, 0.0, 0.0, 0.0, Instant.now().toString())
        dao.upsertCustomer(c)
        return c
    }

    suspend fun stock(): Paged<StockBalance> = StandaloneLedger.stockBalances(dao.allProducts(), dao.allStock()).let { Paged(it, 1, it.size, it.size) }

    suspend fun adjustStock(productId: String, delta: Double) = dao.adjustStock(productId, StandaloneLedger.WAREHOUSE_ID, delta)

    // ---- POS
    suspend fun completeSale(cart: List<CartLine>, payments: List<PaymentInput>, cashier: String?): LocalSaleEntity {
        ensureRegister()
        val now = System.currentTimeMillis()
        val counter = settings.receiptCounter + 1
        settings.receiptCounter = counter
        val number = "POS1-" + DateTimeFormatter.ofPattern("yyMMdd").format(Instant.ofEpochMilli(now).atZone(zone)) + "-%05d".format(counter)
        val built = StandaloneLedger.buildSale(UUID.randomUUID().toString(), number, cart, payments, now, cashier)
        dao.recordSale(built.sale, built.lines, built.stockDeltas, StandaloneLedger.WAREHOUSE_ID)
        return built.sale
    }

    // ---- dashboard / reports / history
    suspend fun dashboard(): Dashboard {
        val since = LocalDate.now(zone).minusMonths(2).atStartOfDay(zone).toInstant().toEpochMilli()
        return StandaloneLedger.dashboard(dao.salesSince(since), dao.linesSince(since), dao.allProducts(), dao.allStock(), dao.allCustomers(), Instant.now(), zone)
    }

    fun reports(): List<ReportInfo> = StandaloneLedger.reports

    suspend fun report(key: String, from: LocalDate, to: LocalDate): ReportTable =
        StandaloneLedger.report(key, dao.allSales(), dao.allLines(), dao.allProducts(), dao.allStock(), from, to, zone)

    suspend fun salesHistory(): Paged<SalesDocument> = StandaloneLedger.salesHistory(dao.allSales(), zone).let { Paged(it, 1, it.size, it.size) }

    // ---- backup
    /** Owner only; the owner's password (re-entered) is the backup key. */
    suspend fun exportBackup(user: LocalUser, password: String): ByteArray = withContext(Dispatchers.Default) {
        if (!user.isOwner) throw LocalAuthException("owner_only")
        if (!auth.verify(user.id, password)) throw LocalAuthException("invalid")
        val payload = BackupPayload(BackupCodec.VERSION, System.currentTimeMillis(), settings.tenantId, settings.tenantName, auth.exportUsers(),
            dao.allProducts(), dao.allCustomers(), dao.allStock(), dao.allRegisters(), dao.allSales(), dao.allLines(), settings.receiptCounter)
        BackupCodec.encrypt(payload, password)
    }

    /** Restores data and local accounts. Requires a valid license on this device (checked by the caller via [licenseValid]). */
    suspend fun restoreBackup(file: ByteArray, password: String, licenseValid: Boolean) = withContext(Dispatchers.Default) {
        if (!licenseValid) throw BackupException("license_required")
        val p = BackupCodec.decrypt(file, password)
        dao.replaceAll(p.products, p.customers, p.stock, p.registers, p.sales, p.lines)
        auth.importUsers(p.usersJson)
        settings.receiptCounter = maxOf(settings.receiptCounter, p.receiptCounter)
        settings.shiftId = null
        ensureRegister()
    }
}
