package com.veltrix.os.data.db

import androidx.room.Dao
import androidx.room.Database
import androidx.room.Entity
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.RoomDatabase
import androidx.room.Transaction
import androidx.room.Upsert
import kotlinx.coroutines.flow.Flow

@Entity(tableName = "products")
data class ProductEntity(
    @PrimaryKey val id: String, val sku: String, val name: String, val nameAr: String?, val type: String, val status: String,
    val categoryId: String?, val taxRatePercent: Double, val salePrice: Double, val trackStock: Boolean, val barcodes: String, val updatedAt: String,
)

@Entity(tableName = "customers")
data class CustomerEntity(
    @PrimaryKey val id: String, val code: String, val name: String, val phone: String?, val balance: Double, val loyaltyPoints: Double,
    val creditLimit: Double, val updatedAt: String, val pendingSync: Boolean = false,
)

@Entity(tableName = "stock", primaryKeys = ["productId", "warehouseId"])
data class StockEntity(val productId: String, val warehouseId: String, val quantity: Double, val available: Double)

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

@Entity(tableName = "local_sales")
data class LocalSaleEntity(@PrimaryKey val id: String, val number: String, val total: Double, val itemCount: Int, val createdAt: Long, val payload: String, val synced: Boolean = false)

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

@Database(entities = [ProductEntity::class, CustomerEntity::class, StockEntity::class, RegisterEntity::class, OutboxEntity::class, LocalSaleEntity::class], version = 1, exportSchema = true)
abstract class VeltrixDatabase : RoomDatabase() {
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
