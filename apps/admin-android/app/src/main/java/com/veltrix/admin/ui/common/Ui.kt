package com.veltrix.admin.ui.common

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDirection
import androidx.compose.ui.unit.dp
import com.veltrix.admin.R
import com.veltrix.admin.ui.theme.Brand
import java.text.DateFormat
import java.util.Date

fun copyText(context: Context, label: String, text: String) {
    val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
    cm.setPrimaryClip(ClipData.newPlainText(label, text))
}

fun shareText(context: Context, subject: String, text: String) {
    val send = Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_SUBJECT, subject).putExtra(Intent.EXTRA_TEXT, text)
    context.startActivity(Intent.createChooser(send, subject))
}

fun formatDate(epochSeconds: Long): String = DateFormat.getDateInstance(DateFormat.MEDIUM).format(Date(epochSeconds * 1000))

fun formatDateTime(epochSeconds: Long): String =
    DateFormat.getDateTimeInstance(DateFormat.MEDIUM, DateFormat.SHORT).format(Date(epochSeconds * 1000))

/** Full-screen branded page (lock screen, first-run key setup). */
@Composable
fun BrandedPage(content: @Composable ColumnScope.() -> Unit) {
    Box(Modifier.fillMaxSize().background(Brand.backdrop)) {
        Column(
            Modifier.fillMaxSize().systemBarsPadding().imePadding().verticalScroll(rememberScrollState()).padding(24.dp),
            horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center,
        ) {
            Column(Modifier.widthIn(max = 440.dp).fillMaxWidth()) {
                Image(painterResource(R.drawable.ic_veltrix_emblem), null, Modifier.size(96.dp).align(Alignment.CenterHorizontally))
                Text(stringResource(R.string.app_name), style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.Bold,
                    modifier = Modifier.align(Alignment.CenterHorizontally))
                Text(stringResource(R.string.app_tagline), color = MaterialTheme.colorScheme.secondary, style = MaterialTheme.typography.bodyMedium,
                    modifier = Modifier.align(Alignment.CenterHorizontally))
                val label = stringResource(R.string.build_label)
                if (label.isNotBlank()) Text(label, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.labelSmall,
                    modifier = Modifier.align(Alignment.CenterHorizontally).padding(top = 4.dp))
                Spacer(Modifier.height(24.dp))
                content()
            }
        }
    }
}

@Composable
fun SectionHeader(text: String, modifier: Modifier = Modifier) {
    Text(text, style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.secondary,
        modifier = modifier.padding(top = 16.dp, bottom = 8.dp))
}

/** Monospace, selectable, always left-to-right (codes and keys must not be mirrored in Arabic). */
@Composable
fun CodeBox(text: String, modifier: Modifier = Modifier) {
    Surface(modifier.fillMaxWidth(), shape = RoundedCornerShape(12.dp), color = MaterialTheme.colorScheme.surfaceVariant) {
        SelectionContainer {
            Text(text, Modifier.padding(12.dp), fontFamily = FontFamily.Monospace,
                style = MaterialTheme.typography.bodySmall.copy(textDirection = TextDirection.Ltr))
        }
    }
}

@Composable
fun WarningCard(title: String, body: String, modifier: Modifier = Modifier, color: Color = MaterialTheme.colorScheme.error) {
    Card(modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = color.copy(alpha = 0.12f))) {
        Column(Modifier.padding(16.dp)) {
            Text(title, style = MaterialTheme.typography.titleSmall, color = color, fontWeight = FontWeight.Bold)
            Spacer(Modifier.height(4.dp))
            Text(body, style = MaterialTheme.typography.bodyMedium)
        }
    }
}
