package com.veltrix.os.data.security

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import dagger.hilt.android.qualifiers.ApplicationContext
import java.security.KeyStore
import java.security.MessageDigest
import java.util.Base64
import javax.crypto.KeyGenerator
import javax.crypto.Mac
import javax.crypto.SecretKey
import javax.inject.Inject
import javax.inject.Singleton

/** What the binding logic needs from the platform (Keystore, secure store); tests use fakes. */
interface BindingPlatform {
    val installationId: String
    /** SHA-256 thumbprint of the non-exportable device identity key (created if missing, so a moved install gets a new one). */
    fun keyThumbprint(): String
    /** HMAC-SHA256 with the Keystore binding key; null when that key does not exist and [create] is false. */
    fun mac(data: ByteArray, create: Boolean): ByteArray?
    /** Encrypted binding record (Keystore AES-GCM secure store); null when absent or not decryptable on this device. */
    fun readBinding(): String?
    fun writeBinding(value: String)
    /** Plain marker that a binding was created for this app data: distinguishes "first launch" from "data moved here". */
    fun hasMarker(): Boolean
    fun writeMarker()
}

enum class BindingStatus { Bound, NewlyBound, NotLicensed }

data class BindingRecord(val installationId: String, val keyThumbprint: String, val boundAt: Long)

/**
 * Trust-on-first-use device binding (no activation codes). First launch: bind {iid, key thumbprint, boundAt} with an HMAC by a
 * Keystore key and store it encrypted. Every launch: verify. Keystore keys are non-exportable, so app data restored or copied to
 * another phone cannot be verified there and the app shows the "not licensed" screen. Root users can still tamper (documented).
 */
class DeviceBinding(private val platform: BindingPlatform, private val now: () -> Long) {
    var record: BindingRecord? = null
        private set

    fun verify(): BindingStatus {
        val stored = platform.readBinding()
        if (stored == null) {
            if (platform.hasMarker()) return BindingStatus.NotLicensed // bound once, binding gone or undecryptable: moved/tampered
            val r = BindingRecord(platform.installationId, platform.keyThumbprint(), now())
            val body = encode(r)
            val mac = platform.mac(body.toByteArray(Charsets.UTF_8), create = true) ?: return BindingStatus.NotLicensed
            platform.writeBinding(body + SEP + b64(mac))
            platform.writeMarker()
            record = r
            return BindingStatus.NewlyBound
        }
        val r = open(stored) ?: return BindingStatus.NotLicensed
        if (r.installationId != platform.installationId || r.keyThumbprint != platform.keyThumbprint()) return BindingStatus.NotLicensed
        if (!platform.hasMarker()) platform.writeMarker()
        record = r
        return BindingStatus.Bound
    }

    private fun open(stored: String): BindingRecord? {
        val i = stored.lastIndexOf(SEP)
        if (i <= 0) return null
        val body = stored.substring(0, i)
        val mac = runCatching { Base64.getDecoder().decode(stored.substring(i + 1)) }.getOrNull() ?: return null
        val expected = runCatching { platform.mac(body.toByteArray(Charsets.UTF_8), create = false) }.getOrNull() ?: return null
        if (!MessageDigest.isEqual(expected, mac)) return null
        val p = body.split(SEP)
        if (p.size != 4 || p[0] != VERSION) return null
        return BindingRecord(p[1], p[2], p[3].toLongOrNull() ?: return null)
    }

    private fun encode(r: BindingRecord) = listOf(VERSION, r.installationId, r.keyThumbprint, r.boundAt.toString()).joinToString(SEP)
    private fun b64(b: ByteArray) = Base64.getEncoder().encodeToString(b)

    companion object {
        private const val VERSION = "v1"
        private const val SEP = "|"
    }
}

/** Android wiring: device identity key, Keystore HMAC key, encrypted [SecureStore] record and a plain marker. */
@Singleton
class DeviceBindingService @Inject constructor(@ApplicationContext context: Context, private val identity: DeviceIdentity,
    private val store: SecureStore) : BindingPlatform {
    private val prefs = context.getSharedPreferences("veltrix_binding", Context.MODE_PRIVATE)
    private val binding = DeviceBinding(this) { System.currentTimeMillis() / 1000 }

    @Volatile var status: BindingStatus? = null
        private set
    val isBound: Boolean get() = (status ?: verify()).let { it == BindingStatus.Bound || it == BindingStatus.NewlyBound }
    val boundAt: Long? get() = binding.record?.boundAt

    @Synchronized
    fun verify(): BindingStatus = runCatching { binding.verify() }.getOrDefault(BindingStatus.NotLicensed).also { status = it }

    override val installationId: String get() = identity.installationId
    override fun keyThumbprint(): String = identity.publicKeyThumbprint()

    override fun mac(data: ByteArray, create: Boolean): ByteArray? {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        val key = (ks.getEntry(MAC_ALIAS, null) as? KeyStore.SecretKeyEntry)?.secretKey ?: if (create) newMacKey() else return null
        return Mac.getInstance("HmacSHA256").apply { init(key) }.doFinal(data)
    }

    private fun newMacKey(): SecretKey = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_HMAC_SHA256, "AndroidKeyStore").apply {
        init(KeyGenParameterSpec.Builder(MAC_ALIAS, KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY).build())
    }.generateKey()

    override fun readBinding(): String? = store.get(SecureStore.DEVICE_BINDING)
    override fun writeBinding(value: String) = store.put(SecureStore.DEVICE_BINDING, value)
    override fun hasMarker(): Boolean = prefs.getBoolean(MARKER, false)
    override fun writeMarker() { prefs.edit().putBoolean(MARKER, true).apply() }

    private companion object {
        const val MAC_ALIAS = "veltrix_binding_mac_v1"
        const val MARKER = "bound"
    }
}
