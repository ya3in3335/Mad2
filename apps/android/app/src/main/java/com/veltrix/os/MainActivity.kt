package com.veltrix.os

import android.os.Bundle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import androidx.appcompat.app.AppCompatActivity
import androidx.appcompat.app.AppCompatDelegate
import androidx.compose.animation.Crossfade
import androidx.compose.animation.core.tween
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.core.os.LocaleListCompat
import androidx.core.splashscreen.SplashScreen.Companion.installSplashScreen
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.veltrix.os.data.api.AppSettings
import com.veltrix.os.data.repo.LicenseRepository
import com.veltrix.os.data.repo.SessionRepository
import com.veltrix.os.data.security.DeviceIdentity
import com.veltrix.os.data.security.LicenseState
import com.veltrix.os.data.work.SyncWorker
import com.veltrix.os.ui.auth.LoginScreen
import com.veltrix.os.ui.main.MainScreen
import com.veltrix.os.ui.onboarding.OnboardingFlow
import com.veltrix.os.ui.splash.AnimatedSplash
import com.veltrix.os.ui.theme.VeltrixTheme
import dagger.hilt.android.AndroidEntryPoint
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

enum class Stage { Splash, Onboarding, Login, Main }

@HiltViewModel
class AppViewModel @Inject constructor(
    val settings: AppSettings, private val license: LicenseRepository, private val session: SessionRepository, private val identity: DeviceIdentity,
) : ViewModel() {
    private val _stage = MutableStateFlow(Stage.Splash)
    val stage = _stage.asStateFlow()
    private val _ready = MutableStateFlow(false)
    val ready = _ready.asStateFlow()
    private var next = Stage.Onboarding

    init {
        // Real startup work: ensure device key, evaluate the signed license offline, read session.
        viewModelScope.launch(Dispatchers.IO) {
            identity.publicKeySpki()
            val state = license.evaluate()
            next = when {
                !settings.languageChosen || !settings.privacyAccepted || state is LicenseState.Missing || state is LicenseState.WrongDevice -> Stage.Onboarding
                !settings.setupCompleted -> Stage.Onboarding
                session.isSignedIn -> Stage.Main
                else -> Stage.Login
            }
            _ready.value = true
        }
        viewModelScope.launch { session.profile.collect { if (it == null && _stage.value == Stage.Main) _stage.value = Stage.Login } }
    }

    fun splashFinished() { _stage.value = next }
    fun onboarded() { _stage.value = Stage.Login }
    fun signedIn() { _stage.value = Stage.Main }
}

@AndroidEntryPoint
class MainActivity : AppCompatActivity() {
    private val vm: AppViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        installSplashScreen()
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        if (AppCompatDelegate.getApplicationLocales().isEmpty && vm.settings.languageChosen)
            AppCompatDelegate.setApplicationLocales(LocaleListCompat.forLanguageTags(vm.settings.language))
        SyncWorker.schedule(this)
        setContent {
            val stage by vm.stage.collectAsState()
            val ready by vm.ready.collectAsState()
            VeltrixTheme(vm.settings.theme) {
                Crossfade(stage, animationSpec = tween(300), label = "stage") { s ->
                    when (s) {
                        Stage.Splash -> AnimatedSplash(ready) { vm.splashFinished() }
                        Stage.Onboarding -> OnboardingFlow(onDone = vm::onboarded)
                        Stage.Login -> LoginScreen(onSignedIn = vm::signedIn)
                        Stage.Main -> MainScreen()
                    }
                }
            }
        }
    }
}
