package com.veltrix.os.ui.onboarding

import androidx.appcompat.app.AppCompatDelegate
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.core.os.LocaleListCompat
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import com.veltrix.os.R
import com.veltrix.os.data.api.AppSettings
import com.veltrix.os.ui.splash.BrandEmblem
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * First run: language → privacy. The device binding already happened automatically at startup (no activation code, no device
 * code); the next screen creates the local owner account.
 */
@HiltViewModel
class OnboardingViewModel @Inject constructor(val settings: AppSettings) : ViewModel() {
    private val _step = MutableStateFlow(if (!settings.languageChosen) 0 else 1)
    val step = _step.asStateFlow()

    fun chooseLanguage(lang: String) {
        settings.language = lang
        settings.languageChosen = true
        AppCompatDelegate.setApplicationLocales(LocaleListCompat.forLanguageTags(lang))
        _step.value = 1
    }

    fun acceptPrivacy() { settings.privacyAccepted = true }
}

@Composable
fun OnboardingFlow(onDone: () -> Unit, vm: OnboardingViewModel = hiltViewModel()) {
    val step by vm.step.collectAsState()
    Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        Column(Modifier.fillMaxSize().systemBarsPadding().imePadding().verticalScroll(rememberScrollState()).padding(24.dp),
            horizontalAlignment = Alignment.CenterHorizontally) {
            BrandEmblem(Modifier.size(72.dp))
            Spacer(Modifier.height(8.dp))
            LinearProgressIndicator(progress = { (step + 1) / 2f }, Modifier.fillMaxWidth().height(3.dp))
            Spacer(Modifier.height(24.dp))
            AnimatedContent(step, transitionSpec = { fadeIn(tween(180)) togetherWith fadeOut(tween(120)) }, label = "step") { s ->
                Column(Modifier.widthIn(max = 520.dp).fillMaxWidth()) {
                    when (s) {
                        0 -> LanguageStep(vm::chooseLanguage)
                        else -> PrivacyStep { vm.acceptPrivacy(); onDone() }
                    }
                }
            }
        }
    }
}

@Composable private fun LanguageStep(onPick: (String) -> Unit) {
    Text(stringResource(R.string.onb_welcome), style = MaterialTheme.typography.headlineMedium)
    Spacer(Modifier.height(8.dp))
    Text(stringResource(R.string.onb_language), color = MaterialTheme.colorScheme.onSurfaceVariant)
    Spacer(Modifier.height(16.dp))
    listOf("ar" to "العربية", "fr" to "Français", "en" to "English").forEach { (code, label) ->
        OutlinedButton(onClick = { onPick(code) }, Modifier.fillMaxWidth().padding(vertical = 4.dp).height(52.dp)) { Text(label) }
    }
}

@Composable private fun PrivacyStep(onAccept: () -> Unit) {
    var checked by remember { mutableStateOf(false) }
    Text(stringResource(R.string.onb_privacyTitle), style = MaterialTheme.typography.headlineMedium)
    Spacer(Modifier.height(12.dp))
    Text(stringResource(R.string.onb_privacyBody), style = MaterialTheme.typography.bodyMedium)
    Spacer(Modifier.height(12.dp))
    Row(verticalAlignment = Alignment.CenterVertically) {
        Checkbox(checked, { checked = it })
        Text(stringResource(R.string.onb_accept))
    }
    Button(onClick = onAccept, enabled = checked, modifier = Modifier.fillMaxWidth().height(48.dp)) { Text(stringResource(R.string.action_next)) }
}

@Composable fun ErrorText(error: String?) {
    if (error != null) Text(if (error == "offline") stringResource(R.string.err_network) else error, color = MaterialTheme.colorScheme.error,
        modifier = Modifier.padding(top = 8.dp))
}
