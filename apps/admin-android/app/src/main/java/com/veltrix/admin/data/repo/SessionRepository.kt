package com.veltrix.admin.data.repo

import com.veltrix.admin.data.api.ApiClient
import com.veltrix.admin.data.api.ApiRoutes
import com.veltrix.admin.data.api.LoginRequest
import com.veltrix.admin.data.api.LoginResponse
import com.veltrix.admin.data.api.LogoutRequest
import com.veltrix.admin.data.api.TokenResponse
import com.veltrix.admin.data.api.UserProfile
import com.veltrix.admin.data.api.VaultTokenSource
import com.veltrix.admin.data.security.TokenVault
import com.veltrix.admin.data.settings.AdminSettings
import java.time.Instant
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

sealed interface LoginOutcome {
    data object MfaRequired : LoginOutcome
    data class SignedIn(val profile: UserProfile) : LoginOutcome
    /** Credentials were valid but the account has no platform permission; the new session was revoked immediately. */
    data object NotPlatformAdmin : LoginOutcome
}

@Singleton
class SessionRepository @Inject constructor(
    private val api: ApiClient,
    private val vault: TokenVault,
    private val tokens: VaultTokenSource,
    private val settings: AdminSettings,
) {
    private val _profile = MutableStateFlow(loadProfile())
    val profile: StateFlow<UserProfile?> = _profile.asStateFlow()

    private fun loadProfile(): UserProfile? =
        vault.get(TokenVault.PROFILE)?.let { runCatching { api.json.decodeFromString(UserProfile.serializer(), it) }.getOrNull() }

    val isSignedIn: Boolean get() = vault.hasUsableSession(Instant.now())

    /** Password + TOTP sign-in against /api/v1/auth/login. MFA is a second call carrying mfaCode. */
    suspend fun login(email: String, password: String, mfaCode: String?): LoginOutcome {
        val r: LoginResponse = api.post(ApiRoutes.AUTH_LOGIN, LoginRequest(email.trim(), password, mfaCode?.ifBlank { null }), auth = false)
        if (r.mfaRequired || r.accessToken == null || r.refreshToken == null || r.user == null) return LoginOutcome.MfaRequired
        val user = r.user
        if (!user.hasPlatformAccess) {
            runCatching { api.postNoContent(ApiRoutes.AUTH_LOGOUT, LogoutRequest(r.refreshToken), auth = false) }
            return LoginOutcome.NotPlatformAdmin
        }
        tokens.store(TokenResponse(r.accessToken, r.accessTokenExpiresAt, r.refreshToken, r.refreshTokenExpiresAt, user))
        settings.lastEmail = email.trim()
        _profile.value = user
        return LoginOutcome.SignedIn(user)
    }

    /** Re-reads the profile (permissions may have changed server-side). */
    suspend fun refreshProfile() {
        val me: UserProfile = api.get(ApiRoutes.ME)
        vault.put(TokenVault.PROFILE, api.json.encodeToString(UserProfile.serializer(), me))
        _profile.value = me
    }

    /** Secure logout: revoke the refresh-token family on the server (best effort), then wipe all local secrets and the Keystore key. */
    suspend fun logout() {
        vault.refreshToken?.let { rt -> runCatching { api.postNoContent(ApiRoutes.AUTH_LOGOUT, LogoutRequest(rt), auth = false) } }
        wipeLocal()
    }

    /** Called when the server rejected the session: nothing left to revoke, just wipe. */
    fun wipeLocal() {
        vault.wipe()
        settings.biometricEnabled = false
        settings.biometricOffered = false
        _profile.value = null
    }
}
