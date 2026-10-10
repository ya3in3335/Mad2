package com.veltrix.admin.ui.common

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import com.veltrix.admin.ui.theme.Brand
import com.veltrix.admin.ui.theme.DarkTokens

/** Vertical bars with month labels. Mirrors in RTL so time still reads in the locale's direction. */
@Composable
fun BarChart(points: List<Pair<String, Double>>, modifier: Modifier = Modifier, description: String = "") {
    if (points.isEmpty()) { EmptyState(); return }
    val rtl = LocalLayoutDirection.current == LayoutDirection.Rtl
    val fractions = ChartMath.normalize(points.map { it.second })
    val grid = MaterialTheme.colorScheme.outline
    Column(modifier.semantics { contentDescription = description }) {
        Canvas(Modifier.fillMaxWidth().height(140.dp)) {
            val n = fractions.size
            val slot = size.width / n
            val barW = slot * 0.56f
            for (g in 1..3) {
                val y = size.height * g / 4f
                drawLine(grid.copy(alpha = 0.5f), Offset(0f, y), Offset(size.width, y), strokeWidth = 1f)
            }
            fractions.forEachIndexed { i, f ->
                val idx = if (rtl) n - 1 - i else i
                val h = (size.height * f).coerceAtLeast(if (points[i].second > 0) 3f else 0f)
                val x = idx * slot + (slot - barW) / 2
                drawRoundRect(
                    brush = Brush.verticalGradient(listOf(DarkTokens.accent, DarkTokens.primary, DarkTokens.violet), startY = size.height - h, endY = size.height),
                    topLeft = Offset(x, size.height - h), size = Size(barW, h), cornerRadius = CornerRadius(6f, 6f),
                )
            }
        }
        Row(Modifier.fillMaxWidth().padding(top = 4.dp)) {
            points.forEach { (label, _) ->
                Text(Formatters.shortMonth(label), Modifier.weight(1f), style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, textAlign = androidx.compose.ui.text.style.TextAlign.Center)
            }
        }
    }
}

/** Area/line chart for trends (activations). */
@Composable
fun LineChart(values: List<Double>, modifier: Modifier = Modifier, color: Color = DarkTokens.accent, description: String = "") {
    if (values.size < 2) { EmptyState(); return }
    val rtl = LocalLayoutDirection.current == LayoutDirection.Rtl
    val fractions = ChartMath.normalize(values)
    Canvas(modifier.fillMaxWidth().height(110.dp).semantics { contentDescription = description }) {
        val step = size.width / (fractions.size - 1)
        fun pt(i: Int): Offset {
            val x = if (rtl) size.width - i * step else i * step
            return Offset(x, size.height - fractions[i] * size.height * 0.9f - 4f)
        }
        val line = Path().apply { fractions.indices.forEach { i -> val p = pt(i); if (i == 0) moveTo(p.x, p.y) else lineTo(p.x, p.y) } }
        val area = Path().apply {
            addPath(line)
            lineTo(pt(fractions.lastIndex).x, size.height); lineTo(pt(0).x, size.height); close()
        }
        drawPath(area, Brush.verticalGradient(listOf(color.copy(alpha = 0.35f), DarkTokens.violet.copy(alpha = 0.05f))))
        drawPath(line, color, style = Stroke(width = 4f, cap = StrokeCap.Round))
        fractions.indices.forEach { drawCircle(color, 5f, pt(it)) }
    }
}

/** Donut with legend (e.g. Android vs Windows installs). */
@Composable
fun DonutChart(slices: List<Pair<String, Double>>, modifier: Modifier = Modifier, colors: List<Color> = Brand.chartPalette, centerText: String? = null) {
    val shares = ChartMath.shares(slices.map { it.second })
    val track = MaterialTheme.colorScheme.surfaceVariant
    Row(modifier, verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(110.dp), contentAlignment = Alignment.Center) {
            Canvas(Modifier.fillMaxSize().padding(6.dp)) {
                val stroke = Stroke(width = size.minDimension * 0.16f, cap = StrokeCap.Butt)
                drawArc(track, 0f, 360f, false, style = stroke)
                var start = -90f
                shares.forEachIndexed { i, s ->
                    val sweep = 360f * s
                    if (sweep > 0f) drawArc(colors[i % colors.size], start, sweep, false, style = stroke)
                    start += sweep
                }
            }
            if (centerText != null) Text(centerText, style = MaterialTheme.typography.titleMedium)
        }
        Spacer(Modifier.width(16.dp))
        Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
            slices.forEachIndexed { i, (label, value) ->
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Box(Modifier.size(10.dp).background(colors[i % colors.size], CircleShape))
                    Spacer(Modifier.width(8.dp))
                    Text("$label · ${Formatters.number(value.toLong())}", style = MaterialTheme.typography.bodyMedium)
                }
            }
        }
    }
}
