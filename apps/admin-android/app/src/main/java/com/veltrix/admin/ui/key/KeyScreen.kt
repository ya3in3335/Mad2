package com.veltrix.admin.ui.key

import androidx.activity.compose.LocalActivity
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.ContentCopy
import androidx.compose.material.icons.outlined.DeleteForever
import androidx.compose.material.icons.outlined.Key
import androidx.compose.material.icons.outlined.Share
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.fragment.app.FragmentActivity
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.veltrix.admin.R
import com.veltrix.admin.data.license.KeyProtection
import com.veltrix.admin.data.license.VendorKeyStore
import com.veltrix.admin.data.security.SessionLockPolicy
import com.veltrix.admin.ui.IssuerViewModel
import com.veltrix.admin.ui.auth.Biometrics
import com.veltrix.admin.ui.common.CodeBox
import com.veltrix.admin.ui.common.SectionHeader
import com.veltrix.admin.ui.common.WarningCard
import com.veltrix.admin.ui.common.copyText
import com.veltrix.admin.ui.common.shareText
import com.veltrix.admin.ui.confirmWithUser
import com.veltrix.admin.ui.theme.DarkTokens

/** Typed confirmation for deleting the key (second of two confirmations). */
const val DELETE_CONFIRM_WORD = "DELETE"

/** First run: explain, then create the Keystore key. */
@Composable
fun KeySetupScreen(vm: IssuerViewModel) {
    val busy by vm.busy.collectAsStateWithLifecycle()
    val context = LocalContext.current
    val secure = remember { Biometrics.available(context) }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp)) {
        Icon(Icons.Outlined.Key, null, Modifier.size(48.dp), tint = MaterialTheme.colorScheme.secondary)
        SectionHeader(stringResource(R.string.key_setup_title))
        Text(stringResource(R.string.key_setup_body), style = MaterialTheme.typography.bodyMedium)
        Spacer(Modifier.height(16.dp))
        WarningCard(stringResource(R.string.key_loss_title), stringResource(R.string.key_loss_body), color = DarkTokens.warning)
        Spacer(Modifier.height(16.dp))
        if (!secure) Text(stringResource(R.string.lock_unavailable), color = MaterialTheme.colorScheme.error)
        Button(onClick = vm::createKey, enabled = !busy && secure, modifier = Modifier.fillMaxWidth().height(52.dp)) {
            if (busy) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
            else { Icon(Icons.Outlined.Key, null); Spacer(Modifier.width(8.dp)); Text(stringResource(R.string.action_create_key)) }
        }
    }
}

@Composable
fun KeyScreen(vm: IssuerViewModel, lockPolicy: SessionLockPolicy) {
    val key by vm.key.collectAsStateWithLifecycle()
    val context = LocalContext.current
    val activity = LocalActivity.current as? FragmentActivity
    var acknowledged by remember { mutableStateOf(vm.settings.keyLossAcknowledged) }
    var confirmStep by remember { mutableIntStateOf(0) }
    var typed by remember { mutableStateOf("") }
    val copied = stringResource(R.string.copied)
    val shareSubject = stringResource(R.string.key_share_subject)
    val deleteTitle = stringResource(R.string.key_delete_title)
    var status by remember { mutableStateOf<String?>(null) }

    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 16.dp)) {
        if (key.invalidated) {
            Spacer(Modifier.height(8.dp))
            WarningCard(stringResource(R.string.key_invalidated_title), stringResource(R.string.key_invalidated))
        }
        val spki = key.spki
        if (spki != null) {
            val b64 = VendorKeyStore.spkiBase64(spki)
            val pem = VendorKeyStore.spkiPem(spki)
            Text(stringResource(R.string.key_public_explain), style = MaterialTheme.typography.bodyMedium, modifier = Modifier.padding(top = 8.dp))
            key.protection?.let {
                Text(stringResource(R.string.key_protection, stringResource(when (it) {
                    KeyProtection.StrongBox -> R.string.key_strongbox
                    KeyProtection.SecureHardware -> R.string.key_tee
                    KeyProtection.Software -> R.string.key_software
                })), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.secondary, modifier = Modifier.padding(top = 8.dp))
            }
            SectionHeader(stringResource(R.string.key_spki))
            CodeBox(b64)
            KeyButtons(onCopy = { copyText(context, "VELTRIX vendor public key", b64); status = copied },
                onShare = { shareText(context, shareSubject, b64) })
            SectionHeader(stringResource(R.string.key_pem))
            CodeBox(pem.trimEnd())
            KeyButtons(onCopy = { copyText(context, "vendor-public-key.pem", pem); status = copied },
                onShare = { shareText(context, shareSubject, pem) })
            status?.let { Text(it, color = MaterialTheme.colorScheme.secondary, style = MaterialTheme.typography.bodySmall) }
        }
        Spacer(Modifier.height(16.dp))
        WarningCard(stringResource(R.string.key_loss_title), stringResource(R.string.key_loss_body), color = DarkTokens.warning)
        Row(Modifier.fillMaxWidth().padding(vertical = 8.dp)) {
            Checkbox(acknowledged, { acknowledged = it; vm.settings.keyLossAcknowledged = it })
            Text(stringResource(R.string.key_loss_ack), style = MaterialTheme.typography.bodyMedium, modifier = Modifier.padding(top = 12.dp))
        }
        Spacer(Modifier.height(16.dp))
        OutlinedButton(onClick = { confirmStep = 1 }, modifier = Modifier.fillMaxWidth(),
            colors = ButtonDefaults.outlinedButtonColors(contentColor = MaterialTheme.colorScheme.error)) {
            Icon(Icons.Outlined.DeleteForever, null); Spacer(Modifier.width(8.dp)); Text(stringResource(R.string.action_delete_key))
        }
        Spacer(Modifier.height(24.dp))
    }

    if (confirmStep == 1) AlertDialog(onDismissRequest = { confirmStep = 0 },
        title = { Text(deleteTitle) }, text = { Text(stringResource(R.string.key_delete_body1)) },
        confirmButton = { TextButton(onClick = { typed = ""; confirmStep = 2 }) { Text(stringResource(R.string.action_continue), color = MaterialTheme.colorScheme.error) } },
        dismissButton = { TextButton(onClick = { confirmStep = 0 }) { Text(stringResource(R.string.action_cancel)) } })
    if (confirmStep == 2) AlertDialog(onDismissRequest = { confirmStep = 0 },
        title = { Text(deleteTitle) },
        text = {
            Column {
                Text(stringResource(R.string.key_delete_body2, DELETE_CONFIRM_WORD))
                OutlinedTextField(typed, { typed = it }, Modifier.fillMaxWidth().padding(top = 8.dp), singleLine = true)
            }
        },
        confirmButton = {
            Button(enabled = typed.trim() == DELETE_CONFIRM_WORD, colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error),
                onClick = { confirmStep = 0; confirmWithUser(activity, lockPolicy, deleteTitle, onOk = vm::deleteKey) }) {
                Text(stringResource(R.string.action_delete_key))
            }
        },
        dismissButton = { TextButton(onClick = { confirmStep = 0 }) { Text(stringResource(R.string.action_cancel)) } })
}

@Composable
private fun KeyButtons(onCopy: () -> Unit, onShare: () -> Unit) {
    Row(Modifier.padding(top = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        OutlinedButton(onClick = onCopy) { Icon(Icons.Outlined.ContentCopy, null, Modifier.size(18.dp)); Spacer(Modifier.width(4.dp)); Text(stringResource(R.string.action_copy)) }
        OutlinedButton(onClick = onShare) { Icon(Icons.Outlined.Share, null, Modifier.size(18.dp)); Spacer(Modifier.width(4.dp)); Text(stringResource(R.string.action_share)) }
    }
}
