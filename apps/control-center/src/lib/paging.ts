export function totalPages(totalCount: number, pageSize: number): number {
  if (pageSize <= 0) return 0;
  return Math.max(1, Math.ceil(totalCount / pageSize));
}

/** Compact page list with ellipses, e.g. [1, "…", 4, 5, 6, "…", 12]. */
export function pageWindow(current: number, total: number, radius = 1): Array<number | "…"> {
  if (total <= 1) return [1];
  const pages = new Set<number>([1, total]);
  for (let p = current - radius; p <= current + radius; p++) if (p >= 1 && p <= total) pages.add(p);
  const sorted = [...pages].sort((a, b) => a - b);
  const out: Array<number | "…"> = [];
  sorted.forEach((p, i) => {
    const prev = sorted[i - 1];
    if (prev !== undefined && p - prev > 1) out.push("…");
    out.push(p);
  });
  return out;
}

export function parsePage(value: string | string[] | undefined, fallback = 1): number {
  const s = Array.isArray(value) ? value[0] : value;
  const n = Number.parseInt(s ?? "", 10);
  return Number.isFinite(n) && n >= 1 && n <= 100_000 ? n : fallback;
}

/** First value of a search param, trimmed, length-limited. */
export function param(value: string | string[] | undefined, max = 200): string {
  const s = Array.isArray(value) ? value[0] : value;
  return (s ?? "").trim().slice(0, max);
}

const GUID = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;
export function isGuid(value: string | null | undefined): value is string {
  return typeof value === "string" && GUID.test(value);
}

/** Builds a page href keeping the other current search params. */
export function withParams(base: string, current: Record<string, string | undefined>, changes: Record<string, string | number | undefined>): string {
  const params = new URLSearchParams();
  for (const [k, v] of Object.entries({ ...current, ...changes })) {
    if (v !== undefined && v !== "" && v !== null) params.set(k, String(v));
  }
  const s = params.toString();
  return s ? `${base}?${s}` : base;
}
