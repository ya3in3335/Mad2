package com.veltrix.os.data.api

import android.content.Context
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import javax.inject.Singleton

/** Non-secret local preferences (server URL, language, theme, selected branch/register, sync cursor, setup progress). */
@Singleton
class AppSettings @Inject constructor(@ApplicationContext context: Context) {
    private val p = context.getSharedPreferences("veltrix_settings", Context.MODE_PRIVATE)
    var serverUrl: String get() = p.getString("server", "") ?: ""; set(v) = p.edit().putString("server", v).apply()
    var language: String get() = p.getString("lang", "fr") ?: "fr"; set(v) = p.edit().putString("lang", v).apply()
    var theme: String get() = p.getString("theme", "dark") ?: "dark"; set(v) = p.edit().putString("theme", v).apply()
    var deviceId: String? get() = p.getString("device_id", null); set(v) = p.edit().putString("device_id", v).apply()
    var tenantId: String? get() = p.getString("tenant_id", null); set(v) = p.edit().putString("tenant_id", v).apply()
    var tenantName: String? get() = p.getString("tenant_name", null); set(v) = p.edit().putString("tenant_name", v).apply()
    var setupToken: String? get() = p.getString("setup_token", null); set(v) = p.edit().putString("setup_token", v).apply()
    var setupCompleted: Boolean get() = p.getBoolean("setup_completed", false); set(v) = p.edit().putBoolean("setup_completed", v).apply()
    var privacyAccepted: Boolean get() = p.getBoolean("privacy", false); set(v) = p.edit().putBoolean("privacy", v).apply()
    var languageChosen: Boolean get() = p.getBoolean("lang_chosen", false); set(v) = p.edit().putBoolean("lang_chosen", v).apply()
    var branchId: String? get() = p.getString("branch_id", null); set(v) = p.edit().putString("branch_id", v).apply()
    var registerId: String? get() = p.getString("register_id", null); set(v) = p.edit().putString("register_id", v).apply()
    var shiftId: String? get() = p.getString("shift_id", null); set(v) = p.edit().putString("shift_id", v).apply()
    var warehouseId: String? get() = p.getString("warehouse_id", null); set(v) = p.edit().putString("warehouse_id", v).apply()
    var syncCursor: String? get() = p.getString("sync_cursor", null); set(v) = p.edit().putString("sync_cursor", v).apply()
    var lastSyncAt: Long get() = p.getLong("last_sync", 0); set(v) = p.edit().putLong("last_sync", v).apply()
    var licenseSummaryJson: String? get() = p.getString("license_summary", null); set(v) = p.edit().putString("license_summary", v).apply()
    var biometricEnabled: Boolean get() = p.getBoolean("biometric", false); set(v) = p.edit().putBoolean("biometric", v).apply()
    var receiptCounter: Long get() = p.getLong("receipt_counter", 0); set(v) = p.edit().putLong("receipt_counter", v).apply()
}
