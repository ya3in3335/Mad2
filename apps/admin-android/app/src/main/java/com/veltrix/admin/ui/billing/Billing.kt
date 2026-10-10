package com.veltrix.admin.ui.billing

import androidx.compose.material3.ExperimentalMaterial3Api
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
import com.veltrix.admin.data.api.*
import com.veltrix.admin.data.repo.AdminRepository
import com.veltrix.admin.ui.common.*
import com.veltrix.admin.ui.licenses.FlowRowChips
import dagger.hilt.android.lifecycle.HiltViewModel
import java.time.Instant
import java.util.UUID
import javax.inject.Inject
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

enum class BillingTab(val label: Int) { Pending(R.string.billing_pending), Subscriptions(R.string.billing_subscriptions), Plans(R.string.billing_plans), Revenue(R.string.billing_revenue) }

/** Manual payment methods accepted for vendor invoices (Algeria). */
val PAYMENT_METHODS = listOf("Transfer", "CCP", "BaridiMob", "Cash", "Cheque")

data class RevenueData(val dashboard: PlatformDashboard?, val paid: List<BillingInvoice>)

@HiltViewModel
class BillingViewModel @Inject constructor(private val repo: AdminRepository) : ViewModel() {
    val tab = MutableStateFlow(BillingTab.Pending)
    val subStatus = MutableStateFlow<String?>(null)
    private val pendingPager = Paginator(key = BillingInvoice::id) { p, s -> repo.invoices(p, s, "Issued") }
    private val subsPager = Paginator(key = Subscription::id) { p, s -> repo.subscriptions(p, s, subStatus.value) }
    private val _pending = MutableStateFlow(PageState<BillingInvoice>())
    val pending = _pending.asStateFlow()
    private val _subs = MutableStateFlow(PageState<Subscription>())
    val subs = _subs.asStateFlow()
    private val _plans = MutableStateFlow<Load<List<Plan>>>(Load.Loading)
    val plans = _plans.asStateFlow()
    private val _revenue = MutableStateFlow<Load<RevenueData>>(Load.Loading)
    val revenue = _revenue.asStateFlow()
    val action = MutableStateFlow<ActionState>(ActionState.Idle)
    val messages = MutableSharedFlow<Int>(extraBufferCapacity = 4)

    init { refreshPending() }

    fun select(t: BillingTab) {
        tab.value = t
        when (t) {
            BillingTab.Pending -> if (_pending.value.page == 0) refreshPending()
            BillingTab.Subscriptions -> if (_subs.value.page == 0) refreshSubs()
            BillingTab.Plans -> if (_plans.value !is Load.Ok) refreshPlans()
            BillingTab.Revenue -> if (_revenue.value !is Load.Ok) refreshRevenue()
        }
    }

    fun refreshPending() = viewModelScope.launch { pendingPager.refresh { _pending.value = it } }
    fun morePending() = viewModelScope.launch { pendingPager.loadMore { _pending.value = it } }
    fun refreshSubs() = viewModelScope.launch { subsPager.refresh { _subs.value = it } }
    fun moreSubs() = viewModelScope.launch { subsPager.loadMore { _subs.value = it } }
    fun setSubStatus(s: String?) { subStatus.value = s; refreshSubs() }
    fun refreshPlans() = viewModelScope.launch { reload(_plans.value, { _plans.value = it }) { repo.plans() } }
    fun refreshRevenue() = viewModelScope.launch {
        reload(_revenue.value, { _revenue.value = it }) {
            coroutineScope {
                val d = async { attempt { repo.platformDashboard() }.getOrNull() }
                val paid = async { repo.invoices(1, 20, "Paid").items }
                RevenueData(d.await(), paid.await())
            }
        }
    }

    fun resetAction() { action.value = ActionState.Idle }

    /** The idempotency key is created once per dialog, so retrying after a timeout cannot double-confirm. */
    fun confirm(invoice: BillingInvoice, method: String, reference: String, password: String, idempotencyKey: String, onDone: () -> Unit) = viewModelScope.launch {
        action.value = ActionState.Busy
        attempt { repo.confirmPayment(invoice.id, ConfirmPaymentRequest(method, reference.ifBlank { null }, Instant.now().toString()), password, idempotencyKey) }.fold(
            { action.value = ActionState.Idle; messages.tryEmit(R.string.msg_payment_confirmed); onDone(); refreshPending() },
            { action.value = ActionState.Failed(it.toAppError()) },
        )
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun BillingScreen(vm: BillingViewModel = hiltViewModel()) {
    val tab by vm.tab.collectAsStateWithLifecycle()
    CollectMessages(vm.messages)
    Column(Modifier.fillMaxSize()) {
        PrimaryScrollableTabRow(selectedTabIndex = tab.ordinal, edgePadding = 8.dp, containerColor = MaterialTheme.colorScheme.background) {
            BillingTab.entries.forEach { t -> Tab(t == tab, onClick = { vm.select(t) }, text = { Text(stringResource(t.label)) }) }
        }
        when (tab) {
            BillingTab.Pending -> PendingTab(vm)
            BillingTab.Subscriptions -> SubscriptionsTab(vm)
            BillingTab.Plans -> PlansTab(vm)
            BillingTab.Revenue -> RevenueTab(vm)
        }
    }
}

@Composable
private fun PendingTab(vm: BillingViewModel) {
    val state by vm.pending.collectAsStateWithLifecycle()
    val action by vm.action.collectAsStateWithLifecycle()
    var selected by remember { mutableStateOf<BillingInvoice?>(null) }
    PagedList(state, onRefresh = { vm.refreshPending() }, onLoadMore = { vm.morePending() }, key = { it.id }) { i ->
        RowCard("${i.tenantName ?: i.tenantId} · ${i.number}", Formatters.money(i.total, i.currency) + (i.dueDate?.let { " · " + stringResource(R.string.invoice_due, Formatters.date(it)) } ?: ""),
            status = i.status, meta = i.description, onClick = { vm.resetAction(); selected = i })
    }
    selected?.let { inv ->
        var method by remember(inv.id) { mutableStateOf(PAYMENT_METHODS.first()) }
        var reference by remember(inv.id) { mutableStateOf("") }
        var step by remember(inv.id) { mutableStateOf(false) }
        val key = remember(inv.id) { UUID.randomUUID().toString() }
        if (!step) {
            FormDialog(stringResource(R.string.payment_confirm_title), ActionState.Idle, onDismiss = { selected = null }, confirmEnabled = true,
                onConfirm = { step = true }, confirmLabel = stringResource(R.string.action_continue)) {
                Text("${inv.tenantName.orEmpty()} · ${inv.number}", style = MaterialTheme.typography.titleSmall)
                Text(Formatters.money(inv.total, inv.currency), style = MaterialTheme.typography.headlineSmall, color = MaterialTheme.colorScheme.secondary)
                Text(stringResource(R.string.field_method), style = MaterialTheme.typography.labelLarge)
                FlowRowChips(PAYMENT_METHODS.map { it to it }, method) { method = it }
                OutlinedTextField(reference, { reference = it.take(120) }, label = { Text(stringResource(R.string.field_reference)) }, singleLine = true,
                    modifier = Modifier.fillMaxWidth())
            }
        } else {
            StepUpDialog(stringResource(R.string.payment_confirm_title), stringResource(R.string.payment_confirm_body, Formatters.money(inv.total, inv.currency)),
                action, onDismiss = { selected = null }, requireReason = false, destructive = false,
                onConfirm = { pw, _ -> vm.confirm(inv, method, reference, pw, key) { selected = null } })
        }
    }
}

@Composable
private fun SubscriptionsTab(vm: BillingViewModel) {
    val state by vm.subs.collectAsStateWithLifecycle()
    val status by vm.subStatus.collectAsStateWithLifecycle()
    val filters = listOf(null to stringResource(R.string.filter_all), "Active" to stringResource(R.string.st_active), "Trialing" to stringResource(R.string.st_trialing),
        "PastDue" to stringResource(R.string.st_past_due), "Expired" to stringResource(R.string.st_expired), "Cancelled" to stringResource(R.string.st_cancelled))
    PagedList(state, onRefresh = { vm.refreshSubs() }, onLoadMore = { vm.moreSubs() }, key = { it.id }, header = {
        item { Spacer(Modifier.height(8.dp)); FilterRow(filters, status, vm::setSubStatus) }
    }) { s ->
        RowCard("${s.tenantName ?: s.tenantId} · ${s.planCode}", stringResource(R.string.sub_period, Formatters.date(s.currentPeriodStart), Formatters.date(s.currentPeriodEnd)),
            status = s.status, meta = Formatters.money(s.periodPrice, s.currency) + " · " + s.billingCycle + " · " + stringResource(R.string.sub_seats, s.seats))
    }
}

@Composable
private fun PlansTab(vm: BillingViewModel) {
    val state by vm.plans.collectAsStateWithLifecycle()
    LoadScreen(state, onRefresh = { vm.refreshPlans() }) { plans ->
        if (plans.isEmpty()) item { EmptyState() }
        plans.forEach { p ->
            item(key = p.id) {
                RowCard("${p.name} (${p.code})", stringResource(R.string.plan_prices, Formatters.money(p.monthlyPrice, p.currency), Formatters.money(p.annualPrice, p.currency)),
                    status = if (p.isActive) "active" else "void",
                    meta = stringResource(R.string.plan_limits, p.maxDevices, p.maxUsers, p.maxBranches) + if (p.trialDays > 0) " · " + stringResource(R.string.plan_trial, p.trialDays) else "")
            }
        }
    }
}

@Composable
private fun RevenueTab(vm: BillingViewModel) {
    val state by vm.revenue.collectAsStateWithLifecycle()
    LoadScreen(state, onRefresh = { vm.refreshRevenue() }) { r ->
        r.dashboard?.let { d ->
            item {
                Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    KpiCard(stringResource(R.string.kpi_mrr), Formatters.money(d.monthlyRecurringRevenue), Modifier.weight(1f))
                    KpiCard(stringResource(R.string.revenue_this_month), Formatters.money(d.revenueThisMonth), Modifier.weight(1f))
                }
            }
            if (d.revenueByMonth.isNotEmpty()) {
                item { SectionHeader(stringResource(R.string.dash_revenue)) }
                item { Box(Modifier.padding(16.dp)) { BarChart(d.revenueByMonth.takeLast(12).map { it.month to it.value }, description = stringResource(R.string.dash_revenue)) } }
            }
        }
        item { SectionHeader(stringResource(R.string.revenue_recent_payments)) }
        if (r.paid.isEmpty()) item { EmptyState() }
        r.paid.forEach { i ->
            item(key = i.id) { RowCard("${i.tenantName ?: i.tenantId} · ${i.number}", Formatters.money(i.total, i.currency) + (i.paymentMethod?.let { " · $it" } ?: ""),
                status = i.status, meta = Formatters.dateTime(i.paidAt)) }
        }
    }
}
