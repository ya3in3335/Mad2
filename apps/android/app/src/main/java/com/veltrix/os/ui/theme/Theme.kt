package com.veltrix.os.ui.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

private val Dark = darkColorScheme(
    primary = DarkTokens.primary, onPrimary = DarkTokens.textPrimary, secondary = DarkTokens.accent, tertiary = DarkTokens.violet,
    background = DarkTokens.background, onBackground = DarkTokens.textPrimary, surface = DarkTokens.surface, onSurface = DarkTokens.textPrimary,
    surfaceVariant = DarkTokens.surfaceRaised, onSurfaceVariant = DarkTokens.textSecondary, outline = DarkTokens.border, error = DarkTokens.danger,
)
private val Light = lightColorScheme(
    primary = LightTokens.primary, secondary = LightTokens.accent, tertiary = LightTokens.violet, background = LightTokens.background,
    onBackground = LightTokens.textPrimary, surface = LightTokens.surface, onSurface = LightTokens.textPrimary, surfaceVariant = LightTokens.surfaceRaised,
    onSurfaceVariant = LightTokens.textSecondary, outline = LightTokens.border, error = LightTokens.danger,
)

private val VeltrixTypography = Typography(
    headlineMedium = TextStyle(fontWeight = FontWeight.SemiBold, fontSize = 26.sp, letterSpacing = 0.2.sp),
    titleLarge = TextStyle(fontWeight = FontWeight.SemiBold, fontSize = 20.sp),
    titleMedium = TextStyle(fontWeight = FontWeight.Medium, fontSize = 16.sp),
    bodyLarge = TextStyle(fontSize = 16.sp), bodyMedium = TextStyle(fontSize = 14.sp), labelLarge = TextStyle(fontWeight = FontWeight.Medium, fontSize = 14.sp),
)

@Composable
fun VeltrixTheme(theme: String = "dark", content: @Composable () -> Unit) {
    val dark = when (theme) { "light" -> false; "system" -> isSystemInDarkTheme(); else -> true }
    MaterialTheme(colorScheme = if (dark) Dark else Light, typography = VeltrixTypography, content = content)
}

object StatusColors {
    val success = DarkTokens.success
    val warning = DarkTokens.warning
    val danger = DarkTokens.danger
}
