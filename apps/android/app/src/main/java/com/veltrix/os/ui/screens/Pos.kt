package com.veltrix.os.ui.screens

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.animation.scaleIn
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.fragment.app.FragmentActivity
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.veltrix.os.R
import com.veltrix.os.data.api.AppSettings
import com.veltrix.os.data.api.PaymentInput
import com.veltrix.os.data.db.ProductEntity
import com.veltrix.os.data.db.VeltrixDatabase
import com.veltrix.os.data.repo.CartLine
import com.veltrix.os.data.repo.PosRepository
import com.veltrix.os.data.repo.SessionRepository
import com.veltrix.os.data.work.SyncWorker
import com.veltrix.os.ui.common.money
import com.veltrix.os.ui.common.requireBiometric
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch

/** Carries camera scan results to whichever screen requested them. */
@Singleton
class ScanBus @Inject constructor() {
    val results = MutableSharedFlow<String>(extraBufferCapacity = 4)
}

@HiltViewModel
class PosViewModel @Inject constructor(private val pos: PosRepository, private val db: VeltrixDatabase, val settings: AppSettings,
    val session: SessionRepository, val scans: ScanBus) : ViewModel() {
    val cart = MutableStateFlow<List<CartLine>>(emptyList())
    val message = MutableStateFlow<String?>(null)
    val lastSale = MutableStateFlow<String?>(null)
    val shiftOpen = MutableStateFlow(settings.shiftId != null)
    private val parked = mutableListOf<List<CartLine>>()

    init { viewModelScope.launch { scans.results.collect { addByCode(it) } } }

    fun search(q: String) = db.catalog().searchProducts(q)

    fun add(p: ProductEntity) {
        val lines = cart.value.toMutableList()
        val i = lines.indexOfFirst { it.product.id == p.id }
        if (i >= 0) lines[i] = lines[i].copy(quantity = lines[i].quantity + 1) else lines += CartLine(p, 1.0)
        cart.value = lines
    }

    fun addByCode(code: String) = viewModelScope.launch {
        db.catalog().findByCode(code.trim())?.let { add(it) } ?: run { message.value = code }
    }

    fun setQty(id: String, q: Double) {
        cart.value = cart.value.mapNotNull { if (it.product.id == id) (if (q <= 0) null else it.copy(quantity = q)) else it }
    }

    fun setDiscount(id: String, pct: Double) {
        if (session.profile.value?.can("pos.discount") != true) { message.value = "permission"; return }
        cart.value = cart.value.map { if (it.product.id == id) it.copy(discountPercent = pct.coerceIn(0.0, 100.0)) else it }
    }

    fun park() { if (cart.value.isNotEmpty()) { parked += cart.value; cart.value = emptyList() } }
    fun resume() { parked.removeLastOrNull()?.let { cart.value = it } }
    val parkedCount get() = parked.size

    fun openShift(float: Double) = viewModelScope.launch {
        runCatching { pos.openShift(float) }.onSuccess { shiftOpen.value = true }.onFailure { message.value = it.message }
    }

    fun closeShift(counted: Double) = viewModelScope.launch {
        pos.closeShift(counted, null)
        shiftOpen.value = false
    }

    fun pay(payments: List<PaymentInput>, context: android.content.Context) = viewModelScope.launch {
        try {
            val sale = pos.completeSale(cart.value, payments, null, allowNegative = false)
            cart.value = emptyList()
            lastSale.value = sale.number
            SyncWorker.syncNow(context)
        } catch (e: IllegalStateException) {
            message.value = e.message
        }
    }
}

@Composable
fun PosScreen(onScan: () -> Unit, vm: PosViewModel = hiltViewModel()) {
    val cart by vm.cart.collectAsState()
    val shiftOpen by vm.shiftOpen.collectAsState()
    val lastSale by vm.lastSale.collectAsState()
    val message by vm.message.collectAsState()
    var query by remember { mutableStateOf("") }
    val results by vm.search(query).collectAsState(emptyList())
    var paying by remember { mutableStateOf(false) }
    var closing by remember { mutableStateOf(false) }
    val total = cart.sumOf { it.total }
    val context = LocalContext.current
    val activity = androidx.activity.compose.LocalActivity.current as FragmentActivity

    if (!shiftOpen) { OpenShiftPanel(vm); return }

    Column(Modifier.fillMaxSize()) {
        Row(Modifier.padding(8.dp), verticalAlignment = Alignment.CenterVertically) {
            OutlinedTextField(query, { query = it }, Modifier.weight(1f), placeholder = { Text(stringResource(R.string.pos_scanHint)) }, singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Text))
            IconButton(onClick = onScan) { Icon(Icons.Outlined.QrCodeScanner, stringResource(R.string.nav_scanner)) }
        }
        if (query.isNotBlank()) {
            LazyColumn(Modifier.heightIn(max = 220.dp)) {
                items(results, key = { it.id }) { p ->
                    ListItem(headlineContent = { Text(p.name) }, supportingContent = { Text(p.sku) }, trailingContent = { Text(money(p.salePrice)) },
                        modifier = Modifier.clickable { vm.add(p); query = "" })
                }
            }
            HorizontalDivider()
        }
        LazyColumn(Modifier.weight(1f)) {
            items(cart, key = { it.product.id }) { l ->
                ListItem(headlineContent = { Text(l.product.name) },
                    supportingContent = { Text(money(l.product.salePrice) + if (l.discountPercent > 0) " −${l.discountPercent}%" else "") },
                    trailingContent = {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            IconButton(onClick = { vm.setQty(l.product.id, l.quantity - 1) }) { Icon(Icons.Outlined.Remove, null) }
                            Text(l.quantity.let { if (it % 1.0 == 0.0) it.toInt().toString() else it.toString() }, fontWeight = FontWeight.SemiBold)
                            IconButton(onClick = { vm.setQty(l.product.id, l.quantity + 1) }) { Icon(Icons.Outlined.Add, null) }
                        }
                    }, modifier = Modifier.clickable { vm.setDiscount(l.product.id, if (l.discountPercent == 0.0) 10.0 else 0.0) })
                HorizontalDivider()
            }
        }
        message?.let { Text(if (it == "permission") stringResource(R.string.err_permission) else it, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(8.dp)) }
        Surface(tonalElevation = 3.dp) {
            Column(Modifier.fillMaxWidth().padding(12.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(stringResource(R.string.pos_total), Modifier.weight(1f), style = MaterialTheme.typography.titleLarge)
                    Text(money(total), style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold)
                }
                Spacer(Modifier.height(8.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(onClick = vm::park, enabled = cart.isNotEmpty()) { Text(stringResource(R.string.pos_suspend)) }
                    OutlinedButton(onClick = vm::resume, enabled = vm.parkedCount > 0) { Text(stringResource(R.string.pos_resume)) }
                    OutlinedButton(onClick = { closing = true }) { Text(stringResource(R.string.pos_closeShift)) }
                }
                Spacer(Modifier.height(8.dp))
                Button(onClick = { paying = true }, enabled = cart.isNotEmpty(), modifier = Modifier.fillMaxWidth().height(52.dp)) { Text(stringResource(R.string.pos_pay)) }
            }
        }
    }
    if (paying) PaymentDialog(total, onDismiss = { paying = false }) { payments -> paying = false; vm.pay(payments, context) }
    if (closing) CloseShiftDialog(onDismiss = { closing = false }) { counted ->
        closing = false
        requireBiometric(activity, context.getString(R.string.pos_closeShift)) { vm.closeShift(counted) }
    }
    lastSale?.let { SaleCompleteDialog(it) { vm.lastSale.value = null } }
}

@Composable
private fun OpenShiftPanel(vm: PosViewModel) {
    var float by remember { mutableStateOf("0") }
    Column(Modifier.fillMaxSize().padding(24.dp), verticalArrangement = Arrangement.Center, horizontalAlignment = Alignment.CenterHorizontally) {
        Icon(Icons.Outlined.PointOfSale, null, Modifier.size(56.dp))
        Spacer(Modifier.height(12.dp))
        if (vm.settings.registerId == null) Text(stringResource(R.string.nav_branch) + " → " + stringResource(R.string.nav_pos), color = MaterialTheme.colorScheme.error)
        OutlinedTextField(float, { float = it.filter { c -> c.isDigit() || c == '.' } }, label = { Text(stringResource(R.string.pos_openingFloat)) }, singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal))
        Spacer(Modifier.height(12.dp))
        Button(onClick = { vm.openShift(float.toDoubleOrNull() ?: 0.0) }, enabled = vm.settings.registerId != null) { Text(stringResource(R.string.pos_openShift)) }
    }
}

@Composable
private fun PaymentDialog(total: Double, onDismiss: () -> Unit, onPay: (List<PaymentInput>) -> Unit) {
    var method by remember { mutableStateOf("Cash") }
    var tendered by remember { mutableStateOf(String.format(java.util.Locale.US, "%.2f", total)) }
    val t = tendered.toDoubleOrNull() ?: 0.0
    AlertDialog(onDismissRequest = onDismiss, title = { Text("${stringResource(R.string.pos_total)}: ${money(total)}") }, text = {
        Column {
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                FilterChip(method == "Cash", { method = "Cash" }, { Text(stringResource(R.string.pos_cash)) })
                FilterChip(method == "Card", { method = "Card" }, { Text(stringResource(R.string.pos_card)) })
            }
            if (method == "Cash") {
                OutlinedTextField(tendered, { tendered = it.filter { c -> c.isDigit() || c == '.' } }, label = { Text(stringResource(R.string.pos_tendered)) },
                    singleLine = true, keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal))
                Text("${stringResource(R.string.pos_change)}: ${money((t - total).coerceAtLeast(0.0))}", fontWeight = FontWeight.SemiBold)
            } else Text(stringResource(R.string.pos_cardNotice), style = MaterialTheme.typography.bodySmall)
        }
    }, confirmButton = {
        TextButton(enabled = method != "Cash" || t >= total, onClick = { onPay(listOf(PaymentInput(method, if (method == "Cash") t else total))) }) {
            Text(stringResource(R.string.action_confirm))
        }
    }, dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.action_cancel)) } })
}

@Composable
private fun CloseShiftDialog(onDismiss: () -> Unit, onClose: (Double) -> Unit) {
    var counted by remember { mutableStateOf("") }
    AlertDialog(onDismissRequest = onDismiss, title = { Text(stringResource(R.string.pos_closeShift)) }, text = {
        OutlinedTextField(counted, { counted = it.filter { c -> c.isDigit() || c == '.' } }, label = { Text(stringResource(R.string.pos_countedCash)) }, singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal))
    }, confirmButton = { TextButton(enabled = counted.toDoubleOrNull() != null, onClick = { onClose(counted.toDouble()) }) { Text(stringResource(R.string.action_confirm)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.action_cancel)) } })
}

/** Successful payment micro-interaction: animated check mark. */
@Composable
private fun SaleCompleteDialog(number: String, onDismiss: () -> Unit) {
    var start by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) { start = true }
    val p by animateFloatAsState(if (start) 1f else 0f, tween(600), label = "check")
    val color = com.veltrix.os.ui.theme.StatusColors.success
    AlertDialog(onDismissRequest = onDismiss, confirmButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.action_close)) } }, text = {
        Column(Modifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally) {
            AnimatedVisibility(true, enter = scaleIn()) {
                Canvas(Modifier.size(72.dp)) {
                    val a = Offset(size.width * 0.2f, size.height * 0.55f); val b = Offset(size.width * 0.42f, size.height * 0.75f); val c = Offset(size.width * 0.8f, size.height * 0.3f)
                    drawCircle(color.copy(alpha = 0.15f))
                    val first = (p * 2).coerceAtMost(1f); val second = ((p - 0.5f) * 2).coerceIn(0f, 1f)
                    drawLine(color, a, Offset(a.x + (b.x - a.x) * first, a.y + (b.y - a.y) * first), 8f, StrokeCap.Round)
                    if (second > 0) drawLine(color, b, Offset(b.x + (c.x - b.x) * second, b.y + (c.y - b.y) * second), 8f, StrokeCap.Round)
                }
            }
            Spacer(Modifier.height(8.dp))
            Text(stringResource(R.string.pos_saleComplete), style = MaterialTheme.typography.titleLarge)
            Text(number)
            Text(stringResource(R.string.pos_savedOffline), style = MaterialTheme.typography.bodySmall)
        }
    })
}
