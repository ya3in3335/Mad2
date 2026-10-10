import { NextResponse, type NextRequest } from "next/server";
import { buildCsp } from "./lib/csp";
import { safeRedirectPath } from "./lib/safe-redirect";
import { accessTokenNeedsRefresh, cookieNames } from "./lib/session-config";
import { clearSessionCookies, refreshTokens, writeSessionCookies } from "./lib/server/tokens";

const PUBLIC_PATHS = ["/login"];
const AUTH_ROUTES = ["/api/auth/"];

function isPublic(pathname: string): boolean {
  return PUBLIC_PATHS.some((p) => pathname === p || pathname.startsWith(`${p}/`));
}

function nonce(): string {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  let s = "";
  bytes.forEach((b) => (s += String.fromCharCode(b)));
  return btoa(s);
}

/**
 * 1. Per-request CSP nonce (Next.js applies it to its own scripts).
 * 2. Redirect unauthenticated users to /login (and signed-in users away from it).
 * 3. Proactively rotate the access token shortly before it expires, so server components always see a valid one.
 */
export async function middleware(req: NextRequest) {
  const { pathname, search } = req.nextUrl;
  const isDev = process.env.NODE_ENV !== "production";
  const n = nonce();
  const csp = buildCsp(n, isDev);
  const names = cookieNames();

  const requestHeaders = new Headers(req.headers);
  requestHeaders.set("x-nonce", n);
  requestHeaders.set("Content-Security-Policy", csp);
  requestHeaders.set("x-vx-path", safeRedirectPath(pathname + search));

  const access = req.cookies.get(names.access)?.value;
  const refresh = req.cookies.get(names.refresh)?.value;
  const exp = Number(req.cookies.get(names.expires)?.value);
  const hasSession = Boolean(access || refresh);

  const finish = (res: NextResponse) => {
    res.headers.set("Content-Security-Policy", csp);
    return res;
  };

  if (AUTH_ROUTES.some((p) => pathname.startsWith(p)) || pathname.startsWith("/api/proxy/")) {
    return finish(NextResponse.next({ request: { headers: requestHeaders } }));
  }

  if (isPublic(pathname)) {
    if (hasSession && access && !accessTokenNeedsRefresh(Number.isFinite(exp) ? exp : null)) {
      const next = safeRedirectPath(req.nextUrl.searchParams.get("next"));
      return finish(NextResponse.redirect(new URL(next, req.url)));
    }
    return finish(NextResponse.next({ request: { headers: requestHeaders } }));
  }

  if (!hasSession) {
    const url = new URL("/login", req.url);
    if (pathname !== "/") url.searchParams.set("next", safeRedirectPath(pathname + search));
    return finish(NextResponse.redirect(url));
  }

  if (refresh && (!access || accessTokenNeedsRefresh(Number.isFinite(exp) ? exp : null))) {
    // Router prefetches must not trigger a token rotation on their own.
    const isPrefetch = req.headers.get("next-router-prefetch") === "1" || req.headers.get("purpose") === "prefetch";
    if (isPrefetch) return finish(new NextResponse(null, { status: 204 }));
    const tokens = await refreshTokens(refresh);
    if (!tokens) {
      const url = new URL("/login", req.url);
      url.searchParams.set("next", safeRedirectPath(pathname + search));
      url.searchParams.set("reason", "expired");
      const res = NextResponse.redirect(url);
      clearSessionCookies(res.cookies);
      return finish(res);
    }
    // Make the new token visible to this request's server components, and persist it in the browser.
    const forward = new Map<string, string>();
    req.cookies.getAll().forEach((c) => forward.set(c.name, c.value));
    forward.set(names.access, tokens.accessToken);
    forward.set(names.refresh, tokens.refreshToken);
    forward.set(names.expires, String(Date.parse(tokens.accessTokenExpiresAt)));
    requestHeaders.set(
      "cookie",
      [...forward.entries()].map(([k, v]) => `${k}=${encodeURIComponent(v)}`).join("; "),
    );
    const res = NextResponse.next({ request: { headers: requestHeaders } });
    writeSessionCookies(res.cookies, tokens);
    return finish(res);
  }

  return finish(NextResponse.next({ request: { headers: requestHeaders } }));
}

export const config = {
  matcher: [
    {
      source: "/((?!_next/static|_next/image|favicon.ico|icon.svg|veltrix-symbol.svg|brand/|robots.txt).*)",
    },
  ],
};
