package com.veltrix.admin.ui.audit

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
import com.veltrix.admin.data.api.AuditLog
import com.veltrix.admin.data.api.SecurityAlert
import com.veltrix.admin.data.repo.AdminRepository
import com.veltrix.admin.ui.common.*
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/** Common audit action prefixes offered as quick filters. */
val AUDIT_ACTIONS = listOf("auth.", "license.", "tenant.", "device.", "subscription.", "platform.")

@HiltViewModel
class AuditViewModel @Inject constructor(private val repo: AdminRepository) : ViewModel() {
    val showAlerts = MutableStateFlow(false)
    val action = MutableStateFlow<String?>(null)
    val search = MutableStateFlow("")
    private val pager = Paginator(pageSize = 40, key = AuditLog::id) { p, s -> repo.audit(p, s, action.value, null, search.value.ifBlank { null }) }
    private val _list = MutableStateFlow(PageState<AuditLog>())
    val list = _list.asStateFlow()
    private val _alerts = MutableStateFlow<Load<List<SecurityAlert>>>(Load.Loading)
    val alerts = _alerts.asStateFlow()
    private var job: Job? = null

    init { refresh() }

    fun refresh() = viewModelScope.launch {
        if (showAlerts.value) reload(_alerts.value, { _alerts.value = it }) { repo.securityAlerts(30) } else pager.refresh { _list.value = it }
    }
    fun loadMore() = viewModelScope.launch { pager.loadMore { _list.value = it } }
    fun setAlerts(v: Boolean) { showAlerts.value = v; refresh() }
    fun setAction(a: String?) { action.value = a; refresh() }
    fun setSearch(q: String) { search.value = q; job?.cancel(); job = viewModelScope.launch { delay(350); refresh() } }
}

@Composable
fun AuditScreen(vm: AuditViewModel = hiltViewModel()) {
    val alertsMode by vm.showAlerts.collectAsStateWithLifecycle()
    val action by vm.action.collectAsStateWithLifecycle()
    val search by vm.search.collectAsStateWithLifecycle()
    val toggle: androidx.compose.foundation.lazy.LazyListScope.() -> Unit = {
        item {
            Row(Modifier.padding(horizontal = 16.dp, vertical = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                FilterChip(!alertsMode, { vm.setAlerts(false) }, label = { Text(stringResource(R.string.audit_log)) })
                FilterChip(alertsMode, { vm.setAlerts(true) }, label = { Text(stringResource(R.string.audit_alerts)) })
            }
        }
    }
    if (alertsMode) {
        val alerts by vm.alerts.collectAsStateWithLifecycle()
        LoadScreen(alerts, onRefresh = { vm.refresh() }) { list ->
            toggle()
            if (list.isEmpty()) item { EmptyState() }
            list.forEachIndexed { i, a -> item(key = "a$i") { RowCard(a.kind, a.description, status = a.severity, meta = Formatters.dateTime(a.occurredAt)) } }
        }
    } else {
        val state by vm.list.collectAsStateWithLifecycle()
        PagedList(state, onRefresh = { vm.refresh() }, onLoadMore = { vm.loadMore() }, key = { it.id }, header = {
            toggle()
            item { SearchField(search, vm::setSearch) }
            item { FilterRow(listOf<Pair<String?, String>>(null to stringResource(R.string.filter_all)) + AUDIT_ACTIONS.map { it to it.trimEnd('.') }, action, vm::setAction) }
        }) { a ->
            RowCard(a.action, listOfNotNull(a.entityType.ifBlank { null }, a.entityId, a.details).joinToString(" · "),
                meta = listOfNotNull(Formatters.dateTime(a.occurredAt), a.ipAddress).joinToString(" · "))
        }
    }
}
