package com.veltrix.admin.data.license

import java.time.Instant
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import java.util.Base64
import java.util.UUID
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/**
 * Mirrors the server's LicenseCertificatePayload as serialized by System.Text.Json Web defaults (camelCase, declaration
 * order). The customer apps verify the signature over these exact bytes and then parse them, so only the field names matter.
 */
@Serializable
data class LicensePayload(
    val v: Int, val lid: String, val tid: String, val tenantName: String, val did: String, val iid: String, val kth: String,
    val fp: String, val plan: String, val type: String, val ent: List<String>, val maxDev: Int, val iat: Long, val exp: Long,
    val lexp: Long?, val status: String,
)

/** Key-source abstraction: Android Keystore in the app, a software key in JVM tests. Same signing code path either way. */
interface LicenseSigner {
    /** ECDSA P-256 / SHA-256 signature over [data], DER (RFC 3279) encoded — what java.security returns for SHA256withECDSA. */
    fun sign(data: ByteArray): ByteArray
}

data class IssuedLicense(val payload: LicensePayload, val code: String)

/** Builds and signs offline licenses exactly like tools/license-issuer (OfflineIssuer) and the server's LicenseSigner. */
object OfflineIssuer {
    const val FULL_PLAN = "full"
    const val OFFLINE_TYPE = "Offline"
    const val MIN_DAYS = 1
    const val MAX_DAYS = 36_500
    val PRESET_DAYS = listOf(30, 90, 365)
    private const val DAY_SECONDS = 86_400L

    /** Features.All from modules/shared-kernel: the single "full" plan grants every feature. */
    val ALL_FEATURES = listOf("pos", "inventory", "purchasing", "crm", "sales", "accounting", "hr", "reports", "intelligence",
        "multi_branch", "mobile", "api")

    private val json = Json

    /** Parses a duration in days; null when not an integer in [MIN_DAYS]..[MAX_DAYS]. */
    fun parseDays(text: String?): Int? = text?.trim()?.toIntOrNull()?.takeIf { it in MIN_DAYS..MAX_DAYS }

    /** Expiry = issue time + days × 24 h, in epoch seconds (same as DateTimeOffset.AddDays). */
    fun expiry(iatEpoch: Long, days: Int): Long {
        require(days in MIN_DAYS..MAX_DAYS) { "Days must be between $MIN_DAYS and $MAX_DAYS." }
        return iatEpoch + days * DAY_SECONDS
    }

    /** Whole days remaining before [expEpoch] (0 once expired). */
    fun daysLeft(expEpoch: Long, nowEpoch: Long): Long = if (nowEpoch >= expEpoch) 0 else (expEpoch - nowEpoch + DAY_SECONDS - 1) / DAY_SECONDS

    fun buildPayload(deviceCode: String, shopName: String, days: Int, now: Instant, newId: () -> String = { UUID.randomUUID().toString() }): LicensePayload {
        require(shopName.isNotBlank()) { "The shop name is required." }
        val device = DeviceCode.decode(deviceCode) ?: throw IllegalArgumentException("Invalid device code.")
        val iat = now.epochSecond
        val exp = expiry(iat, days)
        return LicensePayload(1, newId(), newId(), shopName.trim(), newId(), device.installationId, device.keyThumbprint, "",
            FULL_PLAN, OFFLINE_TYPE, ALL_FEATURES, 1, iat, exp, exp, "Active")
    }

    fun payloadJson(payload: LicensePayload): ByteArray = json.encodeToString(LicensePayload.serializer(), payload).toByteArray(Charsets.UTF_8)

    /** cert = base64url(JSON payload bytes) + "." + base64url(DER signature over those same bytes). */
    fun sign(payload: LicensePayload, signer: LicenseSigner): String {
        val body = payloadJson(payload)
        return b64Url(body) + "." + b64Url(signer.sign(body))
    }

    fun issue(signer: LicenseSigner, deviceCode: String, shopName: String, days: Int, now: Instant = Instant.now()): IssuedLicense {
        val payload = buildPayload(deviceCode, shopName, days, now)
        return IssuedLicense(payload, sign(payload, signer))
    }

    /** Activation file: '#' comment lines (ignored by the apps) followed by the code — same as tools/license-issuer. */
    fun activationFileText(p: LicensePayload, code: String): String = activationFileText(p.tenantName, p.exp, p.lid, code)

    fun activationFileText(shop: String, expEpoch: Long, licenseId: String, code: String): String = buildString {
        append("# VELTRIX OS offline activation code\n")
        append("# Shop: ").append(shop).append('\n')
        append("# Plan: ").append(FULL_PLAN).append(" (all features)\n")
        append("# Valid until: ").append(utc(expEpoch)).append('\n')
        append("# License id: ").append(licenseId).append('\n')
        append(code).append('\n')
    }

    fun safeFileName(name: String): String {
        val s = name.trim().map { if (it.isWhitespace() || it in "\\/:*?\"<>|" || it.code < 32) '-' else it }.joinToString("").trim('-', '.')
        return (if (s.isEmpty()) "shop" else s) + "-activation.txt"
    }

    private fun utc(epoch: Long): String =
        DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm 'UTC'").withZone(ZoneOffset.UTC).format(Instant.ofEpochSecond(epoch))

    fun b64Url(b: ByteArray): String = Base64.getUrlEncoder().withoutPadding().encodeToString(b)
}
