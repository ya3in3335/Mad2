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
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch

@HiltViewModel
class LoginViewModel @Inject constructor(private val session: SessionRepository, val settings: AppSettings) : ViewModel() {
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
