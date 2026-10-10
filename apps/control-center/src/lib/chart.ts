/** Small helpers for the dependency-free SVG charts. */
export function niceMax(values: readonly number[]): number {
  const max = Math.max(0, ...values.filter((v) => Number.isFinite(v)));
  if (max <= 0) return 1;
  const exp = Math.pow(10, Math.floor(Math.log10(max)));
  const f = max / exp;
  const nice = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10;
  return nice * exp;
}

export function ticks(max: number, count = 4): number[] {
  if (max <= 0 || count < 1) return [0];
  const step = max / count;
  return Array.from({ length: count + 1 }, (_, i) => Number((step * i).toPrecision(12)));
}

export interface BarGeometry {
  x: number;
  y: number;
  width: number;
  height: number;
}

/** Lays bars out inside a plot area. */
export function barLayout(
  values: readonly number[],
  plot: { x: number; y: number; width: number; height: number },
  max = niceMax(values),
  gapRatio = 0.35,
): BarGeometry[] {
  if (values.length === 0) return [];
  const slot = plot.width / values.length;
  const width = slot * (1 - gapRatio);
  return values.map((v, i) => {
    const h = max > 0 ? (Math.max(0, v) / max) * plot.height : 0;
    return { x: plot.x + slot * i + (slot - width) / 2, y: plot.y + plot.height - h, width, height: h };
  });
}

/** SVG path for a polyline through the points (one per slot centre). */
export function linePath(
  values: readonly number[],
  plot: { x: number; y: number; width: number; height: number },
  max = niceMax(values),
): string {
  if (values.length === 0) return "";
  const slot = plot.width / values.length;
  return values
    .map((v, i) => {
      const x = plot.x + slot * i + slot / 2;
      const y = plot.y + plot.height - (max > 0 ? (Math.max(0, v) / max) * plot.height : 0);
      return `${i === 0 ? "M" : "L"}${round(x)} ${round(y)}`;
    })
    .join(" ");
}

function round(n: number): number {
  return Math.round(n * 100) / 100;
}
