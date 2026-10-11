package com.veltrix.admin.data.license

import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyInfo
import android.security.keystore.KeyProperties
import android.security.keystore.StrongBoxUnavailableException
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.PrivateKey
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.util.Base64
import javax.inject.Inject
import javax.inject.Singleton

/** Where the vendor key lives, for display. */
enum class KeyProtection { StrongBox, SecureHardware, Software }

/**
 * The vendor signing key: EC P-256 generated INSIDE the Android Keystore, purpose SIGN, SHA-256, non-exportable.
 * StrongBox when the phone has one (falls back to the TEE). Every use requires a biometric / device-credential
 * authentication within the last [AUTH_VALIDITY_SECONDS] seconds, so a stolen unlocked phone still cannot issue silently.
 *
 * The private key never exists in app files or in the APK; only the public key (SPKI) can be read out.
 */
@Singleton
class VendorKeyStore @Inject constructor() {
    private fun keyStore(): KeyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }

    fun exists(): Boolean = runCatching { keyStore().containsAlias(ALIAS) }.getOrDefault(false)

    /** Creates the key. Never replaces an existing one. Returns the protection level actually obtained. */
    fun create(): KeyProtection {
        check(!exists()) { "A signing key already exists." }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            try {
                generate(strongBox = true)
                return protection()
            } catch (_: StrongBoxUnavailableException) {
                runCatching { keyStore().deleteEntry(ALIAS) }
            }
        }
        generate(strongBox = false)
        return protection()
    }

    private fun generate(strongBox: Boolean) {
        val spec = KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_SIGN)
            .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
            .setDigests(KeyProperties.DIGEST_SHA256)
            .setUserAuthenticationRequired(true)
            .apply {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R)
                    setUserAuthenticationParameters(AUTH_VALIDITY_SECONDS, KeyProperties.AUTH_BIOMETRIC_STRONG or KeyProperties.AUTH_DEVICE_CREDENTIAL)
                else @Suppress("DEPRECATION") setUserAuthenticationValidityDurationSeconds(AUTH_VALIDITY_SECONDS)
                setInvalidatedByBiometricEnrollment(false)
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) setUnlockedDeviceRequired(true)
                if (strongBox && Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) setIsStrongBoxBacked(true)
            }
            .build()
        KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, ANDROID_KEYSTORE).apply { initialize(spec) }.generateKeyPair()
    }

    private fun privateKey(): PrivateKey = keyStore().getKey(ALIAS, null) as? PrivateKey ?: error("No signing key.")

    /** X.509 SubjectPublicKeyInfo (DER) of the vendor public key. */
    fun publicKeySpki(): ByteArray = keyStore().getCertificate(ALIAS)?.publicKey?.encoded ?: error("No signing key.")

    fun protection(): KeyProtection {
        val key = privateKey()
        val info = KeyFactory.getInstance(key.algorithm, ANDROID_KEYSTORE).getKeySpec(key, KeyInfo::class.java)
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) when (info.securityLevel) {
            KeyProperties.SECURITY_LEVEL_STRONGBOX -> KeyProtection.StrongBox
            KeyProperties.SECURITY_LEVEL_TRUSTED_ENVIRONMENT -> KeyProtection.SecureHardware
            else -> KeyProtection.Software
        } else @Suppress("DEPRECATION") if (info.isInsideSecureHardware) KeyProtection.SecureHardware else KeyProtection.Software
    }

    /** Signs with the Keystore key; the caller must have authenticated the user just before (see [AUTH_VALIDITY_SECONDS]). */
    fun signer(): LicenseSigner = object : LicenseSigner {
        override fun sign(data: ByteArray): ByteArray =
            Signature.getInstance("SHA256withECDSA").run { initSign(privateKey()); update(data); sign() }
    }

    fun delete() = keyStore().deleteEntry(ALIAS)

    companion object {
        const val ALIAS = "veltrix_vendor_sign_v1"
        const val ANDROID_KEYSTORE = "AndroidKeyStore"
        const val AUTH_VALIDITY_SECONDS = 60

        fun spkiBase64(spki: ByteArray): String = Base64.getEncoder().encodeToString(spki)

        fun spkiPem(spki: ByteArray): String =
            "-----BEGIN PUBLIC KEY-----\n" + spkiBase64(spki).chunked(64).joinToString("\n") + "\n-----END PUBLIC KEY-----\n"
    }
}
