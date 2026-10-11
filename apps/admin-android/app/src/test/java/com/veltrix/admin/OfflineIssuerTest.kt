package com.veltrix.admin

import com.veltrix.admin.customer.LicenseState
import com.veltrix.admin.customer.LicenseVerifier
import com.veltrix.admin.data.license.DeviceCode
import com.veltrix.admin.data.license.LicenseSigner
import com.veltrix.admin.data.license.OfflineIssuer
import com.veltrix.admin.data.license.VendorKeyStore
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.time.Instant
import java.util.Base64
import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Software key behind the same [LicenseSigner] abstraction the Keystore key implements in the app. */
private class SoftwareSigner(private val pair: KeyPair) : LicenseSigner {
    override fun sign(data: ByteArray): ByteArray = Signature.getInstance("SHA256withECDSA").run { initSign(pair.private); update(data); sign() }
}

private fun newPair(): KeyPair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()

class OfflineIssuerTest {
    private val pair = newPair()
    private val spki = Base64.getEncoder().encodeToString(pair.public.encoded)
    private val signer = SoftwareSigner(pair)
    private val iid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301"
    private val kth = "a1".repeat(32)
    private val code = DeviceCode.encode(iid, kth)
    private val now = Instant.parse("2026-10-11T08:00:00Z")

    @Test fun customerAppAcceptsIssuedCode() {
        val issued = OfflineIssuer.issue(signer, DeviceCode.format(code), "  Boutique Amine  ", 365, now)
        val (state, _) = LicenseVerifier.evaluate(issued.code, spki, iid, kth, now.epochSecond + 60, 0)
        assertTrue(state is LicenseState.Valid)
        val cert = (state as LicenseState.Valid).cert
        assertEquals("Boutique Amine", cert.tenantName)
        assertEquals("full", cert.plan)
        assertEquals("Offline", cert.type)
        assertEquals("Active", cert.status)
        assertEquals("", cert.fp)
        assertEquals(1, cert.v)
        assertEquals(1, cert.maxDev)
        assertEquals(OfflineIssuer.ALL_FEATURES, cert.ent)
        assertEquals(12, cert.ent.size)
        assertEquals(now.epochSecond, cert.iat)
        assertEquals(now.epochSecond + 365 * 86_400L, cert.exp)
        assertEquals(cert.exp, cert.lexp)
        listOf(cert.lid, cert.tid, cert.did).forEach { UUID.fromString(it) }
        assertEquals(3, setOf(cert.lid, cert.tid, cert.did).size)
    }

    @Test fun payloadJsonUsesServerFieldNamesInOrder() {
        val p = OfflineIssuer.buildPayload(code, "Shop", 30, now) { "00000000-0000-0000-0000-000000000001" }
        val json = String(OfflineIssuer.payloadJson(p))
        val keys = Regex("\"([a-zA-Z]+)\":").findAll(json).map { it.groupValues[1] }.toList()
        assertEquals(listOf("v", "lid", "tid", "tenantName", "did", "iid", "kth", "fp", "plan", "type", "ent", "maxDev", "iat", "exp", "lexp", "status"), keys)
    }

    @Test fun signatureCoversExactlyTheEncodedPayloadBytes() {
        val issued = OfflineIssuer.issue(signer, code, "Shop", 30, now)
        val (body, sig) = issued.code.split('.')
        val bytes = Base64.getUrlDecoder().decode(body)
        assertTrue(Signature.getInstance("SHA256withECDSA").run { initVerify(pair.public); update(bytes); verify(Base64.getUrlDecoder().decode(sig)) })
        assertTrue(!issued.code.contains('=') && !issued.code.contains('+') && !issued.code.contains('/'))
    }

    @Test fun wrongDeviceIsRejected() {
        val issued = OfflineIssuer.issue(signer, code, "Shop", 30, now)
        val otherIid = "3f2504e0-4f89-11d3-9a0c-0305e82c3302"
        assertEquals(LicenseState.WrongDevice, LicenseVerifier.evaluate(issued.code, spki, otherIid, kth, now.epochSecond, 0).first)
        assertEquals(LicenseState.WrongDevice, LicenseVerifier.evaluate(issued.code, spki, iid, "b2".repeat(32), now.epochSecond, 0).first)
    }

    @Test fun otherVendorKeyOrTamperingIsRejected() {
        val issued = OfflineIssuer.issue(signer, code, "Shop", 30, now)
        val otherSpki = Base64.getEncoder().encodeToString(newPair().public.encoded)
        assertEquals(LicenseState.Tampered, LicenseVerifier.evaluate(issued.code, otherSpki, iid, kth, now.epochSecond, 0).first)
        val (body, sig) = issued.code.split('.')
        val json = String(Base64.getUrlDecoder().decode(body)).replace("\"maxDev\":1", "\"maxDev\":9")
        val forged = OfflineIssuer.b64Url(json.toByteArray()) + "." + sig
        assertEquals(LicenseState.Tampered, LicenseVerifier.evaluate(forged, spki, iid, kth, now.epochSecond, 0).first)
    }

    @Test fun expiresAfterDuration() {
        val issued = OfflineIssuer.issue(signer, code, "Shop", 30, now)
        val exp = now.epochSecond + 30 * 86_400L
        assertTrue(LicenseVerifier.evaluate(issued.code, spki, iid, kth, exp - 1, 0).first is LicenseState.Valid)
        assertTrue(LicenseVerifier.evaluate(issued.code, spki, iid, kth, exp, 0).first is LicenseState.Expired)
    }

    @Test fun invalidInputsAreRefused() {
        assertTrue(runCatching { OfflineIssuer.buildPayload("bad", "Shop", 30, now) }.isFailure)
        assertTrue(runCatching { OfflineIssuer.buildPayload(code, " ", 30, now) }.isFailure)
        assertTrue(runCatching { OfflineIssuer.buildPayload(code, "Shop", 0, now) }.isFailure)
    }

    @Test fun activationFileIsAcceptedByCustomerExtraction() {
        val issued = OfflineIssuer.issue(signer, code, "Shop A", 90, now)
        val text = OfflineIssuer.activationFileText(issued.payload, issued.code)
        assertTrue(text.startsWith("# VELTRIX OS offline activation code\n"))
        val extracted = text.lines().map { it.trim() }.filterNot { it.startsWith("#") }.joinToString("") { l -> l.filterNot(Char::isWhitespace) }
        assertEquals(issued.code, extracted)
        assertTrue(text.contains("# Valid until: " + "2027-01-09 08:00 UTC"))
        assertEquals("Shop-A-activation.txt", OfflineIssuer.safeFileName(" Shop A "))
        assertEquals("shop-activation.txt", OfflineIssuer.safeFileName("///"))
    }

    @Test fun publicKeyPemWrapsSpki() {
        val pem = VendorKeyStore.spkiPem(pair.public.encoded)
        assertTrue(pem.startsWith("-----BEGIN PUBLIC KEY-----\n") && pem.endsWith("-----END PUBLIC KEY-----\n"))
        val body = pem.lines().filter { it.isNotEmpty() && !it.startsWith("-----") }
        assertTrue(body.all { it.length <= 64 })
        assertEquals(spki, body.joinToString(""))
        assertNotNull(LicenseVerifier.decodeAndVerify(OfflineIssuer.issue(signer, code, "S", 1, now).code, VendorKeyStore.spkiBase64(pair.public.encoded)))
        assertNull(LicenseVerifier.decodeAndVerify("abc", spki))
    }
}
