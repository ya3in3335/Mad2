/** Cookie names/options for the admin session. Tokens live only in httpOnly cookies. */
export interface CookieOptions {
  httpOnly: boolean;
  secure: boolean;
  sameSite: "strict";
  path: string;
  maxAge?: number;
}

export const isProduction = (): boolean => process.env.NODE_ENV === "production";

/** In production the __Host- prefix pins cookies to this exact host, HTTPS and path "/". */
export function cookieNames(prod = isProduction()) {
  const prefix = prod ? "__Host-" : "";
  return {
    access: `${prefix}vx_at`,
    refresh: `${prefix}vx_rt`,
    expires: `${prefix}vx_exp`,
  } as const;
}

export function cookieOptions(maxAgeSeconds: number | undefined, prod = isProduction()): CookieOptions {
  return {
    httpOnly: true,
    secure: prod,
    sameSite: "strict",
    path: "/",
    ...(maxAgeSeconds !== undefined ? { maxAge: Math.max(0, Math.floor(maxAgeSeconds)) } : {}),
  };
}

/** Refresh slightly before the access token actually expires. */
export const REFRESH_SKEW_MS = 60_000;

export function accessTokenNeedsRefresh(expiresAtMs: number | null, now = Date.now()): boolean {
  if (expiresAtMs === null || Number.isNaN(expiresAtMs)) return true;
  return expiresAtMs - REFRESH_SKEW_MS <= now;
}

export function secondsUntil(iso: string, now = Date.now()): number {
  const t = Date.parse(iso);
  return Number.isNaN(t) ? 0 : Math.max(0, Math.floor((t - now) / 1000));
}
