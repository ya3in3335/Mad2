package com.veltrix.os.ui.common

import androidx.biometric.BiometricManager
import androidx.biometric.BiometricPrompt
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.slideInVertically
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.CloudOff
import androidx.compose.material.icons.outlined.Inbox
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.fragment.app.FragmentActivity
import com.veltrix.os.R
import com.veltrix.os.data.api.ApiException
import com.veltrix.os.data.api.OfflineException
import java.text.NumberFormat
import java.util.Locale

/** Async UI state for screens backed by API calls. */
sealed interface Load<out T> {
    data object Loading : Load<Nothing>
    data class Ok<T>(val value: T) : Load<T>
    data class Failed(val message: String, val offline: Boolean) : Load<Nothing>
}

suspend fun <T> load(block: suspend () -> T): Load<T> = try {
    Load.Ok(block())
} catch (e: OfflineException) {
    Load.Failed("offline", true)
} catch (e: ApiException) {
    Load.Failed(e.message, false)
}

fun money(v: Double, currency: String = "DZD"): String {
    val f = NumberFormat.getNumberInstance(Locale.getDefault()).apply { minimumFractionDigits = 2; maximumFractionDigits = 2 }
    return f.format(v) + " " + currency
}

@Composable
fun <T> LoadView(state: Load<T>, onRetry: () -> Unit, content: @Composable (T) -> Unit) {
    when (state) {
        is Load.Loading -> Column(Modifier.fillMaxWidth().padding(16.dp)) {
            repeat(4) { SkeletonRow(); Spacer(Modifier.height(10.dp)) }
        }
        is Load.Failed -> EmptyState(
            if (state.offline) stringResource(R.string.err_network) else state.message,
            icon = if (state.offline) Icons.Outlined.CloudOff else Icons.Outlined.Inbox,
            action = stringResource(R.string.action_retry), onAction = onRetry,
        )
        is Load.Ok -> content(state.value)
    }
}

@Composable
fun SkeletonRow() {
    Box(Modifier.fillMaxWidth().height(56.dp).clip(RoundedCornerShape(10.dp)).background(MaterialTheme.colorScheme.surfaceVariant))
}

@Composable
fun EmptyState(text: String, icon: androidx.compose.ui.graphics.vector.ImageVector = Icons.Outlined.Inbox, action: String? = null, onAction: (() -> Unit)? = null) {
    var visible by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) { visible = true }
    AnimatedVisibility(visible, enter = fadeIn() + slideInVertically { it / 8 }) {
        Column(Modifier.fillMaxWidth().padding(32.dp), horizontalAlignment = Alignment.CenterHorizontally) {
            Icon(icon, null, Modifier.size(48.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
            Spacer(Modifier.height(12.dp))
            Text(text, color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodyLarge)
            if (action != null && onAction != null) { Spacer(Modifier.height(12.dp)); OutlinedButton(onClick = onAction) { Text(action) } }
        }
    }
}

@Composable
fun KpiCard(title: String, value: String, modifier: Modifier = Modifier, accent: Color = MaterialTheme.colorScheme.secondary) {
    Card(modifier, colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
        Column(Modifier.padding(16.dp)) {
            Text(title, style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Spacer(Modifier.height(6.dp))
            Text(value, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold, color = accent)
        }
    }
}

@Composable
fun SectionTitle(text: String) {
    Text(text, style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp))
}

/** Biometric re-authentication for sensitive actions; falls back to running the action when no biometrics are enrolled. */
fun requireBiometric(activity: FragmentActivity, title: String, onSuccess: () -> Unit) {
    val can = BiometricManager.from(activity).canAuthenticate(BiometricManager.Authenticators.BIOMETRIC_STRONG or BiometricManager.Authenticators.DEVICE_CREDENTIAL)
    if (can != BiometricManager.BIOMETRIC_SUCCESS) { onSuccess(); return }
    val prompt = BiometricPrompt(activity, ContextCompat.getMainExecutor(activity), object : BiometricPrompt.AuthenticationCallback() {
        override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) = onSuccess()
    })
    prompt.authenticate(BiometricPrompt.PromptInfo.Builder().setTitle(title)
        .setAllowedAuthenticators(BiometricManager.Authenticators.BIOMETRIC_STRONG or BiometricManager.Authenticators.DEVICE_CREDENTIAL).build())
}
