"use server";

import { cookies } from "next/headers";
import { redirect } from "next/navigation";
import { isLocale, LOCALE_COOKIE, THEME_COOKIE } from "@/lib/i18n-core";
import type { ActionResult } from "@/lib/problem";
import { findApiRule, type HttpMethod } from "@/lib/routes";
import { apiRequest } from "@/lib/server/api";
import { readSession } from "@/lib/server/session";
import { clearSessionCookies, revokeRefreshToken } from "@/lib/server/tokens";
import { isProduction } from "@/lib/session-config";

const MAX_BODY_CHARS = 64 * 1024;
const ONE_YEAR = 60 * 60 * 24 * 365;

/**
 * Generic mutation used by client components. The method/path pair must match the allowlist in
 * src/lib/routes.ts; the bearer token is attached server-side and the step-up password (if any) is
 * forwarded once as X-Confirm-Password and never stored.
 */
export async function apiMutation<T = unknown>(method: HttpMethod, path: string, body?: unknown, confirmPassword?: string): Promise<ActionResult<T>> {
  if (method === "GET") return { ok: false, error: { status: 400, code: "proxy.not_allowed", message: "This operation is not available." } };
  const rule = findApiRule(method, path);
  if (!rule) return { ok: false, error: { status: 400, code: "proxy.not_allowed", message: "This operation is not available." } };
  if (body !== undefined && JSON.stringify(body).length > MAX_BODY_CHARS) {
    return { ok: false, error: { status: 413, code: "proxy.too_large", message: "Request too large." } };
  }
  const password = rule.stepUp && typeof confirmPassword === "string" && confirmPassword.length > 0 ? confirmPassword.slice(0, 256) : undefined;
  return apiRequest<T>(method, path, { body: body ?? {}, confirmPassword: password }, "action");
}

export async function setLocaleAction(locale: string): Promise<void> {
  if (!isLocale(locale)) return;
  (await cookies()).set(LOCALE_COOKIE, locale, { path: "/", sameSite: "lax", maxAge: ONE_YEAR, secure: isProduction(), httpOnly: false });
}

export async function setThemeAction(theme: string): Promise<void> {
  if (theme !== "dark" && theme !== "light") return;
  (await cookies()).set(THEME_COOKIE, theme, { path: "/", sameSite: "lax", maxAge: ONE_YEAR, secure: isProduction(), httpOnly: false });
}

/** Revokes the refresh-token family on the API and clears the session cookies. */
export async function signOutAction(): Promise<void> {
  const session = await readSession();
  if (session.refreshToken) await revokeRefreshToken(session.refreshToken);
  clearSessionCookies(await cookies());
  redirect("/login");
}
