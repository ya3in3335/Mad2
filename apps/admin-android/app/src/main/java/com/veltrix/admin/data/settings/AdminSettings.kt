package com.veltrix.admin.data.settings

import android.content.Context
import androidx.core.content.edit
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import javax.inject.Singleton

/** Non-secret preferences. Nothing in here is a credential. */
@Singleton
class AdminSettings @Inject constructor(@ApplicationContext context: Context) {
    private val p = context.getSharedPreferences("veltrix_admin_settings", Context.MODE_PRIVATE)
    var language: String get() = p.getString("lang", "fr") ?: "fr"; set(v) = p.edit { putString("lang", v) }
    var languageChosen: Boolean get() = p.getBoolean("lang_chosen", false); set(v) = p.edit { putBoolean("lang_chosen", v) }
    /** The owner confirmed they understood that losing this phone means losing the signing key. */
    var keyLossAcknowledged: Boolean get() = p.getBoolean("key_loss_ack", false); set(v) = p.edit { putBoolean("key_loss_ack", v) }
}
