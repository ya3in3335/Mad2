package com.veltrix.admin.ui

import androidx.annotation.StringRes
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.*
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ReceiptLong
import androidx.compose.material.icons.outlined.Key
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.material.icons.outlined.VpnKey
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.fragment.app.FragmentActivity
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.veltrix.admin.R
import com.veltrix.admin.data.security.SessionLockPolicy
import com.veltrix.admin.ui.auth.Biometrics
import com.veltrix.admin.ui.history.HistoryScreen
import com.veltrix.admin.ui.issue.IssueScreen
import com.veltrix.admin.ui.key.KeyScreen
import com.veltrix.admin.ui.key.KeySetupScreen
import com.veltrix.admin.ui.settings.SettingsScreen

private val Tab.label: Int
    @StringRes get() = when (this) {
        Tab.Issue -> R.string.nav_issue
        Tab.History -> R.string.nav_history
        Tab.Key -> R.string.nav_key
        Tab.Settings -> R.string.nav_settings
    }

private val Tab.icon: ImageVector
    get() = when (this) {
        Tab.Issue -> Icons.Outlined.VpnKey
        Tab.History -> Icons.AutoMirrored.Outlined.ReceiptLong
        Tab.Key -> Icons.Outlined.Key
        Tab.Settings -> Icons.Outlined.Settings
    }

/** Runs [onOk] after a fresh biometric / device-credential confirmation (required by the Keystore key for every use). */
fun confirmWithUser(activity: FragmentActivity?, lockPolicy: SessionLockPolicy, title: String, onOk: () -> Unit, onFail: (String) -> Unit = {}) {
    val a = activity ?: return
    lockPolicy.authenticating = true
    Biometrics.prompt(a, title, null,
        onSuccess = { lockPolicy.authenticating = false; onOk() },
        onFailure = { _, msg -> lockPolicy.authenticating = false; onFail(msg.toString()) })
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AppShell(lockPolicy: SessionLockPolicy, vm: IssuerViewModel = hiltViewModel()) {
    val tab by vm.tab.collectAsStateWithLifecycle()
    val key by vm.key.collectAsStateWithLifecycle()
    val message by vm.message.collectAsStateWithLifecycle()
    val snackbar = remember { SnackbarHostState() }
    val context = LocalContext.current
    LaunchedEffect(message) {
        message?.let { snackbar.showSnackbar(context.getString(it)); vm.message.value = null }
    }

    Scaffold(
        containerColor = MaterialTheme.colorScheme.background,
        snackbarHost = { SnackbarHost(snackbar) },
        topBar = {
            TopAppBar(
                title = {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Image(painterResource(R.drawable.ic_veltrix_emblem), null, Modifier.size(28.dp))
                        Spacer(Modifier.width(10.dp))
                        Text(stringResource(tab.label), maxLines = 1)
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(containerColor = MaterialTheme.colorScheme.background),
            )
        },
        bottomBar = {
            NavigationBar(containerColor = MaterialTheme.colorScheme.surface) {
                Tab.entries.forEach { t ->
                    NavigationBarItem(selected = t == tab, onClick = { vm.tab.value = t }, icon = { Icon(t.icon, null) },
                        label = { Text(stringResource(t.label), maxLines = 1) })
                }
            }
        },
    ) { padding ->
        Box(Modifier.padding(padding).consumeWindowInsets(padding).fillMaxSize()) {
            when {
                (tab == Tab.Issue || tab == Tab.Key) && !key.exists -> KeySetupScreen(vm)
                tab == Tab.Issue -> IssueScreen(vm, lockPolicy)
                tab == Tab.History -> HistoryScreen(vm)
                tab == Tab.Key -> KeyScreen(vm, lockPolicy)
                else -> SettingsScreen(vm.settings)
            }
        }
    }
}
