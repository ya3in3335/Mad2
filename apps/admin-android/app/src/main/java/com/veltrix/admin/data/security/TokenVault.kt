package com.veltrix.admin.data.security

import java.time.Instant
import java.util.Base64

/** Minimal key/value persistence so the vault can be unit-tested without Android. */
interface KeyValueStore {
    fun getString(key: String): String?
    fun putString(key: String, value: String?)
    fun clear()
}

/** Tokens as issued by POST /api/v1/auth/login and /api/v1/auth/refresh. */
data class SessionTokens(
    val accessToken: String,
    val accessTokenExpiresAt: Instant?,
    val refreshToken: String,
    val refreshTokenExpiresAt: Instant?,
)

/**
 * Encrypted storage for the admin session. Only ciphertext reaches [KeyValueStore]; every value is bound to its
 * entry name. Any decryption failure (key invalidated, tampering, restore on other hardware) reads as "absent",
 * which forces a fresh sign-in rather than ever using a corrupted credential.
 */
class TokenVault(private val kv: KeyValueStore, private val cipher: TokenCipher) {

    @Synchronized
    fun put(name: String, value: String?) {
        if (value == null) { kv.putString(name, null); return }
        val packed = cipher.encrypt(value.toByteArray(Charsets.UTF_8), aad(name))
        kv.putString(name, Base64.getEncoder().encodeToString(packed))
    }

    @Synchronized
    fun get(name: String): String? {
        val raw = kv.getString(name) ?: return null
        return try {
            String(cipher.decrypt(Base64.getDecoder().decode(raw), aad(name)), Charsets.UTF_8)
        } catch (e: Exception) {
            kv.putString(name, null)
            null
        }
    }

    @Synchronized
    fun saveTokens(tokens: SessionTokens) {
        put(ACCESS_TOKEN, tokens.accessToken)
        put(ACCESS_EXPIRES, tokens.accessTokenExpiresAt?.toString())
        put(REFRESH_TOKEN, tokens.refreshToken)
        put(REFRESH_EXPIRES, tokens.refreshTokenExpiresAt?.toString())
    }

    @Synchronized
    fun tokens(): SessionTokens? {
        val access = get(ACCESS_TOKEN) ?: return null
        val refresh = get(REFRESH_TOKEN) ?: return null
        return SessionTokens(access, get(ACCESS_EXPIRES)?.let(::parseInstant), refresh, get(REFRESH_EXPIRES)?.let(::parseInstant))
    }

    val accessToken: String? get() = get(ACCESS_TOKEN)
    val refreshToken: String? get() = get(REFRESH_TOKEN)

    /** True when a refresh token exists and (if its expiry is known) has not passed. */
    fun hasUsableSession(now: Instant): Boolean {
        val t = tokens() ?: return false
        return t.refreshTokenExpiresAt?.isAfter(now) ?: true
    }

    /** Secure wipe: removes all ciphertext and destroys the Keystore key so old ciphertext can never be decrypted. */
    @Synchronized
    fun wipe() {
        kv.clear()
        cipher.destroyKey()
    }

    private fun aad(name: String) = "veltrix-admin:$name".toByteArray(Charsets.UTF_8)

    companion object {
        const val ACCESS_TOKEN = "access_token"
        const val ACCESS_EXPIRES = "access_expires"
        const val REFRESH_TOKEN = "refresh_token"
        const val REFRESH_EXPIRES = "refresh_expires"
        const val PROFILE = "profile"

        fun parseInstant(s: String): Instant? = runCatching { Instant.parse(s) }.getOrNull()
            ?: runCatching { java.time.OffsetDateTime.parse(s).toInstant() }.getOrNull()
    }
}
