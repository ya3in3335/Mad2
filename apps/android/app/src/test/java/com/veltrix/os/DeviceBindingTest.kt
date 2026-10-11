package com.veltrix.os

import com.veltrix.os.data.security.BindingPlatform
import com.veltrix.os.data.security.BindingStatus
import com.veltrix.os.data.security.DeviceBinding
import java.security.SecureRandom
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Test

/** Trust-on-first-use device binding with fakes standing in for the Android Keystore and the encrypted store. */
class DeviceBindingTest {
    /** One phone: its Keystore (identity key thumbprint + HMAC key) never leaves it; app data (prefs) can be copied. */
    private class FakePhone(var data: AppData = AppData()) : BindingPlatform {
        private val keyThumb = ByteArray(32).also { SecureRandom().nextBytes(it) }.joinToString("") { "%02x".format(it) }
        private var macKey: ByteArray? = null
        override val installationId get() = data.iid
        override fun keyThumbprint() = keyThumb
        override fun mac(data: ByteArray, create: Boolean): ByteArray? {
            val k = macKey ?: if (create) ByteArray(32).also { SecureRandom().nextBytes(it); macKey = it } else return null
            return Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(k, "HmacSHA256")) }.doFinal(data)
        }
        // The real store is encrypted by a Keystore AES key: on another phone it reads as null.
        override fun readBinding() = data.binding?.takeIf { data.owner === this }
        override fun writeBinding(value: String) { data.binding = value; data.owner = this }
        override fun hasMarker() = data.marker
        override fun writeMarker() { data.marker = true }
    }

    private data class AppData(var iid: String = "0f8fad5b-d9cb-469f-a165-70867728950e", var binding: String? = null,
        var marker: Boolean = false, var owner: Any? = null)

    private fun binding(p: BindingPlatform) = DeviceBinding(p) { 1_760_000_000L }

    @Test fun firstLaunchBindsThenVerifies() {
        val phone = FakePhone()
        val b = binding(phone)
        assertEquals(BindingStatus.NewlyBound, b.verify())
        assertNotNull(phone.data.binding)
        assertEquals(1_760_000_000L, b.record?.boundAt)
        assertEquals(BindingStatus.Bound, binding(phone).verify())
    }

    @Test fun dataMovedToAnotherPhoneIsRefused() {
        val a = FakePhone().also { binding(it).verify() }
        val other = FakePhone(a.data.copy())
        assertEquals(BindingStatus.NotLicensed, binding(other).verify())
        // Even if the encrypted record were readable there, the HMAC key and identity key are not.
        val leaky = object : BindingPlatform by other { override fun readBinding() = a.data.binding }
        assertEquals(BindingStatus.NotLicensed, binding(leaky).verify())
    }

    @Test fun tamperedRecordIsRefused() {
        val phone = FakePhone().also { binding(it).verify() }
        phone.data.binding = phone.data.binding!!.replace("1760000000", "1700000000")
        assertEquals(BindingStatus.NotLicensed, binding(phone).verify())
        phone.data.binding = "garbage"
        assertEquals(BindingStatus.NotLicensed, binding(phone).verify())
    }

    @Test fun deletedBindingAfterFirstRunIsRefused() {
        val phone = FakePhone().also { binding(it).verify() }
        phone.data.binding = null
        assertEquals(BindingStatus.NotLicensed, binding(phone).verify())
    }

    @Test fun changedInstallationIdIsRefused() {
        val phone = FakePhone().also { binding(it).verify() }
        phone.data.iid = "11111111-2222-3333-4444-555555555555"
        assertEquals(BindingStatus.NotLicensed, binding(phone).verify())
    }
}
