package com.veltrix.admin.ui.diagnostics

import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import com.veltrix.admin.R
import com.veltrix.admin.data.api.*
import com.veltrix.admin.data.repo.AdminRepository
import com.veltrix.admin.ui.common.*
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

data class IncidentFilters(val status: String? = "open", val severity: String? = null, val search: String = "")

@HiltViewModel
class DiagnosticsViewModel @Inject constructor(private val repo: AdminRepository) : ViewModel() {
    val grouped = MutableStateFlow(false)
    val filters = MutableStateFlow(IncidentFilters())
    private val pager = Paginator(key = Incident::id) { p, s -> filters.value.let { f -> repo.incidents(p, s, f.status, f.severity, null, f.search.ifBlank { null }) } }
    private val _list = MutableStateFlow(PageState<Incident>())
    val list = _list.asStateFlow()
    private val _groups = MutableStateFlow<Load<List<IncidentGroup>>>(Load.Loading)
    val groups = _groups.asStateFlow()
    private val _detail = MutableStateFlow<Load<IncidentDetail>?>(null)
    val detail = _detail.asStateFlow()
    val action = MutableStateFlow<ActionState>(ActionState.Idle)
    val messages = MutableSharedFlow<Int>(extraBufferCapacity = 4)
    private var job: Job? = null

    init { refresh() }

    fun refresh() = viewModelScope.launch {
        if (grouped.value) reload(_groups.value, { _groups.value = it }) { repo.incidentGroups(filters.value.status).sortedByDescending { it.occurrences } }
        else pager.refresh { _list.value = it }
    }
    fun loadMore() = viewModelScope.launch { pager.loadMore { _list.value = it } }
    fun setGrouped(g: Boolean) { grouped.value = g; refresh() }
    fun update(f: IncidentFilters, debounce: Boolean = false) {
        filters.value = f
        job?.cancel(); job = viewModelScope.launch { if (debounce) delay(350); refresh().join() }
    }

    fun open(id: String) = viewModelScope.launch {
        _detail.value = Load.Loading
        attempt { repo.incident(id) }.fold({ _detail.value = Load.Ok(it) }, { _detail.value = Load.Failed(it.toAppError()) })
    }
    fun close() { _detail.value = null; action.value = ActionState.Idle }
    fun resetAction() { action.value = ActionState.Idle }

    fun setStatus(id: String, status: String, note: String, onDone: () -> Unit) = viewModelScope.launch {
        action.value = ActionState.Busy
        attempt { repo.updateIncidentStatus(id, status, note.ifBlank { null }) }.fold(
            { action.value = ActionState.Idle; messages.tryEmit(R.string.msg_incident_updated); onDone(); open(id); refresh() },
            { action.value = ActionState.Failed(it.toAppError()) },
        )
    }

    fun requestReport(deviceId: String, reason: String, onDone: () -> Unit) = viewModelScope.launch {
        action.value = ActionState.Busy
        attempt { repo.requestDiagnostics(deviceId, reason.ifBlank { null }) }.fold(
            { action.value = ActionState.Idle; messages.tryEmit(R.string.msg_diag_requested); onDone() },
            { action.value = ActionState.Failed(it.toAppError()) },
        )
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DiagnosticsScreen(vm: DiagnosticsViewModel = hiltViewModel()) {
    val grouped by vm.grouped.collectAsStateWithLifecycle()
    val f by vm.filters.collectAsStateWithLifecycle()
    val detail by vm.detail.collectAsStateWithLifecycle()
    CollectMessages(vm.messages)

    val header: androidx.compose.foundation.lazy.LazyListScope.() -> Unit = {
        item {
            Row(Modifier.padding(horizontal = 16.dp, vertical = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                FilterChip(!grouped, { vm.setGrouped(false) }, label = { Text(stringResource(R.string.diag_incidents)) })
                FilterChip(grouped, { vm.setGrouped(true) }, label = { Text(stringResource(R.string.diag_grouped)) })
            }
        }
        if (!grouped) item { SearchField(f.search, { vm.update(f.copy(search = it), debounce = true) }) }
        item { FilterRow(listOf(null to stringResource(R.string.filter_all), "open" to stringResource(R.string.st_open),
            "acknowledged" to stringResource(R.string.st_acknowledged), "resolved" to stringResource(R.string.st_resolved)), f.status) { vm.update(f.copy(status = it)) } }
        if (!grouped) {
            item { Spacer(Modifier.height(4.dp)) }
            item { FilterRow(listOf(null to stringResource(R.string.filter_any_severity), "critical" to stringResource(R.string.sev_critical),
                "error" to stringResource(R.string.sev_error), "warning" to stringResource(R.string.sev_warning)), f.severity) { vm.update(f.copy(severity = it)) } }
        }
    }

    if (grouped) {
        val groups by vm.groups.collectAsStateWithLifecycle()
        LoadScreen(groups, onRefresh = { vm.refresh() }) { list ->
            header()
            if (list.isEmpty()) item { EmptyState() }
            list.forEach { g ->
                item(key = g.fingerprint) {
                    RowCard(g.title, "${g.app} · ${g.category} · ${g.errorType}", status = g.maxSeverity,
                        meta = stringResource(R.string.diag_group_meta, g.occurrences, g.tenants, g.openIncidents) + " · " + ageText(g.lastSeenAt),
                        onClick = { vm.setGrouped(false); vm.update(f.copy(search = g.errorType.ifBlank { g.title })) })
                }
            }
        }
    } else {
        val state by vm.list.collectAsStateWithLifecycle()
        PagedList(state, onRefresh = { vm.refresh() }, onLoadMore = { vm.loadMore() }, key = { it.id }, header = header) { i ->
            RowCard(i.title, listOfNotNull(i.tenantName, "${i.app} ${i.appVersion}", i.category).joinToString(" · "), status = i.severity,
                meta = statusLabel(i.status) + " · ×" + i.occurrenceCount + " · " + ageText(i.lastSeenAt), onClick = { vm.open(i.id) })
        }
    }

    detail?.let { load ->
        ModalBottomSheet(onDismissRequest = vm::close, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)) { IncidentSheet(load, vm) }
    }
}

private enum class IncidentDialog { Status, Report }

@Composable
private fun IncidentSheet(load: Load<IncidentDetail>, vm: DiagnosticsViewModel) {
    val action by vm.action.collectAsStateWithLifecycle()
    var dialog by remember { mutableStateOf<Pair<IncidentDialog, String>?>(null) }
    when (load) {
        Load.Loading -> SkeletonList(4)
        is Load.Failed -> ErrorState(load.error) { }
        is Load.Ok -> {
            val d = load.value
            val i = d.incident
            LazyColumn(Modifier.fillMaxWidth().navigationBarsPadding()) {
                item {
                    Row(Modifier.padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
                        Text(i.title, style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f)); StatusChip(i.severity)
                    }
                }
                item {
                    InfoRow(stringResource(R.string.field_status), statusLabel(i.status))
                    InfoRow(stringResource(R.string.field_tenant), i.tenantName)
                    InfoRow(stringResource(R.string.field_app_version), "${i.app} ${i.appVersion} · ${i.platform}")
                    InfoRow(stringResource(R.string.field_category), "${i.category} · ${i.errorType}")
                    InfoRow(stringResource(R.string.diag_occurrences), "${i.occurrenceCount} · " + stringResource(R.string.diag_devices, d.affectedDevices))
                    InfoRow(stringResource(R.string.diag_first_seen), Formatters.dateTime(i.firstSeenAt))
                    InfoRow(stringResource(R.string.diag_last_seen), Formatters.dateTime(i.lastSeenAt))
                    d.statusNote?.let { InfoRow(stringResource(R.string.field_notes), it) }
                }
                item {
                    ActionButtonsRow {
                        if (!i.status.equals("acknowledged", true) && !i.status.equals("resolved", true))
                            OutlinedButton(onClick = { vm.resetAction(); dialog = IncidentDialog.Status to "acknowledged" }, Modifier.weight(1f)) { Text(stringResource(R.string.action_acknowledge)) }
                        if (!i.status.equals("resolved", true))
                            Button(onClick = { vm.resetAction(); dialog = IncidentDialog.Status to "resolved" }, Modifier.weight(1f)) { Text(stringResource(R.string.action_resolve)) }
                        else OutlinedButton(onClick = { vm.resetAction(); dialog = IncidentDialog.Status to "open" }, Modifier.weight(1f)) { Text(stringResource(R.string.action_reopen)) }
                    }
                    i.lastDeviceId?.let { dev ->
                        ActionButtonsRow {
                            OutlinedButton(onClick = { vm.resetAction(); dialog = IncidentDialog.Report to dev }, Modifier.weight(1f)) {
                                Text(stringResource(R.string.action_request_diagnostics))
                            }
                        }
                    }
                }
                d.sampleStackTrace?.let { st ->
                    item { SectionHeader(stringResource(R.string.diag_stack)) }
                    item {
                        Text(st.take(4000), Modifier.padding(horizontal = 16.dp).horizontalScroll(rememberScrollState()),
                            style = MaterialTheme.typography.bodySmall, fontFamily = FontFamily.Monospace)
                    }
                }
                if (d.recentEvents.isNotEmpty()) {
                    item { SectionHeader(stringResource(R.string.diag_recent_events)) }
                    d.recentEvents.forEach { e -> item(key = e.id) { RowCard(e.message, e.osVersion, meta = Formatters.dateTime(e.occurredAt)) } }
                }
                item { Spacer(Modifier.height(24.dp)) }
            }
            dialog?.let { (kind, arg) ->
                var note by remember(kind, arg) { mutableStateOf("") }
                FormDialog(
                    stringResource(if (kind == IncidentDialog.Status) R.string.diag_status_title else R.string.action_request_diagnostics), action,
                    onDismiss = { dialog = null }, confirmEnabled = true,
                    onConfirm = { if (kind == IncidentDialog.Status) vm.setStatus(i.id, arg, note) { dialog = null } else vm.requestReport(arg, note) { dialog = null } },
                ) {
                    if (kind == IncidentDialog.Status) Text(stringResource(R.string.diag_status_to, statusLabel(arg)))
                    else Text(stringResource(R.string.diag_request_body), style = MaterialTheme.typography.bodyMedium)
                    OutlinedTextField(note, { note = it.take(500) }, label = { Text(stringResource(if (kind == IncidentDialog.Status) R.string.field_notes else R.string.field_reason)) },
                        modifier = Modifier.fillMaxWidth())
                }
            }
        }
    }
}
