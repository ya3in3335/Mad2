package com.veltrix.admin.ui.common

import java.text.NumberFormat
import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.util.Locale

/** Pure formatting helpers (locale and zone injected for tests). */
object Formatters {
    fun money(amount: Double, currency: String = "DZD", locale: Locale = Locale.getDefault()): String {
        val f = NumberFormat.getNumberInstance(locale).apply { minimumFractionDigits = 2; maximumFractionDigits = 2 }
        return f.format(amount) + " " + currency
    }

    fun number(n: Number, locale: Locale = Locale.getDefault()): String = NumberFormat.getIntegerInstance(locale).format(n)

    /** 950 → "950", 12_400 → "12.4K", 3_200_000 → "3.2M" (Latin digits/suffix, used on compact chart labels). */
    fun compact(v: Double): String {
        val abs = kotlin.math.abs(v)
        val (div, suffix) = when {
            abs >= 1_000_000_000 -> 1_000_000_000.0 to "B"
            abs >= 1_000_000 -> 1_000_000.0 to "M"
            abs >= 1_000 -> 1_000.0 to "K"
            else -> 1.0 to ""
        }
        val scaled = v / div
        val text = if (suffix.isEmpty() || scaled >= 100 || scaled == Math.floor(scaled)) String.format(Locale.ROOT, "%.0f", scaled)
        else String.format(Locale.ROOT, "%.1f", scaled)
        return text + suffix
    }

    fun parseInstant(iso: String?): Instant? {
        if (iso.isNullOrBlank()) return null
        return runCatching { Instant.parse(iso) }.getOrNull()
            ?: runCatching { OffsetDateTime.parse(iso).toInstant() }.getOrNull()
            ?: runCatching { LocalDate.parse(iso).atStartOfDay(ZoneId.of("UTC")).toInstant() }.getOrNull()
    }

    fun dateTime(iso: String?, locale: Locale = Locale.getDefault(), zone: ZoneId = ZoneId.systemDefault()): String {
        val i = parseInstant(iso) ?: return "—"
        return DateTimeFormatter.ofLocalizedDateTime(FormatStyle.MEDIUM, FormatStyle.SHORT).withLocale(locale).withZone(zone).format(i)
    }

    fun date(iso: String?, locale: Locale = Locale.getDefault(), zone: ZoneId = ZoneId.systemDefault()): String {
        val i = parseInstant(iso) ?: return "—"
        return DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM).withLocale(locale).withZone(zone).format(i)
    }

    /** Coarse age bucket used for "last seen" labels; the UI localizes the unit. */
    data class Age(val value: Long, val unit: AgeUnit)
    enum class AgeUnit { Now, Minutes, Hours, Days }

    fun age(iso: String?, now: Instant = Instant.now()): Age? {
        val i = parseInstant(iso) ?: return null
        val d = Duration.between(i, now).coerceAtLeast(Duration.ZERO)
        return when {
            d.toMinutes() < 1 -> Age(0, AgeUnit.Now)
            d.toHours() < 1 -> Age(d.toMinutes(), AgeUnit.Minutes)
            d.toDays() < 1 -> Age(d.toHours(), AgeUnit.Hours)
            else -> Age(d.toDays(), AgeUnit.Days)
        }
    }

    /** Days until an expiry date (negative when already past), or null when there is no expiry. */
    fun daysUntil(iso: String?, now: Instant = Instant.now()): Long? = parseInstant(iso)?.let { Duration.between(now, it).toDays() }

    /** "2026-03" → "03/26" style short month label for chart axes. */
    fun shortMonth(month: String): String {
        val parts = month.split('-')
        return if (parts.size >= 2 && parts[0].length == 4) "${parts[1]}/${parts[0].takeLast(2)}" else month
    }
}

/** Semantic tone for a backend status string (drives chip colors). */
enum class Tone { Positive, Warning, Negative, Neutral, Info }

object StatusTone {
    fun of(status: String?): Tone = when (status?.lowercase()?.replace("_", "")) {
        null, "" -> Tone.Neutral
        "active", "paid", "healthy", "ok", "sent", "resolved", "uploaded", "online", "trialing", "closed" -> Tone.Positive
        "expiring", "pastdue", "pending", "issued", "acknowledged", "degraded", "warning", "delivered", "pendingsetup", "skipped", "waiting" -> Tone.Warning
        "suspended", "revoked", "expired", "cancelled", "failed", "critical", "error", "unhealthy", "down", "open", "void", "offline" -> Tone.Negative
        "info", "new", "released", "replaced", "transferred", "answered" -> Tone.Info
        else -> Tone.Neutral
    }
}
