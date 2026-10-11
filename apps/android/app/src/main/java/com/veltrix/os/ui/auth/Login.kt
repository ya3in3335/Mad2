package com.veltrix.os.ui.auth

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.veltrix.os.R
import com.veltrix.os.data.api.ApiException
import com.veltrix.os.data.api.AppSettings
import com.veltrix.os.data.api.OfflineException
import com.veltrix.os.data.repo.SessionRepository
import com.veltrix.os.ui.onboarding.ErrorText
import com.veltrix.os.ui.splash.BrandEmblem
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import com.veltrix.os.data.standalone.BackupException
import com.veltrix.os.data.standalone.LocalAuth
import com.veltrix.os.data.standalone.LocalAuthException
import com.veltrix.os.data.standalone.LocalSignInStatus
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** Maps local-auth / backup error keys to string resources. */
fun errorRes(key: String): Int = when (key) {
    "pin_short" -> R.string.sa_err_pinShort
    "password_short" -> R.string.sa_err_passwordShort
    "secret_required" -> R.string.sa_err_secretRequired
    "name_invalid" -> R.string.sa_err_name
    "name_taken" -> R.string.sa_err_nameTaken
    "mismatch" -> R.string.sa_err_mismatch
    "last_owner" -> R.string.sa_err_lastOwner
    "owner_only" -> R.string.sa_err_ownerOnly
    "license_required" -> R.string.sa_err_licenseRequired
    "backup_password" -> R.string.sa_err_backupPassword
    "backup_file" -> R.string.sa_err_backupFile
    else -> R.string.sa_err_invalid
}

@HiltViewModel
class LoginViewModel @Inject constructor(private val session: SessionRepository, val settings: AppSettings,
    private val license: com.veltrix.os.data.repo.LicenseRepository, private val local: com.veltrix.os.data.repo.StandaloneRepository) : ViewModel() {
    /** Offline license: local accounts only. */
    val standalone: Boolean get() = license.standalone
    val needsOwnerSetup = MutableStateFlow(license.standalone && !local.auth.hasOwner)
    /** Error/info as string resource id (+ optional number argument). */
    val localError = MutableStateFlow<Pair<Int, Long?>?>(null)
    val restored = MutableStateFlow(false)

    fun localSignIn(name: String, secret: String, confirm: String, onOk: () -> Unit) = viewModelScope.launch {
        busy.value = true; localError.value = null
        try {
            val user = withContext(Dispatchers.Default) {
                if (needsOwnerSetup.value) {
                    LocalAuth.validateName(name)?.let { throw LocalAuthException(it) }
                    LocalAuth.validateSecret(secret)?.let { throw LocalAuthException(it) }
                    if (secret != confirm) throw LocalAuthException("mismatch")
                    local.auth.createOwner(name, secret)
                } else {
                    val r = local.auth.signIn(name, secret)
                    when (r.status) {
                        LocalSignInStatus.SignedIn -> r.user!!
                        LocalSignInStatus.LockedOut -> { localError.value = R.string.sa_lockedFor to ((r.retryAfterMs ?: 30_000L) + 999) / 1000; null }
                        LocalSignInStatus.NoUsers -> { needsOwnerSetup.value = true; null }
                        LocalSignInStatus.Invalid -> throw LocalAuthException("invalid")
                    }
                }
            }
            if (user != null) { needsOwnerSetup.value = false; local.ensureRegister(); session.signInLocal(user); onOk() }
        } catch (e: LocalAuthException) {
            localError.value = errorRes(e.key) to null
        }
        busy.value = false
    }

    fun restore(bytes: ByteArray, password: String) = viewModelScope.launch {
        busy.value = true; localError.value = null
        try {
            local.restoreBackup(bytes, password, license.standalone)
            needsOwnerSetup.value = !local.auth.hasOwner
            restored.value = true
        } catch (e: BackupException) { localError.value = errorRes(e.key) to null } catch (e: LocalAuthException) { localError.value = errorRes(e.key) to null }
        busy.value = false
    }

    val busy = MutableStateFlow(false)
    val error = MutableStateFlow<String?>(null)
    val mfaRequired = MutableStateFlow(false)
    val offlineAvailable = MutableStateFlow(false)

    fun login(email: String, password: String, mfa: String?, onOk: () -> Unit) = viewModelScope.launch {
        busy.value = true; error.value = null
        try {
            val r = session.login(email, password, mfa)
            if (r.mfaRequired) mfaRequired.value = true else onOk()
        } catch (e: OfflineException) {
            // Offline: only the last verified user on this device may unlock.
            if (session.offlineLogin(email, password)) onOk() else { error.value = "offline"; offlineAvailable.value = false }
        } catch (e: ApiException) {
            error.value = e.message
        }
        busy.value = false
    }
}

@Composable
fun LoginScreen(onSignedIn: () -> Unit, vm: LoginViewModel = hiltViewModel()) {
    if (vm.standalone) { StandaloneLogin(onSignedIn, vm); return }
    var email by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var mfa by remember { mutableStateOf("") }
    val busy by vm.busy.collectAsState()
    val error by vm.error.collectAsState()
    val mfaRequired by vm.mfaRequired.collectAsState()
    Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        Column(Modifier.fillMaxSize().systemBarsPadding().imePadding().padding(24.dp), horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center) {
            Column(Modifier.widthIn(max = 420.dp)) {
                BrandEmblem(Modifier.size(84.dp).align(Alignment.CenterHorizontally))
                Spacer(Modifier.height(16.dp))
                Text(stringResource(R.string.auth_title), style = MaterialTheme.typography.headlineMedium)
                vm.settings.tenantName?.let { Text(it, color = MaterialTheme.colorScheme.onSurfaceVariant) }
                Spacer(Modifier.height(16.dp))
                OutlinedTextField(email, { email = it }, label = { Text(stringResource(R.string.auth_email)) }, singleLine = true, modifier = Modifier.fillMaxWidth(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Email))
                Spacer(Modifier.height(8.dp))
                OutlinedTextField(password, { password = it }, label = { Text(stringResource(R.string.auth_password)) }, singleLine = true,
                    visualTransformation = PasswordVisualTransformation(), modifier = Modifier.fillMaxWidth())
                if (mfaRequired) {
                    Spacer(Modifier.height(8.dp))
                    OutlinedTextField(mfa, { mfa = it.filter(Char::isDigit).take(6) }, label = { Text(stringResource(R.string.auth_mfaCode)) }, singleLine = true,
                        modifier = Modifier.fillMaxWidth(), keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword))
                }
                ErrorText(error)
                Spacer(Modifier.height(16.dp))
                Button(onClick = { vm.login(email, password, mfa.ifBlank { null }, onSignedIn) }, enabled = !busy && email.isNotBlank() && password.isNotBlank(),
                    modifier = Modifier.fillMaxWidth().height(50.dp)) {
                    if (busy) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp) else Text(stringResource(R.string.action_signIn))
                }
                Spacer(Modifier.height(8.dp))
                Text(stringResource(R.string.auth_offlineLogin), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
    }
}

/** Standalone (offline license) sign-in; on first run it creates the owner account or restores a backup. */
@Composable
private fun StandaloneLogin(onSignedIn: () -> Unit, vm: LoginViewModel) {
    var name by remember { mutableStateOf("") }
    var secret by remember { mutableStateOf("") }
    var confirm by remember { mutableStateOf("") }
    val busy by vm.busy.collectAsState()
    val setup by vm.needsOwnerSetup.collectAsState()
    val error by vm.localError.collectAsState()
    val restored by vm.restored.collectAsState()
    val context = androidx.compose.ui.platform.LocalContext.current
    val pickBackup = androidx.activity.compose.rememberLauncherForActivityResult(androidx.activity.result.contract.ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) context.contentResolver.openInputStream(uri)?.use { it.readBytes() }?.let { vm.restore(it, secret) }
    }
    Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        Column(Modifier.fillMaxSize().systemBarsPadding().imePadding().padding(24.dp), horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center) {
            Column(Modifier.widthIn(max = 420.dp)) {
                BrandEmblem(Modifier.size(84.dp).align(Alignment.CenterHorizontally))
                Spacer(Modifier.height(16.dp))
                Text(stringResource(if (setup) R.string.sa_setupTitle else R.string.auth_title), style = MaterialTheme.typography.headlineMedium)
                vm.settings.tenantName?.let { Text(it, color = MaterialTheme.colorScheme.onSurfaceVariant) }
                if (setup) Text(stringResource(R.string.sa_setupHint), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Spacer(Modifier.height(16.dp))
                OutlinedTextField(name, { name = it }, label = { Text(stringResource(R.string.sa_userName)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
                Spacer(Modifier.height(8.dp))
                OutlinedTextField(secret, { secret = it }, label = { Text(stringResource(R.string.sa_secret)) }, singleLine = true,
                    visualTransformation = PasswordVisualTransformation(), modifier = Modifier.fillMaxWidth(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password))
                if (setup) {
                    Spacer(Modifier.height(8.dp))
                    OutlinedTextField(confirm, { confirm = it }, label = { Text(stringResource(R.string.sa_confirm)) }, singleLine = true,
                        visualTransformation = PasswordVisualTransformation(), modifier = Modifier.fillMaxWidth(),
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password))
                    Text(stringResource(R.string.sa_secretRule), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                error?.let { (res, n) ->
                    Text(if (n != null) stringResource(res, n.toString()) else stringResource(res), color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(top = 8.dp))
                }
                if (restored) Text(stringResource(R.string.sa_restored), color = MaterialTheme.colorScheme.primary, modifier = Modifier.padding(top = 8.dp))
                Spacer(Modifier.height(16.dp))
                Button(onClick = { vm.localSignIn(name, secret, confirm) { secret = ""; confirm = ""; onSignedIn() } },
                    enabled = !busy && name.isNotBlank() && secret.isNotEmpty(), modifier = Modifier.fillMaxWidth().height(50.dp)) {
                    if (busy) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
                    else Text(stringResource(if (setup) R.string.sa_createOwner else R.string.action_signIn))
                }
                if (setup) {
                    Spacer(Modifier.height(12.dp))
                    Text(stringResource(R.string.sa_restoreHint), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    TextButton(onClick = { pickBackup.launch(arrayOf("*/*")) }, enabled = !busy && secret.isNotEmpty()) { Text(stringResource(R.string.sa_restore)) }
                }
                Spacer(Modifier.height(8.dp))
                Text(stringResource(R.string.sa_localMode), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
    }
}
