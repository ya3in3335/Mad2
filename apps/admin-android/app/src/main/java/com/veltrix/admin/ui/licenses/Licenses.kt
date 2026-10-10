package com.veltrix.admin.ui.licenses

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.os.PersistableBundle
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Add
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.SavedStateHandle
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import com.veltrix.admin.R
import com.veltrix.admin.data.api.*
import com.veltrix.admin.data.repo.AdminRepository
import com.veltrix.admin.ui.common.*
import dagger.hilt.android.lifecycle.HiltViewModel
import java.time.Instant
import javax.inject.Inject
import kotlinx.coroutines.Job
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

@Composable
fun problemText(p: LicenseForms.Problem): String = stringResource(when (p) {
    LicenseForms.Problem.NoTenant -> R.string.form_err_tenant
    LicenseForms.Problem.BadSeats -> R.string.form_err_seats
    LicenseForms.Problem.BadDays -> R.string.form_err_days
    LicenseForms.Problem.NoPlan -> R.string.form_err_plan
    LicenseForms.Problem.NoReason -> R.string.form_err_reason
})

@HiltViewModel
class LicensesViewModel @Inject constructor(private val repo: AdminRepository) : ViewModel() {
    val search = MutableStateFlow("")
    private val paginator = Paginator(key = License::id) { page, size -> repo.licenses(page, size, search.value.ifBlank { null }) }
    private val _state = MutableStateFlow(PageState<License>())
    val state = _state.asStateFlow()
    val action = MutableStateFlow<ActionState>(ActionState.Idle)
    val tenantResults = MutableStateFlow<List<TenantSummary>>(emptyList())
    val plans = MutableStateFlow<List<Plan>>(emptyList())
    /** Shown exactly once after issuance, then dropped from memory. */
    val issuedKey = MutableStateFlow<CreateLicenseResponse?>(null)
    private var job: Job? = null

    init { refresh() }

    fun refresh() = viewModelScope.launch { paginator.refresh { _state.value = it } }
    fun loadMore() = viewModelScope.launch { paginator.loadMore { _state.value = it } }
    fun setSearch(q: String) { search.value = q; job?.cancel(); job = viewModelScope.launch { delay(350); paginator.refresh { _state.value = it } } }

    fun prepareIssue() {
        action.value = ActionState.Idle
        viewModelScope.launch { plans.value = attempt { repo.plans() }.getOrDefault(emptyList()).filter { it.isActive } }
        searchTenants("")
    }
    fun searchTenants(q: String) = viewModelScope.launch {
        tenantResults.value = attempt { repo.tenants(1, 20, q.ifBlank { null }, null).items }.getOrDefault(emptyList())
    }

    fun issueDemo(tenantId: String, days: Int, seats: Int, notes: String?, onDone: () -> Unit) = issue(onDone) {
        repo.issueDemo(IssueDemoLicenseRequest(tenantId, days, seats, notes?.ifBlank { null }))
    }

    fun issueCommercial(tenantId: String, term: CommercialTerm, plan: String, seats: Int, notes: String?, onDone: () -> Unit) = issue(onDone) {
        val start = Instant.now()
        repo.issueCommercial(CreateLicenseRequest(tenantId, term.wireType, plan, seats, start.toString(), LicenseForms.expiry(term, start)?.toString(),
            null, null, notes?.ifBlank { null }))
    }

    private fun issue(onDone: () -> Unit, block: suspend () -> CreateLicenseResponse) = viewModelScope.launch {
        action.value = ActionState.Busy
        attempt { block() }.fold(
            { issuedKey.value = it; action.value = ActionState.Idle; onDone(); refresh() },
            { action.value = ActionState.Failed(it.toAppError()) },
        )
    }

    fun dismissKey() { issuedKey.value = null }
}

@Composable
fun LicensesScreen(onOpen: (String) -> Unit, vm: LicensesViewModel = hiltViewModel()) {
    val state by vm.state.collectAsStateWithLifecycle()
    val search by vm.search.collectAsStateWithLifecycle()
    val issued by vm.issuedKey.collectAsStateWithLifecycle()
    var issuing by remember { mutableStateOf(false) }

    Box(Modifier.fillMaxSize()) {
        PagedList(state, onRefresh = { vm.refresh() }, onLoadMore = { vm.loadMore() }, key = { it.id }, header = {
            item { SearchField(search, vm::setSearch) }
        }) { l ->
            RowCard("${l.tenantName ?: l.tenantId} · …${l.keyHint}", "${l.planCode} · ${l.type} · " + stringResource(R.string.license_seats_used, l.activeDevices, l.maxDevices),
                status = l.effectiveStatus, meta = l.expiresAt?.let { stringResource(R.string.license_expires, Formatters.date(it)) }
                    ?: stringResource(R.string.license_no_expiry), onClick = { onOpen(l.id) })
        }
        ExtendedFloatingActionButton(onClick = { vm.prepareIssue(); issuing = true }, icon = { Icon(Icons.Outlined.Add, null) },
            text = { Text(stringResource(R.string.license_issue)) }, modifier = Modifier.align(Alignment.BottomEnd).padding(16.dp))
    }
    if (issuing) IssueLicenseDialog(vm, onDismiss = { issuing = false })
    issued?.let { IssuedKeyDialog(it, onDismiss = vm::dismissKey) }
}

@Composable
private fun IssueLicenseDialog(vm: LicensesViewModel, onDismiss: () -> Unit) {
    val action by vm.action.collectAsStateWithLifecycle()
    val tenants by vm.tenantResults.collectAsStateWithLifecycle()
    val plans by vm.plans.collectAsStateWithLifecycle()
    var demo by remember { mutableStateOf(true) }
    var tenantQuery by remember { mutableStateOf("") }
    var tenantId by remember { mutableStateOf<String?>(null) }
    var days by remember { mutableStateOf("14") }
    var seats by remember { mutableStateOf("1") }
    var term by remember { mutableStateOf(CommercialTerm.Annual) }
    var plan by remember { mutableStateOf<String?>(null) }
    var notes by remember { mutableStateOf("") }
    val problem = if (demo) LicenseForms.validateDemo(tenantId, days, seats) else LicenseForms.validateCommercial(tenantId, plan, seats)

    FormDialog(stringResource(R.string.license_issue), action, onDismiss, confirmEnabled = problem == null, onConfirm = {
        if (demo) vm.issueDemo(tenantId!!, days.toInt(), seats.toInt(), notes, onDismiss)
        else vm.issueCommercial(tenantId!!, term, plan!!, seats.toInt(), notes, onDismiss)
    }, confirmLabel = stringResource(R.string.action_issue)) {
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            FilterChip(demo, { demo = true }, label = { Text(stringResource(R.string.license_demo)) })
            FilterChip(!demo, { demo = false }, label = { Text(stringResource(R.string.license_commercial)) })
        }
        OutlinedTextField(tenantQuery, { tenantQuery = it; vm.searchTenants(it) }, label = { Text(stringResource(R.string.field_tenant)) }, singleLine = true,
            modifier = Modifier.fillMaxWidth())
        tenants.take(6).forEach { t ->
            Row(Modifier.fillMaxWidth().selectable(tenantId == t.id, onClick = { tenantId = t.id }), verticalAlignment = Alignment.CenterVertically) {
                RadioButton(tenantId == t.id, { tenantId = t.id }); Text(t.name, style = MaterialTheme.typography.bodyMedium)
            }
        }
        if (demo) {
            OutlinedTextField(days, { days = it.filter(Char::isDigit).take(3) }, label = { Text(stringResource(R.string.field_days)) }, singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number), modifier = Modifier.fillMaxWidth())
        } else {
            Text(stringResource(R.string.field_term), style = MaterialTheme.typography.labelLarge)
            FlowRowChips(CommercialTerm.entries.map { it to it.wireType }, term) { term = it }
            Text(stringResource(R.string.field_plan), style = MaterialTheme.typography.labelLarge)
            if (plans.isEmpty()) Text(stringResource(R.string.state_empty), style = MaterialTheme.typography.bodySmall)
            FlowRowChips(plans.map { it.code to "${it.name} (${it.code})" }, plan) { plan = it }
        }
        OutlinedTextField(seats, { seats = it.filter(Char::isDigit).take(4) }, label = { Text(stringResource(R.string.field_seats)) }, singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number), modifier = Modifier.fillMaxWidth())
        OutlinedTextField(notes, { notes = it.take(500) }, label = { Text(stringResource(R.string.field_notes)) }, modifier = Modifier.fillMaxWidth())
        problem?.let { Text(problemText(it), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun <T> FlowRowChips(options: List<Pair<T, String>>, selected: T?, onSelect: (T) -> Unit) {
    FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        options.forEach { (v, label) -> FilterChip(v == selected, { onSelect(v) }, label = { Text(label) }) }
    }
}

@Composable
private fun IssuedKeyDialog(r: CreateLicenseResponse, onDismiss: () -> Unit) {
    val ctx = LocalContext.current
    AlertDialog(onDismissRequest = {}, title = { Text(stringResource(R.string.license_key_title)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(stringResource(R.string.license_key_once), color = MaterialTheme.colorScheme.tertiary)
                SelectionContainer { Text(r.licenseKey, fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.titleMedium) }
                Text("${r.license.tenantName.orEmpty()} · ${r.license.planCode} · ${r.license.type}", style = MaterialTheme.typography.bodySmall)
            }
        },
        confirmButton = { Button(onClick = onDismiss) { Text(stringResource(R.string.action_done)) } },
        dismissButton = { TextButton(onClick = { copySensitive(ctx, r.licenseKey) }) { Text(stringResource(R.string.action_copy)) } })
}

/** Same value as ClipDescription.EXTRA_IS_SENSITIVE (API 33); older versions simply ignore the extra. */
private const val EXTRA_IS_SENSITIVE = "android.content.extra.IS_SENSITIVE"

/** Copies a secret with the "sensitive" flag so Android 13+ hides it from the clipboard preview. */
fun copySensitive(ctx: Context, text: String) {
    val cm = ctx.getSystemService(ClipboardManager::class.java) ?: return
    val clip = ClipData.newPlainText("VELTRIX", text)
    clip.description.extras = PersistableBundle().apply { putBoolean(EXTRA_IS_SENSITIVE, true) }
    cm.setPrimaryClip(clip)
}

// ---------------------------------------------------------------- Detail

data class LicenseDetail(val license: License, val events: List<LicenseEvent>, val failures: List<ActivationFailure>?)

enum class LicenseAction { Renew, Seats, Suspend, Reactivate, Revoke }

@HiltViewModel
class LicenseDetailViewModel @Inject constructor(private val repo: AdminRepository, saved: SavedStateHandle) : ViewModel() {
    val id: String = checkNotNull(saved["id"])
    private val _state = MutableStateFlow<Load<LicenseDetail>>(Load.Loading)
    val state = _state.asStateFlow()
    val action = MutableStateFlow<ActionState>(ActionState.Idle)
    val messages = MutableSharedFlow<Int>(extraBufferCapacity = 4)

    init { refresh() }

    fun refresh() = viewModelScope.launch {
        reload(_state.value, { _state.value = it }) {
            coroutineScope {
                val l = async { repo.license(id) }
                val e = async { attempt { repo.licenseEvents(id) }.getOrDefault(emptyList()) }
                val f = async { attempt { repo.activationFailures(id).items }.getOrNull() }
                LicenseDetail(l.await(), e.await().sortedByDescending { Formatters.parseInstant(it.occurredAt) }, f.await())
            }
        }
    }

    fun resetAction() { action.value = ActionState.Idle }

    fun perform(a: LicenseAction, value: String, reason: String, password: String, onDone: () -> Unit) = viewModelScope.launch {
        action.value = ActionState.Busy
        attempt {
            when (a) {
                LicenseAction.Renew -> repo.renewLicense(id, value.toInt(), reason.ifBlank { null })
                LicenseAction.Seats -> repo.changeSeats(id, value.toInt(), reason)
                LicenseAction.Suspend -> repo.suspendLicense(id, reason, password)
                LicenseAction.Reactivate -> repo.reactivateLicense(id, reason, password)
                LicenseAction.Revoke -> repo.revokeLicense(id, reason, password)
            }
        }.fold(
            {
                action.value = ActionState.Idle
                messages.tryEmit(when (a) {
                    LicenseAction.Renew -> R.string.msg_license_renewed; LicenseAction.Seats -> R.string.msg_license_seats
                    LicenseAction.Suspend -> R.string.msg_license_suspended; LicenseAction.Reactivate -> R.string.msg_license_reactivated
                    LicenseAction.Revoke -> R.string.msg_license_revoked
                })
                onDone(); refresh()
            },
            { action.value = ActionState.Failed(it.toAppError()) },
        )
    }
}

@Composable
fun LicenseDetailScreen(vm: LicenseDetailViewModel = hiltViewModel()) {
    val state by vm.state.collectAsStateWithLifecycle()
    val action by vm.action.collectAsStateWithLifecycle()
    var dialog by remember { mutableStateOf<LicenseAction?>(null) }
    CollectMessages(vm.messages)

    LoadScreen(state, onRefresh = { vm.refresh() }) { d ->
        val l = d.license
        item {
            Column(Modifier.padding(16.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("…${l.keyHint}", style = MaterialTheme.typography.headlineSmall, fontFamily = FontFamily.Monospace, modifier = Modifier.weight(1f))
                    StatusChip(l.effectiveStatus)
                }
                Text(l.tenantName ?: l.tenantId, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        item {
            InfoRow(stringResource(R.string.field_type), l.type)
            InfoRow(stringResource(R.string.field_plan), l.planCode)
            InfoRow(stringResource(R.string.field_seats), stringResource(R.string.license_seats_used, l.activeDevices, l.maxDevices))
            InfoRow(stringResource(R.string.field_starts), Formatters.date(l.startsAt))
            InfoRow(stringResource(R.string.field_expires), l.expiresAt?.let { Formatters.date(it) } ?: stringResource(R.string.license_no_expiry))
            InfoRow(stringResource(R.string.field_grace), stringResource(R.string.days_count, l.offlineGraceDays))
            InfoRow(stringResource(R.string.field_entitlements), l.entitlements.joinToString(", "))
            InfoRow(stringResource(R.string.field_notes), l.notes)
        }
        item {
            val revoked = l.status.equals("Revoked", true) || l.status.equals("Transferred", true)
            if (!revoked) {
                ActionButtonsRow {
                    Button(onClick = { vm.resetAction(); dialog = LicenseAction.Renew }, Modifier.weight(1f)) { Text(stringResource(R.string.action_renew)) }
                    OutlinedButton(onClick = { vm.resetAction(); dialog = LicenseAction.Seats }, Modifier.weight(1f)) { Text(stringResource(R.string.action_seats)) }
                }
                ActionButtonsRow {
                    if (l.status.equals("Suspended", true))
                        OutlinedButton(onClick = { vm.resetAction(); dialog = LicenseAction.Reactivate }, Modifier.weight(1f)) { Text(stringResource(R.string.action_reactivate)) }
                    else OutlinedButton(onClick = { vm.resetAction(); dialog = LicenseAction.Suspend }, Modifier.weight(1f)) { Text(stringResource(R.string.action_suspend)) }
                    OutlinedButton(onClick = { vm.resetAction(); dialog = LicenseAction.Revoke }, Modifier.weight(1f),
                        colors = ButtonDefaults.outlinedButtonColors(contentColor = MaterialTheme.colorScheme.error)) { Text(stringResource(R.string.action_revoke)) }
                }
            }
        }
        item { SectionHeader(stringResource(R.string.license_history)) }
        if (d.events.isEmpty()) item { EmptyState() }
        d.events.forEach { e -> item(key = "e" + e.id) { RowCard(e.type, e.details, meta = Formatters.dateTime(e.occurredAt)) } }
        item { SectionHeader(stringResource(R.string.license_failures)) }
        when {
            d.failures == null -> item { EmptyState(stringResource(R.string.err_not_supported)) }
            d.failures.isEmpty() -> item { EmptyState() }
            else -> d.failures.forEach { f ->
                item(key = "f" + f.id) {
                    RowCard(f.code, listOfNotNull(f.platform, f.ipAddress).joinToString(" · "), status = "failed", meta = Formatters.dateTime(f.occurredAt))
                }
            }
        }
    }

    val current = (state as? Load.Ok)?.value?.license
    when (val a = dialog) {
        null -> Unit
        LicenseAction.Suspend, LicenseAction.Revoke, LicenseAction.Reactivate -> StepUpDialog(
            title = stringResource(when (a) { LicenseAction.Suspend -> R.string.license_suspend_title; LicenseAction.Revoke -> R.string.license_revoke_title
                else -> R.string.license_reactivate_title }),
            message = stringResource(when (a) { LicenseAction.Suspend -> R.string.license_suspend_body; LicenseAction.Revoke -> R.string.license_revoke_body
                else -> R.string.license_reactivate_body }),
            action = action, destructive = a != LicenseAction.Reactivate, onDismiss = { dialog = null },
            onConfirm = { pw, reason -> vm.perform(a, "", reason, pw) { dialog = null } },
        )
        LicenseAction.Renew, LicenseAction.Seats -> {
            var value by remember(a) { mutableStateOf(if (a == LicenseAction.Renew) "365" else (current?.maxDevices ?: 1).toString()) }
            var reason by remember(a) { mutableStateOf("") }
            val problem = if (a == LicenseAction.Renew) LicenseForms.validateRenew(value) else LicenseForms.validateSeats(value, reason)
            FormDialog(stringResource(if (a == LicenseAction.Renew) R.string.license_renew_title else R.string.license_seats_title), action,
                onDismiss = { dialog = null }, confirmEnabled = problem == null, onConfirm = { vm.perform(a, value, reason, "") { dialog = null } }) {
                OutlinedTextField(value, { value = it.filter(Char::isDigit).take(4) },
                    label = { Text(stringResource(if (a == LicenseAction.Renew) R.string.field_days else R.string.field_seats)) }, singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number), modifier = Modifier.fillMaxWidth())
                OutlinedTextField(reason, { reason = it.take(500) }, label = { Text(stringResource(R.string.field_reason)) }, modifier = Modifier.fillMaxWidth())
                problem?.let { Text(problemText(it), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            }
        }
    }
}
