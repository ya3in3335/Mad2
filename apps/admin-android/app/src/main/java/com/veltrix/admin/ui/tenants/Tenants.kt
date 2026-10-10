package com.veltrix.admin.ui.tenants

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.SavedStateHandle
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import com.veltrix.admin.R
import com.veltrix.admin.data.api.TenantOverview
import com.veltrix.admin.data.api.TenantSummary
import com.veltrix.admin.data.api.toAppError
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

@HiltViewModel
class TenantsViewModel @Inject constructor(private val repo: AdminRepository) : ViewModel() {
    val search = MutableStateFlow("")
    val status = MutableStateFlow<String?>(null)
    private val paginator = Paginator(key = TenantSummary::id) { page, size -> repo.tenants(page, size, search.value.ifBlank { null }, status.value) }
    private val _state = MutableStateFlow(PageState<TenantSummary>())
    val state = _state.asStateFlow()
    private var searchJob: Job? = null

    init { refresh() }

    fun refresh() = viewModelScope.launch { paginator.refresh { _state.value = it } }
    fun loadMore() = viewModelScope.launch { paginator.loadMore { _state.value = it } }
    fun setSearch(q: String) {
        search.value = q
        searchJob?.cancel()
        searchJob = viewModelScope.launch { delay(350); paginator.refresh { _state.value = it } }
    }
    fun setStatus(s: String?) { status.value = s; refresh() }
}

@Composable
fun TenantsScreen(onOpen: (String) -> Unit, vm: TenantsViewModel = hiltViewModel()) {
    val state by vm.state.collectAsStateWithLifecycle()
    val search by vm.search.collectAsStateWithLifecycle()
    val status by vm.status.collectAsStateWithLifecycle()
    val filters = listOf(null to stringResource(R.string.filter_all), "Active" to stringResource(R.string.st_active),
        "Suspended" to stringResource(R.string.st_suspended), "Pending" to stringResource(R.string.st_pending), "Closed" to stringResource(R.string.st_closed))
    PagedList(state, onRefresh = { vm.refresh() }, onLoadMore = { vm.loadMore() }, key = { it.id }, header = {
        item { SearchField(search, vm::setSearch) }
        item { FilterRow(filters, status, vm::setStatus) }
        if (state.total > 0) item { Text(stringResource(R.string.list_count, state.total), Modifier.padding(horizontal = 16.dp, vertical = 6.dp),
            style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant) }
    }) { t ->
        RowCard(
            title = t.name,
            subtitle = listOfNotNull(t.planCode, t.subscriptionStatus?.let { statusLabel(it) }, t.wilaya).joinToString(" · "),
            status = t.status,
            meta = stringResource(R.string.tenant_meta, t.activeDevices, t.users),
            onClick = { onOpen(t.id) },
        )
    }
}

// ---------------------------------------------------------------- Detail

@HiltViewModel
class TenantDetailViewModel @Inject constructor(private val repo: AdminRepository, saved: SavedStateHandle) : ViewModel() {
    val id: String = checkNotNull(saved["id"])
    private val _state = MutableStateFlow<Load<TenantOverview>>(Load.Loading)
    val state = _state.asStateFlow()
    val action = MutableStateFlow<ActionState>(ActionState.Idle)
    val messages = MutableSharedFlow<Int>(extraBufferCapacity = 4)

    init { refresh() }

    fun refresh() = viewModelScope.launch { reload(_state.value, { _state.value = it }) { repo.tenantOverview(id) } }

    fun setSuspended(suspend: Boolean, reason: String, password: String, onDone: () -> Unit) = viewModelScope.launch {
        action.value = ActionState.Busy
        attempt { if (suspend) repo.suspendTenant(id, reason, password) else repo.reactivateTenant(id, password) }.fold(
            { action.value = ActionState.Idle; messages.tryEmit(if (suspend) R.string.msg_tenant_suspended else R.string.msg_tenant_reactivated); onDone(); refresh() },
            { action.value = ActionState.Failed(it.toAppError()) },
        )
    }

    fun resetAction() { action.value = ActionState.Idle }
}

@Composable
fun TenantDetailScreen(onOpenLicense: (String) -> Unit, vm: TenantDetailViewModel = hiltViewModel()) {
    val state by vm.state.collectAsStateWithLifecycle()
    val action by vm.action.collectAsStateWithLifecycle()
    var dialog by remember { mutableStateOf<Boolean?>(null) } // true = suspend, false = reactivate
    CollectMessages(vm.messages)

    LoadScreen(state, onRefresh = { vm.refresh() }) { o ->
        val c = o.company
        item {
            Column(Modifier.padding(16.dp)) {
                Row { Text(c.name, style = MaterialTheme.typography.headlineSmall, modifier = Modifier.weight(1f)); StatusChip(c.status) }
                c.legalName?.let { Text(it, color = MaterialTheme.colorScheme.onSurfaceVariant) }
                o.suspendedReason?.let { Text(stringResource(R.string.tenant_suspended_reason, it), color = MaterialTheme.colorScheme.error) }
            }
        }
        item {
            ActionButtonsRow {
                if (c.status.equals("Suspended", true)) Button(onClick = { vm.resetAction(); dialog = false }) { Text(stringResource(R.string.action_reactivate)) }
                else OutlinedButton(onClick = { vm.resetAction(); dialog = true }, colors = ButtonDefaults.outlinedButtonColors(contentColor = MaterialTheme.colorScheme.error)) {
                    Text(stringResource(R.string.action_suspend))
                }
            }
        }
        item { SectionHeader(stringResource(R.string.tenant_profile)) }
        item {
            InfoRow(stringResource(R.string.field_email), c.email)
            InfoRow(stringResource(R.string.field_phone), c.phone)
            InfoRow(stringResource(R.string.field_address), listOfNotNull(c.address, c.city, c.wilaya).joinToString(", "))
            InfoRow(stringResource(R.string.field_tax_id), c.taxId)
            InfoRow(stringResource(R.string.tenant_users), o.users.toString())
            InfoRow(stringResource(R.string.kpi_customers), o.customers.toString())
            InfoRow(stringResource(R.string.tenant_open_incidents), o.openIncidents.toString())
            InfoRow(stringResource(R.string.tenant_diag_consent), stringResource(if (o.diagnosticsConsent) R.string.yes else R.string.no))
        }
        item { SectionHeader(stringResource(R.string.tenant_subscription)) }
        item {
            val s = o.subscription
            if (s == null) EmptyState(stringResource(R.string.tenant_no_subscription)) else RowCard(
                "${s.planCode} · ${s.billingCycle}", stringResource(R.string.sub_period, Formatters.date(s.currentPeriodStart), Formatters.date(s.currentPeriodEnd)),
                status = s.status, meta = Formatters.money(s.periodPrice, s.currency) + " · " + stringResource(R.string.sub_seats, s.seats))
        }
        item { SectionHeader(stringResource(R.string.nav_licenses)) }
        if (o.licenses.isEmpty()) item { EmptyState() }
        o.licenses.forEach { l ->
            item(key = "l" + l.id) {
                RowCard("${l.planCode} · ${l.type}", stringResource(R.string.license_seats_used, l.activeDevices, l.maxDevices) + " · …" + l.keyHint,
                    status = l.effectiveStatus, meta = l.expiresAt?.let { stringResource(R.string.license_expires, Formatters.date(it)) }, onClick = { onOpenLicense(l.id) })
            }
        }
        item { SectionHeader(stringResource(R.string.nav_devices)) }
        if (o.devices.isEmpty()) item { EmptyState() }
        o.devices.forEach { d ->
            item(key = "d" + d.id) {
                RowCard(d.deviceName, "${stringResource(platformLabel(d.platform))} · ${d.appVersion}", status = if (d.online) "online" else d.status,
                    meta = stringResource(R.string.device_last_seen, ageText(d.lastContactAt)))
            }
        }
        item { SectionHeader(stringResource(R.string.tenant_payments)) }
        if (o.invoices.isEmpty()) item { EmptyState() }
        o.invoices.forEach { i ->
            item(key = "i" + i.id) { RowCard(i.number, Formatters.money(i.total, i.currency), status = i.status, meta = Formatters.date(i.issuedAt)) }
        }
        item { SectionHeader(stringResource(R.string.nav_support)) }
        if (o.supportCases.isEmpty()) item { EmptyState() }
        o.supportCases.forEach { s -> item(key = "s" + s.id) { RowCard(s.subject, s.priority, status = s.status, meta = Formatters.dateTime(s.updatedAt)) } }
        item { SectionHeader(stringResource(R.string.license_history)) }
        if (o.activationHistory.isEmpty()) item { EmptyState() }
        o.activationHistory.take(30).forEach { e -> item(key = "e" + e.id) { RowCard(e.type, e.details, meta = Formatters.dateTime(e.occurredAt)) } }
    }

    dialog?.let { suspend ->
        StepUpDialog(
            title = stringResource(if (suspend) R.string.tenant_suspend_title else R.string.tenant_reactivate_title),
            message = stringResource(if (suspend) R.string.tenant_suspend_body else R.string.tenant_reactivate_body),
            action = action, requireReason = suspend, destructive = suspend,
            onDismiss = { dialog = null },
            onConfirm = { pw, reason -> vm.setSuspended(suspend, reason, pw) { dialog = null } },
        )
    }
}
