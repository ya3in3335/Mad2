import Link from "next/link";
import type { Tone } from "@/lib/status";

const ACCENT: Record<Tone, string> = {
  neutral: "before:bg-line",
  info: "before:bg-accent",
  success: "before:bg-success",
  warning: "before:bg-warning",
  danger: "before:bg-danger",
  violet: "before:bg-violet",
};

export function Kpi({ label, value, hint, tone = "info", href }: { label: string; value: string; hint?: string; tone?: Tone; href?: string }) {
  const body = (
    <div
      className={`vx-card relative h-full overflow-hidden p-4 before:absolute before:inset-y-0 before:start-0 before:w-1 ${ACCENT[tone]} ${href ? "transition-colors hover:border-primary/60" : ""}`}
    >
      <p className="text-xs font-medium text-muted">{label}</p>
      <p className="mt-2 text-2xl font-semibold tabular-nums">{value}</p>
      {hint ? <p className="mt-1 text-xs text-muted">{hint}</p> : null}
    </div>
  );
  return href ? (
    <Link href={href} className="block h-full">
      {body}
    </Link>
  ) : (
    body
  );
}
