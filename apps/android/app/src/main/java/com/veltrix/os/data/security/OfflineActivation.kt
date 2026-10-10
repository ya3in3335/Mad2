package com.veltrix.os.data.security

import java.nio.ByteBuffer
import java.util.Base64
import java.util.UUID

/**
 * Offline (server-less) device-bound activation, compatible with the .NET OfflineLicensing helpers and the owner's
 * license-issuer tool. Device code = base64url(16-byte installation id (big-endian UUID) || 32-byte raw SHA-256 key thumbprint).
 */
object OfflineActivation {
    const val OFFLINE_TYPE = "Offline"
    const val DEVICE_CODE_LENGTH = 64
    private const val GROUP_SIZE = 8
    private val codeChars = Regex("^[A-Za-z0-9_-]+$")

    fun encodeDeviceCode(installationId: String, keyThumbprintHex: String): String {
        val kth = hexToBytes(keyThumbprintHex.trim())
        require(kth.size == 32) { "The key thumbprint must be a 32-byte SHA-256 hex string." }
        val uuid = UUID.fromString(installationId)
        val bytes = ByteBuffer.allocate(48).putLong(uuid.mostSignificantBits).putLong(uuid.leastSignificantBits).put(kth).array()
        return Base64.getUrlEncoder().withoutPadding().encodeToString(bytes)
    }

    /** Space-separated blocks of 8 characters for display and copy. */
    fun formatDeviceCode(code: String): String = strip(code).chunked(GROUP_SIZE).joinToString(" ")

    /** Decodes a (possibly grouped) device code into (installation id, lower-case hex thumbprint), or null. */
    fun decodeDeviceCode(text: String?): Pair<String, String>? {
        if (text.isNullOrBlank()) return null
        val clean = strip(text)
        if (clean.length != DEVICE_CODE_LENGTH || !codeChars.matches(clean)) return null
        val bytes = runCatching { Base64.getUrlDecoder().decode(clean) }.getOrNull() ?: return null
        if (bytes.size != 48) return null
        val buf = ByteBuffer.wrap(bytes)
        val uuid = UUID(buf.long, buf.long)
        return uuid.toString() to bytes.copyOfRange(16, 48).joinToString("") { "%02x".format(it) }
    }

    /** Extracts the certificate from pasted text or an activation file ('#' comment lines and line wrapping are ignored). */
    fun extractActivationCode(text: String?): String? {
        if (text.isNullOrBlank()) return null
        val joined = text.lines().map { it.trim() }.filterNot { it.startsWith("#") }.joinToString("") { strip(it) }
        val parts = joined.split('.')
        if (parts.size != 2 || parts[0].isEmpty() || parts[1].isEmpty()) return null
        return if (parts.all { codeChars.matches(it) }) joined else null
    }

    private fun strip(s: String) = s.filterNot { it.isWhitespace() }

    private fun hexToBytes(hex: String): ByteArray {
        require(hex.length % 2 == 0) { "Invalid hex string." }
        return ByteArray(hex.length / 2) { hex.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
    }
}
