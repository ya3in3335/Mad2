import Link from "next/link";
import type { ReactNode } from "react";
import { humanize, statusKey, toneFor, type Tone } from "@/lib/status";
import type { Translator } from "@/lib/i18n-core";

export function PageHeader({ title, description, actions }: { title: string; description?: string; actions?: ReactNode }) {
  return (
    <header className="mb-6 flex flex-wrap items-end justify-between gap-4">
      <div className="min-w-0">
        <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
        {description ? <p className="mt-1 text-sm text-muted">{description}</p> : null}
      </div>
      {actions ? <div className="flex flex-wrap items-center gap-2">{actions}</div> : null}
    </header>
  );
}

export function Card({ title, actions, children, className = "", bodyClassName = "p-5" }: { title?: ReactNode; actions?: ReactNode; children: ReactNode; className?: string; bodyClassName?: string }) {
  return (
    <section className={`vx-card ${className}`}>
      {title || actions ? (
        <div className="flex items-center justify-between gap-3 border-b border-line px-5 py-3">
          {title ? <h2 className="text-sm font-semibold">{title}</h2> : <span />}
          {actions}
        </div>
      ) : null}
      <div className={bodyClassName}>{children}</div>
    </section>
  );
}

const TONE_CLASSES: Record<Tone, string> = {
  neutral: "bg-raised text-muted border-line",
  info: "bg-accent/10 text-accent border-accent/30",
  success: "bg-success/10 text-success border-success/30",
  warning: "bg-warning/10 text-warning border-warning/30",
  danger: "bg-danger/10 text-danger border-danger/30",
  violet: "bg-violet/10 text-violet border-violet/30",
};

export function Badge({ tone = "neutral", children }: { tone?: Tone; children: ReactNode }) {
  return <span className={`inline-flex items-center whitespace-nowrap rounded-full border px-2 py-0.5 text-xs font-medium ${TONE_CLASSES[tone]}`}>{children}</span>;
}

/** Status pill: translated when the dictionary has status.<value>, otherwise humanised. */
export function StatusBadge({ value, t }: { value: string | null | undefined; t: Translator }) {
  if (!value) return <span className="text-muted">—</span>;
  const key = statusKey(value);
  const label = t(key);
  return <Badge tone={toneFor(value)}>{label === key ? humanize(value) : label}</Badge>;
}

export function EmptyState({ title, description, action }: { title: string; description?: string; action?: ReactNode }) {
  return (
    <div className="flex flex-col items-center justify-center gap-2 px-6 py-12 text-center">
      <div aria-hidden className="mb-1 grid h-10 w-10 place-items-center rounded-full bg-raised text-muted">
        ∅
      </div>
      <p className="font-medium">{title}</p>
      {description ? <p className="max-w-md text-sm text-muted">{description}</p> : null}
      {action}
    </div>
  );
}

export function ErrorPanel({ title, message, code, retryHref, retryLabel }: { title: string; message: string; code?: string; retryHref?: string; retryLabel?: string }) {
  return (
    <div role="alert" className="vx-card border-danger/40 p-6">
      <p className="font-semibold text-danger">{title}</p>
      <p className="mt-1 text-sm text-muted">{message}</p>
      {code ? <p className="mt-2 font-mono text-xs text-muted">{code}</p> : null}
      {retryHref ? (
        <Link href={retryHref} className="vx-btn vx-btn-secondary mt-4">
          {retryLabel}
        </Link>
      ) : null}
    </div>
  );
}

export function DefinitionList({ items }: { items: Array<[string, ReactNode]> }) {
  return (
    <dl className="grid grid-cols-1 gap-x-6 gap-y-3 text-sm sm:grid-cols-2">
      {items.map(([k, v]) => (
        <div key={k} className="min-w-0">
          <dt className="text-xs text-muted">{k}</dt>
          <dd className="mt-0.5 break-words">{v ?? "—"}</dd>
        </div>
      ))}
    </dl>
  );
}

export function TableWrap({ children }: { children: ReactNode }) {
  return <div className="overflow-x-auto">{children}</div>;
}
