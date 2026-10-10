/**
 * Declarative forms: server components describe the fields (labels already translated) and name a
 * request builder; the client dialog collects values and calls buildRequest() to validate them and
 * produce the exact JSON body expected by the VELTRIX API contract.
 */
import { isGuid } from "./paging";
import { INCIDENT_STATUSES } from "./types";

export type FieldType = "hidden" | "text" | "email" | "password" | "url" | "textarea" | "number" | "select" | "checkbox" | "checkboxes" | "date" | "datetime";

export interface FieldOption {
  value: string;
  label: string;
}

export interface FieldSpec {
  name: string;
  label: string;
  type: FieldType;
  required?: boolean;
  options?: FieldOption[];
  defaultValue?: string | boolean | string[];
  placeholder?: string;
  help?: string;
  min?: number;
  max?: number;
  step?: string;
  maxLength?: number;
  /** Render with dir="ltr" (codes, URLs, hashes, e-mail addresses). */
  ltr?: boolean;
  /** Spans both columns of the dialog grid. */
  wide?: boolean;
}

export type FormValue = string | boolean | string[];
export type FormValues = Record<string, FormValue | undefined>;

export type BuildResult = { ok: true; body: unknown } | { ok: false; errors: Record<string, string> };

/** Error values are i18n keys (form.required, form.invalidNumber, …). */
class Reader {
  readonly errors: Record<string, string> = {};
  constructor(private readonly v: FormValues) {}

  str(name: string, opts: { required?: boolean; max?: number } = {}): string | null {
    const raw = this.v[name];
    const s = typeof raw === "string" ? raw.trim() : "";
    if (!s) {
      if (opts.required) this.errors[name] = "form.required";
      return null;
    }
    if (opts.max && s.length > opts.max) {
      this.errors[name] = "form.tooLong";
      return null;
    }
    return s;
  }

  int(name: string, opts: { required?: boolean; min?: number; max?: number } = {}): number | null {
    const s = this.str(name, { required: opts.required });
    if (s === null) return null;
    if (!/^-?\d+$/.test(s)) {
      this.errors[name] = "form.invalidNumber";
      return null;
    }
    const n = Number.parseInt(s, 10);
    if ((opts.min !== undefined && n < opts.min) || (opts.max !== undefined && n > opts.max)) {
      this.errors[name] = "form.outOfRange";
      return null;
    }
    return n;
  }

  decimal(name: string, opts: { required?: boolean; min?: number } = {}): number | null {
    const s = this.str(name, { required: opts.required });
    if (s === null) return null;
    const normalised = s.replace(/\s/g, "").replace(",", ".");
    if (!/^-?\d+(\.\d{1,4})?$/.test(normalised)) {
      this.errors[name] = "form.invalidNumber";
      return null;
    }
    const n = Number(normalised);
    if (opts.min !== undefined && n < opts.min) {
      this.errors[name] = "form.outOfRange";
      return null;
    }
    return n;
  }

  bool(name: string): boolean {
    return this.v[name] === true || this.v[name] === "true" || this.v[name] === "on";
  }

  list(name: string): string[] {
    const raw = this.v[name];
    return Array.isArray(raw) ? raw.filter((x) => typeof x === "string" && x.length > 0) : [];
  }

  guid(name: string, opts: { required?: boolean } = {}): string | null {
    const s = this.str(name, opts);
    if (s === null) return null;
    if (!isGuid(s)) {
      this.errors[name] = "form.invalidId";
      return null;
    }
    return s.toLowerCase();
  }

  email(name: string, opts: { required?: boolean } = {}): string | null {
    const s = this.str(name, { ...opts, max: 256 });
    if (s === null) return null;
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(s)) {
      this.errors[name] = "form.invalidEmail";
      return null;
    }
    return s;
  }

  url(name: string, opts: { httpsOnly?: boolean } = {}): string | null {
    const s = this.str(name, { max: 1000 });
    if (s === null) return null;
    try {
      const u = new URL(s);
      if (u.protocol !== "https:" && (opts.httpsOnly || u.protocol !== "http:")) throw new Error("scheme");
      return u.toString();
    } catch {
      this.errors[name] = "form.invalidUrl";
      return null;
    }
  }

  /** "2026-05-01" (date) or "2026-05-01T10:30" (datetime-local, interpreted as UTC) -> ISO 8601. */
  date(name: string, opts: { required?: boolean; endOfDay?: boolean } = {}): string | null {
    const s = this.str(name, { required: opts.required });
    if (s === null) return null;
    let iso: string | null = null;
    if (/^\d{4}-\d{2}-\d{2}$/.test(s)) iso = `${s}T${opts.endOfDay !== false ? "23:59:59" : "00:00:00"}Z`;
    else if (/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?$/.test(s)) iso = `${s.length === 16 ? `${s}:00` : s}Z`;
    if (!iso || Number.isNaN(Date.parse(iso))) {
      this.errors[name] = "form.invalidDate";
      return null;
    }
    return iso;
  }

  result(body: unknown): BuildResult {
    return Object.keys(this.errors).length ? { ok: false, errors: this.errors } : { ok: true, body };
  }
}

const BUILDERS = {
  empty: () => ({}),
  reason: (r: Reader) => ({ reason: r.str("reason", { required: true, max: 500 }) }),
  "tenant.create": (r: Reader) => ({
    name: r.str("name", { required: true, max: 200 }),
    email: r.email("email"),
    phone: r.str("phone", { max: 50 }),
    contactName: r.str("contactName", { max: 200 }),
    wilaya: r.str("wilaya", { max: 100 }),
    notes: r.str("notes", { max: 2000 }),
  }),
  "license.create": (r: Reader) => ({
    tenantId: r.guid("tenantId", { required: true }),
    type: r.str("type", { required: true }),
    planCode: r.str("planCode", { required: true }),
    maxDevices: r.int("maxDevices", { required: true, min: 1, max: 10_000 }),
    startsAt: r.date("startsAt", { endOfDay: false }),
    expiresAt: r.date("expiresAt"),
    offlineGraceDays: r.int("offlineGraceDays", { min: 0, max: 365 }),
    entitlements: r.list("entitlements"),
    notes: r.str("notes", { max: 2000 }),
  }),
  "license.update": (r: Reader) => ({
    maxDevices: r.int("maxDevices", { required: true, min: 1, max: 10_000 }),
    expiresAt: r.date("expiresAt"),
    offlineGraceDays: r.int("offlineGraceDays", { required: true, min: 0, max: 365 }),
    entitlements: r.list("entitlements"),
    notes: r.str("notes", { max: 2000 }),
  }),
  "license.extend": (r: Reader) => ({ days: r.int("days", { required: true, min: 1, max: 3650 }), reason: r.str("reason", { max: 500 }) }),
  "license.demo": (r: Reader) => ({
    tenantId: r.guid("tenantId", { required: true }),
    days: r.int("days", { required: true, min: 1, max: 60 }),
    maxDevices: r.int("maxDevices", { required: true, min: 1, max: 10 }),
    notes: r.str("notes", { max: 2000 }),
  }),
  "license.seats": (r: Reader) => ({ maxDevices: r.int("maxDevices", { required: true, min: 1, max: 10_000 }), reason: r.str("reason", { required: true, max: 500 }) }),
  "device.reassign": (r: Reader) => ({ targetLicenseId: r.guid("targetLicenseId", { required: true }), reason: r.str("reason", { required: true, max: 500 }) }),
  "diagnostic.request": (r: Reader) => ({ reason: r.str("reason", { max: 500 }), expiresInHours: r.int("expiresInHours", { min: 1, max: 168 }) }),
  "incident.status": (r: Reader) => {
    const status = r.str("status", { required: true });
    if (status !== null && !(INCIDENT_STATUSES as readonly string[]).includes(status)) r.errors.status = "form.required";
    return { status, note: r.str("note", { max: 1000 }) };
  },
  "telegram.test": (r: Reader) => ({ text: r.str("text", { max: 1000 }) }),
  "license.transfer": (r: Reader) => ({ targetTenantId: r.guid("targetTenantId", { required: true }), reason: r.str("reason", { required: true, max: 500 }) }),
  "plan.upsert": (r: Reader) => ({
    code: r.str("code", { required: true, max: 50 }),
    name: r.str("name", { required: true, max: 200 }),
    description: r.str("description", { max: 2000 }),
    monthlyPrice: r.decimal("monthlyPrice", { required: true, min: 0 }),
    annualPrice: r.decimal("annualPrice", { required: true, min: 0 }),
    currency: r.str("currency", { required: true, max: 3 })?.toUpperCase() ?? null,
    features: r.list("features"),
    maxDevices: r.int("maxDevices", { required: true, min: 0, max: 100_000 }),
    maxUsers: r.int("maxUsers", { required: true, min: 0, max: 100_000 }),
    maxBranches: r.int("maxBranches", { required: true, min: 0, max: 10_000 }),
    trialDays: r.int("trialDays", { required: true, min: 0, max: 365 }),
    offlineGraceDays: r.int("offlineGraceDays", { required: true, min: 0, max: 365 }),
    isActive: r.bool("isActive"),
    isPublic: r.bool("isPublic"),
  }),
  "subscription.create": (r: Reader) => ({
    tenantId: r.guid("tenantId", { required: true }),
    planCode: r.str("planCode", { required: true }),
    billingCycle: r.str("billingCycle", { required: true }),
    seats: r.int("seats", { min: 1, max: 10_000 }),
    startWithTrial: r.bool("startWithTrial"),
    customPrice: r.decimal("customPrice", { min: 0 }),
  }),
  "subscription.plan": (r: Reader) => ({ planCode: r.str("planCode", { required: true }), billingCycle: r.str("billingCycle"), prorateInvoice: r.bool("prorateInvoice") }),
  "subscription.seats": (r: Reader) => ({ seats: r.int("seats", { required: true, min: 1, max: 10_000 }) }),
  "subscription.cancel": (r: Reader) => ({ immediately: r.bool("immediately"), reason: r.str("reason", { required: true, max: 500 }) }),
  "subscription.renewal": (r: Reader) => ({ taxPercent: r.decimal("taxPercent", { min: 0 }) }),
  "invoice.confirm": (r: Reader) => ({ method: r.str("method", { required: true }), reference: r.str("reference", { max: 200 }), paidAt: r.date("paidAt", { endOfDay: false }) }),
  "admin.create": (r: Reader) => {
    const password = r.str("password", { required: true, max: 128 });
    if (password !== null && password.length < 12) r.errors.password = "form.passwordShort";
    return { email: r.email("email", { required: true }), fullName: r.str("fullName", { required: true, max: 200 }), password };
  },
  "support.reply": (r: Reader) => ({ body: r.str("body", { required: true, max: 8000 }), status: r.str("status") }),
  "release.upsert": (r: Reader) => {
    const sha = r.str("sha256", { max: 64 });
    if (sha !== null && !/^[0-9a-fA-F]{64}$/.test(sha)) r.errors.sha256 = "form.invalidHash";
    return {
      platform: r.str("platform", { required: true, max: 50 }),
      version: r.str("version", { required: true, max: 50 }),
      channel: r.str("channel", { required: true, max: 50 }),
      minSupportedVersion: r.str("minSupportedVersion", { required: true, max: 50 }),
      releaseNotes: r.str("releaseNotes", { max: 8000 }),
      downloadUrl: r.url("downloadUrl", { httpsOnly: true }),
      sha256: sha?.toLowerCase() ?? null,
      isActive: r.bool("isActive"),
    };
  },
  "announcement.upsert": (r: Reader) => ({
    title: r.str("title", { required: true, max: 200 }),
    body: r.str("body", { required: true, max: 4000 }),
    severity: r.str("severity", { required: true }),
    tenantId: r.guid("tenantId"),
    startsAt: r.date("startsAt", { required: true, endOfDay: false }),
    endsAt: r.date("endsAt"),
    published: r.bool("published"),
  }),
  "setting.set": (r: Reader) => {
    const key = r.str("key", { required: true, max: 100 });
    if (key !== null && !/^[A-Za-z0-9._-]{1,100}$/.test(key)) r.errors.key = "form.invalidKey";
    return { key, value: r.str("value", { max: 4000 }) ?? "" };
  },
} satisfies Record<string, (r: Reader) => unknown>;

export type FormKind = keyof typeof BUILDERS;

export function isFormKind(kind: string): kind is FormKind {
  return Object.prototype.hasOwnProperty.call(BUILDERS, kind);
}

export function buildRequest(kind: FormKind, values: FormValues): BuildResult {
  const r = new Reader(values);
  const body = BUILDERS[kind](r);
  return r.result(body);
}

/** Default values from a field list (used to initialise the dialog). */
export function initialValues(fields: readonly FieldSpec[]): FormValues {
  const out: FormValues = {};
  for (const f of fields) {
    if (f.defaultValue !== undefined) out[f.name] = f.defaultValue;
    else if (f.type === "checkbox") out[f.name] = false;
    else if (f.type === "checkboxes") out[f.name] = [];
    else out[f.name] = "";
  }
  return out;
}

/** For setting.set the path depends on the key: /api/v1/platform/settings/{key}. */
export function resolvePath(template: string, values: FormValues): string {
  return template.replace(/\{(\w+)\}/g, (_, name: string) => {
    const v = values[name];
    return typeof v === "string" ? encodeURIComponent(v.trim()) : "";
  });
}
