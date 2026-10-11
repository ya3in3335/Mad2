package com.veltrix.admin.ui.history

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.ContentCopy
import androidx.compose.material.icons.outlined.Inbox
import androidx.compose.material.icons.outlined.Refresh
import androidx.compose.material.icons.outlined.Search
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDirection
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.veltrix.admin.R
import com.veltrix.admin.data.history.HistorySearch
import com.veltrix.admin.data.history.IssuedRecord
import com.veltrix.admin.data.license.DeviceCode
import com.veltrix.admin.data.license.OfflineIssuer
import com.veltrix.admin.ui.IssuerViewModel
import com.veltrix.admin.ui.common.copyText
import com.veltrix.admin.ui.common.formatDate
import com.veltrix.admin.ui.common.formatDateTime
import com.veltrix.admin.ui.theme.DarkTokens

@Composable
fun HistoryScreen(vm: IssuerViewModel) {
    val records by vm.history.records.collectAsStateWithLifecycle()
    var query by rememberSaveable { mutableStateOf("") }
    val shown = remember(records, query) { HistorySearch.filter(records, query) }

    Column(Modifier.fillMaxSize()) {
        OutlinedTextField(query, { query = it }, Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp), singleLine = true,
            leadingIcon = { Icon(Icons.Outlined.Search, null) }, placeholder = { Text(stringResource(R.string.history_search)) })
        if (shown.isEmpty()) {
            Column(Modifier.fillMaxWidth().padding(32.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                Icon(Icons.Outlined.Inbox, null, Modifier.size(40.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
                Spacer(Modifier.height(8.dp))
                Text(stringResource(if (records.isEmpty()) R.string.history_empty else R.string.history_no_match),
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        } else LazyColumn(contentPadding = PaddingValues(16.dp, 0.dp, 16.dp, 16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            items(shown, key = { it.licenseId }) { r -> HistoryRow(r, onRenew = { vm.renew(r) }) }
        }
    }
}

@Composable
private fun HistoryRow(r: IssuedRecord, onRenew: () -> Unit) {
    val context = LocalContext.current
    val now = System.currentTimeMillis() / 1000
    val left = OfflineIssuer.daysLeft(r.expiresAt, now)
    Card(Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
        Column(Modifier.padding(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(r.shop, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f))
                Text(if (left > 0) stringResource(R.string.days_left, left) else stringResource(R.string.status_expired),
                    color = if (left > 0) DarkTokens.success else DarkTokens.danger, style = MaterialTheme.typography.labelMedium)
            }
            Text(DeviceCode.short(r.deviceCode), fontFamily = FontFamily.Monospace,
                style = MaterialTheme.typography.bodySmall.copy(textDirection = TextDirection.Ltr), color = MaterialTheme.colorScheme.onSurfaceVariant)
            Spacer(Modifier.height(4.dp))
            Text(stringResource(R.string.history_issued_at, formatDateTime(r.issuedAt)), style = MaterialTheme.typography.bodySmall)
            Text(stringResource(R.string.history_expires, formatDate(r.expiresAt), r.days), style = MaterialTheme.typography.bodySmall)
            r.note?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.secondary, modifier = Modifier.padding(top = 4.dp)) }
            Row(Modifier.fillMaxWidth().padding(top = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.End)) {
                TextButton(onClick = { copyText(context, "VELTRIX activation", r.activationCode) }) {
                    Icon(Icons.Outlined.ContentCopy, null, Modifier.size(18.dp)); Spacer(Modifier.width(4.dp)); Text(stringResource(R.string.action_copy_code))
                }
                FilledTonalButton(onClick = onRenew) {
                    Icon(Icons.Outlined.Refresh, null, Modifier.size(18.dp)); Spacer(Modifier.width(4.dp)); Text(stringResource(R.string.action_renew))
                }
            }
        }
    }
}
