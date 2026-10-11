package com.veltrix.os.ui.screens

import androidx.appcompat.app.AppCompatDelegate
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.horizontalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.core.os.LocaleListCompat
import androidx.fragment.app.FragmentActivity
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.veltrix.os.BuildConfig
import com.veltrix.os.R
import com.veltrix.os.data.api.*
import com.veltrix.os.data.db.VeltrixDatabase
import com.veltrix.os.data.repo.*
import com.veltrix.os.data.security.LicenseState
import com.veltrix.os.ui.common.*
import com.veltrix.os.ui.splash.BrandEmblem
import dagger.hilt.android.lifecycle.HiltViewModel
import java.time.LocalDate
import java.util.UUID
import javax.inject.Inject
import kotlinx.coroutines.launch
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull

@HiltViewModel
class ScreensViewModel @Inject constructor(
    val remote: RemoteRepository, val db: VeltrixDatabase, val settings: AppSettings, val session: SessionRepository, val license: LicenseRepository,
    val sync: SyncRepository, val local: StandaloneRepository,
) : ViewModel() {
    /** Offline license: every screen reads/writes the local database; server-only features are hidden. */
    val standalone: Boolean get() = license.standalone
}

@Composable
private fun <T> rememberLoad(key: Any = Unit, block: suspend () -> T): Pair<Load<T>, () -> Unit> {
    var nonce by remember { mutableIntStateOf(0) }
    val state by produceState<Load<T>>(Load.Loading, key, nonce) { value = Load.Loading; value = load(block) }
    return state to { nonce++ }
}

// ---------------------------------------------------------------- Dashboard
@Composable
fun DashboardScreen(vm: ScreensViewModel = hiltViewModel()) {
    val (state, retry) = rememberLoad { if (vm.standalone) vm.local.dashboard() else vm.remote.dashboard() }
    val localToday by vm.db.sales().totalSince(LocalDate.now().atStartOfDay(java.time.ZoneId.systemDefault()).toInstant().toEpochMilli()).collectAsState(0.0)
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(12.dp)) {
        item {
            LoadView(state, retry) { d ->
                Column {
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        KpiCard(stringResource(R.string.rep_salesToday), money(d.salesToday), Modifier.weight(1f))
                        KpiCard(stringResource(R.string.rep_transactions), d.transactionsToday.toString(), Modifier.weight(1f))
                    }
                    Spacer(Modifier.height(8.dp))
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        KpiCard(stringResource(R.string.rep_salesMonth), money(d.salesMonth), Modifier.weight(1f))
                        KpiCard(stringResource(R.string.rep_margin), "${d.grossMarginMonth}%", Modifier.weight(1f))
                    }
                    Spacer(Modifier.height(8.dp))
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        KpiCard(stringResource(R.string.inv_lowStock), d.lowStockItems.toString(), Modifier.weight(1f), MaterialTheme.colorScheme.error)
                        KpiCard(stringResource(R.string.rep_receivables), money(d.receivablesOutstanding), Modifier.weight(1f))
                    }
                    SectionTitle(stringResource(R.string.rep_salesMonth))
                    Sparkline(d.last30Days.map { it.value })
                    SectionTitle(stringResource(R.string.rep_topProducts))
                    d.topProducts.forEach { ListItem(headlineContent = { Text(it.name) }, trailingContent = { Text(money(it.value)) }) }
                }
            }
        }
        if (!vm.standalone) item {
            SectionTitle(stringResource(R.string.app_offline) + " / " + stringResource(R.string.pos_cart))
            KpiCard(stringResource(R.string.rep_salesToday) + " (local)", money(localToday), Modifier.fillMaxWidth())
        }
    }
}

/** Real data only: draws the 30-day series returned by the API. */
@Composable
fun Sparkline(values: List<Double>) {
    val color = MaterialTheme.colorScheme.secondary
    Canvas(Modifier.fillMaxWidth().height(120.dp).padding(horizontal = 16.dp)) {
        if (values.size < 2) return@Canvas
        val max = (values.maxOrNull() ?: 0.0).coerceAtLeast(1.0)
        val step = size.width / (values.size - 1)
        val path = Path()
        values.forEachIndexed { i, v ->
            val p = Offset(i * step, size.height - (v / max * size.height).toFloat())
            if (i == 0) path.moveTo(p.x, p.y) else path.lineTo(p.x, p.y)
        }
        drawPath(path, color, style = Stroke(width = 3.dp.toPx()))
    }
}

// ---------------------------------------------------------------- Catalog
@Composable
fun CatalogScreen(vm: ScreensViewModel = hiltViewModel()) {
    var q by remember { mutableStateOf("") }
    val products by (if (vm.standalone) vm.local.products(q) else vm.db.catalog().searchProducts(q)).collectAsState(emptyList())
    var editing by remember { mutableStateOf<com.veltrix.os.data.db.ProductEntity?>(null) }
    var adding by remember { mutableStateOf(false) }
    Scaffold(floatingActionButton = {
        // Standalone: the catalog is managed on this device (online it comes from the server).
        if (vm.standalone) FloatingActionButton(onClick = { adding = true }) { Icon(Icons.Outlined.Add, stringResource(R.string.action_add)) }
    }) { pad ->
        Column(Modifier.fillMaxSize().padding(pad)) {
            OutlinedTextField(q, { q = it }, Modifier.fillMaxWidth().padding(12.dp), placeholder = { Text(stringResource(R.string.nav_search)) },
                leadingIcon = { Icon(Icons.Outlined.Search, null) }, singleLine = true)
            if (products.isEmpty()) EmptyState(stringResource(R.string.state_empty))
            LazyColumn {
                items(products, key = { it.id }) { p ->
                    ListItem(headlineContent = { Text(p.name) }, supportingContent = { Text("${p.sku}${p.nameAr?.let { " · $it" } ?: ""}") },
                        trailingContent = { Text(money(p.salePrice), fontWeight = FontWeight.SemiBold) },
                        modifier = if (vm.standalone) Modifier.clickable { editing = p } else Modifier)
                    HorizontalDivider()
                }
            }
        }
    }
    if (adding || editing != null) ProductDialog(vm, editing) { adding = false; editing = null }
}

@Composable
private fun ProductDialog(vm: ScreensViewModel, original: com.veltrix.os.data.db.ProductEntity?, onClose: () -> Unit) {
    fun num(d: Double) = if (d % 1.0 == 0.0) d.toLong().toString() else d.toString()
    var sku by remember { mutableStateOf(original?.sku ?: "") }
    var name by remember { mutableStateOf(original?.name ?: "") }
    var price by remember { mutableStateOf(original?.salePrice?.let(::num) ?: "") }
    var cost by remember { mutableStateOf(original?.costPrice?.let(::num) ?: "0") }
    var tax by remember { mutableStateOf(original?.taxRatePercent?.let(::num) ?: "0") }
    var low by remember { mutableStateOf(original?.lowStockThreshold?.let(::num) ?: "0") }
    var barcodes by remember { mutableStateOf(original?.barcodes ?: "") }
    var track by remember { mutableStateOf(original?.trackStock ?: true) }
    var error by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()
    fun dec(s: String) = s.filter { c -> c.isDigit() || c == '.' }
    AlertDialog(onDismissRequest = onClose, title = { Text(stringResource(R.string.nav_products)) }, text = {
        Column(Modifier.verticalScroll(rememberScrollState())) {
            OutlinedTextField(sku, { sku = it }, label = { Text("SKU") }, singleLine = true)
            OutlinedTextField(name, { name = it }, label = { Text(stringResource(R.string.cat_name)) }, singleLine = true)
            OutlinedTextField(price, { price = dec(it) }, label = { Text(stringResource(R.string.sa_salePrice)) }, singleLine = true)
            OutlinedTextField(cost, { cost = dec(it) }, label = { Text(stringResource(R.string.sa_costPrice)) }, singleLine = true)
            OutlinedTextField(tax, { tax = dec(it) }, label = { Text(stringResource(R.string.sa_taxRate)) }, singleLine = true)
            OutlinedTextField(low, { low = dec(it) }, label = { Text(stringResource(R.string.inv_lowStock)) }, singleLine = true)
            OutlinedTextField(barcodes, { barcodes = it }, label = { Text(stringResource(R.string.sa_barcodes)) }, singleLine = true)
            Row(verticalAlignment = Alignment.CenterVertically) { Checkbox(track, { track = it }); Text(stringResource(R.string.sa_trackStock)) }
            error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
        }
    }, confirmButton = {
        TextButton(enabled = sku.isNotBlank() && name.isNotBlank() && price.toDoubleOrNull() != null, onClick = {
            scope.launch {
                runCatching { vm.local.saveProduct(original?.id, sku, name, price.toDouble(), cost.toDoubleOrNull() ?: 0.0, tax.toDoubleOrNull() ?: 0.0, track,
                    low.toDoubleOrNull() ?: 0.0, barcodes) }.onSuccess { onClose() }.onFailure { error = it.message }
            }
        }) { Text(stringResource(R.string.action_save)) }
    }, dismissButton = { TextButton(onClick = onClose) { Text(stringResource(R.string.action_cancel)) } })
}

// ---------------------------------------------------------------- Inventory
@Composable
fun InventoryScreen(vm: ScreensViewModel = hiltViewModel()) {
    val (state, retry) = rememberLoad { if (vm.standalone) vm.local.stock() else vm.remote.stock(vm.settings.warehouseId) }
    var adjusting by remember { mutableStateOf<StockBalance?>(null) }
    LoadView(state, retry) { page ->
        if (page.items.isEmpty()) EmptyState(stringResource(R.string.state_empty))
        LazyColumn {
            items(page.items, key = { it.productId + it.warehouseId }) { s ->
                val low = s.lowStockThreshold > 0 && s.quantity <= s.lowStockThreshold
                ListItem(headlineContent = { Text(s.productName) }, supportingContent = { Text("${s.sku} · ${s.warehouseName}") },
                    modifier = if (vm.standalone) Modifier.clickable { adjusting = s } else Modifier,
                    trailingContent = {
                        Column(horizontalAlignment = Alignment.End) {
                            Text("${s.quantity}", fontWeight = FontWeight.SemiBold, color = if (low) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurface)
                            Text("${stringResource(R.string.inv_available)}: ${s.available}", style = MaterialTheme.typography.bodySmall)
                        }
                    })
                HorizontalDivider()
            }
        }
    }
    adjusting?.let { AdjustStockDialog(vm, it) { adjusting = null; retry() } }
}

@Composable
private fun AdjustStockDialog(vm: ScreensViewModel, item: StockBalance, onClose: () -> Unit) {
    var delta by remember { mutableStateOf("") }
    val scope = rememberCoroutineScope()
    AlertDialog(onDismissRequest = onClose, title = { Text(item.productName) }, text = {
        Column {
            Text("${stringResource(R.string.inv_available)}: ${item.quantity}")
            OutlinedTextField(delta, { delta = it.filter { c -> c.isDigit() || c == '.' || c == '-' } }, label = { Text(stringResource(R.string.sa_adjustQty)) }, singleLine = true)
        }
    }, confirmButton = {
        TextButton(enabled = (delta.toDoubleOrNull() ?: 0.0) != 0.0, onClick = { scope.launch { vm.local.adjustStock(item.productId, delta.toDouble()); onClose() } }) {
            Text(stringResource(R.string.action_save))
        }
    }, dismissButton = { TextButton(onClick = onClose) { Text(stringResource(R.string.action_cancel)) } })
}

// ---------------------------------------------------------------- Customers
@Composable
fun CustomersScreen(vm: ScreensViewModel = hiltViewModel()) {
    var q by remember { mutableStateOf("") }
    var adding by remember { mutableStateOf(false) }
    val customers by vm.db.catalog().searchCustomers(q).collectAsState(emptyList())
    val scope = rememberCoroutineScope()
    Scaffold(floatingActionButton = { FloatingActionButton(onClick = { adding = true }) { Icon(Icons.Outlined.PersonAdd, stringResource(R.string.action_add)) } }) { pad ->
        Column(Modifier.padding(pad)) {
            OutlinedTextField(q, { q = it }, Modifier.fillMaxWidth().padding(12.dp), placeholder = { Text(stringResource(R.string.nav_search)) }, singleLine = true)
            LazyColumn {
                items(customers, key = { it.id }) { c ->
                    ListItem(headlineContent = { Text(c.name) }, supportingContent = { Text(listOfNotNull(c.code, c.phone).joinToString(" · ")) },
                        trailingContent = { Text(money(c.balance)) })
                    HorizontalDivider()
                }
            }
        }
    }
    if (adding) {
        var name by remember { mutableStateOf("") }
        var phone by remember { mutableStateOf("") }
        AlertDialog(onDismissRequest = { adding = false }, title = { Text(stringResource(R.string.action_add)) }, text = {
            Column {
                OutlinedTextField(name, { name = it }, label = { Text(stringResource(R.string.cat_name)) }, singleLine = true)
                OutlinedTextField(phone, { phone = it }, label = { Text(stringResource(R.string.crm_phone)) }, singleLine = true)
            }
        }, confirmButton = {
            TextButton(enabled = name.isNotBlank(), onClick = {
                scope.launch {
                    // Offline-first: stable client id + outbox op; the server de-duplicates replays.
                    if (vm.standalone) { vm.local.addCustomer(name, phone); adding = false; return@launch }
                    val id = UUID.randomUUID().toString()
                    vm.db.catalog().upsertCustomers(listOf(com.veltrix.os.data.db.CustomerEntity(id, "…", name, phone, 0.0, 0.0, 0.0, java.time.Instant.now().toString(), true)))
                    vm.sync.enqueue("customer.upsert", UpsertCustomerRequest("Individual", name, phone.ifBlank { null }, null, id = id), UpsertCustomerRequest.serializer(), "cust-$id")
                    adding = false
                }
            }) { Text(stringResource(R.string.action_save)) }
        }, dismissButton = { TextButton(onClick = { adding = false }) { Text(stringResource(R.string.action_cancel)) } })
    }
}

// ---------------------------------------------------------------- Orders, expenses, employees
@Composable
fun OrdersScreen(vm: ScreensViewModel = hiltViewModel()) {
    val (state, retry) = rememberLoad { if (vm.standalone) vm.local.salesHistory() else vm.remote.salesDocuments() }
    LoadView(state, retry) { page ->
        if (page.items.isEmpty()) EmptyState(stringResource(R.string.state_empty))
        LazyColumn {
            items(page.items, key = { it.id }) { d ->
                ListItem(overlineContent = { Text(d.type) }, headlineContent = { Text("${d.number} · ${d.customerName}") },
                    supportingContent = { Text("${d.date} · ${d.status}") }, trailingContent = { Text(money(d.total)) })
                HorizontalDivider()
            }
        }
    }
}

@Composable
fun ExpensesScreen(vm: ScreensViewModel = hiltViewModel()) {
    val (state, retry) = rememberLoad { vm.remote.expenses() }
    var adding by remember { mutableStateOf(false) }
    Scaffold(floatingActionButton = { FloatingActionButton(onClick = { adding = true }) { Icon(Icons.Outlined.Add, stringResource(R.string.action_add)) } }) { pad ->
        Box(Modifier.padding(pad)) {
            LoadView(state, retry) { page ->
                LazyColumn {
                    items(page.items, key = { it.id }) { e ->
                        ListItem(headlineContent = { Text(e.expenseAccountName) }, supportingContent = { Text("${e.number} · ${e.date} · ${e.payee ?: ""}") },
                            trailingContent = { Text(money(e.amount)) })
                        HorizontalDivider()
                    }
                }
            }
        }
    }
    if (adding) ExpenseDialog(vm) { adding = false; retry() }
}

@Composable
private fun ExpenseDialog(vm: ScreensViewModel, onClose: () -> Unit) {
    val (accounts, _) = rememberLoad { vm.remote.accounts() }
    var amount by remember { mutableStateOf("") }
    var payee by remember { mutableStateOf("") }
    var expenseAccount by remember { mutableStateOf<Account?>(null) }
    val scope = rememberCoroutineScope()
    AlertDialog(onDismissRequest = onClose, title = { Text(stringResource(R.string.nav_expenses)) }, text = {
        Column {
            when (accounts) {
                is Load.Ok -> {
                    val expenses = accounts.value.filter { it.type == "Expense" && it.isPostable }
                    Row(Modifier.horizontalScroll(rememberScrollState())) {
                        expenses.forEach { a -> FilterChip(selected = expenseAccount == a, onClick = { expenseAccount = a }, label = { Text("${a.code} ${a.name}") }, modifier = Modifier.padding(end = 4.dp)) }
                    }
                }
                is Load.Failed -> Text(stringResource(R.string.err_network), color = MaterialTheme.colorScheme.error)
                else -> LinearProgressIndicator()
            }
            OutlinedTextField(amount, { amount = it.filter { c -> c.isDigit() || c == '.' } }, label = { Text(stringResource(R.string.pos_total)) }, singleLine = true)
            OutlinedTextField(payee, { payee = it }, label = { Text("Payee") }, singleLine = true)
        }
    }, confirmButton = {
        TextButton(enabled = expenseAccount != null && (amount.toDoubleOrNull() ?: 0.0) > 0, onClick = {
            val cash = (accounts as? Load.Ok)?.value?.firstOrNull { it.systemRole == "cash" } ?: return@TextButton
            scope.launch {
                val id = UUID.randomUUID().toString()
                vm.sync.enqueue("expense.create", CreateExpenseRequest(LocalDate.now().toString(), expenseAccount!!.id, amount.toDouble(), 0.0, cash.id,
                    payee.ifBlank { null }, null, vm.settings.branchId, id), CreateExpenseRequest.serializer(), "exp-$id")
                onClose()
            }
        }) { Text(stringResource(R.string.action_save)) }
    }, dismissButton = { TextButton(onClick = onClose) { Text(stringResource(R.string.action_cancel)) } })
}

@Composable
fun EmployeesScreen(vm: ScreensViewModel = hiltViewModel()) {
    val (state, retry) = rememberLoad { vm.remote.employees() }
    val scope = rememberCoroutineScope()
    val snackbar = remember { SnackbarHostState() }
    val profile by vm.session.profile.collectAsState()
    Scaffold(snackbarHost = { SnackbarHost(snackbar) }) { pad ->
        Box(Modifier.padding(pad)) {
            LoadView(state, retry) { page ->
                LazyColumn {
                    items(page.items, key = { it.id }) { e ->
                        val self = e.userId == profile?.id
                        ListItem(headlineContent = { Text(e.fullName) }, supportingContent = { Text("${e.code} · ${e.status}") }, trailingContent = {
                            if (self) Row {
                                TextButton(onClick = { scope.launch { runCatching { vm.remote.checkIn(e.id, true) }.onSuccess { snackbar.showSnackbar("✓") } } }) { Text(stringResource(R.string.hr_checkIn)) }
                                TextButton(onClick = { scope.launch { runCatching { vm.remote.checkIn(e.id, false) }.onSuccess { snackbar.showSnackbar("✓") } } }) { Text(stringResource(R.string.hr_checkOut)) }
                            }
                        })
                        HorizontalDivider()
                    }
                }
            }
        }
    }
}

// ---------------------------------------------------------------- Reports
@Composable
fun ReportsScreen(vm: ScreensViewModel = hiltViewModel()) {
    val (list, retry) = rememberLoad { if (vm.standalone) vm.local.reports() else vm.remote.reports() }
    var selected by remember { mutableStateOf<ReportInfo?>(null) }
    val from = LocalDate.now().withDayOfMonth(1).toString()
    val to = LocalDate.now().toString()
    Column(Modifier.fillMaxSize()) {
        LoadView(list, retry) { reports ->
            Row(Modifier.horizontalScroll(rememberScrollState()).padding(8.dp)) {
                reports.forEach { r -> FilterChip(selected = selected == r, onClick = { selected = r }, label = { Text(r.title) }, modifier = Modifier.padding(end = 6.dp)) }
            }
        }
        Text("${stringResource(R.string.rep_from)} $from ${stringResource(R.string.rep_to)} $to", Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall)
        selected?.let { r ->
            val (table, retryTable) = rememberLoad(r.key) {
                if (vm.standalone) vm.local.report(r.key, LocalDate.parse(from), LocalDate.parse(to)) else vm.remote.report(r.key, from, to)
            }
            LoadView(table, retryTable) { t ->
                LazyColumn(Modifier.horizontalScroll(rememberScrollState())) {
                    item { Row { t.columns.forEach { c -> Text(c.title, Modifier.width(140.dp).padding(8.dp), fontWeight = FontWeight.SemiBold) } } }
                    items(t.rows) { row -> Row { row.forEach { v -> Text((v as? JsonPrimitive)?.contentOrNull ?: "", Modifier.width(140.dp).padding(8.dp)) } } }
                    item { t.summary.forEach { (k, v) -> Text("$k: ${(v as? JsonPrimitive)?.contentOrNull ?: v}", Modifier.padding(8.dp), fontWeight = FontWeight.SemiBold) } }
                }
            }
        }
        Text(stringResource(R.string.rep_forecastDisclaimer), Modifier.padding(16.dp), style = MaterialTheme.typography.bodySmall)
    }
}

// ---------------------------------------------------------------- Notifications
@Composable
fun NotificationsScreen(vm: ScreensViewModel = hiltViewModel()) {
    val (state, retry) = rememberLoad { vm.remote.notifications() }
    val scope = rememberCoroutineScope()
    Column {
        TextButton(onClick = { scope.launch { runCatching { vm.remote.markAllRead() }; retry() } }, Modifier.align(Alignment.End)) { Text(stringResource(R.string.notif_markAllRead)) }
        LoadView(state, retry) { list ->
            if (list.isEmpty()) EmptyState(stringResource(R.string.notif_empty))
            LazyColumn {
                items(list, key = { it.id }) { n ->
                    ListItem(headlineContent = { Text(n.title, fontWeight = if (n.read) FontWeight.Normal else FontWeight.SemiBold) }, supportingContent = { Text(n.body) },
                        leadingContent = { Icon(if (n.severity == "Critical" || n.severity == "Warning") Icons.Outlined.Warning else Icons.Outlined.Info, null) })
                    HorizontalDivider()
                }
            }
        }
    }
}

// ---------------------------------------------------------------- Branch, license, settings, profile, sync issues
@Composable
fun BranchScreen(vm: ScreensViewModel = hiltViewModel()) {
    val (state, retry) = rememberLoad { vm.remote.branches() }
    val registers by vm.db.catalog().registers().collectAsState(emptyList())
    var selectedRegister by remember { mutableStateOf(vm.settings.registerId) }
    LazyColumn {
        item { SectionTitle(stringResource(R.string.nav_branch)) }
        item {
            LoadView(state, retry) { branches ->
                Column { branches.forEach { b -> ListItem(headlineContent = { Text(b.name) }, supportingContent = { Text(b.code) },
                    trailingContent = { RadioButton(selected = vm.settings.branchId == b.id, onClick = { vm.settings.branchId = b.id }) }) } }
            }
        }
        item { SectionTitle(stringResource(R.string.nav_pos)) }
        items(registers, key = { it.id }) { r ->
            ListItem(headlineContent = { Text(r.name) }, supportingContent = { Text(r.code) }, trailingContent = {
                RadioButton(selected = selectedRegister == r.id, onClick = {
                    selectedRegister = r.id; vm.settings.registerId = r.id; vm.settings.warehouseId = r.warehouseId; vm.settings.branchId = r.branchId
                    vm.settings.shiftId = r.openShiftId
                })
            })
        }
    }
}

@Composable
fun LicenseScreen(vm: ScreensViewModel = hiltViewModel()) {
    val state by vm.license.state.collectAsState()
    val summary = vm.license.summary
    val scope = rememberCoroutineScope()
    val activity = androidx.activity.compose.LocalActivity.current as FragmentActivity
    var confirmRelease by remember { mutableStateOf(false) }
    Column(Modifier.fillMaxSize().padding(16.dp)) {
        BrandEmblem(Modifier.size(64.dp))
        Spacer(Modifier.height(12.dp))
        if (vm.standalone) Info(stringResource(R.string.bind_status),
            vm.license.boundAt?.let { java.time.Instant.ofEpochSecond(it).atZone(java.time.ZoneId.systemDefault()).toLocalDate().toString() } ?: "—")
        summary?.let { s ->
            Info(stringResource(R.string.lic_plan), s.planCode.replaceFirstChar { it.uppercase() } + " (" + s.type + ")")
            Info(stringResource(R.string.lic_status), s.effectiveStatus)
            Info(stringResource(R.string.lic_expires), s.expiresAt ?: "—")
            Info(stringResource(R.string.lic_seats), "${s.activeDevices} / ${s.maxDevices}")
            Info(stringResource(R.string.lic_remaining), "${(s.maxDevices - s.activeDevices).coerceAtLeast(0)}")
        }
        (state as? LicenseState.Valid)?.let { Info(stringResource(R.string.lic_offlineUntil), java.time.Instant.ofEpochSecond(it.cert.exp).toString()) }
        Spacer(Modifier.height(12.dp))
        Text(stringResource(R.string.lic_recovery), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.height(16.dp))
        if (!vm.standalone) Button(onClick = { scope.launch { runCatching { vm.license.refresh() } } }) { Text(stringResource(R.string.action_refresh)) }
        // An offline license has no server seat to release.
        if (!vm.standalone) OutlinedButton(onClick = { requireBiometric(activity, activity.getString(R.string.lic_release)) { confirmRelease = true } }) { Text(stringResource(R.string.lic_release)) }
        else Text(stringResource(R.string.sa_localMode), style = MaterialTheme.typography.bodySmall)
    }
    if (confirmRelease) AlertDialog(onDismissRequest = { confirmRelease = false }, text = { Text(stringResource(R.string.dlg_confirmDelete)) },
        confirmButton = { TextButton(onClick = { scope.launch { runCatching { vm.license.release(); vm.session.logout() } }; confirmRelease = false }) { Text(stringResource(R.string.action_confirm)) } },
        dismissButton = { TextButton(onClick = { confirmRelease = false }) { Text(stringResource(R.string.action_cancel)) } })
}

@Composable
private fun Info(label: String, value: String) {
    Row(Modifier.fillMaxWidth().padding(vertical = 6.dp)) {
        Text(label, Modifier.weight(1f), color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(value, fontWeight = FontWeight.SemiBold)
    }
}

@Composable
fun SettingsScreen(vm: ScreensViewModel = hiltViewModel()) {
    var theme by remember { mutableStateOf(vm.settings.theme) }
    var biometric by remember { mutableStateOf(vm.settings.biometricEnabled) }
    LazyColumn(Modifier.fillMaxSize()) {
        item { SectionTitle(stringResource(R.string.set_language)) }
        item {
            Row(Modifier.padding(horizontal = 16.dp)) {
                listOf("ar" to "العربية", "fr" to "Français", "en" to "English").forEach { (code, label) ->
                    FilterChip(selected = vm.settings.language == code, onClick = {
                        vm.settings.language = code
                        AppCompatDelegate.setApplicationLocales(LocaleListCompat.forLanguageTags(code))
                    }, label = { Text(label) }, modifier = Modifier.padding(end = 6.dp))
                }
            }
        }
        item { SectionTitle(stringResource(R.string.set_theme)) }
        item {
            Row(Modifier.padding(horizontal = 16.dp)) {
                listOf("dark" to R.string.set_themeDark, "light" to R.string.set_themeLight).forEach { (code, label) ->
                    FilterChip(selected = theme == code, onClick = { theme = code; vm.settings.theme = code }, label = { Text(stringResource(label)) }, modifier = Modifier.padding(end = 6.dp))
                }
            }
        }
        item {
            ListItem(headlineContent = { Text(stringResource(R.string.auth_biometric)) }, trailingContent = {
                Switch(biometric, { biometric = it; vm.settings.biometricEnabled = it })
            })
        }
        if (vm.standalone) item { Text(stringResource(R.string.sa_serverOnly), Modifier.padding(16.dp), style = MaterialTheme.typography.bodyMedium) }
        else {
            item { ListItem(headlineContent = { Text(stringResource(R.string.onb_server)) }, supportingContent = { Text(vm.settings.serverUrl) }) }
            item { ListItem(headlineContent = { Text(stringResource(R.string.sync_lastSync)) }, supportingContent = { Text(if (vm.settings.lastSyncAt == 0L) "—" else java.time.Instant.ofEpochMilli(vm.settings.lastSyncAt).toString()) }) }
        }
        item { ListItem(headlineContent = { Text(stringResource(R.string.app_version)) }, supportingContent = { Text("${BuildConfig.VERSION_NAME} (${BuildConfig.VERSION_CODE}) ${stringResource(R.string.build_label)}") }) }
        item { Text(stringResource(R.string.acc_reviewNotice), Modifier.padding(16.dp), style = MaterialTheme.typography.bodySmall) }
    }
}

@Composable
fun ProfileScreen(vm: ScreensViewModel = hiltViewModel()) {
    val profile by vm.session.profile.collectAsState()
    val scope = rememberCoroutineScope()
    Column(Modifier.padding(16.dp)) {
        profile?.let {
            Text(it.fullName, style = MaterialTheme.typography.headlineMedium)
            Text(it.email, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Spacer(Modifier.height(8.dp))
            Text(it.roles.joinToString(", "))
        }
        Spacer(Modifier.height(24.dp))
        Button(onClick = { scope.launch { vm.session.logout() } }) { Text(stringResource(R.string.action_signOut)) }
    }
}

@Composable
fun SyncIssuesScreen(vm: ScreensViewModel = hiltViewModel()) {
    val ops by vm.sync.needingAttention.collectAsState(emptyList())
    if (ops.isEmpty()) EmptyState(stringResource(R.string.sync_done))
    LazyColumn {
        items(ops, key = { it.opId }) { op ->
            ListItem(overlineContent = { Text(op.status.uppercase()) }, headlineContent = { Text(op.type) }, supportingContent = { Text(op.lastError ?: "") })
            HorizontalDivider()
        }
    }
}

@Composable
fun MoreScreen(vm: ScreensViewModel = hiltViewModel(), onNavigate: (String) -> Unit) {
    val entries = listOf(
        Triple("inventory", R.string.nav_inventory, Icons.Outlined.Warehouse), Triple("orders", R.string.nav_orders, Icons.Outlined.ReceiptLong),
        Triple("expenses", R.string.nav_expenses, Icons.Outlined.Payments), Triple("reports", R.string.nav_reports, Icons.Outlined.Assessment),
        Triple("employees", R.string.nav_employees, Icons.Outlined.Badge), Triple("notifications", R.string.nav_notifications, Icons.Outlined.Notifications),
        Triple("branch", R.string.nav_branch, Icons.Outlined.Store), Triple("license", R.string.nav_licensing, Icons.Outlined.VerifiedUser),
        Triple("sync", R.string.action_sync, Icons.Outlined.Sync), Triple("settings", R.string.nav_settings, Icons.Outlined.Settings),
        Triple("profile", R.string.nav_profile, Icons.Outlined.AccountCircle),
        Triple("users", R.string.sa_users, Icons.Outlined.Group), Triple("backup", R.string.sa_backup, Icons.Outlined.Backup),
    ).filter { com.veltrix.os.data.standalone.ModePolicy.isVisible(it.first, vm.standalone) }
    LazyColumn {
        items(entries) { (route, label, icon) ->
            ListItem(headlineContent = { Text(stringResource(label)) }, leadingContent = { Icon(icon, null) }, modifier = Modifier.clickable { onNavigate(route) })
        }
    }
}

// ---------------------------------------------------------------- Standalone: local users and encrypted backup
@Composable
fun UsersScreen(vm: ScreensViewModel = hiltViewModel()) {
    var users by remember { mutableStateOf(vm.local.auth.users) }
    val isOwner = vm.session.localUser?.isOwner == true
    var name by remember { mutableStateOf("") }
    var secret by remember { mutableStateOf("") }
    var role by remember { mutableStateOf(com.veltrix.os.data.standalone.LocalRoles.CASHIER) }
    var error by remember { mutableStateOf<Int?>(null) }
    var busy by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(16.dp)) {
        item { SectionTitle(stringResource(R.string.sa_users)) }
        items(users, key = { it.id }) { u ->
            ListItem(headlineContent = { Text(u.name) }, supportingContent = { Text(u.role) }, trailingContent = {
                if (isOwner && u.id != vm.session.localUser?.id) IconButton(onClick = {
                    runCatching { vm.local.auth.removeUser(u.id) }.onFailure { error = (it as? com.veltrix.os.data.standalone.LocalAuthException)?.let { e -> com.veltrix.os.ui.auth.errorRes(e.key) } }
                    users = vm.local.auth.users
                }) { Icon(Icons.Outlined.Delete, stringResource(R.string.action_delete)) }
            })
            HorizontalDivider()
        }
        if (isOwner) item {
            Column(Modifier.padding(top = 16.dp)) {
                OutlinedTextField(name, { name = it }, label = { Text(stringResource(R.string.sa_userName)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
                OutlinedTextField(secret, { secret = it }, label = { Text(stringResource(R.string.sa_secret)) }, singleLine = true, modifier = Modifier.fillMaxWidth(),
                    visualTransformation = androidx.compose.ui.text.input.PasswordVisualTransformation())
                Text(stringResource(R.string.sa_secretRule), style = MaterialTheme.typography.bodySmall)
                Row {
                    listOf(com.veltrix.os.data.standalone.LocalRoles.CASHIER, com.veltrix.os.data.standalone.LocalRoles.OWNER).forEach { r ->
                        FilterChip(role == r, { role = r }, { Text(r) }, Modifier.padding(end = 6.dp))
                    }
                }
                error?.let { Text(stringResource(it), color = MaterialTheme.colorScheme.error) }
                Button(enabled = !busy && name.isNotBlank() && secret.isNotEmpty(), onClick = {
                    busy = true; error = null
                    scope.launch {
                        try {
                            kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.Default) { vm.local.auth.addUser(name, secret, role) }
                            name = ""; secret = ""; users = vm.local.auth.users
                        } catch (e: com.veltrix.os.data.standalone.LocalAuthException) { error = com.veltrix.os.ui.auth.errorRes(e.key) }
                        busy = false
                    }
                }) { Text(stringResource(R.string.action_add)) }
            }
        }
    }
}

@Composable
fun BackupScreen(vm: ScreensViewModel = hiltViewModel()) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var password by remember { mutableStateOf("") }
    var message by remember { mutableStateOf<Int?>(null) }
    var busy by remember { mutableStateOf(false) }
    val owner = vm.session.localUser?.takeIf { it.isOwner }
    fun fail(e: Throwable) {
        message = when (e) {
            is com.veltrix.os.data.standalone.LocalAuthException -> com.veltrix.os.ui.auth.errorRes(e.key)
            is com.veltrix.os.data.standalone.BackupException -> com.veltrix.os.ui.auth.errorRes(e.key)
            else -> R.string.sa_err_backupFile
        }
    }
    val create = androidx.activity.compose.rememberLauncherForActivityResult(
        androidx.activity.result.contract.ActivityResultContracts.CreateDocument(com.veltrix.os.data.standalone.BackupCodec.MIME)) { uri ->
        if (uri == null || owner == null) return@rememberLauncherForActivityResult
        busy = true
        scope.launch {
            runCatching {
                val bytes = vm.local.exportBackup(owner, password)
                kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) { context.contentResolver.openOutputStream(uri)?.use { it.write(bytes) } ?: error("write") }
            }.onSuccess { message = R.string.sa_backupSaved }.onFailure(::fail)
            busy = false
        }
    }
    val open = androidx.activity.compose.rememberLauncherForActivityResult(androidx.activity.result.contract.ActivityResultContracts.OpenDocument()) { uri ->
        if (uri == null) return@rememberLauncherForActivityResult
        busy = true
        scope.launch {
            runCatching {
                val bytes = kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) { context.contentResolver.openInputStream(uri)?.use { it.readBytes() } } ?: error("read")
                vm.local.restoreBackup(bytes, password, vm.standalone)
            }.onSuccess { message = R.string.sa_restoredHere }.onFailure(::fail)
            busy = false
        }
    }
    Column(Modifier.fillMaxSize().padding(16.dp).verticalScroll(rememberScrollState())) {
        SectionTitle(stringResource(R.string.sa_backup))
        Text(stringResource(R.string.sa_backupHint), style = MaterialTheme.typography.bodyMedium)
        Spacer(Modifier.height(12.dp))
        if (owner == null) Text(stringResource(R.string.sa_err_ownerOnly), color = MaterialTheme.colorScheme.error)
        else {
            OutlinedTextField(password, { password = it }, label = { Text(stringResource(R.string.sa_backupPassword)) }, singleLine = true, modifier = Modifier.fillMaxWidth(),
                visualTransformation = androidx.compose.ui.text.input.PasswordVisualTransformation())
            Spacer(Modifier.height(8.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(enabled = !busy && password.isNotEmpty(), onClick = { create.launch("veltrix-backup-${LocalDate.now()}${com.veltrix.os.data.standalone.BackupCodec.EXTENSION}") }) {
                    Text(stringResource(R.string.sa_export))
                }
                OutlinedButton(enabled = !busy && password.isNotEmpty(), onClick = { open.launch(arrayOf("*/*")) }) { Text(stringResource(R.string.sa_restore)) }
            }
            if (busy) LinearProgressIndicator(Modifier.fillMaxWidth().padding(top = 8.dp))
            message?.let { Text(stringResource(it), Modifier.padding(top = 8.dp)) }
        }
        Spacer(Modifier.height(16.dp))
        Text(stringResource(R.string.sa_serverOnly), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}
