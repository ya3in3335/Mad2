package com.veltrix.os.data.db

import androidx.room.ColumnInfo
import androidx.room.Dao
import androidx.room.Database
import androidx.room.Entity
import androidx.room.Index
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.RoomDatabase
import androidx.room.Transaction
import androidx.room.Upsert
import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase
import kotlinx.coroutines.flow.Flow
import kotlinx.serialization.Serializable

@Serializable
@Entity(tableName = "products")
data class ProductEntity(
    @PrimaryKey val id: String, val sku: String, val name: String, val nameAr: String?, val type: String, val status: String,
    val categoryId: String?, val taxRatePercent: Double, val salePrice: Double, val trackStock: Boolean, val barcodes: String, val updatedAt: String,
    /** v2: purchase cost and low-stock threshold, maintained locally in standalone mode. */
    @ColumnInfo(defaultValue = "0") val costPrice: Double = 0.0,
    @ColumnInfo(defaultValue = "0") val lowStockThreshold: Double = 0.0,
)

@Serializable
@Entity(tableName = "customers")
data class CustomerEntity(
    @PrimaryKey val id: String, val code: String, val name: String, val phone: String?, val balance: Double, val loyaltyPoints: Double,
    val creditLimit: Double, val updatedAt: String, val pendingSync: Boolean = false,
)

@Serializable
@Entity(tableName = "stock", primaryKeys = ["productId", "warehouseId"])
data class StockEntity(val productId: String, val warehouseId: String, val quantity: Double, val available: Double)

@Serializable
@Entity(tableName = "registers")
data class RegisterEntity(@PrimaryKey val id: String, val branchId: String, val warehouseId: String, val code: String, val name: String, val openShiftId: String?)

/** Durable synchronization outbox. Operations are append-only and replay-safe (OpId = idempotency key). */
@Entity(tableName = "outbox")
data class OutboxEntity(
    @PrimaryKey val opId: String, val type: String, val payload: String, val clientTime: String, val baseVersion: String?,
    val status: String = STATUS_PENDING, val attempts: Int = 0, val lastError: String? = null, val createdAt: Long = System.currentTimeMillis(), val seq: Long,
) {
    companion object {
        const val STATUS_PENDING = "pending"
        const val STATUS_SYNCED = "synced"
        const val STATUS_REJECTED = "rejected"
        const val STATUS_CONFLICT = "conflict"
    }
}

@Serializable
@Entity(tableName = "local_sales")
data class LocalSaleEntity(@PrimaryKey val id: String, val number: String, val total: Double, val itemCount: Int, val createdAt: Long, val payload: String, val synced: Boolean = false)

/** v2: lines of locally recorded sales (standalone reports, top products, margin). */
@Serializable
@Entity(tableName = "local_sale_lines", indices = [Index("saleId"), Index("createdAt")])
data class LocalSaleLineEntity(
    @PrimaryKey val id: String, val saleId: String, val productId: String, val name: String, val quantity: Double, val unitPrice: Double,
    val discount: Double, val total: Double, val unitCost: Double, val createdAt: Long,
)

/** Additive v1 → v2 migration: existing products, customers, sales and the outbox are kept. */
val MIGRATION_1_2 = object : Migration(1, 2) {
    override fun migrate(db: SupportSQLiteDatabase) {
        db.execSQL("ALTER TABLE `products` ADD COLUMN `costPrice` REAL NOT NULL DEFAULT 0")
        db.execSQL("ALTER TABLE `products` ADD COLUMN `lowStockThreshold` REAL NOT NULL DEFAULT 0")
        db.execSQL("CREATE TABLE IF NOT EXISTS `local_sale_lines` (`id` TEXT NOT NULL, `saleId` TEXT NOT NULL, `productId` TEXT NOT NULL, `name` TEXT NOT NULL, " +
            "`quantity` REAL NOT NULL, `unitPrice` REAL NOT NULL, `discount` REAL NOT NULL, `total` REAL NOT NULL, `unitCost` REAL NOT NULL, `createdAt` INTEGER NOT NULL, PRIMARY KEY(`id`))")
        db.execSQL("CREATE INDEX IF NOT EXISTS `index_local_sale_lines_saleId` ON `local_sale_lines` (`saleId`)")
        db.execSQL("CREATE INDEX IF NOT EXISTS `index_local_sale_lines_createdAt` ON `local_sale_lines` (`createdAt`)")
    }
}

@Dao
interface CatalogDao {
    @Upsert suspend fun upsertProducts(items: List<ProductEntity>)
    @Upsert suspend fun upsertCustomers(items: List<CustomerEntity>)
    @Upsert suspend fun upsertStock(items: List<StockEntity>)
    @Upsert suspend fun upsertRegisters(items: List<RegisterEntity>)
    @Query("SELECT * FROM products WHERE status = 'Active' AND (:q = '' OR name LIKE '%' || :q || '%' OR sku LIKE '%' || :q || '%' OR nameAr LIKE '%' || :q || '%') ORDER BY name LIMIT 300")
    fun searchProducts(q: String): Flow<List<ProductEntity>>
    @Query("SELECT * FROM products WHERE sku = :code OR (',' || barcodes || ',') LIKE '%,' || :code || ',%' LIMIT 1")
    suspend fun findByCode(code: String): ProductEntity?
    @Query("SELECT * FROM products WHERE id = :id") suspend fun product(id: String): ProductEntity?
    @Query("SELECT * FROM customers WHERE (:q = '' OR name LIKE '%' || :q || '%' OR phone LIKE '%' || :q || '%' OR code LIKE '%' || :q || '%') ORDER BY name LIMIT 300")
    fun searchCustomers(q: String): Flow<List<CustomerEntity>>
    @Query("SELECT * FROM stock WHERE warehouseId = :warehouseId") fun stock(warehouseId: String): Flow<List<StockEntity>>
    @Query("SELECT * FROM stock WHERE productId = :productId AND warehouseId = :warehouseId") suspend fun stockOf(productId: String, warehouseId: String): StockEntity?
    @Query("UPDATE stock SET quantity = quantity - :qty, available = available - :qty WHERE productId = :productId AND warehouseId = :warehouseId")
    suspend fun decrementStock(productId: String, warehouseId: String, qty: Double)
    @Query("SELECT * FROM registers ORDER BY code") fun registers(): Flow<List<RegisterEntity>>
    @Query("SELECT COUNT(*) FROM products") suspend fun productCount(): Int
}

@Dao
interface OutboxDao {
    @Insert(onConflict = OnConflictStrategy.IGNORE) suspend fun insert(op: OutboxEntity)
    @Query("SELECT * FROM outbox WHERE status = 'pending' ORDER BY seq LIMIT :limit") suspend fun pending(limit: Int = 100): List<OutboxEntity>
    @Query("SELECT COUNT(*) FROM outbox WHERE status = 'pending'") fun pendingCount(): Flow<Int>
    @Query("SELECT COUNT(*) FROM outbox WHERE status IN ('rejected','conflict')") fun attentionCount(): Flow<Int>
    @Query("SELECT * FROM outbox WHERE status IN ('rejected','conflict') ORDER BY seq DESC") fun needingAttention(): Flow<List<OutboxEntity>>
    @Query("UPDATE outbox SET status = :status, lastError = :error, attempts = attempts + 1 WHERE opId = :opId")
    suspend fun mark(opId: String, status: String, error: String?)
    @Query("UPDATE outbox SET attempts = attempts + 1, lastError = :error WHERE opId = :opId") suspend fun retryLater(opId: String, error: String?)
    @Query("SELECT COALESCE(MAX(seq), 0) + 1 FROM outbox") suspend fun nextSeq(): Long
    @Query("DELETE FROM outbox WHERE status = 'synced' AND createdAt < :before") suspend fun purgeSynced(before: Long)
}

@Dao
interface SalesDao {
    @Insert suspend fun insert(sale: LocalSaleEntity)
    @Query("SELECT * FROM local_sales ORDER BY createdAt DESC LIMIT 200") fun recent(): Flow<List<LocalSaleEntity>>
    @Query("UPDATE local_sales SET synced = 1 WHERE id = :id") suspend fun markSynced(id: String)
    @Query("SELECT COALESCE(SUM(total), 0) FROM local_sales WHERE createdAt >= :since") fun totalSince(since: Long): Flow<Double>
}

@Database(entities = [ProductEntity::class, CustomerEntity::class, StockEntity::class, RegisterEntity::class, OutboxEntity::class, LocalSaleEntity::class,
    LocalSaleLineEntity::class], version = 2, exportSchema = true)
abstract class VeltrixDatabase : RoomDatabase() {
    abstract fun standalone(): StandaloneDao
    abstract fun catalog(): CatalogDao
    abstract fun outbox(): OutboxDao
    abstract fun sales(): SalesDao
    abstract fun posLocal(): PosLocalDao
}

/** Atomic local sale: stock decrement + receipt + outbox entry commit together. */
@Dao
abstract class PosLocalDao {
    @Insert abstract suspend fun insertSale(sale: LocalSaleEntity)
    @Insert(onConflict = OnConflictStrategy.IGNORE) abstract suspend fun insertOp(op: OutboxEntity)
    @Query("UPDATE stock SET quantity = quantity - :qty, available = available - :qty WHERE productId = :productId AND warehouseId = :warehouseId")
    abstract suspend fun decrement(productId: String, warehouseId: String, qty: Double)

    @Transaction
    open suspend fun recordSale(sale: LocalSaleEntity, op: OutboxEntity, lines: List<Pair<String, Double>>, warehouseId: String) {
        lines.forEach { (pid, q) -> decrement(pid, warehouseId, q) }
        insertSale(sale)
        insertOp(op)
    }
}

/** Local-only reads/writes for standalone (offline-licensed) installations. */
@Dao
abstract class StandaloneDao {
    @Upsert abstract suspend fun upsertProduct(p: ProductEntity)
    @Upsert abstract suspend fun upsertCustomer(c: CustomerEntity)
    @Upsert abstract suspend fun upsertRegister(r: RegisterEntity)
    @Query("SELECT COUNT(*) FROM products WHERE sku = :sku AND id != :id") abstract suspend fun skuTaken(sku: String, id: String): Int
    @Query("SELECT * FROM products ORDER BY name") abstract suspend fun allProducts(): List<ProductEntity>
    @Query("SELECT * FROM products WHERE (:q = '' OR name LIKE '%' || :q || '%' OR sku LIKE '%' || :q || '%') ORDER BY name LIMIT 500")
    abstract fun products(q: String): Flow<List<ProductEntity>>
    @Query("SELECT * FROM customers") abstract suspend fun allCustomers(): List<CustomerEntity>
    @Query("SELECT COUNT(*) FROM customers") abstract suspend fun customerCount(): Int
    @Query("SELECT * FROM stock") abstract suspend fun allStock(): List<StockEntity>
    @Query("SELECT * FROM registers") abstract suspend fun allRegisters(): List<RegisterEntity>
    @Query("SELECT * FROM local_sales ORDER BY createdAt DESC") abstract suspend fun allSales(): List<LocalSaleEntity>
    @Query("SELECT * FROM local_sales WHERE createdAt >= :since ORDER BY createdAt DESC") abstract suspend fun salesSince(since: Long): List<LocalSaleEntity>
    @Query("SELECT * FROM local_sale_lines") abstract suspend fun allLines(): List<LocalSaleLineEntity>
    @Query("SELECT * FROM local_sale_lines WHERE createdAt >= :since") abstract suspend fun linesSince(since: Long): List<LocalSaleLineEntity>
    @Query("SELECT * FROM stock WHERE productId = :productId AND warehouseId = :warehouseId") abstract suspend fun stockOf(productId: String, warehouseId: String): StockEntity?
    @Upsert abstract suspend fun upsertStock(s: StockEntity)
    @Insert abstract suspend fun insertSale(sale: LocalSaleEntity)
    @Insert abstract suspend fun insertLines(lines: List<LocalSaleLineEntity>)

    /** Adds [delta] to the local stock row, creating it at 0 when the product was never counted. */
    @Transaction
    open suspend fun adjustStock(productId: String, warehouseId: String, delta: Double) {
        val s = stockOf(productId, warehouseId) ?: StockEntity(productId, warehouseId, 0.0, 0.0)
        upsertStock(s.copy(quantity = s.quantity + delta, available = s.available + delta))
    }

    /** Atomic standalone sale: receipt, lines and stock decrements commit together. Nothing is queued for a server. */
    @Transaction
    open suspend fun recordSale(sale: LocalSaleEntity, lines: List<LocalSaleLineEntity>, stockDeltas: Map<String, Double>, warehouseId: String) {
        insertSale(sale)
        insertLines(lines)
        stockDeltas.forEach { (pid, d) -> adjustStock(pid, warehouseId, d) }
    }

    @Query("DELETE FROM products") abstract suspend fun clearProducts()
    @Query("DELETE FROM customers") abstract suspend fun clearCustomers()
    @Query("DELETE FROM stock") abstract suspend fun clearStock()
    @Query("DELETE FROM registers") abstract suspend fun clearRegisters()
    @Query("DELETE FROM local_sales") abstract suspend fun clearSales()
    @Query("DELETE FROM local_sale_lines") abstract suspend fun clearLines()
    @Query("DELETE FROM outbox") abstract suspend fun clearOutbox()
    @Insert(onConflict = OnConflictStrategy.REPLACE) abstract suspend fun insertProducts(items: List<ProductEntity>)
    @Insert(onConflict = OnConflictStrategy.REPLACE) abstract suspend fun insertCustomers(items: List<CustomerEntity>)
    @Insert(onConflict = OnConflictStrategy.REPLACE) abstract suspend fun insertStock(items: List<StockEntity>)
    @Insert(onConflict = OnConflictStrategy.REPLACE) abstract suspend fun insertRegisters(items: List<RegisterEntity>)
    @Insert(onConflict = OnConflictStrategy.REPLACE) abstract suspend fun insertSales(items: List<LocalSaleEntity>)

    /** Replaces all business data with a restored backup, atomically. */
    @Transaction
    open suspend fun replaceAll(products: List<ProductEntity>, customers: List<CustomerEntity>, stock: List<StockEntity>, registers: List<RegisterEntity>,
        sales: List<LocalSaleEntity>, lines: List<LocalSaleLineEntity>) {
        clearLines(); clearSales(); clearStock(); clearRegisters(); clearCustomers(); clearProducts(); clearOutbox()
        insertProducts(products); insertCustomers(customers); insertStock(stock); insertRegisters(registers); insertSales(sales); insertLines(lines)
    }
}
