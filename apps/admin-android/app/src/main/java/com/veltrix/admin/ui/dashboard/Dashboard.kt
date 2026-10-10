package com.veltrix.admin.ui.dashboard

import androidx.annotation.StringRes
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
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
import com.veltrix.admin.ui.nav.Dest
import com.veltrix.admin.ui.theme.Brand
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

data class DashboardData(
    val owner: OwnerDashboard?,
    val platform: PlatformDashboard?,
    val health: SystemHealth?,
    val releases: List<AppRelease>,
    val currency: String = "DZD",
)

enum class KpiKind { Count, Money, Text }

enum class KpiId(@StringRes val label: Int, val dest: Dest?) {
    Tenants(R.string.kpi_tenants, Dest.Tenants),
    Customers(R.string.kpi_customers, null),
    SubsActive(R.string.kpi_subs_active, Dest.Billing),
    SubsExpired(R.string.kpi_subs_expired, Dest.Billing),
    LicensesActive(R.string.kpi_licenses_active, Dest.Licenses),
    LicensesSuspended(R.string.kpi_licenses_suspended, Dest.Licenses),
    Devices(R.string.kpi_devices, Dest.Devices),
    Online(R.string.kpi_online, Dest.Devices),
    AndroidInstalls(R.string.kpi_android, Dest.Devices),
    WindowsInstalls(R.string.kpi_windows, Dest.Devices),
    Mrr(R.string.kpi_mrr, Dest.Billing),
    PendingPayments(R.string.kpi_pending_payments, Dest.Billing),
    NewTickets(R.string.kpi_new_tickets, Dest.Support),
    UnresolvedErrors(R.string.kpi_unresolved_errors, Dest.Diagnostics),
    FailedSyncs(R.string.kpi_failed_syncs, Dest.Diagnostics),
    Health(R.string.kpi_health, null),
    ExpiringLicenses(R.string.kpi_expiring_licenses, Dest.Licenses),
    SecurityAlerts(R.string.kpi_security_alerts, Dest.Audit),
}

data class Kpi(val id: KpiId, val kind: KpiKind, val number: Double = 0.0, val text: String? = null, val tone: Tone = Tone.Info)

/** Maps the owner dashboard (preferred) or the older platform dashboard (fallback) to KPI cards. Pure for testing. */
object DashboardMapper {
    private fun count(id: KpiId, n: Int, warnAbove: Int? = null, badAbove: Int? = null) = Kpi(id, KpiKind.Count, n.toDouble(), tone = when {
        badAbove != null && n > badAbove -> Tone.Negative
        warnAbove != null && n > warnAbove -> Tone.Warning
        else -> Tone.Info
    })

    fun kpis(d: DashboardData): List<Kpi> {
        val o = d.owner
        val p = d.platform
        val health = o?.backendHealth ?: d.health?.status
        val out = mutableListOf<Kpi>()
        if (o != null) {
            out += count(KpiId.Tenants, o.totalTenants)
            out += count(KpiId.Customers, o.totalCustomers)
            out += count(KpiId.SubsActive, o.activeSubscriptions)
            out += count(KpiId.SubsExpired, o.expiredSubscriptions, warnAbove = 0)
            out += count(KpiId.LicensesActive, o.activeLicenses)
            out += count(KpiId.LicensesSuspended, o.suspendedLicenses, warnAbove = 0)
            out += count(KpiId.Devices, o.registeredDevices)
            out += count(KpiId.Online, o.onlineDevices)
            out += count(KpiId.AndroidInstalls, o.androidInstallations)
            out += count(KpiId.WindowsInstalls, o.windowsInstallations)
            out += Kpi(KpiId.Mrr, KpiKind.Money, o.monthlyRecurringRevenue)
            out += count(KpiId.PendingPayments, o.pendingManualPayments, warnAbove = 0)
            out += count(KpiId.NewTickets, o.newSupportTickets, warnAbove = 0)
            out += count(KpiId.UnresolvedErrors, o.unresolvedErrors, warnAbove = 0, badAbove = if (o.criticalUnresolvedErrors > 0) 0 else null)
            out += count(KpiId.FailedSyncs, o.failedSyncOperations24h + o.deviceReportedFailedSyncOperations, warnAbove = 0)
        } else if (p != null) {
            out += count(KpiId.Tenants, p.tenants)
            out += count(KpiId.LicensesActive, p.activeLicenses)
            out += count(KpiId.ExpiringLicenses, p.expiringLicenses14d, warnAbove = 0)
            out += count(KpiId.Devices, p.activeDevices)
            out += Kpi(KpiId.Mrr, KpiKind.Money, p.monthlyRecurringRevenue)
            out += count(KpiId.NewTickets, p.openSupportCases, warnAbove = 0)
            out += count(KpiId.SecurityAlerts, p.securityAlerts7d, warnAbove = 0)
        }
        if (health != null) out += Kpi(KpiId.Health, KpiKind.Text, text = health, tone = StatusTone.of(health))
        return out
    }

    /** Latest release per platform/channel, newest first. */
    fun latestReleases(d: DashboardData): List<AppRelease> {
        val source = d.owner?.latestReleases?.takeIf { it.isNotEmpty() } ?: d.releases
        return source.filter { it.isActive }.sortedByDescending { Formatters.parseInstant(it.publishedAt) }
            .distinctBy { it.platform.lowercase() + "/" + it.channel.lowercase() }
    }
}

@HiltViewModel
class DashboardViewModel @Inject constructor(private val repo: AdminRepository) : ViewModel() {
    private val _state = MutableStateFlow<Load<DashboardData>>(Load.Loading)
    val state = _state.asStateFlow()

    init { refresh() }

    fun refresh() = viewModelScope.launch {
        reload(_state.value, { _state.value = it }) {
            coroutineScope {
                val owner = async { attempt { repo.ownerDashboard() } }
                val platform = async { attempt { repo.platformDashboard() } }
                val health = async { attempt { repo.health() } }
                val releases = async { attempt { repo.releases() } }
                val o = owner.await(); val p = platform.await()
                // Both dashboards failing is an error; one missing (older/newer server) is fine.
                if (o.isFailure && p.isFailure) throw (o.exceptionOrNull() ?: p.exceptionOrNull()!!)
                DashboardData(o.getOrNull(), p.getOrNull(), health.await().getOrNull(), releases.await().getOrDefault(emptyList()))
            }
        }
    }
}

@Composable
fun DashboardScreen(onOpen: (Dest) -> Unit, vm: DashboardViewModel = hiltViewModel()) {
    val state by vm.state.collectAsStateWithLifecycle()
    LoadScreen(state, onRefresh = { vm.refresh() }) { data ->
        val kpis = DashboardMapper.kpis(data)
        item { SectionHeader(stringResource(R.string.dash_overview)) }
        kpis.chunked(2).forEach { pair ->
            item {
                Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    pair.forEach { k -> KpiTile(k, Modifier.weight(1f).let { m -> k.id.dest?.let { d -> m.clickable { onOpen(d) } } ?: m }) }
                    if (pair.size == 1) Spacer(Modifier.weight(1f))
                }
            }
        }
        data.platform?.revenueByMonth?.takeIf { it.isNotEmpty() }?.let { rev ->
            item { SectionHeader(stringResource(R.string.dash_revenue)) }
            item {
                ChartCard { BarChart(rev.takeLast(12).map { it.month to it.value }, description = stringResource(R.string.dash_revenue)) }
            }
        }
        data.platform?.activationsByMonth?.takeIf { it.size >= 2 }?.let { act ->
            item { SectionHeader(stringResource(R.string.dash_activations)) }
            item { ChartCard { LineChart(act.takeLast(12).map { it.value }, description = stringResource(R.string.dash_activations)) } }
        }
        data.owner?.let { o ->
            item { SectionHeader(stringResource(R.string.dash_installs)) }
            item {
                ChartCard {
                    DonutChart(listOf(stringResource(R.string.platform_android) to o.androidInstallations.toDouble(),
                        stringResource(R.string.platform_windows) to o.windowsInstallations.toDouble()),
                        centerText = Formatters.compact((o.androidInstallations + o.windowsInstallations).toDouble()))
                }
            }
            item { SectionHeader(stringResource(R.string.dash_licenses)) }
            item {
                ChartCard {
                    DonutChart(listOf(stringResource(R.string.st_active) to o.activeLicenses.toDouble(), stringResource(R.string.st_suspended) to o.suspendedLicenses.toDouble(),
                        stringResource(R.string.st_expiring) to o.expiringLicenses14d.toDouble()),
                        colors = listOf(Brand.tone(Tone.Positive), Brand.tone(Tone.Negative), Brand.tone(Tone.Warning)))
                }
            }
        }
        data.health?.let { h ->
            item { SectionHeader(stringResource(R.string.dash_health)) }
            item {
                ChartCard {
                    Row { Text(stringResource(R.string.dash_backend) + " " + h.version, Modifier.weight(1f)); StatusChip(h.status) }
                    h.workers.forEach { w ->
                        Row(Modifier.padding(top = 6.dp)) {
                            Text(w.name, Modifier.weight(1f), style = MaterialTheme.typography.bodySmall)
                            StatusChip(if (w.stale) "degraded" else w.lastStatus)
                        }
                    }
                }
            }
        }
        val releases = DashboardMapper.latestReleases(data)
        if (releases.isNotEmpty()) {
            item { SectionHeader(stringResource(R.string.dash_latest_releases)) }
            releases.forEach { r ->
                item(key = "rel-" + r.id) {
                    RowCard("${stringResource(platformLabel(r.platform))} ${r.version}", r.channel, meta = Formatters.date(r.publishedAt))
                }
            }
        }
        data.owner?.generatedAt?.let { g -> item { Text(stringResource(R.string.dash_generated, Formatters.dateTime(g)), Modifier.padding(16.dp),
            style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant) } }
    }
}

@Composable
private fun KpiTile(k: Kpi, modifier: Modifier) {
    val value = when (k.kind) {
        KpiKind.Count -> Formatters.number(k.number.toLong())
        KpiKind.Money -> Formatters.money(k.number)
        KpiKind.Text -> statusLabel(k.text)
    }
    KpiCard(stringResource(k.id.label), value, modifier, accent = Brand.tone(k.tone))
}

@Composable
private fun ChartCard(content: @Composable ColumnScope.() -> Unit) {
    Card(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp), shape = RoundedCornerShape(16.dp),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
        Column(Modifier.padding(16.dp), content = content)
    }
}
