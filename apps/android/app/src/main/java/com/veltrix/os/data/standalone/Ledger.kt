package com.veltrix.os.data.standalone

import com.veltrix.os.data.api.Dashboard
import com.veltrix.os.data.api.NamedValue
import com.veltrix.os.data.api.PaymentInput
import com.veltrix.os.data.api.ReportColumn
import com.veltrix.os.data.api.ReportInfo
import com.veltrix.os.data.api.ReportTable
import com.veltrix.os.data.api.SalesDocument
import com.veltrix.os.data.api.SeriesPoint
import com.veltrix.os.data.api.StockBalance
import com.veltrix.os.data.db.CustomerEntity
import com.veltrix.os.data.db.LocalSaleEntity
import com.veltrix.os.data.db.LocalSaleLineEntity
import com.veltrix.os.data.db.ProductEntity
import com.veltrix.os.data.db.StockEntity
import com.veltrix.os.data.repo.CartLine
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import kotlin.math.round
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonPrimitive

/** Which app mode a license selects, and what is available in it. */
object ModePolicy {
    /** Legacy: a vendor-issued "Offline" certificate also meant standalone. Standalone is now selected by the device binding. */
    fun isStandalone(certificateType: String?): Boolean = certificateType == "Offline"

    /** Screens that inherently need the server (multi-device sync, accounting, HR, notifications/Telegram, branches). */
    val SERVER_ONLY_ROUTES = setOf("expenses", "employees", "notifications", "branch", "sync")
    /** Screens that only make sense standalone (local users, encrypted backup). */
    val STANDALONE_ONLY_ROUTES = setOf("users", "backup")

    fun isVisible(route: String, standalone: Boolean): Boolean =
        if (standalone) route !in SERVER_ONLY_ROUTES else route !in STANDALONE_ONLY_ROUTES

    fun syncAllowed(standalone: Boolean): Boolean = !standalone
}

@Serializable data class LocalReceiptLine(val name: String, val quantity: Double, val unitPrice: Double, val discount: Double, val total: Double)
@Serializable data class LocalReceipt(val number: String, val createdAt: Long, val cashier: String?, val lines: List<LocalReceiptLine>, val total: Double,
    val payments: List<PaymentInput>, val change: Double, val customerId: String? = null)

/** Pure bookkeeping for standalone mode (kept free of Android/Room so it is unit-tested on the JVM). */
object StandaloneLedger {
    const val REGISTER_ID = "local-register"
    const val WAREHOUSE_ID = "local-main"
    const val BRANCH_ID = "local-branch"
    const val WAREHOUSE_NAME = "Main"
    private val json = Json { ignoreUnknownKeys = true }

    data class BuiltSale(val sale: LocalSaleEntity, val lines: List<LocalSaleLineEntity>, val stockDeltas: Map<String, Double>, val receipt: LocalReceipt)

    fun round2(v: Double) = round(v * 100.0) / 100.0

    fun buildSale(id: String, number: String, cart: List<CartLine>, payments: List<PaymentInput>, now: Long, cashier: String?, customerId: String? = null): BuiltSale {
        require(cart.isNotEmpty()) { "The cart is empty." }
        val total = round2(cart.sumOf { it.total })
        val paid = payments.sumOf { it.amount }
        require(paid + 0.005 >= total) { "The payment does not cover the amount due." }
        val lines = cart.mapIndexed { i, l ->
            LocalSaleLineEntity("$id-$i", id, l.product.id, l.product.name, l.quantity, l.product.salePrice, round2(l.discount), round2(l.total), l.product.costPrice, now)
        }
        val receipt = LocalReceipt(number, now, cashier, cart.map { LocalReceiptLine(it.product.name, it.quantity, it.product.salePrice, round2(it.discount), round2(it.total)) },
            total, payments, round2((paid - total).coerceAtLeast(0.0)), customerId)
        val deltas = cart.filter { it.product.trackStock }.groupBy { it.product.id }.mapValues { (_, v) -> -v.sumOf { it.quantity } }
        val sale = LocalSaleEntity(id, number, total, cart.size, now, json.encodeToString(LocalReceipt.serializer(), receipt), synced = true)
        return BuiltSale(sale, lines, deltas, receipt)
    }

    fun receiptOf(sale: LocalSaleEntity): LocalReceipt? = runCatching { json.decodeFromString(LocalReceipt.serializer(), sale.payload) }.getOrNull()

    fun stockBalances(products: List<ProductEntity>, stock: List<StockEntity>): List<StockBalance> {
        val byId = stock.filter { it.warehouseId == WAREHOUSE_ID }.associateBy { it.productId }
        return products.filter { it.trackStock }.sortedBy { it.name }.map { p ->
            val q = byId[p.id]?.quantity ?: 0.0
            StockBalance(WAREHOUSE_ID, WAREHOUSE_NAME, p.id, p.sku, p.name, q, 0.0, q, p.costPrice, round2(q * p.costPrice), p.lowStockThreshold)
        }
    }

    fun dashboard(sales: List<LocalSaleEntity>, lines: List<LocalSaleLineEntity>, products: List<ProductEntity>, stock: List<StockEntity>,
        customers: List<CustomerEntity>, now: Instant, zone: ZoneId): Dashboard {
        val today = now.atZone(zone).toLocalDate()
        fun day(ms: Long) = Instant.ofEpochMilli(ms).atZone(zone).toLocalDate()
        val monthStart = today.withDayOfMonth(1)
        val prevStart = monthStart.minusMonths(1)
        val todays = sales.filter { day(it.createdAt) == today }
        val month = sales.filter { !day(it.createdAt).isBefore(monthStart) }
        val prev = sales.filter { val d = day(it.createdAt); !d.isBefore(prevStart) && d.isBefore(monthStart) }
        val monthLines = lines.filter { !day(it.createdAt).isBefore(monthStart) }
        val revenue = monthLines.sumOf { it.total }
        val cost = monthLines.sumOf { it.unitCost * it.quantity }
        val margin = if (revenue == 0.0) 0.0 else round2((revenue - cost) / revenue * 100.0)
        val series = (29 downTo 0).map { today.minusDays(it.toLong()) }.map { d -> SeriesPoint(d.toString(), round2(sales.filter { day(it.createdAt) == d }.sumOf { it.total })) }
        val top = monthLines.groupBy { it.name }.map { (n, l) -> NamedValue(n, round2(l.sumOf { it.total })) }.sortedByDescending { it.value }.take(5)
        val low = stockBalances(products, stock).count { it.lowStockThreshold > 0 && it.quantity <= it.lowStockThreshold }
        val todayTotal = round2(todays.sumOf { it.total })
        return Dashboard(todayTotal, round2(month.sumOf { it.total }), round2(prev.sumOf { it.total }), todays.size,
            if (todays.isEmpty()) 0.0 else round2(todayTotal / todays.size), margin, low, round2(customers.sumOf { it.balance.coerceAtLeast(0.0) }), 0.0, 0,
            series, top, emptyList())
    }

    val reports = listOf(ReportInfo("sales-by-day", "Sales by day"), ReportInfo("sales-by-product", "Sales by product"), ReportInfo("stock", "Stock on hand"))

    fun report(key: String, sales: List<LocalSaleEntity>, lines: List<LocalSaleLineEntity>, products: List<ProductEntity>, stock: List<StockEntity>,
        from: LocalDate, to: LocalDate, zone: ZoneId): ReportTable {
        fun inRange(ms: Long) = Instant.ofEpochMilli(ms).atZone(zone).toLocalDate().let { !it.isBefore(from) && !it.isAfter(to) }
        fun n(v: Number) = JsonPrimitive(v)
        fun t(v: String) = JsonPrimitive(v)
        return when (key) {
            "sales-by-day" -> {
                val s = sales.filter { inRange(it.createdAt) }
                val rows = s.groupBy { Instant.ofEpochMilli(it.createdAt).atZone(zone).toLocalDate() }.toSortedMap()
                    .map { (d, l) -> listOf(t(d.toString()), n(l.size), n(round2(l.sumOf { it.total }))) }
                ReportTable(key, "Sales by day", listOf(ReportColumn("date", "Date", "date"), ReportColumn("count", "Receipts", "number"), ReportColumn("total", "Total", "money")),
                    rows, mapOf("Receipts" to n(s.size), "Total" to n(round2(s.sumOf { it.total }))))
            }
            "sales-by-product" -> {
                val l = lines.filter { inRange(it.createdAt) }
                val rows = l.groupBy { it.name }.map { (name, g) -> Triple(name, g.sumOf { it.quantity }, g.sumOf { it.total }) to g.sumOf { it.total - it.unitCost * it.quantity } }
                    .sortedByDescending { it.first.third }
                    .map { (r, m) -> listOf(t(r.first), n(r.second), n(round2(r.third)), n(round2(m))) }
                ReportTable(key, "Sales by product", listOf(ReportColumn("name", "Product", "text"), ReportColumn("qty", "Quantity", "number"),
                    ReportColumn("total", "Total", "money"), ReportColumn("margin", "Margin", "money")), rows, mapOf("Total" to n(round2(l.sumOf { it.total }))))
            }
            "stock" -> {
                val b = stockBalances(products, stock)
                ReportTable(key, "Stock on hand", listOf(ReportColumn("sku", "SKU", "text"), ReportColumn("name", "Product", "text"), ReportColumn("qty", "Quantity", "number"),
                    ReportColumn("value", "Value", "money")), b.map { listOf(t(it.sku), t(it.productName), n(it.quantity), n(it.value)) }, mapOf("Value" to n(round2(b.sumOf { it.value }))))
            }
            else -> error("Unknown report: $key")
        }
    }

    fun salesHistory(sales: List<LocalSaleEntity>, zone: ZoneId): List<SalesDocument> = sales.sortedByDescending { it.createdAt }.map {
        SalesDocument(it.id, "POS", it.number, "Completed", "", Instant.ofEpochMilli(it.createdAt).atZone(zone).toLocalDate().toString(), it.total)
    }
}
