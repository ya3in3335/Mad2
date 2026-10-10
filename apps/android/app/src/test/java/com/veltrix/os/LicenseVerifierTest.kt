package com.veltrix.os

import com.veltrix.os.data.security.LicenseState
import com.veltrix.os.data.security.LicenseVerifier
import java.security.KeyPairGenerator
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.util.Base64
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class LicenseVerifierTest {
    private val vendor = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
    private val spki = Base64.getEncoder().encodeToString(vendor.public.encoded)
    private val now = 1_800_000_000L

    private fun cert(iid: String = "inst-1", kth: String = "thumb", exp: Long = now + 3600, iat: Long = now - 60): String {
        val payload = """{"v":1,"lid":"l","tid":"t","tenantName":"Alpha","did":"d","iid":"$iid","kth":"$kth","fp":"f","plan":"pro","type":"Annual","ent":["pos"],"maxDev":2,"iat":$iat,"exp":$exp,"status":"Active"}"""
        val body = payload.toByteArray()
        val sig = Signature.getInstance("SHA256withECDSA").run { initSign(vendor.private); update(body); sign() }
        val enc = Base64.getUrlEncoder().withoutPadding()
        return enc.encodeToString(body) + "." + enc.encodeToString(sig)
    }

    @Test fun validCertificate() {
        val (s, mark) = LicenseVerifier.evaluate(cert(), spki, "inst-1", "thumb", now, 0)
        assertTrue(s is LicenseState.Valid)
        assertEquals(now, mark)
    }

    @Test fun tamperedCertificateIsRejected() {
        val c = cert()
        val forged = Base64.getUrlEncoder().withoutPadding().encodeToString(String(Base64.getUrlDecoder().decode(c.substringBefore('.'))).replace("\"maxDev\":2", "\"maxDev\":99").toByteArray()) + "." + c.substringAfter('.')
        assertTrue(LicenseVerifier.evaluate(forged, spki, "inst-1", "thumb", now, 0).first is LicenseState.Tampered)
    }

    @Test fun certificateFromAnotherDeviceIsRejected() =
        assertTrue(LicenseVerifier.evaluate(cert(iid = "other"), spki, "inst-1", "thumb", now, 0).first is LicenseState.WrongDevice)

    @Test fun expiredCertificate() =
        assertTrue(LicenseVerifier.evaluate(cert(exp = now - 1), spki, "inst-1", "thumb", now, 0).first is LicenseState.Expired)

    @Test fun clockRollbackDetected() =
        assertTrue(LicenseVerifier.evaluate(cert(), spki, "inst-1", "thumb", now - 7200, now).first is LicenseState.ClockRollback)

    @Test fun missingCertificate() =
        assertTrue(LicenseVerifier.evaluate(null, spki, "inst-1", "thumb", now, 0).first is LicenseState.Missing)
}
