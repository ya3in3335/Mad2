package com.veltrix.admin.ui.issue

import androidx.activity.compose.LocalActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.ContentCopy
import androidx.compose.material.icons.outlined.Save
import androidx.compose.material.icons.outlined.Share
import androidx.compose.material.icons.outlined.VpnKey
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextDirection
import androidx.compose.ui.unit.dp
import androidx.fragment.app.FragmentActivity
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.veltrix.admin.R
import com.veltrix.admin.data.history.IssuedRecord
import com.veltrix.admin.data.license.DeviceCode
import com.veltrix.admin.data.license.OfflineIssuer
import com.veltrix.admin.data.security.SessionLockPolicy
import com.veltrix.admin.ui.IssueForm
import com.veltrix.admin.ui.IssuerViewModel
import com.veltrix.admin.ui.common.CodeBox
import com.veltrix.admin.ui.common.SectionHeader
import com.veltrix.admin.ui.common.copyText
import com.veltrix.admin.ui.common.formatDate
import com.veltrix.admin.ui.common.shareText
import com.veltrix.admin.ui.confirmWithUser

/** Text of the .txt / shared message: '#' comment lines (ignored by VELTRIX OS) followed by the code. */
fun activationText(r: IssuedRecord): String = OfflineIssuer.activationFileText(r.shop, r.expiresAt, r.licenseId, r.activationCode)

@Composable
fun IssueScreen(vm: IssuerViewModel, lockPolicy: SessionLockPolicy) {
    val form by vm.form.collectAsStateWithLifecycle()
    val result by vm.result.collectAsStateWithLifecycle()
    val busy by vm.busy.collectAsStateWithLifecycle()
    val activity = LocalActivity.current as? FragmentActivity
    val confirmTitle = stringResource(R.string.issue_confirm_title)
    var showErrors by remember { mutableStateOf(false) }
    val problem = form.problem()

    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).imePadding().padding(horizontal = 16.dp)) {
        result?.let { r ->
            IssuedResult(r, lockPolicy, onNew = { showErrors = false; vm.newIssue() })
            return@Column
        }
        Text(stringResource(R.string.issue_intro), style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 8.dp))
        Spacer(Modifier.height(12.dp))

        val deviceBad = showErrors && problem == IssueForm.Problem.BadDevice ||
            form.deviceCode.isNotBlank() && DeviceCode.strip(form.deviceCode).length >= DeviceCode.LENGTH && !DeviceCode.isValid(form.deviceCode)
        OutlinedTextField(form.deviceCode, { v -> vm.editForm { it.copy(deviceCode = v) } }, Modifier.fillMaxWidth(),
            label = { Text(stringResource(R.string.field_device_code)) },
            supportingText = {
                Text(if (deviceBad) stringResource(R.string.err_device_code)
                    else stringResource(R.string.device_code_hint, DeviceCode.strip(form.deviceCode).length, DeviceCode.LENGTH))
            },
            isError = deviceBad, minLines = 2,
            textStyle = LocalTextStyle.current.copy(fontFamily = FontFamily.Monospace, textDirection = TextDirection.Ltr),
            keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.None, autoCorrectEnabled = false, keyboardType = KeyboardType.Ascii))

        OutlinedTextField(form.shop, { v -> vm.editForm { it.copy(shop = v) } }, Modifier.fillMaxWidth(), singleLine = true,
            label = { Text(stringResource(R.string.field_shop)) },
            isError = showErrors && problem == IssueForm.Problem.NoShop,
            supportingText = if (showErrors && problem == IssueForm.Problem.NoShop) { { Text(stringResource(R.string.err_shop)) } } else null,
            keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Words))

        SectionHeader(stringResource(R.string.field_duration))
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OfflineIssuer.PRESET_DAYS.forEach { d ->
                FilterChip(selected = form.preset == d, onClick = { vm.editForm { it.copy(preset = d) } },
                    label = { Text(stringResource(R.string.days_n, d)) })
            }
            FilterChip(selected = form.preset == null, onClick = { vm.editForm { it.copy(preset = null) } },
                label = { Text(stringResource(R.string.days_custom)) })
        }
        if (form.preset == null) {
            val bad = showErrors && problem == IssueForm.Problem.BadDays
            OutlinedTextField(form.customDays, { v -> vm.editForm { it.copy(customDays = v.filter(Char::isDigit).take(5)) } },
                Modifier.fillMaxWidth(), singleLine = true, label = { Text(stringResource(R.string.field_days)) }, isError = bad,
                supportingText = { Text(stringResource(R.string.days_range, OfflineIssuer.MIN_DAYS, OfflineIssuer.MAX_DAYS)) },
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number))
        }
        OutlinedTextField(form.note, { v -> vm.editForm { it.copy(note = v.take(500)) } }, Modifier.fillMaxWidth().padding(top = 8.dp),
            label = { Text(stringResource(R.string.field_note)) }, minLines = 2)

        Spacer(Modifier.height(16.dp))
        Button(
            onClick = {
                showErrors = true
                if (problem == null) confirmWithUser(activity, lockPolicy, confirmTitle, onOk = vm::issue)
            },
            enabled = !busy, modifier = Modifier.fillMaxWidth().height(52.dp),
        ) {
            if (busy) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
            else { Icon(Icons.Outlined.VpnKey, null); Spacer(Modifier.width(8.dp)); Text(stringResource(R.string.action_issue)) }
        }
        Spacer(Modifier.height(24.dp))
    }
}

@Composable
private fun IssuedResult(r: IssuedRecord, lockPolicy: SessionLockPolicy, onNew: () -> Unit) {
    val context = LocalContext.current
    val copied = stringResource(R.string.copied)
    val subject = stringResource(R.string.share_activation_subject, r.shop)
    val saved = stringResource(R.string.file_saved)
    val saveFailed = stringResource(R.string.file_save_failed)
    var status by remember { mutableStateOf<String?>(null) }
    val save = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("text/plain")) { uri ->
        lockPolicy.authenticating = false
        if (uri != null) status = runCatching {
            context.contentResolver.openOutputStream(uri)?.use { it.write(activationText(r).toByteArray(Charsets.UTF_8)) } ?: error("no stream")
        }.fold({ saved }, { saveFailed })
    }

    SectionHeader(stringResource(R.string.issue_done_title))
    Text(stringResource(R.string.issue_done_body, r.shop, formatDate(r.expiresAt)), style = MaterialTheme.typography.bodyMedium)
    Spacer(Modifier.height(12.dp))
    CodeBox(r.activationCode)
    Spacer(Modifier.height(12.dp))
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        OutlinedButton(onClick = { copyText(context, "VELTRIX activation", r.activationCode); status = copied }, Modifier.weight(1f)) {
            Icon(Icons.Outlined.ContentCopy, null, Modifier.size(18.dp)); Spacer(Modifier.width(4.dp)); Text(stringResource(R.string.action_copy), maxLines = 1)
        }
        OutlinedButton(onClick = { shareText(context, subject, activationText(r)) }, Modifier.weight(1f)) {
            Icon(Icons.Outlined.Share, null, Modifier.size(18.dp)); Spacer(Modifier.width(4.dp)); Text(stringResource(R.string.action_share), maxLines = 1)
        }
        OutlinedButton(onClick = { lockPolicy.authenticating = true; save.launch(OfflineIssuer.safeFileName(r.shop)) }, Modifier.weight(1f)) {
            Icon(Icons.Outlined.Save, null, Modifier.size(18.dp)); Spacer(Modifier.width(4.dp)); Text(stringResource(R.string.action_save_txt), maxLines = 1)
        }
    }
    status?.let { Text(it, color = MaterialTheme.colorScheme.secondary, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(top = 8.dp)) }
    Spacer(Modifier.height(12.dp))
    Text(stringResource(R.string.issue_done_hint), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    Spacer(Modifier.height(16.dp))
    Button(onClick = onNew, modifier = Modifier.fillMaxWidth()) { Text(stringResource(R.string.action_new_issue)) }
    Spacer(Modifier.height(24.dp))
}
