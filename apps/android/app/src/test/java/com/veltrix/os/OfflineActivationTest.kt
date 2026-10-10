package com.veltrix.os

import com.veltrix.os.data.security.LicenseState
import com.veltrix.os.data.security.LicenseVerifier
import com.veltrix.os.data.security.OfflineActivation
import java.security.KeyPairGenerator
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.util.Base64
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class OfflineActivationTest {
    private val iid = "00112233-4455-6677-8899-aabbccddeeff"
    private val kth = "ab".repeat(32)

    /** Same vector as the .NET OfflineLicensingTests: both apps and the issuer must agree byte for byte. */
    @Test fun deviceCodeMatchesKnownVector() =
        assertEquals("ABEiM0RVZneImaq7zN3u_6urq6urq6urq6urq6urq6urq6urq6urq6urq6urq6ur", OfflineActivation.encodeDeviceCode(iid, kth))

    @Test fun deviceCodeRoundTripsGrouped() {
        val grouped = OfflineActivation.formatDeviceCode(OfflineActivation.encodeDeviceCode(iid, kth))
        assertEquals(7, grouped.count { it == ' ' })
        assertEquals(iid to kth, OfflineActivation.decodeDeviceCode(" " + grouped.replace(" ", "\n ") + " "))
    }

    @Test fun malformedDeviceCodesAreRejected() {
        assertNull(OfflineActivation.decodeDeviceCode(null))
        assertNull(OfflineActivation.decodeDeviceCode("abc"))
        assertNull(OfflineActivation.decodeDeviceCode("!".repeat(64)))
    }

    @Test fun activationFileIsParsedAndVerifiedOnTheBoundDeviceOnly() {
        val vendor = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
        val spki = Base64.getEncoder().encodeToString(vendor.public.encoded)
        val now = 1_800_000_000L
        val payload = """{"v":1,"lid":"l","tid":"t","tenantName":"Shop","did":"d","iid":"$iid","kth":"$kth","fp":"","plan":"full","type":"Offline","ent":["pos","inventory"],"maxDev":1,"iat":$now,"exp":${now + 86400},"lexp":${now + 86400},"status":"Active"}"""
        val body = payload.toByteArray()
        val sig = Signature.getInstance("SHA256withECDSA").run { initSign(vendor.private); update(body); sign() }
        val enc = Base64.getUrlEncoder().withoutPadding()
        val cert = enc.encodeToString(body) + "." + enc.encodeToString(sig)
        val file = "# VELTRIX OS offline activation code\n# Shop: Shop\n" + cert.chunked(40).joinToString("\r\n") + "\n"
        val extracted = OfflineActivation.extractActivationCode(file)
        assertEquals(cert, extracted)
        val state = LicenseVerifier.evaluate(extracted, spki, iid, kth, now + 60, 0).first
        assertTrue(state is LicenseState.Valid && state.cert.type == OfflineActivation.OFFLINE_TYPE)
        assertTrue(LicenseVerifier.evaluate(extracted, spki, iid, "cd".repeat(32), now + 60, 0).first is LicenseState.WrongDevice)
        assertNull(OfflineActivation.extractActivationCode("# only a comment"))
    }
}
