package com.veltrix.os.data.security

import java.security.KeyFactory
import java.security.Signature
import java.security.spec.X509EncodedKeySpec
import java.util.Base64
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/** Decoded, signature-verified license certificate (see server LicenseCertificatePayload). */
@Serializable
data class LicenseCertificate(
    val v: Int, val lid: String, val tid: String, val tenantName: String, val did: String, val iid: String, val kth: String,
    val fp: String, val plan: String, val type: String, val ent: List<String>, val maxDev: Int, val iat: Long, val exp: Long,
    val lexp: Long? = null, val status: String,
)

sealed interface LicenseState {
    data class Valid(val cert: LicenseCertificate, val offlineSecondsLeft: Long) : LicenseState
    data class Expired(val cert: LicenseCertificate) : LicenseState
    data object Missing : LicenseState
    data object Tampered : LicenseState
    data object WrongDevice : LicenseState
    data object ClockRollback : LicenseState
}

/**
 * Offline license validation. The vendor signs certificates with ECDSA P-256 (DER); the client only holds the public key.
 * Limits (documented in SECURITY_NOTES.md): a remote revocation takes effect only at the next online refresh, or when
 * the offline certificate expires (plan grace period). Clock rollback beyond a tolerance is detected with a monotonic
 * watermark, forcing an online re-validation.
 */
object LicenseVerifier {
    private val json = Json { ignoreUnknownKeys = true }
    const val CLOCK_TOLERANCE_SECONDS = 600L

    fun decodeAndVerify(certificate: String, vendorSpki: String): LicenseCertificate? = try {
        val parts = certificate.split('.')
        if (parts.size != 2) null else {
            val body = Base64.getUrlDecoder().decode(parts[0])
            val sig = Base64.getUrlDecoder().decode(parts[1])
            val pub = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(Base64.getDecoder().decode(vendorSpki)))
            val ok = Signature.getInstance("SHA256withECDSA").run { initVerify(pub); update(body); verify(sig) }
            if (ok) json.decodeFromString(LicenseCertificate.serializer(), String(body, Charsets.UTF_8)) else null
        }
    } catch (e: Exception) {
        null
    }

    /**
     * @param watermark highest wall-clock time (epoch seconds) previously observed; returns the new watermark.
     */
    fun evaluate(certificate: String?, vendorSpki: String?, installationId: String, keyThumbprint: String, nowEpoch: Long, watermark: Long): Pair<LicenseState, Long> {
        if (certificate.isNullOrEmpty() || vendorSpki.isNullOrEmpty()) return LicenseState.Missing to watermark
        val cert = decodeAndVerify(certificate, vendorSpki) ?: return LicenseState.Tampered to watermark
        if (cert.iid != installationId || cert.kth != keyThumbprint) return LicenseState.WrongDevice to watermark
        if (nowEpoch + CLOCK_TOLERANCE_SECONDS < maxOf(watermark, cert.iat)) return LicenseState.ClockRollback to watermark
        val newWatermark = maxOf(watermark, nowEpoch)
        return if (nowEpoch >= cert.exp) LicenseState.Expired(cert) to newWatermark
        else LicenseState.Valid(cert, cert.exp - nowEpoch) to newWatermark
    }
}
