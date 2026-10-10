package com.veltrix.admin.ui.common

import androidx.annotation.StringRes
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.CloudOff
import androidx.compose.material.icons.outlined.ErrorOutline
import androidx.compose.material.icons.outlined.Inbox
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material.icons.outlined.Search
import androidx.compose.material3.*
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.veltrix.admin.R
import com.veltrix.admin.data.api.AppError
import com.veltrix.admin.ui.theme.Brand
import com.veltrix.admin.ui.theme.DarkTokens

/** Snackbar host provided by the main shell so any screen can confirm an action. */
val LocalSnackbar = staticCompositionLocalOf { SnackbarHostState() }

@Composable
fun errorText(e: AppError): String = when (e) {
    AppError.Offline -> stringResource(R.string.err_offline)
    AppError.SessionExpired -> stringResource(R.string.err_session_expired)
    AppError.Forbidden -> stringResource(R.string.err_forbidden)
    AppError.StepUpFailed -> stringResource(R.string.err_step_up)
    AppError.NotFound -> stringResource(R.string.err_not_found)
    AppError.NotSupported -> stringResource(R.string.err_not_supported)
    AppError.RateLimited -> stringResource(R.string.err_rate_limited)
    AppError.InvalidCredentials -> stringResource(R.string.err_invalid_credentials)
    AppError.InvalidMfa -> stringResource(R.string.err_invalid_mfa)
    AppError.LockedOut -> stringResource(R.string.err_locked_out)
    is AppError.Rejected -> e.message
    is AppError.Server -> stringResource(R.string.err_server, e.status) + (e.correlationId?.let { " · $it" } ?: "")
    is AppError.Unknown -> stringResource(R.string.err_unknown)
}

@Composable
fun statusLabel(status: String?): String {
    val id = when (status?.lowercase()?.replace("_", "")) {
        "active" -> R.string.st_active; "suspended" -> R.string.st_suspended; "revoked" -> R.string.st_revoked
        "expired" -> R.string.st_expired; "expiring" -> R.string.st_expiring; "pending" -> R.string.st_pending
        "pastdue" -> R.string.st_past_due; "trialing" -> R.string.st_trialing; "cancelled" -> R.string.st_cancelled
        "issued" -> R.string.st_issued; "paid" -> R.string.st_paid; "void" -> R.string.st_void
        "open" -> R.string.st_open; "acknowledged" -> R.string.st_acknowledged; "resolved" -> R.string.st_resolved
        "closed" -> R.string.st_closed; "online" -> R.string.st_online; "offline" -> R.string.st_offline
        "released" -> R.string.st_released; "replaced" -> R.string.st_replaced; "transferred" -> R.string.st_transferred
        "sent" -> R.string.st_sent; "failed" -> R.string.st_failed; "skipped" -> R.string.st_skipped
        "uploaded" -> R.string.st_uploaded; "delivered" -> R.string.st_delivered; "healthy", "ok" -> R.string.st_healthy
        "degraded" -> R.string.st_degraded; "unhealthy", "down" -> R.string.st_unhealthy
        "critical" -> R.string.sev_critical; "error" -> R.string.sev_error; "warning" -> R.string.sev_warning; "info" -> R.string.sev_info
        else -> null
    }
    return id?.let { stringResource(it) } ?: (status ?: "—")
}

@Composable
fun StatusChip(status: String?, modifier: Modifier = Modifier) {
    val color = Brand.tone(StatusTone.of(status))
    Box(modifier.clip(RoundedCornerShape(50)).background(color.copy(alpha = 0.16f)).padding(horizontal = 10.dp, vertical = 3.dp)) {
        Text(statusLabel(status), color = color, style = MaterialTheme.typography.labelMedium, maxLines = 1)
    }
}

@Composable
fun SkeletonList(rows: Int = 6) {
    Column(Modifier.fillMaxWidth().padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        repeat(rows) { Box(Modifier.fillMaxWidth().height(64.dp).clip(RoundedCornerShape(12.dp)).background(MaterialTheme.colorScheme.surfaceVariant)) }
    }
}

@Composable
fun MessageState(text: String, icon: ImageVector, action: String? = null, onAction: (() -> Unit)? = null) {
    Column(Modifier.fillMaxWidth().padding(32.dp), horizontalAlignment = Alignment.CenterHorizontally) {
        Icon(icon, null, Modifier.size(48.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.height(12.dp))
        Text(text, color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodyLarge)
        if (action != null && onAction != null) { Spacer(Modifier.height(12.dp)); OutlinedButton(onClick = onAction) { Text(action) } }
    }
}

@Composable
fun EmptyState(text: String = stringResource(R.string.state_empty)) = MessageState(text, Icons.Outlined.Inbox)

@Composable
fun ErrorState(error: AppError, onRetry: () -> Unit) {
    val icon = when (error) {
        AppError.Offline -> Icons.Outlined.CloudOff
        AppError.Forbidden -> Icons.Outlined.Lock
        else -> Icons.Outlined.ErrorOutline
    }
    MessageState(errorText(error), icon, stringResource(R.string.action_retry), onRetry)
}

/** Pull-to-refresh wrapper for a [Load]-backed screen. Loading/error states stay scrollable so pull works everywhere. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun <T> LoadScreen(state: Load<T>, onRefresh: () -> Unit, content: LazyListScope.(T) -> Unit) {
    val refreshing = (state as? Load.Ok)?.refreshing == true
    PullToRefreshBox(isRefreshing = refreshing, onRefresh = onRefresh, modifier = Modifier.fillMaxSize()) {
        when (state) {
            Load.Loading -> Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) { SkeletonList() }
            is Load.Failed -> Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) { ErrorState(state.error, onRefresh) }
            is Load.Ok -> LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) { content(state.value) }
        }
    }
}

/** Paged, pull-to-refresh list with infinite scroll. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun <T> PagedList(
    state: PageState<T>,
    onRefresh: () -> Unit,
    onLoadMore: () -> Unit,
    key: (T) -> String,
    header: (LazyListScope.() -> Unit)? = null,
    row: @Composable (T) -> Unit,
) {
    val listState = rememberLazyListState()
    val nearEnd by remember { derivedStateOf { listState.layoutInfo.visibleItemsInfo.lastOrNull()?.index?.let { it >= listState.layoutInfo.totalItemsCount - 4 } ?: false } }
    LaunchedEffect(nearEnd, state.items.size) { if (nearEnd && !state.endReached && !state.loading && state.items.isNotEmpty()) onLoadMore() }
    PullToRefreshBox(isRefreshing = state.refreshing, onRefresh = onRefresh, modifier = Modifier.fillMaxSize()) {
        LazyColumn(Modifier.fillMaxSize(), state = listState, contentPadding = PaddingValues(bottom = 24.dp)) {
            header?.invoke(this)
            when {
                state.isInitialLoading -> item { SkeletonList() }
                state.error != null && state.items.isEmpty() -> item { ErrorState(state.error, onRefresh) }
                state.isEmpty -> item { EmptyState() }
                else -> {
                    items(state.items, key = key) { row(it) }
                    if (state.loading) item { Box(Modifier.fillMaxWidth().padding(16.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(Modifier.size(24.dp)) } }
                    state.error?.let { e -> item { ErrorState(e, onLoadMore) } }
                }
            }
        }
    }
}

@Composable
fun SectionHeader(text: String, modifier: Modifier = Modifier) {
    Text(text, style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.secondary,
        modifier = modifier.padding(start = 16.dp, end = 16.dp, top = 16.dp, bottom = 8.dp))
}

@Composable
fun InfoRow(label: String, value: String?, modifier: Modifier = Modifier) {
    Row(modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 6.dp), horizontalArrangement = Arrangement.SpaceBetween) {
        Text(label, color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f))
        Spacer(Modifier.width(12.dp))
        Text(value?.ifBlank { "—" } ?: "—", style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1.4f))
    }
}

@Composable
fun RowCard(title: String, subtitle: String?, modifier: Modifier = Modifier, status: String? = null, meta: String? = null, onClick: (() -> Unit)? = null) {
    Card(
        modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 5.dp).let { if (onClick != null) it.clickable(onClick = onClick) else it },
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
        shape = RoundedCornerShape(14.dp),
    ) {
        Row(Modifier.padding(14.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(title, style = MaterialTheme.typography.titleSmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
                if (!subtitle.isNullOrBlank()) Text(subtitle, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 2, overflow = TextOverflow.Ellipsis)
                if (!meta.isNullOrBlank()) Text(meta, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.secondary, maxLines = 1)
            }
            if (status != null) { Spacer(Modifier.width(8.dp)); StatusChip(status) }
        }
    }
}

@Composable
fun KpiCard(title: String, value: String, modifier: Modifier = Modifier, sub: String? = null, accent: Color = DarkTokens.accent, icon: ImageVector? = null) {
    Card(modifier, shape = RoundedCornerShape(16.dp), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
        Box(Modifier.background(Brand.violetGlow)) {
            Column(Modifier.fillMaxWidth().padding(14.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    if (icon != null) { Icon(icon, null, Modifier.size(16.dp), tint = accent); Spacer(Modifier.width(6.dp)) }
                    Text(title, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                Spacer(Modifier.height(6.dp))
                Text(value, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold, color = accent, maxLines = 1)
                if (sub != null) Text(sub, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1)
            }
        }
    }
}

@Composable
fun SearchField(value: String, onChange: (String) -> Unit, modifier: Modifier = Modifier) {
    OutlinedTextField(value, onChange, modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp), singleLine = true,
        leadingIcon = { Icon(Icons.Outlined.Search, null) }, placeholder = { Text(stringResource(R.string.action_search)) }, shape = RoundedCornerShape(14.dp))
}

/** Single-select filter chips; the `null` value means "all". */
@Composable
fun <T> FilterRow(options: List<Pair<T?, String>>, selected: T?, onSelect: (T?) -> Unit) {
    LazyRow(contentPadding = PaddingValues(horizontal = 16.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        items(options) { (value, label) ->
            FilterChip(selected = value == selected, onClick = { onSelect(value) }, label = { Text(label) })
        }
    }
}

/** Busy/error state of a mutating action (dialogs show it inline). */
sealed interface ActionState {
    data object Idle : ActionState
    data object Busy : ActionState
    data class Failed(val error: AppError) : ActionState
}

/**
 * Step-up confirmation: re-prompts for the account password, sent once as X-Confirm-Password and never stored.
 * Optionally also collects a reason (audited server-side).
 */
@Composable
fun StepUpDialog(
    title: String,
    message: String?,
    action: ActionState,
    onDismiss: () -> Unit,
    requireReason: Boolean = true,
    confirmLabel: String = stringResource(R.string.action_confirm),
    destructive: Boolean = true,
    onConfirm: (password: String, reason: String) -> Unit,
) {
    var password by remember { mutableStateOf("") }
    var reason by remember { mutableStateOf("") }
    val busy = action is ActionState.Busy
    AlertDialog(
        onDismissRequest = { if (!busy) onDismiss() },
        icon = { Icon(Icons.Outlined.Lock, null) },
        title = { Text(title) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                if (message != null) Text(message, style = MaterialTheme.typography.bodyMedium)
                if (requireReason) OutlinedTextField(reason, { reason = it.take(500) }, label = { Text(stringResource(R.string.field_reason)) },
                    modifier = Modifier.fillMaxWidth(), enabled = !busy)
                OutlinedTextField(password, { password = it }, label = { Text(stringResource(R.string.stepup_password)) }, singleLine = true,
                    visualTransformation = PasswordVisualTransformation(), keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                    modifier = Modifier.fillMaxWidth(), enabled = !busy)
                Text(stringResource(R.string.stepup_hint), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                if (action is ActionState.Failed) Text(errorText(action.error), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
            }
        },
        confirmButton = {
            Button(onClick = { onConfirm(password, reason.trim()) }, enabled = !busy && password.isNotEmpty() && (!requireReason || reason.isNotBlank()),
                colors = if (destructive) ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error) else ButtonDefaults.buttonColors()) {
                if (busy) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp) else Text(confirmLabel)
            }
        },
        dismissButton = { TextButton(onClick = onDismiss, enabled = !busy) { Text(stringResource(R.string.action_cancel)) } },
    )
}

/** Simple form dialog for non-step-up actions. */
@Composable
fun FormDialog(
    title: String,
    action: ActionState,
    onDismiss: () -> Unit,
    confirmEnabled: Boolean,
    onConfirm: () -> Unit,
    confirmLabel: String = stringResource(R.string.action_confirm),
    content: @Composable ColumnScope.() -> Unit,
) {
    val busy = action is ActionState.Busy
    AlertDialog(
        onDismissRequest = { if (!busy) onDismiss() },
        title = { Text(title) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                content()
                if (action is ActionState.Failed) Text(errorText(action.error), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
            }
        },
        confirmButton = {
            Button(onClick = onConfirm, enabled = !busy && confirmEnabled) {
                if (busy) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp) else Text(confirmLabel)
            }
        },
        dismissButton = { TextButton(onClick = onDismiss, enabled = !busy) { Text(stringResource(R.string.action_cancel)) } },
    )
}

/** Shows one-shot confirmation messages emitted by a view model. */
@Composable
fun CollectMessages(messages: kotlinx.coroutines.flow.Flow<Int>) {
    val host = LocalSnackbar.current
    val ctx = androidx.compose.ui.platform.LocalContext.current
    LaunchedEffect(messages) { messages.collect { host.showSnackbar(ctx.getString(it)) } }
}

@Composable
fun ageText(iso: String?): String {
    val a = Formatters.age(iso) ?: return "—"
    return when (a.unit) {
        Formatters.AgeUnit.Now -> stringResource(R.string.age_now)
        Formatters.AgeUnit.Minutes -> stringResource(R.string.age_minutes, a.value)
        Formatters.AgeUnit.Hours -> stringResource(R.string.age_hours, a.value)
        Formatters.AgeUnit.Days -> stringResource(R.string.age_days, a.value)
    }
}

@Composable
fun ActionButtonsRow(content: @Composable RowScope.() -> Unit) {
    Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp), content = content)
}

@StringRes
fun platformLabel(platform: String?): Int = when (platform?.lowercase()) {
    "android" -> R.string.platform_android
    "windows" -> R.string.platform_windows
    else -> R.string.platform_other
}
