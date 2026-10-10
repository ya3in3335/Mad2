import { barLayout, linePath, niceMax, ticks } from "@/lib/chart";
import { formatCompact, formatMonth } from "@/lib/format";
import type { MonthValue } from "@/lib/types";

const W = 560;
const H = 220;
const PLOT = { x: 44, y: 12, width: W - 56, height: H - 44 };

interface ChartProps {
  data: readonly MonthValue[];
  locale: string;
  label: string;
  formatValue?: (v: number) => string;
}

function Axes({ max, labels, locale }: { max: number; labels: readonly string[]; locale: string }) {
  const slot = labels.length ? PLOT.width / labels.length : 0;
  const every = labels.length > 8 ? Math.ceil(labels.length / 6) : 1;
  return (
    <g fontSize="11" fill="currentColor" className="text-muted">
      {ticks(max).map((v) => {
        const y = PLOT.y + PLOT.height - (v / max) * PLOT.height;
        return (
          <g key={v}>
            <line x1={PLOT.x} x2={PLOT.x + PLOT.width} y1={y} y2={y} stroke="var(--vx-border)" strokeDasharray={v === 0 ? undefined : "3 4"} />
            <text x={PLOT.x - 8} y={y + 4} textAnchor="end">
              {formatCompact(v)}
            </text>
          </g>
        );
      })}
      {labels.map((m, i) =>
        i % every === 0 ? (
          <text key={m + i} x={PLOT.x + slot * i + slot / 2} y={H - 8} textAnchor="middle">
            {formatMonth(m, locale)}
          </text>
        ) : null,
      )}
    </g>
  );
}

/** Dependency-free SVG column chart. Charts are always drawn left-to-right (time axis) even in RTL. */
export function BarChart({ data, locale, label, formatValue = formatCompact }: ChartProps) {
  const values = data.map((d) => Number(d.value) || 0);
  const max = niceMax(values);
  const bars = barLayout(values, PLOT, max);
  return (
    <figure className="m-0">
      <svg viewBox={`0 0 ${W} ${H}`} role="img" aria-label={label} className="h-auto w-full" direction="ltr">
        <defs>
          <linearGradient id="vx-bar" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0" stopColor="var(--vx-accent)" />
            <stop offset="1" stopColor="var(--vx-primary)" />
          </linearGradient>
        </defs>
        <Axes max={max} labels={data.map((d) => d.month)} locale={locale} />
        {bars.map((b, i) => (
          <rect key={i} x={b.x} y={b.y} width={b.width} height={Math.max(0, b.height)} rx={3} fill="url(#vx-bar)">
            <title>{`${data[i]?.month}: ${formatValue(values[i] ?? 0)}`}</title>
          </rect>
        ))}
      </svg>
      <ChartTable data={data} label={label} formatValue={formatValue} />
    </figure>
  );
}

export function LineChart({ data, locale, label, formatValue = formatCompact }: ChartProps) {
  const values = data.map((d) => Number(d.value) || 0);
  const max = niceMax(values);
  const path = linePath(values, PLOT, max);
  const slot = values.length ? PLOT.width / values.length : 0;
  const area = path && values.length ? `${path} L${PLOT.x + slot * (values.length - 0.5)} ${PLOT.y + PLOT.height} L${PLOT.x + slot / 2} ${PLOT.y + PLOT.height} Z` : "";
  return (
    <figure className="m-0">
      <svg viewBox={`0 0 ${W} ${H}`} role="img" aria-label={label} className="h-auto w-full" direction="ltr">
        <defs>
          <linearGradient id="vx-area" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0" stopColor="var(--vx-violet)" stopOpacity="0.35" />
            <stop offset="1" stopColor="var(--vx-violet)" stopOpacity="0" />
          </linearGradient>
        </defs>
        <Axes max={max} labels={data.map((d) => d.month)} locale={locale} />
        {area ? <path d={area} fill="url(#vx-area)" /> : null}
        {path ? <path d={path} fill="none" stroke="var(--vx-violet)" strokeWidth={2.5} strokeLinejoin="round" strokeLinecap="round" /> : null}
        {values.map((v, i) => (
          <circle key={i} cx={PLOT.x + slot * i + slot / 2} cy={PLOT.y + PLOT.height - (Math.max(0, v) / max) * PLOT.height} r={3.5} fill="var(--vx-surface)" stroke="var(--vx-violet)" strokeWidth={2}>
            <title>{`${data[i]?.month}: ${formatValue(v)}`}</title>
          </circle>
        ))}
      </svg>
      <ChartTable data={data} label={label} formatValue={formatValue} />
    </figure>
  );
}

/** Screen-reader equivalent of the chart. */
function ChartTable({ data, label, formatValue }: { data: readonly MonthValue[]; label: string; formatValue: (v: number) => string }) {
  return (
    <table className="sr-only">
      <caption>{label}</caption>
      <tbody>
        {data.map((d) => (
          <tr key={d.month}>
            <th scope="row">{d.month}</th>
            <td>{formatValue(Number(d.value) || 0)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
