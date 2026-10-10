/**
 * Allowlist of backend endpoints the Control Center may call on behalf of the signed-in administrator.
 * Used by the generic mutation server action and by the /api/proxy route handler. Anything not listed
 * here is rejected before a request ever leaves the Next.js server.
 */
export type HttpMethod = "GET" | "POST" | "PUT";

export interface ApiRule {
  method: HttpMethod;
  pattern: RegExp;
  /** Backend requires the X-Confirm-Password step-up header. */
  stepUp?: boolean;
}

const ID = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";
const P = "/api/v1/platform";
const r = (method: HttpMethod, path: string, stepUp = false): ApiRule => ({
  method,
  pattern: new RegExp(`^${path}$`),
  stepUp,
});

export const API_RULES: readonly ApiRule[] = [
  // Reads
  r("GET", "/api/v1/me"),
  r("GET", "/api/v1/me/sessions"),
  r("GET", "/api/v1/plans"),
  r(
    "GET",
    `${P}/(dashboard|tenants|admins|support|audit|security-alerts|health|backups|releases|announcements|settings|licenses|devices|plans|subscriptions|invoices)`,
  ),
  r("GET", `${P}/tenants/${ID}`),
  r("GET", `${P}/licenses/${ID}(/events)?`),
  r("GET", `${P}/subscriptions/${ID}(/history)?`),
  r("GET", `${P}/dashboard/admin`),
  r("GET", `${P}/licenses/activation-failures`),
  r("GET", `${P}/fleet/devices(/${ID})?`),
  r("GET", `${P}/incidents(/groups|/${ID})?`),
  r("GET", `${P}/diagnostic-requests/${ID}`),
  r("GET", `${P}/telegram/(status|deliveries)`),
  // Tenants
  r("POST", `${P}/tenants`),
  r("POST", `${P}/tenants/${ID}/suspend`, true),
  r("POST", `${P}/tenants/${ID}/reactivate`, true),
  // Platform administrators
  r("POST", `${P}/admins`, true),
  r("POST", `${P}/admins/${ID}/(disable|enable)`, true),
  // Support
  r("POST", `${P}/support/${ID}/reply`),
  // Releases / announcements / settings
  r("POST", `${P}/releases`),
  r("PUT", `${P}/releases/${ID}`),
  r("POST", `${P}/announcements`),
  r("PUT", `${P}/announcements/${ID}`),
  r("PUT", `${P}/settings/[A-Za-z0-9._-]{1,100}`),
  // Licensing
  r("POST", `${P}/licenses`),
  r("POST", `${P}/licenses/demo`),
  r("POST", `${P}/licenses/${ID}/seats`),
  r("PUT", `${P}/licenses/${ID}`),
  r("POST", `${P}/licenses/${ID}/extend`),
  r("POST", `${P}/licenses/${ID}/reactivate`, true),
  r("POST", `${P}/licenses/${ID}/(suspend|revoke|transfer)`, true),
  r("POST", `${P}/devices/${ID}/revoke`, true),
  // Subscriptions & billing
  r("POST", `${P}/plans`),
  r("PUT", `${P}/plans/${ID}`),
  r("POST", `${P}/subscriptions`),
  r("POST", `${P}/subscriptions/${ID}/(renewal-invoice|plan|seats)`),
  r("POST", `${P}/subscriptions/${ID}/cancel`, true),
  r("POST", `${P}/invoices/${ID}/confirm-payment`, true),
  r("POST", `${P}/invoices/${ID}/void`),
  // Device fleet & diagnostics
  r("POST", `${P}/fleet/devices/${ID}/(deactivate|replace|reassign)`, true),
  r("POST", `${P}/fleet/devices/${ID}/diagnostic-requests`),
  r("POST", `${P}/diagnostic-requests/${ID}/cancel`),
  // Incidents
  r("POST", `${P}/incidents/${ID}/status`),
  // Telegram
  r("POST", `${P}/telegram/test`, true),
  // Operations
  r("POST", `${P}/backups/run`, true),
  r("POST", `${P}/jobs/[a-z0-9-]{1,60}/run`),
];

/** Returns the matching rule, or null when the method/path pair is not allowed. */
export function findApiRule(method: string, path: string): ApiRule | null {
  const m = method.toUpperCase();
  if (typeof path !== "string" || path.length > 300) return null;
  // Reject anything that could escape the intended path (traversal, encoded separators, queries, fragments).
  if (/[?#\\%]|\.\.|\/\/|\s/.test(path)) return null;
  return API_RULES.find((rule) => rule.method === m && rule.pattern.test(path)) ?? null;
}

export function isAllowedApiRequest(method: string, path: string): boolean {
  return findApiRule(method, path) !== null;
}

export function requiresStepUp(method: string, path: string): boolean {
  return findApiRule(method, path)?.stepUp === true;
}

const QUERY_KEY = /^[A-Za-z][A-Za-z0-9]{0,30}$/;

/** Serialises a query object, dropping empty values and rejecting odd keys. */
export function toQueryString(query?: Record<string, string | number | boolean | null | undefined>): string {
  if (!query) return "";
  const params = new URLSearchParams();
  for (const [k, v] of Object.entries(query)) {
    if (!QUERY_KEY.test(k) || v === undefined || v === null || v === "") continue;
    params.set(k, String(v));
  }
  const s = params.toString();
  return s ? `?${s}` : "";
}
