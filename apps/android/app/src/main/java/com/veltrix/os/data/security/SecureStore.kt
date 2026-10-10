package com.veltrix.os.data.security

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import com.veltrix.os.data.standalone.SecretStore
import dagger.hilt.android.qualifiers.ApplicationContext
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import javax.inject.Inject
import javax.inject.Singleton

/**
 * Small encrypted key/value store. Values are encrypted with an AES-256-GCM key that lives in the
 * Android Keystore (non-exportable); only ciphertext is written to SharedPreferences.
 * Used for session tokens, the license certificate and the clock watermark.
 */
@Singleton
class SecureStore @Inject constructor(@ApplicationContext context: Context) : SecretStore {
    private val prefs = context.getSharedPreferences("veltrix_secure", Context.MODE_PRIVATE)

    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getEntry(ALIAS, null) as? KeyStore.SecretKeyEntry)?.let { return it.secretKey }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        gen.init(
            KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build()
        )
        return gen.generateKey()
    }

    @Synchronized
    override fun put(name: String, value: String?) {
        if (value == null) {
            prefs.edit().remove(name).apply()
            return
        }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.ENCRYPT_MODE, key()) }
        val ct = cipher.doFinal(value.toByteArray(Charsets.UTF_8))
        val packed = cipher.iv + ct
        prefs.edit().putString(name, Base64.encodeToString(packed, Base64.NO_WRAP)).apply()
    }

    @Synchronized
    override fun get(name: String): String? {
        val raw = prefs.getString(name, null) ?: return null
        return try {
            val packed = Base64.decode(raw, Base64.NO_WRAP)
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, packed, 0, 12))
            String(cipher.doFinal(packed, 12, packed.size - 12), Charsets.UTF_8)
        } catch (e: Exception) {
            // Key invalidated (e.g. device restored from backup on other hardware): treat as missing.
            prefs.edit().remove(name).apply()
            null
        }
    }

    fun clear() = prefs.edit().clear().apply()

    companion object {
        private const val ALIAS = "veltrix_secure_store_v1"
        const val ACCESS_TOKEN = "access_token"
        const val REFRESH_TOKEN = "refresh_token"
        const val CERTIFICATE = "license_certificate"
        const val VENDOR_KEY = "vendor_public_key"
        const val CLOCK_WATERMARK = "clock_watermark"
        const val PROFILE = "user_profile"
        const val OFFLINE_VERIFIER = "offline_verifier"
    }
}
