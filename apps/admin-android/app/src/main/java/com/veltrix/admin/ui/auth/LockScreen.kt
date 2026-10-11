package com.veltrix.admin.ui.auth

import androidx.activity.compose.LocalActivity
import androidx.compose.foundation.layout.*
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Fingerprint
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.fragment.app.FragmentActivity
import com.veltrix.admin.R
import com.veltrix.admin.data.security.SessionLockPolicy
import com.veltrix.admin.ui.common.BrandedPage

/**
 * Mandatory gate on every cold start and after [SessionLockPolicy.DEFAULT_GRACE_MS] in background. There is no bypass:
 * without a secure screen lock the app (and its Keystore signing key) cannot be used at all.
 */
@Composable
fun LockScreen(lockPolicy: SessionLockPolicy, onUnlocked: () -> Unit) {
    val context = LocalContext.current
    val activity = LocalActivity.current as? FragmentActivity
    val title = stringResource(R.string.bio_prompt_title)
    var failure by remember { mutableStateOf<String?>(null) }
    var available by remember { mutableStateOf(Biometrics.available(context)) }

    fun prompt() {
        available = Biometrics.available(context)
        val a = activity ?: return
        if (!available) return
        failure = null
        lockPolicy.authenticating = true
        Biometrics.prompt(a, title, null,
            onSuccess = { lockPolicy.authenticating = false; onUnlocked() },
            onFailure = { _, msg -> lockPolicy.authenticating = false; failure = msg.toString() })
    }
    LaunchedEffect(Unit) { prompt() }

    BrandedPage {
        Text(stringResource(R.string.lock_title), style = MaterialTheme.typography.titleLarge, modifier = Modifier.align(Alignment.CenterHorizontally))
        Spacer(Modifier.height(16.dp))
        if (!available) Text(stringResource(R.string.lock_unavailable), color = MaterialTheme.colorScheme.error, textAlign = TextAlign.Center,
            modifier = Modifier.fillMaxWidth())
        failure?.let { Text(it, color = MaterialTheme.colorScheme.error, textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth()) }
        Spacer(Modifier.height(16.dp))
        Button(onClick = ::prompt, modifier = Modifier.fillMaxWidth().height(50.dp)) {
            Icon(Icons.Outlined.Fingerprint, null); Spacer(Modifier.width(8.dp))
            Text(stringResource(if (available) R.string.action_unlock else R.string.action_retry))
        }
    }
}
