package com.veltrix.admin.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp
import com.veltrix.admin.ui.common.Tone

// The admin console is always dark: navy surfaces, electric-blue primary, cyan highlights, subtle violet gradients.
private val AdminDark = darkColorScheme(
    primary = DarkTokens.primary, onPrimary = DarkTokens.textPrimary, primaryContainer = DarkTokens.primaryButton, onPrimaryContainer = DarkTokens.textPrimary,
    secondary = DarkTokens.accent, onSecondary = DarkTokens.background, tertiary = DarkTokens.violet, onTertiary = DarkTokens.textPrimary,
    background = DarkTokens.background, onBackground = DarkTokens.textPrimary, surface = DarkTokens.surface, onSurface = DarkTokens.textPrimary,
    surfaceVariant = DarkTokens.surfaceRaised, onSurfaceVariant = DarkTokens.textSecondary, surfaceContainer = DarkTokens.surface,
    surfaceContainerHigh = DarkTokens.surfaceRaised, surfaceContainerHighest = DarkTokens.surfaceRaised, surfaceContainerLow = DarkTokens.surface,
    outline = DarkTokens.border, outlineVariant = DarkTokens.border, error = DarkTokens.danger, onError = DarkTokens.textPrimary,
)

private val AdminTypography = Typography(
    headlineMedium = TextStyle(fontWeight = FontWeight.SemiBold, fontSize = 26.sp, letterSpacing = 0.2.sp),
    headlineSmall = TextStyle(fontWeight = FontWeight.SemiBold, fontSize = 22.sp),
    titleLarge = TextStyle(fontWeight = FontWeight.SemiBold, fontSize = 20.sp),
    titleMedium = TextStyle(fontWeight = FontWeight.Medium, fontSize = 16.sp),
    titleSmall = TextStyle(fontWeight = FontWeight.Medium, fontSize = 14.sp),
    bodyLarge = TextStyle(fontSize = 16.sp), bodyMedium = TextStyle(fontSize = 14.sp), bodySmall = TextStyle(fontSize = 12.sp),
    labelLarge = TextStyle(fontWeight = FontWeight.Medium, fontSize = 14.sp), labelMedium = TextStyle(fontWeight = FontWeight.Medium, fontSize = 12.sp),
    labelSmall = TextStyle(fontWeight = FontWeight.Medium, fontSize = 11.sp),
)

@Composable
fun AdminTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = AdminDark, typography = AdminTypography, content = content)
}

object Brand {
    /** Subtle navy → violet wash used behind headers and the sign-in screen. */
    val backdrop = Brush.verticalGradient(listOf(DarkTokens.background, Color(0xFF0B1630), Color(0xFF1A1440)))
    /** Electric blue → cyan accent used on highlighted KPI values and chart fills. */
    val accent = Brush.horizontalGradient(listOf(DarkTokens.primary, DarkTokens.accent))
    val violetGlow = Brush.linearGradient(listOf(DarkTokens.violet.copy(alpha = 0.35f), DarkTokens.primary.copy(alpha = 0.10f)))
    val chartPalette = listOf(DarkTokens.primary, DarkTokens.accent, DarkTokens.violet, DarkTokens.success, DarkTokens.warning, DarkTokens.danger)

    fun tone(t: Tone): Color = when (t) {
        Tone.Positive -> DarkTokens.success
        Tone.Warning -> DarkTokens.warning
        Tone.Negative -> DarkTokens.danger
        Tone.Info -> DarkTokens.accent
        Tone.Neutral -> DarkTokens.textSecondary
    }
}
