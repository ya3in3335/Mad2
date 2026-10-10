const LOCALE_TAGS: Record<string, string> = { en: "en-GB", fr: "fr-FR", ar: "ar-DZ" };

export function localeTag(locale: string): string {
  return LOCALE_TAGS[locale] ?? "en-GB";
}

export function formatNumber(value: number, locale = "en", fractionDigits = 0): string {
  if (!Number.isFinite(value)) return "—";
  return new Intl.NumberFormat(localeTag(locale), {
    minimumFractionDigits: fractionDigits,
    maximumFractionDigits: fractionDigits,
    numberingSystem: "latn",
  }).format(value);
}

/** Money is shown with two decimals and the ISO currency code (DZD has no reliable symbol across locales). */
export function formatMoney(value: number, currency = "DZD", locale = "en"): string {
  if (!Number.isFinite(value)) return "—";
  return `${formatNumber(value, locale, 2)} ${currency}`;
}

/** Compact form for chart axes: 1.2k, 3.4M. */
export function formatCompact(value: number): string {
  const abs = Math.abs(value);
  if (abs >= 1_000_000_000) return `${trim(value / 1_000_000_000)}B`;
  if (abs >= 1_000_000) return `${trim(value / 1_000_000)}M`;
  if (abs >= 1_000) return `${trim(value / 1_000)}k`;
  return trim(value);
}

function trim(n: number): string {
  return Number.isInteger(n) ? String(n) : n.toFixed(1).replace(/\.0$/, "");
}

export function formatDate(iso: string | null | undefined, locale = "en"): string {
  if (!iso) return "—";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "—";
  return new Intl.DateTimeFormat(localeTag(locale), { dateStyle: "medium", timeZone: "UTC", numberingSystem: "latn" }).format(d);
}

export function formatDateTime(iso: string | null | undefined, locale = "en"): string {
  if (!iso) return "—";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "—";
  return `${new Intl.DateTimeFormat(localeTag(locale), {
    dateStyle: "medium",
    timeStyle: "short",
    timeZone: "UTC",
    numberingSystem: "latn",
  }).format(d)} UTC`;
}

export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return "—";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let v = bytes;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return `${i === 0 ? v : v.toFixed(1)} ${units[i]}`;
}

/** "2026-03" -> short month label in the given locale. */
export function formatMonth(month: string, locale = "en"): string {
  const m = /^(\d{4})-(\d{2})$/.exec(month);
  if (!m) return month;
  const d = new Date(Date.UTC(Number(m[1]), Number(m[2]) - 1, 1));
  return new Intl.DateTimeFormat(localeTag(locale), { month: "short", timeZone: "UTC" }).format(d);
}

/** Days between now and a date (negative when in the past). */
export function daysUntil(iso: string | null | undefined, now = Date.now()): number | null {
  if (!iso) return null;
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return null;
  return Math.ceil((t - now) / 86_400_000);
}

/** Value for <input type="date"> from an ISO timestamp. */
export function toDateInput(iso: string | null | undefined): string {
  if (!iso) return "";
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? "" : d.toISOString().slice(0, 10);
}

/** Date input value ("2026-05-01") -> ISO timestamp at end of that UTC day, or null. */
export function fromDateInput(value: string | null | undefined, endOfDay = true): string | null {
  if (!value || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return null;
  return endOfDay ? `${value}T23:59:59Z` : `${value}T00:00:00Z`;
}

export function shortId(id: string | null | undefined): string {
  return id ? id.slice(0, 8) : "—";
}
