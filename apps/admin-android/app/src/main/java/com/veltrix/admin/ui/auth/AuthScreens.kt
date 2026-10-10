package com.veltrix.admin.ui.auth

import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Fingerprint
import androidx.compose.material.icons.outlined.Language
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.fragment.app.FragmentActivity
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import com.veltrix.admin.BuildConfig
import com.veltrix.admin.R
import com.veltrix.admin.data.api.ApiClient
import com.veltrix.admin.data.api.AppError
import com.veltrix.admin.data.api.ServerUrlValidator
import com.veltrix.admin.data.api.toAppError
import com.veltrix.admin.data.repo.LoginOutcome
import com.veltrix.admin.data.repo.SessionRepository
import com.veltrix.admin.data.security.SessionLockPolicy
import com.veltrix.admin.data.settings.AdminSettings
import com.veltrix.admin.ui.common.errorText
import com.veltrix.admin.ui.settings.LanguagePicker
import com.veltrix.admin.ui.theme.Brand
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

@Composable
private fun BrandedPage(content: @Composable ColumnScope.() -> Unit) {
    Box(Modifier.fillMaxSize().background(Brand.backdrop)) {
        Column(
            Modifier.fillMaxSize().systemBarsPadding().imePadding().verticalScroll(rememberScrollState()).padding(24.dp),
            horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center,
        ) {
            Column(Modifier.widthIn(max = 440.dp).fillMaxWidth()) {
                Image(painterResource(R.drawable.ic_veltrix_emblem), null, Modifier.size(96.dp).align(Alignment.CenterHorizontally))
                Text(stringResource(R.string.app_name), style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.Bold,
                    modifier = Modifier.align(Alignment.CenterHorizontally))
                Text(stringResource(R.string.app_tagline), color = MaterialTheme.colorScheme.secondary, style = MaterialTheme.typography.bodyMedium,
                    modifier = Modifier.align(Alignment.CenterHorizontally))
                val label = stringResource(R.string.build_label)
                if (label.isNotBlank()) Text(label, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.labelSmall,
                    modifier = Modifier.align(Alignment.CenterHorizontally).padding(top = 4.dp))
                Spacer(Modifier.height(24.dp))
                content()
            }
        }
    }
}

// ---------------------------------------------------------------- Server setup

@HiltViewModel
class ServerSetupViewModel @Inject constructor(private val settings: AdminSettings, private val api: ApiClient) : ViewModel() {
    val busy = MutableStateFlow(false)
    val error = MutableStateFlow<Int?>(null)
    val probeError = MutableStateFlow<AppError?>(null)
    val initial: String get() = settings.serverUrl

    fun submit(input: String, onDone: () -> Unit) {
        error.value = null; probeError.value = null
        when (val r = ServerUrlValidator.validate(input, BuildConfig.ALLOW_CLEARTEXT_DEV_HOSTS)) {
            is ServerUrlValidator.Result.Invalid -> error.value = when (r.error) {
                ServerUrlValidator.Error.Empty -> R.string.server_err_empty
                ServerUrlValidator.Error.Malformed -> R.string.server_err_malformed
                ServerUrlValidator.Error.HttpsRequired -> R.string.server_err_https
                ServerUrlValidator.Error.NotAllowed -> R.string.server_err_malformed
            }
            is ServerUrlValidator.Result.Valid -> viewModelScope.launch {
                busy.value = true
                runCatching { api.probe(r.url) }.fold(
                    { ok -> if (ok) { settings.serverUrl = r.url; onDone() } else error.value = R.string.server_err_unreachable },
                    { e -> probeError.value = e.toAppError() },
                )
                busy.value = false
            }
        }
    }
}

@Composable
fun ServerSetupScreen(onDone: () -> Unit, vm: ServerSetupViewModel = hiltViewModel()) {
    var url by remember { mutableStateOf(vm.initial) }
    val busy by vm.busy.collectAsStateWithLifecycle()
    val error by vm.error.collectAsStateWithLifecycle()
    val probeError by vm.probeError.collectAsStateWithLifecycle()
    var showLanguage by remember { mutableStateOf(false) }
    BrandedPage {
        Text(stringResource(R.string.server_title), style = MaterialTheme.typography.titleLarge)
        Text(stringResource(R.string.server_body), color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodyMedium)
        Spacer(Modifier.height(12.dp))
        OutlinedTextField(url, { url = it.trim() }, label = { Text(stringResource(R.string.server_url)) }, placeholder = { Text("https://api.example.com") },
            singleLine = true, modifier = Modifier.fillMaxWidth(), isError = error != null || probeError != null,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri, imeAction = ImeAction.Go))
        error?.let { Text(stringResource(it), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
        probeError?.let { Text(errorText(it), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
        if (BuildConfig.ALLOW_CLEARTEXT_DEV_HOSTS) Text(stringResource(R.string.server_debug_hint), style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.height(16.dp))
        Button(onClick = { vm.submit(url, onDone) }, enabled = !busy, modifier = Modifier.fillMaxWidth().height(50.dp)) {
            if (busy) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp) else Text(stringResource(R.string.action_continue))
        }
        TextButton(onClick = { showLanguage = true }, modifier = Modifier.align(Alignment.CenterHorizontally)) {
            Icon(Icons.Outlined.Language, null); Spacer(Modifier.width(6.dp)); Text(stringResource(R.string.settings_language))
        }
    }
    if (showLanguage) LanguagePicker(onDismiss = { showLanguage = false })
}

// ---------------------------------------------------------------- Login

@HiltViewModel
class LoginViewModel @Inject constructor(private val session: SessionRepository, val settings: AdminSettings) : ViewModel() {
    val busy = MutableStateFlow(false)
    val error = MutableStateFlow<AppError?>(null)
    val denied = MutableStateFlow(false)
    private val _mfaRequired = MutableStateFlow(false)
    val mfaRequired = _mfaRequired.asStateFlow()
    /** After a successful sign-in, offer biometric unlock once (only when the device supports it). */
    val offerBiometric = MutableStateFlow(false)

    fun login(email: String, password: String, mfa: String, biometricAvailable: Boolean, onOk: () -> Unit) = viewModelScope.launch {
        busy.value = true; error.value = null; denied.value = false
        runCatching { session.login(email, password, mfa.takeIf { _mfaRequired.value }) }.fold(
            { r ->
                when (r) {
                    LoginOutcome.MfaRequired -> _mfaRequired.value = true
                    LoginOutcome.NotPlatformAdmin -> denied.value = true
                    is LoginOutcome.SignedIn -> if (biometricAvailable && !settings.biometricOffered) offerBiometric.value = true else onOk()
                }
            },
            { e -> error.value = e.toAppError() },
        )
        busy.value = false
    }

    fun biometricDecision(enable: Boolean) {
        settings.biometricOffered = true
        settings.biometricEnabled = enable
        offerBiometric.value = false
    }

    fun resetMfa() { _mfaRequired.value = false }
}

@Composable
fun LoginScreen(sessionExpired: Boolean, onSignedIn: () -> Unit, onChangeServer: () -> Unit, vm: LoginViewModel = hiltViewModel()) {
    val context = LocalContext.current
    val activity = context as? FragmentActivity
    var email by remember { mutableStateOf(vm.settings.lastEmail.orEmpty()) }
    var password by remember { mutableStateOf("") }
    var mfa by remember { mutableStateOf("") }
    val busy by vm.busy.collectAsStateWithLifecycle()
    val error by vm.error.collectAsStateWithLifecycle()
    val denied by vm.denied.collectAsStateWithLifecycle()
    val mfaRequired by vm.mfaRequired.collectAsStateWithLifecycle()
    val offer by vm.offerBiometric.collectAsStateWithLifecycle()
    val biometricAvailable = remember { Biometrics.available(context) }

    BrandedPage {
        Text(stringResource(R.string.login_title), style = MaterialTheme.typography.titleLarge)
        Text(vm.settings.serverUrl, color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodySmall)
        if (sessionExpired) Text(stringResource(R.string.err_session_expired), color = MaterialTheme.colorScheme.tertiary, style = MaterialTheme.typography.bodyMedium,
            modifier = Modifier.padding(top = 8.dp))
        Spacer(Modifier.height(12.dp))
        OutlinedTextField(email, { email = it; vm.resetMfa() }, label = { Text(stringResource(R.string.login_email)) }, singleLine = true,
            modifier = Modifier.fillMaxWidth(), enabled = !busy, keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Email, imeAction = ImeAction.Next))
        Spacer(Modifier.height(8.dp))
        OutlinedTextField(password, { password = it }, label = { Text(stringResource(R.string.login_password)) }, singleLine = true, enabled = !busy,
            visualTransformation = PasswordVisualTransformation(), modifier = Modifier.fillMaxWidth(),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password))
        if (mfaRequired) {
            Spacer(Modifier.height(8.dp))
            Text(stringResource(R.string.login_mfa_prompt), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.secondary)
            OutlinedTextField(mfa, { mfa = it.filter(Char::isDigit).take(6) }, label = { Text(stringResource(R.string.login_mfa)) }, singleLine = true,
                enabled = !busy, modifier = Modifier.fillMaxWidth(), keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword))
        }
        error?.let { Text(errorText(it), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(top = 8.dp)) }
        if (denied) Text(stringResource(R.string.login_denied), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodyMedium,
            modifier = Modifier.padding(top = 8.dp))
        Spacer(Modifier.height(16.dp))
        Button(
            onClick = { vm.login(email, password, mfa, biometricAvailable) { password = ""; onSignedIn() } },
            enabled = !busy && email.isNotBlank() && password.isNotEmpty() && (!mfaRequired || mfa.length == 6),
            modifier = Modifier.fillMaxWidth().height(50.dp),
        ) {
            if (busy) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp) else Text(stringResource(R.string.action_sign_in))
        }
        TextButton(onClick = onChangeServer, enabled = !busy, modifier = Modifier.align(Alignment.CenterHorizontally)) { Text(stringResource(R.string.server_change)) }
        Text(stringResource(R.string.login_footer), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth())
    }

    if (offer) {
        AlertDialog(
            onDismissRequest = {},
            icon = { Icon(Icons.Outlined.Fingerprint, null) },
            title = { Text(stringResource(R.string.bio_offer_title)) },
            text = { Text(stringResource(R.string.bio_offer_body)) },
            confirmButton = {
                val title = stringResource(R.string.bio_prompt_title)
                Button(onClick = {
                    if (activity == null) { vm.biometricDecision(false); password = ""; onSignedIn() }
                    else Biometrics.prompt(activity, title, null, onSuccess = { vm.biometricDecision(true); password = ""; onSignedIn() })
                }) { Text(stringResource(R.string.action_enable)) }
            },
            dismissButton = { TextButton(onClick = { vm.biometricDecision(false); password = ""; onSignedIn() }) { Text(stringResource(R.string.action_not_now)) } },
        )
    }
}

// ---------------------------------------------------------------- Lock

@HiltViewModel
class LockViewModel @Inject constructor(private val session: SessionRepository) : ViewModel() {
    val profileName: String? get() = session.profile.value?.fullName
    fun signOut(onDone: () -> Unit) = viewModelScope.launch { session.logout(); onDone() }
}

@Composable
fun LockScreen(lockPolicy: SessionLockPolicy, onUnlocked: () -> Unit, onSignedOut: () -> Unit, vm: LockViewModel = hiltViewModel()) {
    val context = LocalContext.current
    val activity = context as? FragmentActivity
    val title = stringResource(R.string.bio_prompt_title)
    val subtitle = vm.profileName
    var failure by remember { mutableStateOf<String?>(null) }
    val unavailable = remember { !Biometrics.available(context) }

    fun prompt() {
        val a = activity ?: return
        failure = null
        lockPolicy.authenticating = true
        Biometrics.prompt(a, title, subtitle,
            onSuccess = { lockPolicy.authenticating = false; onUnlocked() },
            onFailure = { _, msg -> lockPolicy.authenticating = false; failure = msg.toString() })
    }
    LaunchedEffect(Unit) { if (!unavailable) prompt() }

    BrandedPage {
        Text(stringResource(R.string.lock_title), style = MaterialTheme.typography.titleLarge, modifier = Modifier.align(Alignment.CenterHorizontally))
        subtitle?.let { Text(it, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.align(Alignment.CenterHorizontally)) }
        Spacer(Modifier.height(16.dp))
        if (unavailable) Text(stringResource(R.string.lock_unavailable), color = MaterialTheme.colorScheme.error, textAlign = TextAlign.Center,
            modifier = Modifier.fillMaxWidth())
        failure?.let { Text(it, color = MaterialTheme.colorScheme.error, textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth()) }
        Spacer(Modifier.height(16.dp))
        if (!unavailable) Button(onClick = ::prompt, modifier = Modifier.fillMaxWidth().height(50.dp)) {
            Icon(Icons.Outlined.Fingerprint, null); Spacer(Modifier.width(8.dp)); Text(stringResource(R.string.action_unlock))
        }
        OutlinedButton(onClick = { vm.signOut(onSignedOut) }, modifier = Modifier.fillMaxWidth().padding(top = 8.dp)) { Text(stringResource(R.string.action_sign_out)) }
    }
}
