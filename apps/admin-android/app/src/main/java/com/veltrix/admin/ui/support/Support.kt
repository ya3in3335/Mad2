package com.veltrix.admin.ui.support

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import com.veltrix.admin.R
import com.veltrix.admin.data.api.SupportCase
import com.veltrix.admin.data.api.toAppError
import com.veltrix.admin.data.repo.AdminRepository
import com.veltrix.admin.ui.common.*
import com.veltrix.admin.ui.licenses.FlowRowChips
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

val SUPPORT_STATUSES = listOf("Open", "Pending", "Resolved", "Closed")

@HiltViewModel
class SupportViewModel @Inject constructor(private val repo: AdminRepository) : ViewModel() {
    val status = MutableStateFlow<String?>("Open")
    private val _state = MutableStateFlow<Load<List<SupportCase>>>(Load.Loading)
    val state = _state.asStateFlow()
    val selectedId = MutableStateFlow<String?>(null)
    val action = MutableStateFlow<ActionState>(ActionState.Idle)
    val messages = MutableSharedFlow<Int>(extraBufferCapacity = 4)

    init { refresh() }

    fun refresh() = viewModelScope.launch {
        reload(_state.value, { _state.value = it }) { repo.supportCases(status.value).sortedByDescending { Formatters.parseInstant(it.updatedAt) } }
    }
    fun setStatus(s: String?) { status.value = s; _state.value = Load.Loading; refresh() }
    fun select(id: String?) { selectedId.value = id; action.value = ActionState.Idle }

    fun reply(id: String, body: String, newStatus: String?, onDone: () -> Unit) = viewModelScope.launch {
        action.value = ActionState.Busy
        attempt { repo.replySupport(id, body, newStatus) }.fold(
            { action.value = ActionState.Idle; messages.tryEmit(R.string.msg_reply_sent); onDone(); refresh() },
            { action.value = ActionState.Failed(it.toAppError()) },
        )
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SupportScreen(vm: SupportViewModel = hiltViewModel()) {
    val state by vm.state.collectAsStateWithLifecycle()
    val status by vm.status.collectAsStateWithLifecycle()
    val selectedId by vm.selectedId.collectAsStateWithLifecycle()
    CollectMessages(vm.messages)
    val filters = listOf<Pair<String?, String>>(null to stringResource(R.string.filter_all)) + SUPPORT_STATUSES.map { it to statusLabel(it) }

    LoadScreen(state, onRefresh = { vm.refresh() }) { cases ->
        item { Spacer(Modifier.height(8.dp)); FilterRow(filters, status, vm::setStatus) }
        if (cases.isEmpty()) item { EmptyState() }
        cases.forEach { c ->
            item(key = c.id) {
                RowCard(c.subject, listOfNotNull(c.tenantName, c.priority).joinToString(" · "), status = c.status,
                    meta = stringResource(R.string.support_meta, c.messages.size) + " · " + ageText(c.updatedAt), onClick = { vm.select(c.id) })
            }
        }
    }

    val case = (state as? Load.Ok)?.value?.firstOrNull { it.id == selectedId }
    if (case != null) {
        ModalBottomSheet(onDismissRequest = { vm.select(null) }, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)) { CaseSheet(case, vm) }
    }
}

@Composable
private fun CaseSheet(c: SupportCase, vm: SupportViewModel) {
    val action by vm.action.collectAsStateWithLifecycle()
    var body by remember(c.id) { mutableStateOf("") }
    var newStatus by remember(c.id) { mutableStateOf<String?>(null) }
    Column(Modifier.fillMaxWidth().navigationBarsPadding().imePadding()) {
        Row(Modifier.padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(c.subject, style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f)); StatusChip(c.status)
        }
        Text(listOfNotNull(c.tenantName, c.priority, Formatters.dateTime(c.createdAt)).joinToString(" · "), Modifier.padding(horizontal = 16.dp),
            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        LazyColumn(Modifier.weight(1f, fill = false).heightIn(max = 380.dp).padding(top = 8.dp)) {
            c.messages.forEach { m ->
                item(key = m.id) {
                    Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp),
                        horizontalArrangement = if (m.fromVendor) Arrangement.End else Arrangement.Start) {
                        Column(Modifier.widthIn(max = 300.dp).clip(RoundedCornerShape(14.dp))
                            .background(if (m.fromVendor) MaterialTheme.colorScheme.primary.copy(alpha = 0.22f) else MaterialTheme.colorScheme.surfaceVariant).padding(10.dp)) {
                            Text(m.body, style = MaterialTheme.typography.bodyMedium)
                            Text(Formatters.dateTime(m.createdAt), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                }
            }
        }
        OutlinedTextField(body, { body = it.take(4000) }, label = { Text(stringResource(R.string.support_reply)) }, minLines = 2,
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp))
        Box(Modifier.padding(horizontal = 16.dp)) {
            FlowRowChips(listOf<Pair<String?, String>>(null to stringResource(R.string.support_keep_status)) + SUPPORT_STATUSES.map { it to statusLabel(it) }, newStatus) { newStatus = it }
        }
        if (action is ActionState.Failed) Text(errorText((action as ActionState.Failed).error), Modifier.padding(horizontal = 16.dp), color = MaterialTheme.colorScheme.error)
        Button(onClick = { vm.reply(c.id, body.trim(), newStatus) { body = "" } }, enabled = body.isNotBlank() && action !is ActionState.Busy,
            modifier = Modifier.fillMaxWidth().padding(16.dp)) {
            if (action is ActionState.Busy) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp) else Text(stringResource(R.string.action_send))
        }
    }
}
