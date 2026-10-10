package com.veltrix.admin.ui.settings

import androidx.appcompat.app.AppCompatDelegate
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.Logout
import androidx.compose.material.icons.outlined.Dns
import androidx.compose.material.icons.outlined.Fingerprint
import androidx.compose.material.icons.outlined.Language
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.core.os.LocaleListCompat
import androidx.fragment.app.FragmentActivity
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import com.veltrix.admin.BuildConfig
import com.veltrix.admin.R
import com.veltrix.admin.data.repo.SessionRepository
import com.veltrix.admin.data.security.SessionLockPolicy
import com.veltrix.admin.data.settings.AdminSettings
import com.veltrix.admin.ui.auth.Biometrics
import com.veltrix.admin.ui.common.InfoRow
import com.veltrix.admin.ui.common.SectionHeader
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch

val LANGUAGES = listOf("ar" to "العربية", "fr" to "Français", "en" to "English")

@HiltViewModel
class SettingsViewModel @Inject constructor(
    val settings: AdminSettings,
    private val session: SessionRepository,
    val lockPolicy: SessionLockPolicy,
) : ViewModel() {
    val profile = session.profile
    val biometric = MutableStateFlow(settings.biometricEnabled)
    val busy = MutableStateFlow(false)

    fun setBiometric(enabled: Boolean) { settings.biometricEnabled = enabled; settings.biometricOffered = true; biometric.value = enabled }

    fun logout(onDone: () -> Unit) = viewModelScope.launch {
        busy.value = true
        session.logout()
        busy.value = false
        onDone()
    }
}

/** Language choice shared by the first-launch screen and Settings (per-app locale via AppCompat). */
@Composable
fun LanguagePicker(onDismiss: () -> Unit, vm: SettingsViewModel = hiltViewModel()) {
    AlertDialog(onDismissRequest = onDismiss, confirmButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.action_close)) } },
        title = { Text(stringResource(R.string.settings_language)) },
        text = {
            Column {
                LANGUAGES.forEach { (tag, name) ->
                    Row(Modifier.fillMaxWidth().clickable {
                        vm.settings.language = tag; vm.settings.languageChosen = true
                        AppCompatDelegate.setApplicationLocales(LocaleListCompat.forLanguageTags(tag))
                        onDismiss()
                    }.padding(vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                        RadioButton(selected = vm.settings.language == tag && vm.settings.languageChosen, onClick = null)
                        Spacer(Modifier.width(8.dp)); Text(name)
                    }
                }
            }
        })
}

@Composable
fun SettingsScreen(onSignedOut: () -> Unit, onChangeServer: () -> Unit, vm: SettingsViewModel = hiltViewModel()) {
    val ctx = LocalContext.current
    val activity = ctx as? FragmentActivity
    val profile by vm.profile.collectAsStateWithLifecycle()
    val biometric by vm.biometric.collectAsStateWithLifecycle()
    val busy by vm.busy.collectAsStateWithLifecycle()
    val available = remember { Biometrics.available(ctx) }
    var showLanguage by remember { mutableStateOf(false) }
    var confirmLogout by remember { mutableStateOf(false) }
    var confirmServer by remember { mutableStateOf(false) }
    val promptTitle = stringResource(R.string.bio_prompt_title)

    LazyColumn(Modifier.fillMaxSize()) {
        item { SectionHeader(stringResource(R.string.settings_account)) }
        item {
            InfoRow(stringResource(R.string.field_name), profile?.fullName)
            InfoRow(stringResource(R.string.field_email), profile?.email)
            InfoRow(stringResource(R.string.settings_roles), profile?.roles?.joinToString(", "))
            InfoRow(stringResource(R.string.settings_mfa), stringResource(if (profile?.mfaEnabled == true) R.string.yes else R.string.no))
            if (profile?.mfaEnabled != true) Text(stringResource(R.string.settings_mfa_warning), Modifier.padding(horizontal = 16.dp),
                color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
        }
        item { SectionHeader(stringResource(R.string.settings_security)) }
        item {
            ListItem(
                headlineContent = { Text(stringResource(R.string.settings_biometric)) },
                supportingContent = { Text(stringResource(if (available) R.string.settings_biometric_hint else R.string.settings_biometric_unavailable)) },
                leadingContent = { Icon(Icons.Outlined.Fingerprint, null) },
                trailingContent = {
                    Switch(checked = biometric, enabled = available && activity != null, onCheckedChange = { want ->
                        // Changing the gate itself requires passing it.
                        activity?.let { a ->
                            vm.lockPolicy.authenticating = true
                            Biometrics.prompt(a, promptTitle, null,
                                onSuccess = { vm.lockPolicy.authenticating = false; vm.setBiometric(want) },
                                onFailure = { _, _ -> vm.lockPolicy.authenticating = false })
                        }
                    })
                },
            )
        }
        item { SectionHeader(stringResource(R.string.settings_general)) }
        item {
            ListItem(headlineContent = { Text(stringResource(R.string.settings_language)) },
                supportingContent = { Text(LANGUAGES.firstOrNull { it.first == vm.settings.language }?.second ?: vm.settings.language) },
                leadingContent = { Icon(Icons.Outlined.Language, null) }, modifier = Modifier.clickable { showLanguage = true })
            ListItem(headlineContent = { Text(stringResource(R.string.settings_server)) }, supportingContent = { Text(vm.settings.serverUrl) },
                leadingContent = { Icon(Icons.Outlined.Dns, null) }, modifier = Modifier.clickable { confirmServer = true })
        }
        item {
            Button(onClick = { confirmLogout = true }, enabled = !busy, modifier = Modifier.fillMaxWidth().padding(16.dp),
                colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error)) {
                Icon(Icons.AutoMirrored.Outlined.Logout, null); Spacer(Modifier.width(8.dp)); Text(stringResource(R.string.action_sign_out))
            }
        }
        item {
            val label = stringResource(R.string.build_label)
            Text(stringResource(R.string.settings_version, BuildConfig.VERSION_NAME) + if (label.isNotBlank()) " · $label" else "",
                Modifier.padding(16.dp), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }

    if (showLanguage) LanguagePicker(onDismiss = { showLanguage = false }, vm = vm)
    if (confirmLogout) AlertDialog(onDismissRequest = { confirmLogout = false },
        title = { Text(stringResource(R.string.logout_title)) }, text = { Text(stringResource(R.string.logout_body)) },
        confirmButton = { Button(onClick = { confirmLogout = false; vm.logout(onSignedOut) }) { Text(stringResource(R.string.action_sign_out)) } },
        dismissButton = { TextButton(onClick = { confirmLogout = false }) { Text(stringResource(R.string.action_cancel)) } })
    if (confirmServer) AlertDialog(onDismissRequest = { confirmServer = false },
        title = { Text(stringResource(R.string.server_change)) }, text = { Text(stringResource(R.string.server_change_body)) },
        confirmButton = { Button(onClick = { confirmServer = false; vm.logout(onChangeServer) }) { Text(stringResource(R.string.action_continue)) } },
        dismissButton = { TextButton(onClick = { confirmServer = false }) { Text(stringResource(R.string.action_cancel)) } })
}
