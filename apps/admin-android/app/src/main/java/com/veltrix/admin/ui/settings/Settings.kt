package com.veltrix.admin.ui.settings

import androidx.appcompat.app.AppCompatDelegate
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.CloudOff
import androidx.compose.material.icons.outlined.Language
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.core.os.LocaleListCompat
import com.veltrix.admin.BuildConfig
import com.veltrix.admin.R
import com.veltrix.admin.data.settings.AdminSettings
import com.veltrix.admin.ui.common.SectionHeader

val LANGUAGES = listOf("ar" to "العربية", "fr" to "Français", "en" to "English")

/** Language choice (per-app locale via AppCompat). */
@Composable
fun LanguagePicker(settings: AdminSettings, onDismiss: () -> Unit) {
    AlertDialog(onDismissRequest = onDismiss, confirmButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.action_close)) } },
        title = { Text(stringResource(R.string.settings_language)) },
        text = {
            Column {
                LANGUAGES.forEach { (tag, name) ->
                    Row(Modifier.fillMaxWidth().clickable {
                        settings.language = tag; settings.languageChosen = true
                        AppCompatDelegate.setApplicationLocales(LocaleListCompat.forLanguageTags(tag))
                        onDismiss()
                    }.padding(vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                        RadioButton(selected = settings.language == tag && settings.languageChosen, onClick = null)
                        Spacer(Modifier.width(8.dp)); Text(name)
                    }
                }
            }
        })
}

@Composable
fun SettingsScreen(settings: AdminSettings) {
    var showLanguage by remember { mutableStateOf(false) }
    LazyColumn(Modifier.fillMaxSize()) {
        item { SectionHeader(stringResource(R.string.settings_general), Modifier.padding(horizontal = 16.dp)) }
        item {
            ListItem(headlineContent = { Text(stringResource(R.string.settings_language)) },
                supportingContent = { Text(LANGUAGES.firstOrNull { it.first == settings.language }?.second ?: settings.language) },
                leadingContent = { Icon(Icons.Outlined.Language, null) }, modifier = Modifier.clickable { showLanguage = true })
        }
        item { SectionHeader(stringResource(R.string.settings_security), Modifier.padding(horizontal = 16.dp)) }
        item {
            ListItem(headlineContent = { Text(stringResource(R.string.settings_lock)) },
                supportingContent = { Text(stringResource(R.string.settings_lock_hint)) }, leadingContent = { Icon(Icons.Outlined.Lock, null) })
            ListItem(headlineContent = { Text(stringResource(R.string.settings_offline)) },
                supportingContent = { Text(stringResource(R.string.settings_offline_hint)) }, leadingContent = { Icon(Icons.Outlined.CloudOff, null) })
        }
        item {
            val label = stringResource(R.string.build_label)
            Text(stringResource(R.string.settings_version, BuildConfig.VERSION_NAME) + if (label.isNotBlank()) " · $label" else "",
                Modifier.padding(16.dp), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
    if (showLanguage) LanguagePicker(settings, onDismiss = { showLanguage = false })
}
