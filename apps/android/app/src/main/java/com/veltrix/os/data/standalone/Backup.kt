package com.veltrix.os.data.standalone

import com.veltrix.os.data.db.CustomerEntity
import com.veltrix.os.data.db.LocalSaleEntity
import com.veltrix.os.data.db.LocalSaleLineEntity
import com.veltrix.os.data.db.ProductEntity
import com.veltrix.os.data.db.RegisterEntity
import com.veltrix.os.data.db.StockEntity
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.SecureRandom
import java.util.zip.GZIPInputStream
import java.util.zip.GZIPOutputStream
import javax.crypto.AEADBadTagException
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

@Serializable
data class BackupPayload(
    val version: Int, val createdAt: Long, val tenantId: String?, val tenantName: String?, val usersJson: String,
    val products: List<ProductEntity>, val customers: List<CustomerEntity>, val stock: List<StockEntity>, val registers: List<RegisterEntity>,
    val sales: List<LocalSaleEntity>, val lines: List<LocalSaleLineEntity>, val receiptCounter: Long = 0,
    /** Workforce & Attendance tables (null in backups made before schema v3; restoring such a file keeps the current workforce data). */
    val workforce: com.veltrix.os.data.workforce.WorkforceSnapshot? = null,
)

class BackupException(val key: String) : Exception(key)

/**
 * Encrypted backup file (same container as the Windows client; the JSON payload is platform-specific):
 * `"VLXBAK01" | iterations (int32 LE) | salt (16) | nonce (12) | tag (16) | ciphertext`. AES-256-GCM, key = PBKDF2-SHA256(owner password),
 * header authenticated as associated data, plaintext = gzip(JSON). Wrong password or tampering → [BackupException] "backup_password".
 */
object BackupCodec {
    const val VERSION = 1
    const val MIME = "application/octet-stream"
    const val EXTENSION = ".vlxbak"
    private val MAGIC = "VLXBAK01".toByteArray(Charsets.US_ASCII)
    private const val HEADER = 8 + 4 + 16
    private val json = Json { ignoreUnknownKeys = true }

    fun encrypt(payload: BackupPayload, password: String, iterations: Int = PasswordHasher.DEFAULT_ITERATIONS): ByteArray {
        val raw = ByteArrayOutputStream().also { o -> GZIPOutputStream(o).use { it.write(json.encodeToString(BackupPayload.serializer(), payload).toByteArray()) } }.toByteArray()
        val rnd = SecureRandom()
        val salt = ByteArray(16).also(rnd::nextBytes)
        val nonce = ByteArray(12).also(rnd::nextBytes)
        val header = ByteBuffer.allocate(HEADER).order(ByteOrder.LITTLE_ENDIAN).put(MAGIC).putInt(iterations).put(salt).array()
        val key = PasswordHasher.derive(password, salt, iterations)
        val c = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(128, nonce)); updateAAD(header) }
        key.fill(0)
        val out = c.doFinal(raw) // ciphertext || tag
        val cipher = out.copyOfRange(0, out.size - 16)
        val tag = out.copyOfRange(out.size - 16, out.size)
        return header + nonce + tag + cipher
    }

    fun decrypt(file: ByteArray, password: String): BackupPayload {
        if (file.size < HEADER + 28 || !file.copyOfRange(0, 8).contentEquals(MAGIC)) throw BackupException("backup_file")
        val iterations = ByteBuffer.wrap(file, 8, 4).order(ByteOrder.LITTLE_ENDIAN).int
        if (iterations !in PasswordHasher.MIN_ITERATIONS..10_000_000) throw BackupException("backup_file")
        val salt = file.copyOfRange(12, 28)
        val nonce = file.copyOfRange(HEADER, HEADER + 12)
        val tag = file.copyOfRange(HEADER + 12, HEADER + 28)
        val cipher = file.copyOfRange(HEADER + 28, file.size)
        val key = PasswordHasher.derive(password, salt, iterations)
        val plain = try {
            Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(128, nonce)); updateAAD(file.copyOfRange(0, HEADER)) }
                .doFinal(cipher + tag)
        } catch (e: AEADBadTagException) { throw BackupException("backup_password") } finally { key.fill(0) }
        val text = GZIPInputStream(ByteArrayInputStream(plain)).use { it.readBytes() }.toString(Charsets.UTF_8)
        val p = runCatching { json.decodeFromString(BackupPayload.serializer(), text) }.getOrElse { throw BackupException("backup_file") }
        if (p.version > VERSION) throw BackupException("backup_file")
        return p
    }
}
