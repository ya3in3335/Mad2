package com.veltrix.admin.data.settings

import android.content.Context
import androidx.core.content.edit
import com.veltrix.admin.data.security.KeyValueStore
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/** Non-secret preferences. Nothing in here is a credential. */
@Singleton
class AdminSettings @Inject constructor(@ApplicationContext context: Context) {
    private val p = context.getSharedPreferences("veltrix_admin_settings", Context.MODE_PRIVATE)
    private val _serverUrl = MutableStateFlow(p.getString("server", "") ?: "")
    val serverUrlFlow: StateFlow<String> = _serverUrl.asStateFlow()

    var serverUrl: String
        get() = _serverUrl.value
        set(v) { p.edit { putString("server", v) }; _serverUrl.value = v }
    var language: String get() = p.getString("lang", "fr") ?: "fr"; set(v) = p.edit { putString("lang", v) }
    var languageChosen: Boolean get() = p.getBoolean("lang_chosen", false); set(v) = p.edit { putBoolean("lang_chosen", v) }
    var biometricEnabled: Boolean get() = p.getBoolean("biometric", false); set(v) = p.edit { putBoolean("biometric", v) }
    var biometricOffered: Boolean get() = p.getBoolean("biometric_offered", false); set(v) = p.edit { putBoolean("biometric_offered", v) }
    var lastEmail: String? get() = p.getString("last_email", null); set(v) = p.edit { putString("last_email", v) }
}

/** SharedPreferences adapter holding only ciphertext produced by [com.veltrix.admin.data.security.TokenVault]. */
class SharedPrefsKeyValueStore(context: Context, name: String) : KeyValueStore {
    private val p = context.getSharedPreferences(name, Context.MODE_PRIVATE)
    override fun getString(key: String): String? = p.getString(key, null)
    override fun putString(key: String, value: String?) = p.edit { if (value == null) remove(key) else putString(key, value) }
    override fun clear() = p.edit { clear() }
}
