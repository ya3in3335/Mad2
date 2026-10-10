package com.veltrix.admin.data.api

import okhttp3.HttpUrl.Companion.toHttpUrlOrNull

/**
 * Validates the server address typed on first launch. HTTPS is mandatory; plain HTTP is accepted only in debug builds
 * and only for the emulator host / loopback (matching the debug network-security-config).
 */
object ServerUrlValidator {
    enum class Error { Empty, Malformed, HttpsRequired, NotAllowed }

    sealed interface Result {
        data class Valid(val url: String) : Result
        data class Invalid(val error: Error) : Result
    }

    val DEV_CLEARTEXT_HOSTS = setOf("10.0.2.2", "localhost", "127.0.0.1")

    fun validate(input: String, allowDevCleartext: Boolean): Result {
        val raw = input.trim()
        if (raw.isEmpty()) return Result.Invalid(Error.Empty)
        val withScheme = if (raw.contains("://")) raw else "https://$raw"
        val scheme = withScheme.substringBefore("://").lowercase()
        if (scheme != "https" && scheme != "http") return Result.Invalid(Error.Malformed)
        val url = withScheme.toHttpUrlOrNull() ?: return Result.Invalid(Error.Malformed)
        if (url.username.isNotEmpty() || url.password.isNotEmpty() || url.query != null || url.fragment != null) return Result.Invalid(Error.NotAllowed)
        if (url.host.isBlank()) return Result.Invalid(Error.Malformed)
        if (!url.isHttps && !(allowDevCleartext && url.host in DEV_CLEARTEXT_HOSTS)) return Result.Invalid(Error.HttpsRequired)
        // Keep an optional reverse-proxy path prefix, drop the trailing slash.
        return Result.Valid(url.toString().trimEnd('/'))
    }
}
