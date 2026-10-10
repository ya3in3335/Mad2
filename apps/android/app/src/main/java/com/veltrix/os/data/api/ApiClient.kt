package com.veltrix.os.data.api

import com.veltrix.os.BuildConfig
import com.veltrix.os.data.security.SecureStore
import java.io.IOException
import java.util.concurrent.TimeUnit
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.serialization.KSerializer
import kotlinx.serialization.json.Json
import kotlinx.serialization.serializer
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody

class ApiException(val status: Int, val code: String?, override val message: String) : Exception(message)

/** Thrown when the server cannot be reached; callers fall back to offline behaviour. */
class OfflineException(cause: Throwable) : IOException("Server unreachable", cause)

@Singleton
class ApiClient @Inject constructor(private val store: SecureStore, private val settings: AppSettings) {
    val json = Json { ignoreUnknownKeys = true; explicitNulls = false; encodeDefaults = true }
    private val http = OkHttpClient.Builder().connectTimeout(15, TimeUnit.SECONDS).readTimeout(30, TimeUnit.SECONDS).build()
    private val refreshLock = Mutex()
    private val _sessionExpired = MutableSharedFlow<Unit>(extraBufferCapacity = 1)
    val sessionExpired = _sessionExpired.asSharedFlow()

    val baseUrl: String get() = settings.serverUrl.ifBlank { BuildConfig.DEFAULT_SERVER_URL }.trimEnd('/')

    suspend inline fun <reified T> get(path: String, auth: Boolean = true): T = send("GET", path, null, serializer<T>(), auth)
    suspend inline fun <reified B, reified T> post(path: String, body: B, auth: Boolean = true): T =
        send("POST", path, json.encodeToString(serializer<B>(), body), serializer<T>(), auth)
    suspend inline fun <reified B> postNoContent(path: String, body: B, auth: Boolean = true) {
        send("POST", path, json.encodeToString(serializer<B>(), body), null, auth)
    }

    suspend fun <T> send(method: String, path: String, body: String?, ser: KSerializer<T>?, auth: Boolean): T = withContext(Dispatchers.IO) {
        var response = execute(method, path, body, auth)
        if (response.first == 401 && auth && tryRefresh()) response = execute(method, path, body, auth)
        val (status, text) = response
        if (status !in 200..299) {
            val problem = runCatching { json.decodeFromString(ProblemResponse.serializer(), text) }.getOrNull()
            if (status == 401 && auth) _sessionExpired.tryEmit(Unit)
            throw ApiException(status, problem?.code, problem?.errors?.values?.flatten()?.firstOrNull() ?: problem?.title ?: "HTTP $status")
        }
        @Suppress("UNCHECKED_CAST")
        if (ser == null || text.isBlank()) Unit as T else json.decodeFromString(ser, text)
    }

    private fun execute(method: String, path: String, body: String?, auth: Boolean): Pair<Int, String> {
        val builder = Request.Builder().url(baseUrl + path).header("Accept", "application/json")
            .header("Accept-Language", settings.language)
        if (auth) store.get(SecureStore.ACCESS_TOKEN)?.let { builder.header("Authorization", "Bearer $it") }
        val rb = body?.toRequestBody("application/json".toMediaType())
        builder.method(method, if (method == "GET") null else rb ?: "".toRequestBody(null))
        return try {
            http.newCall(builder.build()).execute().use { it.code to (it.body?.string() ?: "") }
        } catch (e: IOException) {
            throw OfflineException(e)
        }
    }

    /** Refresh-token rotation; concurrent 401s share one refresh. */
    private suspend fun tryRefresh(): Boolean = refreshLock.withLock {
        val refresh = store.get(SecureStore.REFRESH_TOKEN) ?: return false
        val (status, text) = execute("POST", "/api/v1/auth/refresh", json.encodeToString(RefreshRequest.serializer(), RefreshRequest(refresh)), false)
        if (status != 200) {
            store.put(SecureStore.ACCESS_TOKEN, null)
            store.put(SecureStore.REFRESH_TOKEN, null)
            return false
        }
        val t = json.decodeFromString(TokenResponse.serializer(), text)
        store.put(SecureStore.ACCESS_TOKEN, t.accessToken)
        store.put(SecureStore.REFRESH_TOKEN, t.refreshToken)
        store.put(SecureStore.PROFILE, json.encodeToString(UserProfile.serializer(), t.user))
        true
    }
}
