package com.veltrix.admin.ui.nav

import androidx.annotation.StringRes
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.automirrored.outlined.ReceiptLong
import androidx.compose.material.icons.automirrored.outlined.Send
import androidx.compose.material.icons.outlined.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.navigation.NavGraph.Companion.findStartDestination
import androidx.navigation.NavType
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import androidx.navigation.navArgument
import com.veltrix.admin.R
import com.veltrix.admin.ui.audit.AuditScreen
import com.veltrix.admin.ui.billing.BillingScreen
import com.veltrix.admin.ui.common.LocalSnackbar
import com.veltrix.admin.ui.dashboard.DashboardScreen
import com.veltrix.admin.ui.devices.DevicesScreen
import com.veltrix.admin.ui.diagnostics.DiagnosticsScreen
import com.veltrix.admin.ui.licenses.LicenseDetailScreen
import com.veltrix.admin.ui.licenses.LicensesScreen
import com.veltrix.admin.ui.releases.ReleasesScreen
import com.veltrix.admin.ui.settings.SettingsScreen
import com.veltrix.admin.ui.support.SupportScreen
import com.veltrix.admin.ui.telegram.TelegramScreen
import com.veltrix.admin.ui.tenants.TenantDetailScreen
import com.veltrix.admin.ui.tenants.TenantsScreen
import com.veltrix.admin.ui.theme.Brand
import kotlinx.coroutines.launch

enum class Dest(val route: String, @StringRes val label: Int, val icon: ImageVector) {
    Dashboard("dashboard", R.string.nav_dashboard, Icons.Outlined.Dashboard),
    Tenants("tenants", R.string.nav_tenants, Icons.Outlined.Business),
    Devices("devices", R.string.nav_devices, Icons.Outlined.Devices),
    Licenses("licenses", R.string.nav_licenses, Icons.Outlined.VpnKey),
    Billing("billing", R.string.nav_billing, Icons.Outlined.Payments),
    Diagnostics("diagnostics", R.string.nav_diagnostics, Icons.Outlined.BugReport),
    Support("support", R.string.nav_support, Icons.Outlined.SupportAgent),
    Releases("releases", R.string.nav_releases, Icons.Outlined.NewReleases),
    Audit("audit", R.string.nav_audit, Icons.AutoMirrored.Outlined.ReceiptLong),
    Telegram("telegram", R.string.nav_telegram, Icons.AutoMirrored.Outlined.Send),
    Settings("settings", R.string.nav_settings, Icons.Outlined.Settings),
}

private const val TENANT_ROUTE = "tenant/{id}"
private const val LICENSE_ROUTE = "license/{id}"

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AdminShell(onSignedOut: () -> Unit, onChangeServer: () -> Unit) {
    val nav = rememberNavController()
    val drawer = rememberDrawerState(DrawerValue.Closed)
    val scope = rememberCoroutineScope()
    val snackbar = remember { SnackbarHostState() }
    val entry by nav.currentBackStackEntryAsState()
    val route = entry?.destination?.route
    val top = Dest.entries.firstOrNull { it.route == route }
    val title = when (route) {
        TENANT_ROUTE -> stringResource(R.string.tenant_detail)
        LICENSE_ROUTE -> stringResource(R.string.license_detail)
        else -> stringResource(top?.label ?: R.string.app_name)
    }

    fun go(d: Dest) {
        nav.navigate(d.route) {
            popUpTo(nav.graph.findStartDestination().id) { saveState = true }
            launchSingleTop = true
            restoreState = true
        }
        scope.launch { drawer.close() }
    }

    CompositionLocalProvider(LocalSnackbar provides snackbar) {
        ModalNavigationDrawer(drawerState = drawer, gesturesEnabled = top != null, drawerContent = {
            ModalDrawerSheet(drawerContainerColor = MaterialTheme.colorScheme.background) {
                Column(Modifier.verticalScroll(rememberScrollState())) {
                    Row(Modifier.fillMaxWidth().background(Brand.backdrop).padding(20.dp), verticalAlignment = Alignment.CenterVertically) {
                        Image(painterResource(R.drawable.ic_veltrix_emblem), null, Modifier.size(44.dp))
                        Spacer(Modifier.width(12.dp))
                        Column {
                            Text(stringResource(R.string.app_name), style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Bold)
                            Text(stringResource(R.string.app_tagline), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.secondary)
                        }
                    }
                    Spacer(Modifier.height(8.dp))
                    Dest.entries.forEach { d ->
                        NavigationDrawerItem(
                            label = { Text(stringResource(d.label)) }, icon = { Icon(d.icon, null) }, selected = d == top, onClick = { go(d) },
                            modifier = Modifier.padding(horizontal = 12.dp),
                            colors = NavigationDrawerItemDefaults.colors(selectedContainerColor = MaterialTheme.colorScheme.primary.copy(alpha = 0.18f),
                                selectedIconColor = MaterialTheme.colorScheme.secondary, selectedTextColor = MaterialTheme.colorScheme.onSurface),
                        )
                    }
                }
            }
        }) {
            Scaffold(
                containerColor = MaterialTheme.colorScheme.background,
                snackbarHost = { SnackbarHost(snackbar) },
                topBar = {
                    TopAppBar(
                        title = { Text(title, maxLines = 1) },
                        navigationIcon = {
                            if (top != null) IconButton(onClick = { scope.launch { drawer.open() } }) { Icon(Icons.Outlined.Menu, stringResource(R.string.nav_menu)) }
                            else IconButton(onClick = { nav.popBackStack() }) { Icon(Icons.AutoMirrored.Outlined.ArrowBack, stringResource(R.string.action_back)) }
                        },
                        colors = TopAppBarDefaults.topAppBarColors(containerColor = MaterialTheme.colorScheme.background),
                    )
                },
            ) { padding ->
                NavHost(nav, startDestination = Dest.Dashboard.route, modifier = Modifier.padding(padding).consumeWindowInsets(padding)) {
                    composable(Dest.Dashboard.route) { DashboardScreen(onOpen = ::go) }
                    composable(Dest.Tenants.route) { TenantsScreen(onOpen = { nav.navigate("tenant/$it") }) }
                    composable(TENANT_ROUTE, arguments = listOf(navArgument("id") { type = NavType.StringType })) {
                        TenantDetailScreen(onOpenLicense = { id -> nav.navigate("license/$id") })
                    }
                    composable(Dest.Devices.route) { DevicesScreen() }
                    composable(Dest.Licenses.route) { LicensesScreen(onOpen = { nav.navigate("license/$it") }) }
                    composable(LICENSE_ROUTE, arguments = listOf(navArgument("id") { type = NavType.StringType })) { LicenseDetailScreen() }
                    composable(Dest.Billing.route) { BillingScreen() }
                    composable(Dest.Diagnostics.route) { DiagnosticsScreen() }
                    composable(Dest.Support.route) { SupportScreen() }
                    composable(Dest.Releases.route) { ReleasesScreen() }
                    composable(Dest.Audit.route) { AuditScreen() }
                    composable(Dest.Telegram.route) { TelegramScreen() }
                    composable(Dest.Settings.route) { SettingsScreen(onSignedOut = onSignedOut, onChangeServer = onChangeServer) }
                }
            }
        }
    }
}
