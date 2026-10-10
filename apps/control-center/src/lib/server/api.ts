import "server-only";
import { cookies, headers } from "next/headers";
import { redirect } from "next/navigation";
import { problemToError, type ActionResult } from "../problem";
import { findApiRule, toQueryString, type HttpMethod } from "../routes";
import { API_TIMEOUT_MS, apiBaseUrl } from "./config";
import { readSession } from "./session";
import { clearSessionCookies, refreshTokens, writeSessionCookies } from "./tokens";

export type Query = Record<string, string | number | boolean | null | undefined>;

export interface ApiOptions {
  query?: Query;
  body?: unknown;
  /** Step-up password, sent as X-Confirm-Password. Never logged or stored. */
  confirmPassword?: string;
}

/**
 * "render": server components (cookies are read-only, so an expired session is sent through the
 *           /api/auth/refresh route handler which can rotate the cookies and come back).
 * "action": server actions / route handlers (cookies are writable, refresh + retry inline).
 */
export type ApiMode = "render" | "action";

async function send(method: HttpMethod, path: string, opts: ApiOptions, accessToken: string | null): Promise<Response> {
  const h: Record<string, string> = { Accept: "application/json" };
  if (accessToken) h.Authorization = `Bearer ${accessToken}`;
  if (opts.body !== undefined) h["Content-Type"] = "application/json";
  if (opts.confirmPassword) h["X-Confirm-Password"] = opts.confirmPassword;
  const incoming = await headers();
  const fwd = incoming.get("x-forwarded-for");
  if (fwd) h["X-Forwarded-For"] = fwd.slice(0, 200);
  const ua = incoming.get("user-agent");
  if (ua) h["User-Agent"] = ua.slice(0, 300);
  return fetch(`${apiBaseUrl()}${path}${toQueryString(opts.query)}`, {
    method,
    headers: h,
    body: opts.body === undefined ? undefined : JSON.stringify(opts.body),
    cache: "no-store",
    redirect: "manual",
    signal: AbortSignal.timeout(API_TIMEOUT_MS),
  });
}

async function parse<T>(res: Response): Promise<ActionResult<T>> {
  const text = await res.text();
  let body: unknown = null;
  if (text) {
    try {
      body = JSON.parse(text);
    } catch {
      body = null;
    }
  }
  if (res.ok) return { ok: true, data: body as T };
  return { ok: false, error: problemToError(res.status, body) };
}

/** Calls an allow-listed VELTRIX API endpoint with the admin's bearer token. */
export async function apiRequest<T>(method: HttpMethod, path: string, opts: ApiOptions = {}, mode: ApiMode = "action"): Promise<ActionResult<T>> {
  if (!findApiRule(method, path)) {
    return { ok: false, error: { status: 400, code: "proxy.not_allowed", message: "This operation is not available." } };
  }
  const session = await readSession();
  if (!session.accessToken && !session.refreshToken) {
    if (mode === "render") redirect("/login");
    return { ok: false, error: { status: 401, code: "auth.session_missing", message: "Your session has expired. Sign in again." } };
  }

  let res: Response;
  try {
    res = await send(method, path, opts, session.accessToken);
  } catch {
    return { ok: false, error: problemToError(0, null) };
  }

  if (res.status === 401) {
    if (mode === "render") {
      const current = (await headers()).get("x-vx-path") ?? "/";
      redirect(`/api/auth/refresh?next=${encodeURIComponent(current)}`);
    }
    const tokens = session.refreshToken ? await refreshTokens(session.refreshToken) : null;
    const jar = await cookies();
    if (!tokens) {
      clearSessionCookies(jar);
      return { ok: false, error: { status: 401, code: "auth.session_expired", message: "Your session has expired. Sign in again." } };
    }
    writeSessionCookies(jar, tokens);
    try {
      res = await send(method, path, opts, tokens.accessToken);
    } catch {
      return { ok: false, error: problemToError(0, null) };
    }
  }
  return parse<T>(res);
}

/** Convenience for server components: GET with render-mode session handling. */
export function apiGet<T>(path: string, query?: Query): Promise<ActionResult<T>> {
  return apiRequest<T>("GET", path, { query }, "render");
}

/** Anonymous auth endpoints (login). */
export async function authPost<T>(path: "/api/v1/auth/login", body: unknown): Promise<ActionResult<T>> {
  try {
    const incoming = await headers();
    const h: Record<string, string> = { "Content-Type": "application/json", Accept: "application/json" };
    const ua = incoming.get("user-agent");
    if (ua) h["User-Agent"] = ua.slice(0, 300);
    const fwd = incoming.get("x-forwarded-for");
    if (fwd) h["X-Forwarded-For"] = fwd.slice(0, 200);
    const res = await fetch(`${apiBaseUrl()}${path}`, {
      method: "POST",
      headers: h,
      body: JSON.stringify(body),
      cache: "no-store",
      signal: AbortSignal.timeout(API_TIMEOUT_MS),
    });
    return parse<T>(res);
  } catch {
    return { ok: false, error: problemToError(0, null) };
  }
}
