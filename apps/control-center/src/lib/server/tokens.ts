/**
 * Token refresh shared by middleware (edge runtime) and server code. The backend rotates refresh tokens
 * and revokes the whole family on reuse, so concurrent refreshes with the same token must be coalesced:
 * the first call performs the rotation and later callers (within a short window) receive the same result.
 */
import { cookieNames, cookieOptions, secondsUntil } from "../session-config";
import type { TokenResponse } from "../types";
import { API_TIMEOUT_MS, apiBaseUrl } from "./config";

interface Entry {
  promise: Promise<TokenResponse | null>;
  at: number;
}

const inflight = new Map<string, Entry>();
const REUSE_WINDOW_MS = 30_000;

export function refreshTokens(refreshToken: string): Promise<TokenResponse | null> {
  const now = Date.now();
  for (const [k, v] of inflight) if (now - v.at > REUSE_WINDOW_MS) inflight.delete(k);
  const existing = inflight.get(refreshToken);
  if (existing) return existing.promise;
  const promise = doRefresh(refreshToken);
  inflight.set(refreshToken, { promise, at: now });
  return promise;
}

async function doRefresh(refreshToken: string): Promise<TokenResponse | null> {
  try {
    const res = await fetch(`${apiBaseUrl()}/api/v1/auth/refresh`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Accept: "application/json" },
      body: JSON.stringify({ refreshToken }),
      cache: "no-store",
      signal: AbortSignal.timeout(API_TIMEOUT_MS),
    });
    if (!res.ok) return null;
    const body = (await res.json()) as TokenResponse;
    if (!body?.accessToken || !body.refreshToken || !body.user?.isPlatformAdmin) return null;
    return body;
  } catch {
    return null;
  }
}

/** Minimal cookie jar interface satisfied by next/headers cookies() and NextResponse.cookies. */
export interface CookieJar {
  set(name: string, value: string, options: ReturnType<typeof cookieOptions>): unknown;
}

export function writeSessionCookies(jar: CookieJar, tokens: TokenResponse): void {
  const names = cookieNames();
  const refreshMaxAge = secondsUntil(tokens.refreshTokenExpiresAt);
  jar.set(names.access, tokens.accessToken, cookieOptions(secondsUntil(tokens.accessTokenExpiresAt) + 60));
  jar.set(names.refresh, tokens.refreshToken, cookieOptions(refreshMaxAge));
  jar.set(names.expires, String(Date.parse(tokens.accessTokenExpiresAt)), cookieOptions(refreshMaxAge));
}

export function clearSessionCookies(jar: CookieJar): void {
  const names = cookieNames();
  for (const n of Object.values(names)) jar.set(n, "", cookieOptions(0));
}

/** Best-effort server-side revocation of a refresh token family. */
export async function revokeRefreshToken(refreshToken: string): Promise<void> {
  try {
    await fetch(`${apiBaseUrl()}/api/v1/auth/logout`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ refreshToken }),
      cache: "no-store",
      signal: AbortSignal.timeout(API_TIMEOUT_MS),
    });
  } catch {
    // The cookies are cleared regardless; the token will expire on its own.
  }
}
