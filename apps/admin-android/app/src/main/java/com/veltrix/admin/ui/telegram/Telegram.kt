package com.veltrix.admin.ui.telegram

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import com.veltrix.admin.R
import com.veltrix.admin.data.api.TelegramDelivery
import com.veltrix.admin.data.api.TelegramStatus
import com.veltrix.admin.data.api.toAppError
import com.veltrix.admin.data.repo.AdminRepository
import com.veltrix.admin.ui.common.*
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/** The bot token is server-side configuration only; this screen shows whether it is set, never its value. */
data class TelegramData(val status: TelegramStatus, val deliveries: List<TelegramDelivery>)

@HiltViewModel
class TelegramViewModel @Inject constructor(private val repo: AdminRepository) : ViewModel() {
    private val _state = MutableStateFlow<Load<TelegramData>>(Load.Loading)
    val state = _state.asStateFlow()
    val action = MutableStateFlow<ActionState>(ActionState.Idle)
    val messages = MutableSharedFlow<Int>(extraBufferCapacity = 4)

    init { refresh() }

    fun refresh() = viewModelScope.launch {
        reload(_state.value, { _state.value = it }) {
            coroutineScope {
                val s = async { repo.telegramStatus() }
                val d = async { attempt { repo.telegramDeliveries().items }.getOrDefault(emptyList()) }
                TelegramData(s.await(), d.await())
            }
        }
    }

    fun sendTest(text: String) = viewModelScope.launch {
        action.value = ActionState.Busy
        attempt { repo.telegramTest(text.ifBlank { null }) }.fold(
            { action.value = ActionState.Idle; messages.tryEmit(R.string.msg_telegram_queued); refresh() },
            { action.value = ActionState.Failed(it.toAppError()) },
        )
    }
}

@Composable
fun TelegramScreen(vm: TelegramViewModel = hiltViewModel()) {
    val state by vm.state.collectAsStateWithLifecycle()
    val action by vm.action.collectAsStateWithLifecycle()
    var text by remember { mutableStateOf("") }
    CollectMessages(vm.messages)
    LoadScreen(state, onRefresh = { vm.refresh() }) { d ->
        val s = d.status
        item {
            Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                KpiCard(stringResource(R.string.tg_sent_24h), s.sent24h.toString(), Modifier.weight(1f))
                KpiCard(stringResource(R.string.tg_pending), s.pending.toString(), Modifier.weight(1f))
                KpiCard(stringResource(R.string.tg_failed), s.failed.toString(), Modifier.weight(1f), accent = if (s.failed > 0) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.secondary)
            }
        }
        item {
            InfoRow(stringResource(R.string.tg_enabled), stringResource(if (s.enabled) R.string.yes else R.string.no))
            InfoRow(stringResource(R.string.tg_token), stringResource(if (s.botTokenConfigured) R.string.tg_configured else R.string.tg_missing))
            InfoRow(stringResource(R.string.tg_webhook), stringResource(if (s.webhookSecretConfigured) R.string.tg_configured else R.string.tg_missing))
            InfoRow(stringResource(R.string.tg_chats), s.allowedChats.toString())
            InfoRow(stringResource(R.string.tg_skipped), s.skipped24h.toString())
            InfoRow(stringResource(R.string.tg_last_sent), Formatters.dateTime(s.lastSentAt))
            s.lastError?.let { InfoRow(stringResource(R.string.tg_last_error), it) }
            Text(stringResource(R.string.tg_token_note), Modifier.padding(16.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        item { SectionHeader(stringResource(R.string.tg_test)) }
        item {
            Column(Modifier.padding(horizontal = 16.dp)) {
                OutlinedTextField(text, { text = it.take(500) }, label = { Text(stringResource(R.string.tg_test_text)) }, modifier = Modifier.fillMaxWidth())
                (action as? ActionState.Failed)?.let { Text(errorText(it.error), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
                Button(onClick = { vm.sendTest(text) }, enabled = action !is ActionState.Busy && s.enabled && s.botTokenConfigured,
                    modifier = Modifier.fillMaxWidth().padding(top = 8.dp)) {
                    if (action is ActionState.Busy) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp) else Text(stringResource(R.string.tg_send_test))
                }
            }
        }
        if (d.deliveries.isNotEmpty()) {
            item { SectionHeader(stringResource(R.string.tg_recent)) }
            d.deliveries.forEach { m ->
                item(key = m.id) { RowCard(m.type, m.text.take(140), status = m.status, meta = Formatters.dateTime(m.sentAt ?: m.createdAt) + (m.lastError?.let { " · $it" } ?: "")) }
            }
        }
    }
}
