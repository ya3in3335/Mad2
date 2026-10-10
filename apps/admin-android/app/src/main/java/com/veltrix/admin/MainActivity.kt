package com.veltrix.admin

import android.os.Bundle
import android.view.WindowManager
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import androidx.appcompat.app.AppCompatActivity
import androidx.appcompat.app.AppCompatDelegate
import androidx.compose.animation.Crossfade
import androidx.compose.animation.core.tween
import androidx.compose.runtime.getValue
import androidx.core.os.LocaleListCompat
import androidx.core.splashscreen.SplashScreen.Companion.installSplashScreen
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import com.veltrix.admin.data.api.SessionEvents
import com.veltrix.admin.data.repo.SessionRepository
import com.veltrix.admin.data.security.SessionLockPolicy
import com.veltrix.admin.data.settings.AdminSettings
import com.veltrix.admin.ui.auth.LockScreen
import com.veltrix.admin.ui.auth.LoginScreen
import com.veltrix.admin.ui.auth.ServerSetupScreen
import com.veltrix.admin.ui.nav.AdminShell
import com.veltrix.admin.ui.theme.AdminTheme
import dagger.hilt.android.AndroidEntryPoint
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

enum class Stage { ServerSetup, Login, Locked, Main }

/** Top-level gate: server configured → signed in → unlocked. */
@HiltViewModel
class AppViewModel @Inject constructor(
    val settings: AdminSettings,
    private val session: SessionRepository,
    private val events: SessionEvents,
    val lockPolicy: SessionLockPolicy,
) : ViewModel() {
    private val _stage = MutableStateFlow(initialStage())
    val stage = _stage.asStateFlow()
    /** Set when the user was sent back to sign-in because the server ended the session. */
    val sessionExpired = MutableStateFlow(false)

    private fun initialStage(): Stage = when {
        settings.serverUrl.isBlank() -> Stage.ServerSetup
        !session.isSignedIn -> { if (session.profile.value != null) session.wipeLocal(); Stage.Login }
        lockPolicy.lockOnColdStart(settings.biometricEnabled, true) -> Stage.Locked
        else -> Stage.Main
    }

    init {
        viewModelScope.launch {
            events.expired.collect {
                session.wipeLocal()
                if (_stage.value != Stage.ServerSetup) { sessionExpired.value = true; _stage.value = Stage.Login }
            }
        }
        viewModelScope.launch { session.profile.collect { if (it == null && _stage.value in setOf(Stage.Main, Stage.Locked)) _stage.value = Stage.Login } }
    }

    fun serverConfigured() { _stage.value = if (session.isSignedIn) Stage.Main else Stage.Login }
    fun changeServer() { _stage.value = Stage.ServerSetup }
    fun signedIn() { sessionExpired.value = false; _stage.value = Stage.Main }
    fun unlocked() { _stage.value = Stage.Main }
    fun lock() { if (_stage.value == Stage.Main) _stage.value = Stage.Locked }
    fun signedOut() { _stage.value = Stage.Login }

    fun onForeground() {
        if (lockPolicy.onForeground(settings.biometricEnabled, session.isSignedIn)) lock()
    }
}

@AndroidEntryPoint
class MainActivity : AppCompatActivity() {
    private val vm: AppViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        installSplashScreen()
        super.onCreate(savedInstanceState)
        // Admin data must never appear in screenshots, screen recordings or the recent-apps thumbnail.
        window.setFlags(WindowManager.LayoutParams.FLAG_SECURE, WindowManager.LayoutParams.FLAG_SECURE)
        enableEdgeToEdge()
        if (AppCompatDelegate.getApplicationLocales().isEmpty && vm.settings.languageChosen)
            AppCompatDelegate.setApplicationLocales(LocaleListCompat.forLanguageTags(vm.settings.language))
        setContent {
            val stage by vm.stage.collectAsStateWithLifecycle()
            val expired by vm.sessionExpired.collectAsStateWithLifecycle()
            AdminTheme {
                Crossfade(stage, animationSpec = tween(250), label = "stage") { s ->
                    when (s) {
                        Stage.ServerSetup -> ServerSetupScreen(onDone = vm::serverConfigured)
                        Stage.Login -> LoginScreen(sessionExpired = expired, onSignedIn = vm::signedIn, onChangeServer = vm::changeServer)
                        Stage.Locked -> LockScreen(lockPolicy = vm.lockPolicy, onUnlocked = vm::unlocked, onSignedOut = vm::signedOut)
                        Stage.Main -> AdminShell(onSignedOut = vm::signedOut, onChangeServer = vm::changeServer)
                    }
                }
            }
        }
    }

    override fun onStart() {
        super.onStart()
        vm.onForeground()
    }

    override fun onStop() {
        super.onStop()
        if (!isChangingConfigurations) vm.lockPolicy.onBackground()
    }
}
