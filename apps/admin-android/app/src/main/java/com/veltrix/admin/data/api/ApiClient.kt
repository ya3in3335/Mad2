package com.veltrix.admin.data.api

import com.veltrix.admin.data.security.SessionTokens
import com.veltrix.admin.data.security.TokenVault
import com.veltrix.admin.data.settings.AdminSettings
import java.io.IOException
import java.util.concurrent.TimeUnit
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.withContext
import kotlinx.serialization.KSerializer
import kotlinx.serialization.json.Json
import kotlinx.serialization.serializer
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody

val AdminJson = Json { ignoreUnknownKeys = true; explicitNulls = false; encodeDefaults = true; coerceInputValues = true }

/** Broadcasts that the session ended server-side (refresh rejected) so the UI can return to sign-in. */
@Singleton
class SessionEvents @Inject constructor() {
    private val _expired = MutableSharedFlow<Unit>(extraBufferCapacity = 1)
    val expired: SharedFlow<Unit> = _expired.asSharedFlow()
    fun emitExpired() { _expired.tryEmit(Unit) }
}

/** [TokenSource] over the encrypted vault; refreshed profiles are persisted alongside the rotated tokens. */
@Singleton
class VaultTokenSource @Inject constructor(private val vault: TokenVault) : TokenSource {
    override fun accessToken(): String? = vault.accessToken
    override fun refreshToken(): String? = vault.refreshToken
    override fun store(tokens: TokenResponse) {
        vault.saveTokens(SessionTokens(tokens.accessToken, tokens.accessTokenExpiresAt?.let(TokenVault::parseInstant), tokens.refreshToken,
            tokens.refreshTokenExpiresAt?.let(TokenVault::parseInstant)))
        tokens.user?.let { vault.put(TokenVault.PROFILE, AdminJson.encodeToString(UserProfile.serializer(), it)) }
    }
    override fun clear() {
        listOf(TokenVault.ACCESS_TOKEN, TokenVault.ACCESS_EXPIRES, TokenVault.REFRESH_TOKEN, TokenVault.REFRESH_EXPIRES, TokenVault.PROFILE).forEach { vault.put(it, null) }
    }
}

/**
 * HTTP client for the VELTRIX API. OkHttp + kotlinx.serialization, identical stack to the VELTRIX OS app.
 * Authentication: bearer access token, OkHttp [TokenAuthenticator] for refresh rotation, X-Confirm-Password for step-up.
 */
@Singleton
class ApiClient @Inject constructor(
    private val settings: AdminSettings,
    private val tokens: VaultTokenSource,
    private val events: SessionEvents,
) {
    val json = AdminJson
    private val base: OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS).readTimeout(30, TimeUnit.SECONDS).writeTimeout(30, TimeUnit.SECONDS)
        .retryOnConnectionFailure(true)
        .build()

    private val refresher = TokenRefresher { refreshToken ->
        val body = json.encodeToString(RefreshRequest.serializer(), RefreshRequest(refreshToken)).toRequestBody(JSON)
        val request = Request.Builder().url(url(ApiRoutes.AUTH_REFRESH)).header("Accept", "application/json").post(body).build()
        try {
            base.newCall(request).execute().use { r ->
                val text = r.body?.string().orEmpty()
                when {
                    r.isSuccessful -> runCatching { RefreshOutcome.Refreshed(json.decodeFromString(TokenResponse.serializer(), text)) }
                        .getOrElse { RefreshOutcome.Unavailable }
                    r.code in 400..499 && r.code != 429 -> RefreshOutcome.Rejected
                    else -> RefreshOutcome.Unavailable
                }
            }
        } catch (e: IOException) {
            RefreshOutcome.Unavailable
        }
    }

    private val client: OkHttpClient = base.newBuilder()
        .addInterceptor(AuthHeaderInterceptor(tokens) { settings.language })
        .authenticator(TokenAuthenticator(tokens, refresher, events::emitExpired))
        .build()

    /** Checks that [candidate] answers the anonymous liveness probe (first-launch server setup). */
    suspend fun probe(candidate: String): Boolean = withContext(Dispatchers.IO) {
        val target = candidate.toHttpUrlOrNull()?.newBuilder()?.addPathSegments(ApiRoutes.HEALTH_LIVE)?.build() ?: return@withContext false
        try {
            base.newCall(Request.Builder().url(target).get().build()).execute().use { it.isSuccessful }
        } catch (e: IOException) {
            throw OfflineException(e)
        }
    }

    private fun baseUrl(): HttpUrl = settings.serverUrl.toHttpUrlOrNull() ?: throw IllegalStateException("Server URL not configured")

    fun url(path: String, query: Map<String, Any?> = emptyMap()): HttpUrl {
        val b = baseUrl().newBuilder().addPathSegments(path.trim('/'))
        query.forEach { (k, v) -> if (v != null && v.toString().isNotEmpty()) b.addQueryParameter(k, v.toString()) }
        return b.build()
    }

    suspend inline fun <reified T> get(path: String, query: Map<String, Any?> = emptyMap()): T =
        send("GET", path, query, null, serializer<T>())

    suspend inline fun <reified B, reified T> post(path: String, body: B, headers: Map<String, String> = emptyMap(), auth: Boolean = true): T =
        send("POST", path, emptyMap(), json.encodeToString(serializer<B>(), body), serializer<T>(), headers, auth)

    suspend inline fun <reified B> postNoContent(path: String, body: B, headers: Map<String, String> = emptyMap(), auth: Boolean = true) {
        send<Unit>("POST", path, emptyMap(), json.encodeToString(serializer<B>(), body), null, headers, auth)
    }

    suspend inline fun <reified B, reified T> put(path: String, body: B, headers: Map<String, String> = emptyMap()): T =
        send("PUT", path, emptyMap(), json.encodeToString(serializer<B>(), body), serializer<T>(), headers)

    suspend fun <T> send(
        method: String, path: String, query: Map<String, Any?>, body: String?, ser: KSerializer<T>?,
        headers: Map<String, String> = emptyMap(), auth: Boolean = true,
    ): T = withContext(Dispatchers.IO) {
        val builder = Request.Builder().url(url(path, query))
        headers.forEach { (k, v) -> builder.header(k, v) }
        if (!auth) builder.header(AuthHeaderInterceptor.SKIP_AUTH, "1")
        val rb = body?.toRequestBody(JSON)
        builder.method(method, if (method == "GET") null else rb ?: "".toRequestBody(null))
        val (status, text) = try {
            client.newCall(builder.build()).execute().use { it.code to (it.body?.string() ?: "") }
        } catch (e: IOException) {
            throw OfflineException(e)
        }
        if (status !in 200..299) {
            if (status == 401 && auth && tokens.refreshToken() == null) throw SessionExpiredException()
            val problem = runCatching { json.decodeFromString(ProblemResponse.serializer(), text) }.getOrNull()
            val msg = problem?.errors?.values?.flatten()?.firstOrNull() ?: problem?.detail ?: problem?.title ?: "HTTP $status"
            throw ApiException(status, problem?.code, msg, problem?.correlationId)
        }
        @Suppress("UNCHECKED_CAST")
        if (ser == null || text.isBlank()) Unit as T else json.decodeFromString(ser, text)
    }

    companion object {
        private val JSON = "application/json".toMediaType()
        const val CONFIRM_PASSWORD = "X-Confirm-Password"
        const val IDEMPOTENCY_KEY = "Idempotency-Key"
    }
}
