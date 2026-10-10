package com.veltrix.admin.ui.common

/** Geometry helpers for the Canvas charts, kept free of Compose so they can be unit-tested. */
object ChartMath {
    /** Values scaled to 0..1 against the series maximum (all-zero or empty series → zeros). Negative values clamp to 0. */
    fun normalize(values: List<Double>): List<Float> {
        val max = values.maxOrNull() ?: return emptyList()
        if (max <= 0.0) return values.map { 0f }
        return values.map { (it.coerceAtLeast(0.0) / max).toFloat() }
    }

    /** Fractions of a whole for a donut chart; zero total → all zeros. */
    fun shares(values: List<Double>): List<Float> {
        val total = values.sumOf { it.coerceAtLeast(0.0) }
        if (total <= 0.0) return values.map { 0f }
        return values.map { (it.coerceAtLeast(0.0) / total).toFloat() }
    }

    /** "Nice" axis maximum (1, 2, 2.5, 5 × 10^n) at or above [max]. */
    fun niceCeiling(max: Double): Double {
        if (max <= 0.0) return 1.0
        val exp = Math.floor(Math.log10(max))
        val base = Math.pow(10.0, exp)
        val f = max / base
        val nice = when {
            f <= 1.0 -> 1.0
            f <= 2.0 -> 2.0
            f <= 2.5 -> 2.5
            f <= 5.0 -> 5.0
            else -> 10.0
        }
        return nice * base
    }
}
