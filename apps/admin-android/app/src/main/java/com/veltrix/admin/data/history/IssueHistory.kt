package com.veltrix.admin.data.history

import android.content.Context
import android.util.AtomicFile
import com.veltrix.admin.data.license.DeviceCode
import dagger.hilt.android.qualifiers.ApplicationContext
import java.io.File
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.json.Json

/** One issued activation code (no secrets: the code itself is only usable on the device it was issued for). */
@Serializable
data class IssuedRecord(
    val licenseId: String,
    val shop: String,
    val deviceCode: String,
    val issuedAt: Long,
    val expiresAt: Long,
    val days: Int,
    val note: String? = null,
    val activationCode: String,
)

object HistorySearch {
    /** Case-insensitive match on shop, note, license id or device code (whitespace in the query is ignored for codes). */
    fun filter(records: List<IssuedRecord>, query: String): List<IssuedRecord> {
        val q = query.trim()
        if (q.isEmpty()) return records
        val compact = DeviceCode.strip(q)
        return records.filter {
            it.shop.contains(q, true) || it.note?.contains(q, true) == true || it.licenseId.contains(q, true) ||
                it.deviceCode.contains(compact, true)
        }
    }
}

/** Local issuance history, stored as JSON in app-private storage (excluded from backups). Newest first. */
@Singleton
class IssueHistory @Inject constructor(@ApplicationContext context: Context) {
    private val file = AtomicFile(File(context.filesDir, "issue-history.json"))
    private val json = Json { ignoreUnknownKeys = true }
    private val serializer = ListSerializer(IssuedRecord.serializer())
    private val mutex = Mutex()
    private val _records = MutableStateFlow<List<IssuedRecord>>(emptyList())
    val records: StateFlow<List<IssuedRecord>> = _records.asStateFlow()
    @Volatile private var loaded = false

    suspend fun load() = withContext(Dispatchers.IO) {
        mutex.withLock {
            if (loaded) return@withLock
            _records.value = runCatching { json.decodeFromString(serializer, String(file.readFully(), Charsets.UTF_8)) }
                .getOrDefault(emptyList()).sortedByDescending { it.issuedAt }
            loaded = true
        }
    }

    suspend fun add(record: IssuedRecord) {
        load()
        withContext(Dispatchers.IO) {
            mutex.withLock {
                val next = listOf(record) + _records.value
                val out = file.startWrite()
                try {
                    out.write(json.encodeToString(serializer, next).toByteArray(Charsets.UTF_8))
                    file.finishWrite(out)
                } catch (e: Exception) {
                    file.failWrite(out)
                    throw e
                }
                _records.value = next
            }
        }
    }
}
