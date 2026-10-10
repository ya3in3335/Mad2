package com.veltrix.admin

import com.veltrix.admin.data.api.ServerUrlValidator
import com.veltrix.admin.data.api.ServerUrlValidator.Error
import com.veltrix.admin.data.api.ServerUrlValidator.Result
import org.junit.Assert.assertEquals
import org.junit.Test

class ServerUrlValidatorTest {
    @Test fun emptyIsRejected() = assertEquals(Result.Invalid(Error.Empty), ServerUrlValidator.validate("  ", false))

    @Test fun missingSchemeDefaultsToHttps() =
        assertEquals(Result.Valid("https://api.example.com"), ServerUrlValidator.validate("api.example.com/", false))

    @Test fun keepsProxyPathPrefix() =
        assertEquals(Result.Valid("https://example.com/veltrix"), ServerUrlValidator.validate("https://example.com/veltrix/", false))

    @Test fun plainHttpRejectedInRelease() =
        assertEquals(Result.Invalid(Error.HttpsRequired), ServerUrlValidator.validate("http://10.0.2.2:8080", false))

    @Test fun plainHttpAllowedOnlyForEmulatorInDebug() {
        assertEquals(Result.Valid("http://10.0.2.2:8080"), ServerUrlValidator.validate("http://10.0.2.2:8080", true))
        assertEquals(Result.Invalid(Error.HttpsRequired), ServerUrlValidator.validate("http://example.com", true))
    }

    @Test fun credentialsAndQueriesRejected() {
        assertEquals(Result.Invalid(Error.NotAllowed), ServerUrlValidator.validate("https://user:pw@example.com", false))
        assertEquals(Result.Invalid(Error.NotAllowed), ServerUrlValidator.validate("https://example.com/?x=1", false))
    }

    @Test fun otherSchemesRejected() = assertEquals(Result.Invalid(Error.Malformed), ServerUrlValidator.validate("ftp://example.com", false))
}
