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
import com.veltrix.admin.data.security.SessionLockPolicy
import com.veltrix.admin.data.settings.AdminSettings
import com.veltrix.admin.ui.AppShell
import com.veltrix.admin.ui.auth.LockScreen
import com.veltrix.admin.ui.theme.AdminTheme
import dagger.hilt.android.AndroidEntryPoint
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow

enum class Stage { Locked, Main }

/**
 * Standalone, offline owner tool: no server, no account. The only gate is the device owner's biometric / screen lock,
 * required on every cold start and after the app spent [SessionLockPolicy.DEFAULT_GRACE_MS] in background.
 */
@HiltViewModel
class AppViewModel @Inject constructor(val settings: AdminSettings, val lockPolicy: SessionLockPolicy) : ViewModel() {
    private val _stage = MutableStateFlow(if (lockPolicy.lockOnColdStart(biometricEnabled = true, signedIn = true)) Stage.Locked else Stage.Main)
    val stage = _stage.asStateFlow()

    fun unlocked() { _stage.value = Stage.Main }
    fun onForeground() {
        if (lockPolicy.onForeground(biometricEnabled = true, signedIn = true)) _stage.value = Stage.Locked
    }
}

@AndroidEntryPoint
class MainActivity : AppCompatActivity() {
    private val vm: AppViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        installSplashScreen()
        super.onCreate(savedInstanceState)
        // Activation codes, device codes and the key screen must never appear in screenshots, recordings or recents.
        window.setFlags(WindowManager.LayoutParams.FLAG_SECURE, WindowManager.LayoutParams.FLAG_SECURE)
        enableEdgeToEdge()
        if (AppCompatDelegate.getApplicationLocales().isEmpty && vm.settings.languageChosen)
            AppCompatDelegate.setApplicationLocales(LocaleListCompat.forLanguageTags(vm.settings.language))
        setContent {
            val stage by vm.stage.collectAsStateWithLifecycle()
            AdminTheme {
                Crossfade(stage, animationSpec = tween(250), label = "stage") { s ->
                    when (s) {
                        Stage.Locked -> LockScreen(lockPolicy = vm.lockPolicy, onUnlocked = vm::unlocked)
                        Stage.Main -> AppShell(lockPolicy = vm.lockPolicy)
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
