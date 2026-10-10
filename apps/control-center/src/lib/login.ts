import type { TokenResponse } from "./types";

export interface LoginState {
  step: "password" | "mfa";
  /** An i18n key, or an already human readable message from the API. */
  error?: string;
  email?: string;
}

export const INITIAL_LOGIN_STATE: LoginState = { step: "password" };

type Validated =
  | { ok: true; email: string; password: string; mfaCode?: string }
  | { ok: false; error: string; email: string; mfaStep: boolean };

const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/** Validates the raw form values (FormData entries are string | File | null). */
export function validateLoginInput(raw: { email: unknown; password: unknown; mfaCode: unknown }): Validated {
  const email = typeof raw.email === "string" ? raw.email.trim().slice(0, 256) : "";
  const password = typeof raw.password === "string" ? raw.password.slice(0, 128) : "";
  const mfaRaw = typeof raw.mfaCode === "string" ? raw.mfaCode.replace(/\s+/g, "") : "";
  const mfaStep = typeof raw.mfaCode === "string";
  if (!email || !password) return { ok: false, error: "err.required", email, mfaStep: false };
  if (!EMAIL.test(email)) return { ok: false, error: "err.invalidEmail", email, mfaStep: false };
  if (mfaStep) {
    if (!/^\d{6,8}$/.test(mfaRaw)) return { ok: false, error: "login.mfaFormat", email, mfaStep: true };
    return { ok: true, email, password, mfaCode: mfaRaw };
  }
  return { ok: true, email, password };
}

export type ParsedLogin = { kind: "tokens"; tokens: TokenResponse } | { kind: "mfa" } | { kind: "invalid" };

/** The login endpoint answers 200 with either a TokenResponse or { mfaRequired: true }. */
export function parseLoginResponse(body: unknown): ParsedLogin {
  if (!body || typeof body !== "object") return { kind: "invalid" };
  const b = body as Partial<TokenResponse> & { mfaRequired?: unknown };
  if (b.mfaRequired === true) return { kind: "mfa" };
  if (typeof b.accessToken === "string" && typeof b.refreshToken === "string" && typeof b.accessTokenExpiresAt === "string" && b.user && typeof b.user === "object") {
    return { kind: "tokens", tokens: b as TokenResponse };
  }
  return { kind: "invalid" };
}

const LOGIN_ERROR_KEYS: Record<string, string> = {
  "auth.invalid_credentials": "auth.invalid",
  "auth.locked_out": "auth.lockedOut",
  "auth.invalid_mfa": "login.mfaInvalid",
  "auth.inactive": "login.inactive",
};

/** Maps an API error code to an i18n key (falls back to the API's own message). */
export function loginErrorKey(code: string, message: string): string {
  return LOGIN_ERROR_KEYS[code] ?? message;
}
