package com.veltrix.admin.ui

import android.security.keystore.KeyPermanentlyInvalidatedException
import android.security.keystore.UserNotAuthenticatedException
import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.veltrix.admin.R
import com.veltrix.admin.data.history.IssueHistory
import com.veltrix.admin.data.history.IssuedRecord
import com.veltrix.admin.data.license.DeviceCode
import com.veltrix.admin.data.license.KeyProtection
import com.veltrix.admin.data.license.OfflineIssuer
import com.veltrix.admin.data.license.VendorKeyStore
import com.veltrix.admin.data.settings.AdminSettings
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

enum class Tab { Issue, History, Key, Settings }

/** Days choice: one of [OfflineIssuer.PRESET_DAYS] or null for "custom" (then [IssueForm.customDays] is used). */
data class IssueForm(
    val deviceCode: String = "",
    val shop: String = "",
    val preset: Int? = 365,
    val customDays: String = "",
    val note: String = "",
) {
    enum class Problem { BadDevice, NoShop, BadDays }

    fun days(): Int? = if (preset != null) preset else OfflineIssuer.parseDays(customDays)

    fun problem(): Problem? = when {
        !DeviceCode.isValid(deviceCode) -> Problem.BadDevice
        shop.isBlank() -> Problem.NoShop
        days() == null -> Problem.BadDays
        else -> null
    }
}

data class KeyState(val exists: Boolean, val spki: ByteArray? = null, val protection: KeyProtection? = null, val invalidated: Boolean = false)

@HiltViewModel
class IssuerViewModel @Inject constructor(
    private val keys: VendorKeyStore,
    val history: IssueHistory,
    val settings: AdminSettings,
) : ViewModel() {
    val tab = MutableStateFlow(Tab.Issue)
    private val _key = MutableStateFlow(KeyState(exists = false))
    val key = _key.asStateFlow()
    val form = MutableStateFlow(IssueForm())
    val result = MutableStateFlow<IssuedRecord?>(null)
    val busy = MutableStateFlow(false)
    /** One-shot user message (string resource). */
    val message = MutableStateFlow<Int?>(null)

    init {
        viewModelScope.launch { history.load() }
        refreshKey()
    }

    fun refreshKey() = viewModelScope.launch {
        _key.value = withContext(Dispatchers.IO) {
            if (!keys.exists()) KeyState(false)
            else runCatching { KeyState(true, keys.publicKeySpki(), runCatching { keys.protection() }.getOrNull()) }
                .getOrElse { KeyState(true, invalidated = true) }
        }
    }

    fun createKey() = viewModelScope.launch {
        busy.value = true
        val ok = withContext(Dispatchers.IO) { runCatching { keys.create() }.isSuccess }
        busy.value = false
        if (!ok) message.value = R.string.key_create_failed
        refreshKey()
        if (ok) tab.value = Tab.Key
    }

    fun deleteKey() = viewModelScope.launch {
        withContext(Dispatchers.IO) { runCatching { keys.delete() } }
        settings.keyLossAcknowledged = false
        result.value = null
        refreshKey()
    }

    fun editForm(change: (IssueForm) -> IssueForm) { form.update(change); result.value = null }

    fun renew(record: IssuedRecord) {
        form.value = IssueForm(deviceCode = DeviceCode.format(record.deviceCode), shop = record.shop, preset = 365)
        result.value = null
        tab.value = Tab.Issue
    }

    fun newIssue() { form.value = IssueForm(); result.value = null }

    /** Call only right after a successful BiometricPrompt: the Keystore key requires a recent user authentication. */
    fun issue() {
        val f = form.value
        if (f.problem() != null || busy.value) return
        val days = f.days() ?: return
        viewModelScope.launch {
            busy.value = true
            val outcome = withContext(Dispatchers.Default) {
                runCatching { OfflineIssuer.issue(keys.signer(), f.deviceCode, f.shop, days) }
            }
            outcome.onSuccess { issued ->
                val p = issued.payload
                val record = IssuedRecord(p.lid, p.tenantName, DeviceCode.strip(f.deviceCode), p.iat, p.exp, days,
                    f.note.trim().ifEmpty { null }, issued.code)
                runCatching { history.add(record) }.onFailure { message.value = R.string.history_save_failed }
                result.value = record
            }.onFailure { e ->
                message.value = issueError(e)
                if (e is KeyPermanentlyInvalidatedException) refreshKey()
            }
            busy.value = false
        }
    }

    @StringRes
    private fun issueError(e: Throwable): Int = when (e) {
        is UserNotAuthenticatedException -> R.string.issue_auth_expired
        is KeyPermanentlyInvalidatedException -> R.string.key_invalidated
        else -> R.string.issue_failed
    }
}
