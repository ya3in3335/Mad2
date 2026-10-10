package com.veltrix.os.data.security

import android.annotation.SuppressLint
import android.content.Context
import android.os.Build
import android.provider.Settings
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import dagger.hilt.android.qualifiers.ApplicationContext
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.util.UUID
import javax.inject.Inject
import javax.inject.Singleton

/**
 * Privacy-conscious device identity:
 *  - a random installation id (app-specific, regenerated on reinstall),
 *  - an ECDSA P-256 key pair generated inside the Android Keystore (private key never leaves the device),
 *  - a versioned, salted one-way fingerprint of limited characteristics used only to recognise the same hardware after a
 *    reinstall. IMEI, MAC addresses and hardware serial numbers are never collected. ANDROID_ID is app-scoped on API 26+.
 */
@Singleton
class DeviceIdentity @Inject constructor(@ApplicationContext private val context: Context) {
    private val prefs = context.getSharedPreferences("veltrix_device", Context.MODE_PRIVATE)

    val installationId: String
        @Synchronized get() = prefs.getString("installation_id", null) ?: UUID.randomUUID().toString().also {
            prefs.edit().putString("installation_id", it).apply()
        }

    private fun privateKey(): PrivateKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(KEY_ALIAS, null) as? PrivateKey)?.let { return it }
        val gen = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore")
        gen.initialize(
            KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY)
                .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
                .setDigests(KeyProperties.DIGEST_SHA256)
                .build()
        )
        return gen.generateKeyPair().private
    }

    /** SubjectPublicKeyInfo, base64 — matches the server's ImportSubjectPublicKeyInfo. */
    fun publicKeySpki(): String {
        privateKey()
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        return Base64.encodeToString(ks.getCertificate(KEY_ALIAS).publicKey.encoded, Base64.NO_WRAP)
    }

    /** ECDSA-SHA256 signature (DER) over UTF-8 "{nonce}|{installationId}|{purpose}". */
    fun signChallenge(nonce: String, purpose: String): String {
        val sig = Signature.getInstance("SHA256withECDSA").apply {
            initSign(privateKey())
            update("$nonce|$installationId|$purpose".toByteArray(Charsets.UTF_8))
        }
        return Base64.encodeToString(sig.sign(), Base64.NO_WRAP)
    }

    @SuppressLint("HardwareIds")
    fun fingerprintHash(): String {
        val androidId = Settings.Secure.getString(context.contentResolver, Settings.Secure.ANDROID_ID) ?: ""
        val material = listOf(FINGERPRINT_SALT, Build.MANUFACTURER, Build.MODEL, Build.BOARD, Build.HARDWARE,
            Build.SUPPORTED_ABIS.joinToString(","), androidId).joinToString("|")
        return MessageDigest.getInstance("SHA-256").digest(material.toByteArray()).joinToString("") { "%02x".format(it) }
    }

    fun publicKeyThumbprint(): String =
        MessageDigest.getInstance("SHA-256").digest(Base64.decode(publicKeySpki(), Base64.NO_WRAP)).joinToString("") { "%02x".format(it) }

    val deviceName: String get() = "${Build.MANUFACTURER} ${Build.MODEL}".trim()

    /** Key rotation / reset after revocation: a new key pair requires a fresh activation. */
    fun resetKeys() {
        KeyStore.getInstance("AndroidKeyStore").apply { load(null) }.deleteEntry(KEY_ALIAS)
        prefs.edit().remove("installation_id").apply()
    }

    companion object {
        private const val KEY_ALIAS = "veltrix_device_key_v1"
        private const val FINGERPRINT_SALT = "veltrix-fp-v1"
        const val FINGERPRINT_VERSION = 1
    }
}
