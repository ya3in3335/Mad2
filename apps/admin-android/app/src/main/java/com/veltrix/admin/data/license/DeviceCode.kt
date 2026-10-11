package com.veltrix.admin.data.license

import java.nio.ByteBuffer
import java.util.Base64
import java.util.UUID

/** A decoded customer device code: installation id (lower-case UUID) and lower-case hex SHA-256 key thumbprint. */
data class DeviceIdentity(val installationId: String, val keyThumbprint: String)

/**
 * Device code shown by VELTRIX OS on its activation screen (same format as the .NET OfflineLicensing helpers and the
 * customer app's OfflineActivation): base64url(16-byte installation id, big-endian UUID || 32-byte raw SHA-256 thumbprint),
 * 64 characters without padding, usually displayed in space-separated groups of 8.
 */
object DeviceCode {
    const val LENGTH = 64
    private const val GROUP_SIZE = 8
    private val codeChars = Regex("^[A-Za-z0-9_-]+$")

    fun strip(text: String): String = text.filterNot { it.isWhitespace() }

    fun decode(text: String?): DeviceIdentity? {
        if (text.isNullOrBlank()) return null
        val clean = strip(text)
        if (clean.length != LENGTH || !codeChars.matches(clean)) return null
        val bytes = runCatching { Base64.getUrlDecoder().decode(clean) }.getOrNull() ?: return null
        if (bytes.size != 48) return null
        val buf = ByteBuffer.wrap(bytes)
        val uuid = UUID(buf.long, buf.long)
        return DeviceIdentity(uuid.toString(), bytes.copyOfRange(16, 48).joinToString("") { "%02x".format(it) })
    }

    fun isValid(text: String?): Boolean = decode(text) != null

    fun encode(installationId: String, keyThumbprintHex: String): String {
        val hex = keyThumbprintHex.trim()
        require(hex.length == 64) { "The key thumbprint must be a 32-byte SHA-256 hex string." }
        val kth = ByteArray(32) { hex.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
        val uuid = UUID.fromString(installationId)
        val bytes = ByteBuffer.allocate(48).putLong(uuid.mostSignificantBits).putLong(uuid.leastSignificantBits).put(kth).array()
        return Base64.getUrlEncoder().withoutPadding().encodeToString(bytes)
    }

    fun format(code: String): String = strip(code).chunked(GROUP_SIZE).joinToString(" ")

    /** Short form for lists: first and last 6 characters. */
    fun short(code: String): String {
        val c = strip(code)
        return if (c.length <= 14) c else c.take(6) + "…" + c.takeLast(6)
    }
}
