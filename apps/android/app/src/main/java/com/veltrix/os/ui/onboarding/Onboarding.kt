package com.veltrix.os.ui.onboarding

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatDelegate
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.core.os.LocaleListCompat
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.veltrix.os.R
import com.veltrix.os.data.api.ApiException
import com.veltrix.os.data.api.AppSettings
import com.veltrix.os.data.api.CompleteOnboardingRequest
import com.veltrix.os.data.api.OfflineException
import com.veltrix.os.data.repo.LicenseRepository
import com.veltrix.os.data.repo.RemoteRepository
import com.veltrix.os.data.security.LicenseState
import com.veltrix.os.ui.splash.BrandEmblem
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

@HiltViewModel
class OnboardingViewModel @Inject constructor(val settings: AppSettings, private val license: LicenseRepository, private val remote: RemoteRepository) : ViewModel() {
    // Resume from persisted progress if setup was interrupted.
    private val _step = MutableStateFlow(
        when {
            !settings.languageChosen -> 0
            !settings.privacyAccepted -> 1
            license.evaluate() !is LicenseState.Valid && license.evaluate() !is LicenseState.Expired -> 2
            !settings.setupCompleted -> 3
            else -> 4
        }
    )
    val step = _step.asStateFlow()
    val busy = MutableStateFlow(false)
    val error = MutableStateFlow<String?>(null)

    fun chooseLanguage(lang: String) {
        settings.language = lang
        settings.languageChosen = true
        AppCompatDelegate.setApplicationLocales(LocaleListCompat.forLanguageTags(lang))
        _step.value = 1
    }

    fun acceptPrivacy() { settings.privacyAccepted = true; _step.value = 2 }

    fun activate(server: String, key: String) = viewModelScope.launch {
        if (server.isNotBlank()) settings.serverUrl = server.trim()
        busy.value = true; error.value = null
        try {
            val res = license.activate(key)
            _step.value = if (res.setupCompleted) 4 else 3
        } catch (e: ApiException) { error.value = e.message } catch (e: OfflineException) { error.value = "offline" }
        busy.value = false
    }

    // ---- Offline activation (no server): device code → owner issues an activation code → paste or open it here ----
    val offlineConfigured: Boolean get() = license.offlineActivationConfigured
    val deviceCode: String by lazy { license.deviceCode }
    /** String resource of the last offline activation error, or null. */
    val offlineError = MutableStateFlow<Int?>(null)

    fun activateOffline(text: String) {
        val res = license.activateOffline(text)
        offlineError.value = when (res) {
            LicenseRepository.OfflineResult.Activated -> null
            LicenseRepository.OfflineResult.NotConfigured -> R.string.offline_notConfigured
            LicenseRepository.OfflineResult.WrongDevice -> R.string.offline_wrongDevice
            LicenseRepository.OfflineResult.Expired -> R.string.offline_expired
            LicenseRepository.OfflineResult.Invalid -> R.string.offline_invalid
        }
        if (res == LicenseRepository.OfflineResult.Activated) _step.value = 4
    }

    fun completeSetup(req: CompleteOnboardingRequest) = viewModelScope.launch {
        busy.value = true; error.value = null
        try {
            val r = remote.completeOnboarding(req.copy(setupToken = settings.setupToken ?: ""))
            settings.setupCompleted = true
            settings.setupToken = null
            settings.branchId = r.branchId
            settings.warehouseId = r.warehouseId
            _step.value = 4
        } catch (e: ApiException) { error.value = e.message } catch (e: OfflineException) { error.value = "offline" }
        busy.value = false
    }
}

@Composable
fun OnboardingFlow(onDone: () -> Unit, vm: OnboardingViewModel = hiltViewModel()) {
    val step by vm.step.collectAsState()
    val busy by vm.busy.collectAsState()
    val error by vm.error.collectAsState()
    Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        Column(Modifier.fillMaxSize().systemBarsPadding().imePadding().verticalScroll(rememberScrollState()).padding(24.dp),
            horizontalAlignment = Alignment.CenterHorizontally) {
            BrandEmblem(Modifier.size(72.dp))
            Spacer(Modifier.height(8.dp))
            LinearProgressIndicator(progress = { (step + 1) / 5f }, Modifier.fillMaxWidth().height(3.dp))
            Spacer(Modifier.height(24.dp))
            AnimatedContent(step, transitionSpec = { fadeIn(tween(180)) togetherWith fadeOut(tween(120)) }, label = "step") { s ->
                Column(Modifier.widthIn(max = 520.dp).fillMaxWidth()) {
                    when (s) {
                        0 -> LanguageStep(vm::chooseLanguage)
                        1 -> PrivacyStep(vm::acceptPrivacy)
                        2 -> {
                            ActivationStep(vm.settings.serverUrl, busy, error, vm::activate)
                            OfflineActivationSection(vm)
                        }
                        3 -> CompanyStep(busy, error, vm.settings.language, vm::completeSetup)
                        else -> DoneStep(onDone)
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

@Composable private fun ActivationStep(initialServer: String, busy: Boolean, error: String?, onActivate: (String, String) -> Unit) {
    var server by remember { mutableStateOf(initialServer) }
    var key by remember { mutableStateOf("") }
    Text(stringResource(R.string.onb_activate), style = MaterialTheme.typography.headlineMedium)
    Spacer(Modifier.height(12.dp))
    OutlinedTextField(server, { server = it }, label = { Text(stringResource(R.string.onb_server)) }, singleLine = true, modifier = Modifier.fillMaxWidth(),
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri))
    Spacer(Modifier.height(8.dp))
    OutlinedTextField(key, { key = it.uppercase() }, label = { Text(stringResource(R.string.onb_licenseKey)) }, singleLine = true, modifier = Modifier.fillMaxWidth(),
        placeholder = { Text("VLX-XXXXX-XXXXX-XXXXX-XXXXX") })
    ErrorText(error)
    Spacer(Modifier.height(12.dp))
    Button(onClick = { onActivate(server, key) }, enabled = !busy && key.length >= 20, modifier = Modifier.fillMaxWidth().height(48.dp)) {
        if (busy) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp) else Text(stringResource(R.string.onb_activate))
    }
    Spacer(Modifier.height(12.dp))
    Text(stringResource(R.string.lic_recovery), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
}

@Composable private fun CompanyStep(busy: Boolean, error: String?, lang: String, onSubmit: (CompleteOnboardingRequest) -> Unit) {
    var name by remember { mutableStateOf("") }
    var nif by remember { mutableStateOf("") }
    var rc by remember { mutableStateOf("") }
    var address by remember { mutableStateOf("") }
    var wilaya by remember { mutableStateOf("") }
    var hq by remember { mutableStateOf("") }
    var currency by remember { mutableStateOf("DZD") }
    var tax by remember { mutableStateOf(true) }
    var ownerName by remember { mutableStateOf("") }
    var email by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    Text(stringResource(R.string.onb_company), style = MaterialTheme.typography.headlineMedium)
    Field(stringResource(R.string.onb_companyName), name) { name = it }
    Field(stringResource(R.string.onb_taxId), nif) { nif = it }
    Field(stringResource(R.string.onb_rc), rc) { rc = it }
    Field(stringResource(R.string.onb_address), address) { address = it }
    Field(stringResource(R.string.onb_wilaya), wilaya) { wilaya = it }
    Field(stringResource(R.string.onb_headOffice), hq) { hq = it }
    Spacer(Modifier.height(12.dp))
    Text(stringResource(R.string.onb_currencyTax), style = MaterialTheme.typography.titleMedium)
    Field(stringResource(R.string.onb_currency), currency) { currency = it.uppercase().take(3) }
    Row(verticalAlignment = Alignment.CenterVertically) { Checkbox(tax, { tax = it }); Text(stringResource(R.string.onb_algeriaTax), style = MaterialTheme.typography.bodyMedium) }
    Spacer(Modifier.height(12.dp))
    Text(stringResource(R.string.onb_admin), style = MaterialTheme.typography.titleMedium)
    Field(stringResource(R.string.onb_fullName), ownerName) { ownerName = it }
    Field(stringResource(R.string.auth_email), email, KeyboardType.Email) { email = it }
    OutlinedTextField(password, { password = it }, label = { Text(stringResource(R.string.auth_password)) }, singleLine = true,
        visualTransformation = PasswordVisualTransformation(), modifier = Modifier.fillMaxWidth(), supportingText = { Text(stringResource(R.string.err_passwordPolicy)) })
    ErrorText(error)
    Button(onClick = {
        onSubmit(CompleteOnboardingRequest("", name, name, nif.ifBlank { null }, rc.ifBlank { null }, address.ifBlank { null }, null, wilaya.ifBlank { null }, null,
            currency, when (lang) { "ar" -> "ar-DZ"; "en" -> "en-US"; else -> "fr-DZ" }, email, ownerName, password, lang, hq.ifBlank { name }, tax))
    }, enabled = !busy && name.isNotBlank() && email.contains('@') && password.length >= 10 && ownerName.isNotBlank(), modifier = Modifier.fillMaxWidth().height(48.dp)) {
        if (busy) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp) else Text(stringResource(R.string.action_finish))
    }
}
@Composable private fun OfflineActivationSection(vm: OnboardingViewModel) {
    val context = LocalContext.current
    val offlineError by vm.offlineError.collectAsState()
    var expanded by rememberSaveable { mutableStateOf(false) }
    var code by rememberSaveable { mutableStateOf("") }
    val openFile = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) {
            val text = runCatching {
                context.contentResolver.openInputStream(uri)?.use { it.readNBytesCompat(64 * 1024).toString(Charsets.UTF_8) }
            }.getOrNull()
            if (text != null) code = text.trim() else vm.offlineError.value = R.string.offline_invalid
        }
    }
    Spacer(Modifier.height(20.dp))
    HorizontalDivider()
    TextButton(onClick = { expanded = !expanded }, modifier = Modifier.fillMaxWidth()) { Text(stringResource(R.string.offline_title)) }
    if (!expanded) return
    if (!vm.offlineConfigured) {
        Text(stringResource(R.string.offline_notConfigured), color = MaterialTheme.colorScheme.error)
        return
    }
    Text(stringResource(R.string.offline_hint), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
    Spacer(Modifier.height(8.dp))
    Text(stringResource(R.string.offline_deviceCode), style = MaterialTheme.typography.labelLarge)
    SelectionContainer {
        Text(vm.deviceCode, fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.bodyLarge, modifier = Modifier.padding(vertical = 4.dp))
    }
    val shareTitle = stringResource(R.string.offline_deviceCode)
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        OutlinedButton(onClick = {
            (context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager).setPrimaryClip(ClipData.newPlainText(shareTitle, vm.deviceCode))
        }) { Text(stringResource(R.string.offline_copy)) }
        OutlinedButton(onClick = {
            val send = Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, vm.deviceCode)
            context.startActivity(Intent.createChooser(send, shareTitle))
        }) { Text(stringResource(R.string.offline_share)) }
    }
    Spacer(Modifier.height(8.dp))
    OutlinedTextField(code, { code = it }, label = { Text(stringResource(R.string.offline_activationCode)) }, minLines = 3, maxLines = 6,
        textStyle = LocalTextStyle.current.copy(fontFamily = FontFamily.Monospace), modifier = Modifier.fillMaxWidth())
    offlineError?.let { Text(stringResource(it), color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(top = 8.dp)) }
    Spacer(Modifier.height(8.dp))
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        OutlinedButton(onClick = { openFile.launch(arrayOf("text/plain", "application/octet-stream")) }) { Text(stringResource(R.string.offline_openFile)) }
        Button(onClick = { vm.activateOffline(code) }, enabled = code.isNotBlank()) { Text(stringResource(R.string.offline_activate)) }
    }
}

/** Reads at most [limit] bytes (InputStream.readNBytes needs API 33). */
private fun java.io.InputStream.readNBytesCompat(limit: Int): ByteArray {
    val out = java.io.ByteArrayOutputStream()
    val buf = ByteArray(8192)
    while (out.size() < limit) {
        val n = read(buf, 0, minOf(buf.size, limit - out.size()))
        if (n < 0) break
        out.write(buf, 0, n)
    }
    return out.toByteArray()
}

@Composable private fun DoneStep(onDone: () -> Unit) {
    val p by animateFloatAsState(1f, tween(900), label = "emblem")
    Column(Modifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally) {
        BrandEmblem(Modifier.size(120.dp), p)
        Spacer(Modifier.height(16.dp))
        Text(stringResource(R.string.onb_activated), style = MaterialTheme.typography.headlineMedium)
        Spacer(Modifier.height(16.dp))
        Button(onClick = onDone, Modifier.fillMaxWidth().height(48.dp)) { Text(stringResource(R.string.action_signIn)) }
    }
}

@Composable private fun Field(label: String, value: String, type: KeyboardType = KeyboardType.Text, onChange: (String) -> Unit) {
    OutlinedTextField(value, onChange, label = { Text(label) }, singleLine = true, modifier = Modifier.fillMaxWidth().padding(top = 6.dp),
        keyboardOptions = KeyboardOptions(keyboardType = type))
}

@Composable fun ErrorText(error: String?) {
    if (error != null) Text(if (error == "offline") stringResource(R.string.err_network) else error, color = MaterialTheme.colorScheme.error,
        modifier = Modifier.padding(top = 8.dp))
}
