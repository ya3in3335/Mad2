/** Visual tone for status-like values coming from the API (enum names are serialised as strings). */
export type Tone = "neutral" | "info" | "success" | "warning" | "danger" | "violet";

const TONES: Record<string, Tone> = {
  // tenants / generic
  active: "success",
  pending: "warning",
  suspended: "danger",
  closed: "neutral",
  // licences / devices
  expired: "danger",
  revoked: "danger",
  grace: "warning",
  expiring: "warning",
  transferred: "neutral",
  replaced: "neutral",
  released: "neutral",
  // subscriptions
  trialing: "info",
  pastdue: "warning",
  cancelled: "neutral",
  canceled: "neutral",
  // invoices
  issued: "warning",
  paid: "success",
  void: "neutral",
  // support
  open: "info",
  resolved: "success",
  acknowledged: "warning",
  unresolved: "warning",
  delivered: "info",
  uploaded: "success",
  sent: "success",
  skipped: "neutral",
  // severities
  info: "info",
  low: "info",
  normal: "neutral",
  success: "success",
  medium: "warning",
  warning: "warning",
  high: "danger",
  urgent: "danger",
  critical: "danger",
  // health / backups / workers
  healthy: "success",
  ok: "success",
  succeeded: "success",
  completed: "success",
  degraded: "warning",
  running: "info",
  failed: "danger",
  unhealthy: "danger",
  error: "danger",
};

export function toneFor(status: string | null | undefined): Tone {
  if (!status) return "neutral";
  return TONES[status.toLowerCase().replace(/[\s_-]/g, "")] ?? "neutral";
}

/** Converts "PastDue" / "past_due" / "auth.login_failed" into a readable label. */
export function humanize(value: string | null | undefined): string {
  if (!value) return "—";
  const s = value
    .replace(/[._-]+/g, " ")
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .trim()
    .toLowerCase();
  return s.charAt(0).toUpperCase() + s.slice(1);
}

/** i18n key for a status value; callers fall back to humanize() when the key is missing. */
export function statusKey(value: string): string {
  return `status.${value.toLowerCase().replace(/[\s_-]/g, "")}`;
}
