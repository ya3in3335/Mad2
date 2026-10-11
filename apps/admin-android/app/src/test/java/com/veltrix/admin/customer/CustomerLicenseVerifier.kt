package com.veltrix.admin.customer

// Verbatim copy (package renamed) of the customer app's verifier:
// apps/android/app/src/main/java/com/veltrix/os/data/security/LicenseVerifier.kt. Keep in sync.

import java.security.KeyFactory
import java.security.Signature
import java.security.spec.X509EncodedKeySpec
import java.util.Base64
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

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
