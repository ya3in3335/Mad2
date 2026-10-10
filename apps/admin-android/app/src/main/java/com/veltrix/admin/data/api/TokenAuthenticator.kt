package com.veltrix.admin.data.api

import okhttp3.Authenticator
import okhttp3.Interceptor
import okhttp3.Request
import okhttp3.Response
import okhttp3.Route

/** Read/write access to the current session tokens (backed by the encrypted vault). */
interface TokenSource {
    fun accessToken(): String?
    fun refreshToken(): String?
    fun store(tokens: TokenResponse)
    fun clear()
}

/** Result of calling POST /api/v1/auth/refresh. */
sealed interface RefreshOutcome {
    data class Refreshed(val tokens: TokenResponse) : RefreshOutcome
    /** Server rejected the refresh token (expired, revoked, reused): the session is over. */
    data object Rejected : RefreshOutcome
    /** Network/server problem: keep the session, fail this request only. */
    data object Unavailable : RefreshOutcome
}

fun interface TokenRefresher {
    fun refresh(refreshToken: String): RefreshOutcome
}

/** Adds the bearer token and language to every authenticated API request. */
class AuthHeaderInterceptor(private val tokens: TokenSource, private val language: () -> String) : Interceptor {
    override fun intercept(chain: Interceptor.Chain): Response {
        val b = chain.request().newBuilder().header("Accept", "application/json").header("Accept-Language", language())
        if (chain.request().header(SKIP_AUTH) != null) b.removeHeader(SKIP_AUTH)
        else tokens.accessToken()?.let { b.header("Authorization", "Bearer $it") }
        return chain.proceed(b.build())
    }

    companion object { const val SKIP_AUTH = "X-Veltrix-Skip-Auth" }
}

/**
 * OkHttp [Authenticator] implementing refresh-token rotation. On 401 it refreshes once (serialized across threads so
 * concurrent 401s share a single rotation — the server treats refresh-token reuse as theft), retries the request with the
 * new access token, and on rejection wipes the session and reports expiry.
 */
class TokenAuthenticator(
    private val tokens: TokenSource,
    private val refresher: TokenRefresher,
    private val onSessionExpired: () -> Unit,
) : Authenticator {
    private val lock = Any()

    override fun authenticate(route: Route?, response: Response): Request? {
        val sentAuth = response.request.header("Authorization") ?: return null // anonymous call: nothing to refresh
        if (priorResponses(response) >= MAX_ATTEMPTS) return null
        synchronized(lock) {
            val current = tokens.accessToken()
            // Another thread already rotated the tokens while this request was in flight: just retry with the new token.
            if (current != null && "Bearer $current" != sentAuth) return withToken(response.request, current)
            val refresh = tokens.refreshToken()
            if (refresh == null) { expire(); return null }
            return when (val outcome = refresher.refresh(refresh)) {
                is RefreshOutcome.Refreshed -> {
                    tokens.store(outcome.tokens)
                    withToken(response.request, outcome.tokens.accessToken)
                }
                RefreshOutcome.Rejected -> { expire(); null }
                RefreshOutcome.Unavailable -> null
            }
        }
    }

    private fun expire() {
        tokens.clear()
        onSessionExpired()
    }

    private fun withToken(request: Request, token: String) = request.newBuilder().header("Authorization", "Bearer $token").build()

    private fun priorResponses(response: Response): Int {
        var n = 0
        var r = response.priorResponse
        while (r != null) { n++; r = r.priorResponse }
        return n
    }

    companion object { const val MAX_ATTEMPTS = 2 }
}
