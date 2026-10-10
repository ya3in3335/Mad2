package com.veltrix.admin.ui.devices

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.selection.selectable
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

data class DeviceFilters(val search: String = "", val platform: String? = null, val online: Boolean? = null, val status: String? = null)

@HiltViewModel
class DevicesViewModel @Inject constructor(private val repo: AdminRepository) : ViewModel() {
    val filters = MutableStateFlow(DeviceFilters())
    private val paginator = Paginator(key = AdminDevice::id) { page, size ->
        val f = filters.value
        repo.devices(page, size, f.search.ifBlank { null }, f.platform, f.online, f.status)
    }
    private val _state = MutableStateFlow(PageState<AdminDevice>())
    val state = _state.asStateFlow()
    private val _detail = MutableStateFlow<Load<AdminDeviceDetail>?>(null)
    val detail = _detail.asStateFlow()
    val action = MutableStateFlow<ActionState>(ActionState.Idle)
    val messages = MutableSharedFlow<Int>(extraBufferCapacity = 4)
    val candidateLicenses = MutableStateFlow<List<License>>(emptyList())
    val report = MutableStateFlow<Load<DiagnosticReport>?>(null)
    private var searchJob: Job? = null

    init { refresh() }

    fun refresh() = viewModelScope.launch { paginator.refresh { _state.value = it } }
    fun loadMore() = viewModelScope.launch { paginator.loadMore { _state.value = it } }
    fun update(f: DeviceFilters, debounce: Boolean = false) {
        filters.value = f
        searchJob?.cancel()
        searchJob = viewModelScope.launch { if (debounce) delay(350); paginator.refresh { _state.value = it } }
    }

    fun open(d: AdminDevice) = viewModelScope.launch {
        _detail.value = Load.Loading
        // Older servers have no device-detail endpoint: fall back to the list row so the sheet still works.
        attempt { repo.device(d.id) }.fold(
            { _detail.value = Load.Ok(it) },
            { e -> _detail.value = if (e.toAppError() == AppError.NotSupported) Load.Ok(AdminDeviceDetail(d)) else Load.Failed(e.toAppError()) },
        )
    }
    fun close() { _detail.value = null; action.value = ActionState.Idle }
    fun resetAction() { action.value = ActionState.Idle }

    private fun run(msg: Int, block: suspend () -> Unit, onDone: () -> Unit) = viewModelScope.launch {
        action.value = ActionState.Busy
        attempt { block() }.fold(
            { action.value = ActionState.Idle; messages.tryEmit(msg); onDone(); refresh(); (_detail.value as? Load.Ok)?.value?.device?.let { open(it) } },
            { action.value = ActionState.Failed(it.toAppError()) },
        )
    }

    fun deactivate(id: String, reason: String, password: String, onDone: () -> Unit) =
        run(R.string.msg_device_deactivated, { repo.deactivateDevice(id, reason, password) }, onDone)
    fun reassign(id: String, target: String, reason: String, password: String, onDone: () -> Unit) =
        run(R.string.msg_device_reassigned, { repo.reassignDevice(id, target, reason, password) }, onDone)
    fun requestDiagnostics(id: String, reason: String, onDone: () -> Unit) =
        run(R.string.msg_diag_requested, { repo.requestDiagnostics(id, reason.ifBlank { null }) }, onDone)

    fun loadLicensesFor(tenantId: String) = viewModelScope.launch {
        candidateLicenses.value = attempt { repo.licenses(1, 50, null, tenantId).items }.getOrDefault(emptyList())
    }

    fun openReport(requestId: String) = viewModelScope.launch {
        report.value = Load.Loading
        attempt { repo.diagnosticReport(requestId) }.fold({ report.value = Load.Ok(it) }, { report.value = Load.Failed(it.toAppError()) })
    }
    fun closeReport() { report.value = null }
}

private enum class DeviceDialog { Deactivate, Reassign, Diagnostics }

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DevicesScreen(vm: DevicesViewModel = hiltViewModel()) {
    val state by vm.state.collectAsStateWithLifecycle()
    val f by vm.filters.collectAsStateWithLifecycle()
    val detail by vm.detail.collectAsStateWithLifecycle()
    CollectMessages(vm.messages)

    PagedList(state, onRefresh = { vm.refresh() }, onLoadMore = { vm.loadMore() }, key = { it.id }, header = {
        item { SearchField(f.search, { vm.update(f.copy(search = it), debounce = true) }) }
        item { FilterRow(listOf(null to stringResource(R.string.filter_all_platforms), "android" to stringResource(R.string.platform_android),
            "windows" to stringResource(R.string.platform_windows)), f.platform) { vm.update(f.copy(platform = it)) } }
        item { Spacer(Modifier.height(4.dp)) }
        item { FilterRow(listOf(null to stringResource(R.string.filter_any_connectivity), true to stringResource(R.string.st_online),
            false to stringResource(R.string.st_offline)), f.online) { vm.update(f.copy(online = it)) } }
        item { Spacer(Modifier.height(4.dp)) }
        item { FilterRow(listOf(null to stringResource(R.string.filter_all), "Active" to stringResource(R.string.st_active),
            "Revoked" to stringResource(R.string.st_revoked), "Released" to stringResource(R.string.st_released), "Replaced" to stringResource(R.string.st_replaced)),
            f.status) { vm.update(f.copy(status = it)) } }
    }) { d ->
        RowCard(d.deviceName, listOfNotNull(d.tenantName, stringResource(platformLabel(d.platform)) + " " + d.appVersion).joinToString(" · "),
            status = if (d.status.equals("Active", true)) (if (d.online) "online" else "offline") else d.status,
            meta = stringResource(R.string.device_last_seen, ageText(d.lastContactAt)), onClick = { vm.open(d) })
    }

    detail?.let { load ->
        ModalBottomSheet(onDismissRequest = vm::close, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)) {
            DeviceSheet(load, vm)
        }
    }
    val report by vm.report.collectAsStateWithLifecycle()
    report?.let { ReportDialog(it, onDismiss = vm::closeReport) }
}

@Composable
private fun DeviceSheet(load: Load<AdminDeviceDetail>, vm: DevicesViewModel) {
    val action by vm.action.collectAsStateWithLifecycle()
    var dialog by remember { mutableStateOf<DeviceDialog?>(null) }
    when (load) {
        Load.Loading -> SkeletonList(4)
        is Load.Failed -> ErrorState(load.error) { }
        is Load.Ok -> {
            val dd = load.value
            val d = dd.device
            LazyColumn(Modifier.fillMaxWidth().navigationBarsPadding()) {
                item {
                    Row(Modifier.padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
                        Text(d.deviceName, style = MaterialTheme.typography.titleLarge, modifier = Modifier.weight(1f)); StatusChip(d.status)
                    }
                }
                item {
                    InfoRow(stringResource(R.string.field_tenant), d.tenantName)
                    InfoRow(stringResource(R.string.field_platform), stringResource(platformLabel(d.platform)) + (d.osVersion?.let { " · $it" } ?: ""))
                    InfoRow(stringResource(R.string.field_app_version), d.appVersion)
                    InfoRow(stringResource(R.string.field_connectivity), statusLabel(if (d.online) "online" else "offline"))
                    InfoRow(stringResource(R.string.field_activated), Formatters.dateTime(d.activatedAt))
                    InfoRow(stringResource(R.string.field_last_contact), Formatters.dateTime(d.lastContactAt))
                    InfoRow(stringResource(R.string.field_last_sync), Formatters.dateTime(d.lastSyncAt))
                    InfoRow(stringResource(R.string.field_sync_queue), stringResource(R.string.device_sync_counts, d.pendingSyncOperations ?: 0, d.failedSyncOperations ?: 0))
                    dd.licensePlan?.let { InfoRow(stringResource(R.string.field_license), "$it · ${dd.licenseType.orEmpty()} · ${statusLabel(dd.licenseStatus)}") }
                    InfoRow(stringResource(R.string.device_errors), stringResource(R.string.device_error_counts, dd.reportedErrors, dd.openIncidents))
                    dd.revokedReason?.let { InfoRow(stringResource(R.string.field_reason), it) }
                }
                if (d.status.equals("Active", true)) item {
                    ActionButtonsRow {
                        OutlinedButton(onClick = { vm.resetAction(); vm.loadLicensesFor(d.tenantId); dialog = DeviceDialog.Reassign }, Modifier.weight(1f)) {
                            Text(stringResource(R.string.action_reassign))
                        }
                        OutlinedButton(onClick = { vm.resetAction(); dialog = DeviceDialog.Deactivate }, Modifier.weight(1f),
                            colors = ButtonDefaults.outlinedButtonColors(contentColor = MaterialTheme.colorScheme.error)) { Text(stringResource(R.string.action_deactivate)) }
                    }
                }
                item {
                    ActionButtonsRow {
                        Button(onClick = { vm.resetAction(); dialog = DeviceDialog.Diagnostics }, Modifier.weight(1f)) { Text(stringResource(R.string.action_request_diagnostics)) }
                    }
                    if (!dd.tenantDiagnosticsConsent) Text(stringResource(R.string.diag_no_consent), Modifier.padding(horizontal = 16.dp),
                        style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                if (dd.diagnosticRequests.isNotEmpty()) {
                    item { SectionHeader(stringResource(R.string.diag_requests)) }
                    dd.diagnosticRequests.forEach { r ->
                        item(key = "r" + r.id) {
                            RowCard(Formatters.dateTime(r.requestedAt), r.reason, status = r.status,
                                onClick = if (r.status.equals("uploaded", true)) ({ vm.openReport(r.id) }) else null)
                        }
                    }
                }
                if (dd.history.isNotEmpty()) {
                    item { SectionHeader(stringResource(R.string.license_history)) }
                    dd.history.forEach { e -> item(key = "h" + e.id) { RowCard(e.type, e.details, meta = Formatters.dateTime(e.occurredAt)) } }
                }
                item { Spacer(Modifier.height(24.dp)) }
            }
            when (dialog) {
                DeviceDialog.Deactivate -> StepUpDialog(stringResource(R.string.device_deactivate_title), stringResource(R.string.device_deactivate_body), action,
                    onDismiss = { dialog = null }, onConfirm = { pw, reason -> vm.deactivate(d.id, reason, pw) { dialog = null } })
                DeviceDialog.Reassign -> ReassignDialog(d, vm, action) { dialog = null }
                DeviceDialog.Diagnostics -> {
                    var reason by remember { mutableStateOf("") }
                    FormDialog(stringResource(R.string.action_request_diagnostics), action, onDismiss = { dialog = null }, confirmEnabled = true,
                        onConfirm = { vm.requestDiagnostics(d.id, reason) { dialog = null } }) {
                        Text(stringResource(R.string.diag_request_body), style = MaterialTheme.typography.bodyMedium)
                        OutlinedTextField(reason, { reason = it.take(300) }, label = { Text(stringResource(R.string.field_reason)) }, modifier = Modifier.fillMaxWidth())
                    }
                }
                null -> Unit
            }
        }
    }
}

@Composable
private fun ReassignDialog(d: AdminDevice, vm: DevicesViewModel, action: ActionState, onDismiss: () -> Unit) {
    val licenses by vm.candidateLicenses.collectAsStateWithLifecycle()
    var target by remember { mutableStateOf<String?>(null) }
    var step by remember { mutableStateOf(false) }
    if (!step) {
        FormDialog(stringResource(R.string.device_reassign_title), ActionState.Idle, onDismiss, confirmEnabled = target != null,
            onConfirm = { step = true }, confirmLabel = stringResource(R.string.action_continue)) {
            Text(stringResource(R.string.device_reassign_body), style = MaterialTheme.typography.bodyMedium)
            val options = licenses.filter { it.id != d.licenseId }
            if (options.isEmpty()) Text(stringResource(R.string.device_reassign_none), color = MaterialTheme.colorScheme.onSurfaceVariant)
            options.forEach { l ->
                Row(Modifier.fillMaxWidth().selectable(target == l.id, onClick = { target = l.id }).padding(vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                    RadioButton(target == l.id, onClick = { target = l.id })
                    Column {
                        Text("${l.planCode} · ${l.type} · …${l.keyHint}")
                        Text(stringResource(R.string.license_seats_used, l.activeDevices, l.maxDevices), style = MaterialTheme.typography.bodySmall)
                    }
                }
            }
        }
    } else {
        StepUpDialog(stringResource(R.string.device_reassign_title), null, action, onDismiss = onDismiss, destructive = false,
            onConfirm = { pw, reason -> vm.reassign(d.id, target!!, reason, pw, onDismiss) })
    }
}

@Composable
private fun ReportDialog(load: Load<DiagnosticReport>, onDismiss: () -> Unit) {
    AlertDialog(onDismissRequest = onDismiss, confirmButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.action_close)) } },
        title = { Text(stringResource(R.string.diag_report)) },
        text = {
            when (load) {
                Load.Loading -> CircularProgressIndicator()
                is Load.Failed -> Text(errorText(load.error), color = MaterialTheme.colorScheme.error)
                is Load.Ok -> LazyColumn(Modifier.heightIn(max = 420.dp)) {
                    val r = load.value
                    item { Text("${r.appVersion.orEmpty()} · ${r.osVersion.orEmpty()}", style = MaterialTheme.typography.labelMedium) }
                    r.facts.toSortedMap().forEach { (k, v) -> item { Text("$k: $v", style = MaterialTheme.typography.bodySmall) } }
                    if (r.recentLogLines.isNotEmpty()) item { Text(stringResource(R.string.diag_logs), Modifier.padding(top = 8.dp), style = MaterialTheme.typography.titleSmall) }
                    r.recentLogLines.forEach { line -> item { Text(line, style = MaterialTheme.typography.bodySmall, fontFamily = FontFamily.Monospace) } }
                }
            }
        })
}

