package com.veltrix.os.ui.splash

import android.provider.Settings
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.translate
import androidx.compose.ui.graphics.drawscope.withTransform
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.veltrix.os.R
import com.veltrix.os.ui.theme.DarkTokens
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** Symbol geometry on a 512 grid — identical to branding/tools/symbol.py. */
private object Geometry {
    val left = listOf(84f to 104f, 184f to 104f, 282f to 356f, 240f to 432f)
    val right = listOf(428f to 104f, 346f to 104f, 282f to 268f, 322f to 336f)
    val trace = (156f to 156f) to (226f to 316f)
    val core = 300f to 384f
}

private fun polygon(points: List<Pair<Float, Float>>, s: Float) = Path().apply {
    moveTo(points[0].first * s, points[0].second * s)
    points.drop(1).forEach { lineTo(it.first * s, it.second * s) }
    close()
}

private val Ease = CubicBezierEasing(0.2f, 0.8f, 0.2f, 1f)

/**
 * Cinematic startup: particles → blades assemble → trace draws → core glows → wordmark → (real) initialization indicator.
 * [ready] is driven by actual startup work; the indicator only shows while that work is still running.
 * Respects the system "Remove animations" setting with a static fallback.
 */
@Composable
fun AnimatedSplash(ready: Boolean, onFinished: () -> Unit) {
    val context = LocalContext.current
    val reducedMotion = remember {
        Settings.Global.getFloat(context.contentResolver, Settings.Global.ANIMATOR_DURATION_SCALE, 1f) == 0f
    }
    val bg = remember { Animatable(if (reducedMotion) 1f else 0f) }
    val leftA = remember { Animatable(if (reducedMotion) 1f else 0f) }
    val rightA = remember { Animatable(if (reducedMotion) 1f else 0f) }
    val traceA = remember { Animatable(if (reducedMotion) 1f else 0f) }
    val coreA = remember { Animatable(if (reducedMotion) 1f else 0f) }
    val wordA = remember { Animatable(if (reducedMotion) 1f else 0f) }
    var brandDone by remember { mutableStateOf(reducedMotion) }

    LaunchedEffect(Unit) {
        if (reducedMotion) return@LaunchedEffect
        launch { bg.animateTo(1f, tween(450)) }
        delay(300); launch { leftA.animateTo(1f, tween(900, easing = Ease)) }
        delay(150); launch { rightA.animateTo(1f, tween(900, easing = Ease)) }
        delay(350); launch { traceA.animateTo(1f, tween(700)) }
        delay(500); launch { coreA.animateTo(1f, tween(500)) }
        delay(200); wordA.animateTo(1f, tween(600))
        brandDone = true
    }
    LaunchedEffect(brandDone, ready) {
        if (brandDone && ready) { delay(if (reducedMotion) 150 else 250); onFinished() }
    }

    val particles = remember { List(14) { i -> ((i * 97) % 360) / 360f to ((i * 53) % 100) / 100f } }
    val drift = rememberInfiniteTransition(label = "particles")
    val driftY by drift.animateFloat(0f, 1f, infiniteRepeatable(tween(6000, easing = LinearEasing), RepeatMode.Restart), label = "drift")

    Box(Modifier.fillMaxSize().background(DarkTokens.background).semantics { contentDescription = "VELTRIX OS" }, contentAlignment = Alignment.Center) {
        Canvas(Modifier.fillMaxSize()) {
            if (!reducedMotion) particles.forEach { (px, py) ->
                val y = ((py + 1f - driftY) % 1f) * size.height
                drawCircle(DarkTokens.accent.copy(alpha = 0.25f * bg.value), radius = 2.dp.toPx(), center = Offset(px * size.width, y))
            }
        }
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Canvas(Modifier.size(180.dp)) { drawSymbol(leftA.value, rightA.value, traceA.value, coreA.value) }
            Spacer(Modifier.height(24.dp))
            Box(Modifier.offset(y = ((1f - wordA.value) * 12).dp)) {
                Row {
                    Text("VELTRIX ", color = DarkTokens.textPrimary.copy(alpha = wordA.value), fontSize = 30.sp, fontWeight = FontWeight.Light, letterSpacing = 6.sp)
                    Text("OS", color = DarkTokens.accent.copy(alpha = wordA.value), fontSize = 30.sp, fontWeight = FontWeight.Light, letterSpacing = 6.sp)
                }
            }
            Spacer(Modifier.height(8.dp))
            Text(stringResource(R.string.app_slogan), color = DarkTokens.textSecondary.copy(alpha = wordA.value), fontSize = 13.sp)
            Spacer(Modifier.height(32.dp))
            // Shown only while genuine startup work is still in progress after the brand moment.
            if (brandDone && !ready) {
                LinearProgressIndicator(Modifier.width(120.dp).height(2.dp), color = DarkTokens.accent, trackColor = DarkTokens.surfaceRaised)
                Spacer(Modifier.height(8.dp))
                Text(stringResource(R.string.app_initializing), color = DarkTokens.textSecondary, fontSize = 12.sp)
            } else Spacer(Modifier.height(26.dp))
        }
    }
}

private fun DrawScope.drawSymbol(left: Float, right: Float, trace: Float, core: Float) {
    val s = size.width / 512f
    val rise = 24f * s
    translate(top = (1f - left) * rise) {
        drawPath(polygon(Geometry.left, s), Brush.linearGradient(listOf(DarkTokens.primary, DarkTokens.accent), Offset(84 * s, 104 * s), Offset(260 * s, 432 * s)), alpha = left)
    }
    translate(top = (1f - right) * rise) {
        drawPath(polygon(Geometry.right, s), Brush.linearGradient(listOf(DarkTokens.violet, DarkTokens.primary), Offset(428 * s, 104 * s), Offset(300 * s, 336 * s)), alpha = right)
    }
    val (a, b) = Geometry.trace
    if (trace > 0f) {
        val end = Offset(a.first * s + (b.first - a.first) * s * trace, a.second * s + (b.second - a.second) * s * trace)
        drawLine(DarkTokens.textPrimary.copy(alpha = 0.85f), Offset(a.first * s, a.second * s), end, strokeWidth = 6f * s, cap = StrokeCap.Round)
        drawCircle(DarkTokens.textPrimary.copy(alpha = 0.85f * trace), 9f * s, Offset(a.first * s, a.second * s))
        drawCircle(DarkTokens.textPrimary.copy(alpha = 0.85f * trace), 9f * s, Offset(b.first * s, b.second * s))
    }
    if (core > 0f) withTransform({ scale(0.6f + 0.4f * core, 0.6f + 0.4f * core, Offset(Geometry.core.first * s, Geometry.core.second * s)) }) {
        drawCircle(DarkTokens.accent.copy(alpha = 0.25f * core), 26f * s, Offset(Geometry.core.first * s, Geometry.core.second * s))
        drawCircle(DarkTokens.accent.copy(alpha = core), 14f * s, Offset(Geometry.core.first * s, Geometry.core.second * s))
    }
}

/** Static brand emblem (empty states, about screen, license activation success). */
@Composable
fun BrandEmblem(modifier: Modifier = Modifier.size(96.dp), progress: Float = 1f) {
    Canvas(modifier) { drawSymbol(progress, progress, progress, progress) }
}

@Suppress("unused") private val unusedColor = Color.Transparent
