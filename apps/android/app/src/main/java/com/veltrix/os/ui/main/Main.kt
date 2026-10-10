package com.veltrix.os.ui.main

import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.core.tween
import androidx.compose.foundation.layout.*
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.*
import androidx.compose.material3.*
import androidx.compose.material3.windowsizeclass.ExperimentalMaterial3WindowSizeClassApi
import androidx.compose.material3.windowsizeclass.WindowWidthSizeClass
import androidx.compose.material3.windowsizeclass.calculateWindowSizeClass
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.navigation.NavHostController
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import com.veltrix.os.R
import com.veltrix.os.data.api.AppSettings
import com.veltrix.os.data.repo.LicenseRepository
import com.veltrix.os.data.repo.SessionRepository
import com.veltrix.os.data.repo.SyncRepository
import com.veltrix.os.data.security.LicenseState
import com.veltrix.os.data.work.SyncWorker
import com.veltrix.os.ui.screens.*
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject

@HiltViewModel
class MainViewModel @Inject constructor(val session: SessionRepository, val sync: SyncRepository, val license: LicenseRepository, val settings: AppSettings) : ViewModel()

data class Dest(val route: String, val label: Int, val icon: ImageVector)

val primaryDestinations = listOf(
    Dest("dashboard", R.string.nav_dashboard, Icons.Outlined.Dashboard),
    Dest("pos", R.string.nav_pos, Icons.Outlined.PointOfSale),
    Dest("catalog", R.string.nav_products, Icons.Outlined.Inventory2),
    Dest("customers", R.string.nav_customers, Icons.Outlined.People),
    Dest("more", R.string.nav_settings, Icons.Outlined.Menu),
)

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3WindowSizeClassApi::class)
@Composable
fun MainScreen(vm: MainViewModel = hiltViewModel()) {
    val nav = rememberNavController()
    val activity = androidx.activity.compose.LocalActivity.current!!
    val wide = calculateWindowSizeClass(activity).widthSizeClass != WindowWidthSizeClass.Compact
    val backStack by nav.currentBackStackEntryAsState()
    val current = backStack?.destination?.route
    val pending by vm.sync.pendingCount.collectAsState(0)
    val attention by vm.sync.attentionCount.collectAsState(0)
    val licenseState by vm.license.state.collectAsState()

    val content: @Composable (PaddingValues) -> Unit = { padding ->
        Column(Modifier.padding(padding)) {
            LicenseBanner(licenseState, vm)
            if (pending > 0 || attention > 0) SyncBanner(pending, attention) { nav.navigate("sync") }
            Host(nav, Modifier.weight(1f))
        }
    }
    val context = LocalContext.current
    val topBar: @Composable () -> Unit = {
        TopAppBar(title = { Text(vm.settings.tenantName ?: stringResource(R.string.app_name)) }, actions = {
            IconButton(onClick = { SyncWorker.syncNow(context) }) { Icon(Icons.Outlined.Sync, stringResource(R.string.action_sync)) }
            IconButton(onClick = { nav.navigate("notifications") }) { Icon(Icons.Outlined.Notifications, stringResource(R.string.nav_notifications)) }
            IconButton(onClick = { nav.navigate("scanner") }) { Icon(Icons.Outlined.QrCodeScanner, stringResource(R.string.nav_scanner)) }
        })
    }
    if (wide) {
        Row {
            NavigationRail {
                primaryDestinations.forEach { d ->
                    NavigationRailItem(selected = current == d.route, onClick = { navigateTop(nav, d.route) }, icon = { Icon(d.icon, null) },
                        label = { Text(stringResource(d.label)) })
                }
            }
            Scaffold(topBar = topBar, content = content)
        }
    } else {
        Scaffold(topBar = topBar, bottomBar = {
            NavigationBar {
                primaryDestinations.forEach { d ->
                    NavigationBarItem(selected = current == d.route, onClick = { navigateTop(nav, d.route) }, icon = { Icon(d.icon, null) },
                        label = { Text(stringResource(d.label), maxLines = 1) })
                }
            }
        }, content = content)
    }
}

private fun navigateTop(nav: NavHostController, route: String) = nav.navigate(route) {
    popUpTo("dashboard") { saveState = true }
    launchSingleTop = true
    restoreState = true
}

@Composable
private fun Host(nav: NavHostController, modifier: Modifier) {
    NavHost(nav, "dashboard", modifier, enterTransition = { fadeIn(tween(180)) }, exitTransition = { fadeOut(tween(120)) }) {
        composable("dashboard") { DashboardScreen() }
        composable("pos") { PosScreen(onScan = { nav.navigate("scanner") }) }
        composable("catalog") { CatalogScreen() }
        composable("customers") { CustomersScreen() }
        composable("more") { MoreScreen { nav.navigate(it) } }
        composable("inventory") { InventoryScreen() }
        composable("orders") { OrdersScreen() }
        composable("expenses") { ExpensesScreen() }
        composable("reports") { ReportsScreen() }
        composable("employees") { EmployeesScreen() }
        composable("notifications") { NotificationsScreen() }
        composable("branch") { BranchScreen() }
        composable("license") { LicenseScreen() }
        composable("settings") { SettingsScreen() }
        composable("profile") { ProfileScreen() }
        composable("sync") { SyncIssuesScreen() }
        composable("scanner") { ScannerScreen(onDone = { nav.popBackStack() }) }
    }
}

@Composable
private fun SyncBanner(pending: Int, attention: Int, onClick: () -> Unit) {
    Surface(onClick = onClick, color = MaterialTheme.colorScheme.surfaceVariant, modifier = Modifier.fillMaxWidth()) {
        Text(if (attention > 0) stringResource(R.string.sync_error) else stringResource(R.string.sync_pending, pending.toString()),
            Modifier.padding(horizontal = 16.dp, vertical = 8.dp), style = MaterialTheme.typography.bodyMedium)
    }
}

@Composable
private fun LicenseBanner(state: LicenseState, vm: MainViewModel) {
    val text = when (state) {
        is LicenseState.Expired -> stringResource(R.string.lic_expired)
        is LicenseState.ClockRollback -> stringResource(R.string.lic_clockRollback)
        is LicenseState.WrongDevice -> stringResource(R.string.lic_revoked)
        is LicenseState.Valid -> vm.license.summary?.daysRemaining?.takeIf { it in 0..14 }?.let { stringResource(R.string.lic_renewSoon, it.toString()) }
        else -> null
    } ?: return
    Surface(color = MaterialTheme.colorScheme.errorContainer, modifier = Modifier.fillMaxWidth()) {
        Text(text, Modifier.padding(horizontal = 16.dp, vertical = 8.dp), color = MaterialTheme.colorScheme.onErrorContainer, style = MaterialTheme.typography.bodyMedium)
    }
}
