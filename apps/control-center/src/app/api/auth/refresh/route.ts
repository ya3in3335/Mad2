import { NextResponse, type NextRequest } from "next/server";
import { safeRedirectPath } from "@/lib/safe-redirect";
import { cookieNames } from "@/lib/session-config";
import { clearSessionCookies, refreshTokens, writeSessionCookies } from "@/lib/server/tokens";

export const dynamic = "force-dynamic";

/** Used by server components that got a 401: rotate the session cookies, then go back. */
export async function GET(req: NextRequest) {
  const next = safeRedirectPath(req.nextUrl.searchParams.get("next"));
  const refresh = req.cookies.get(cookieNames().refresh)?.value;
  const tokens = refresh ? await refreshTokens(refresh) : null;
  if (!tokens) {
    const url = new URL("/login", req.url);
    url.searchParams.set("next", next);
    url.searchParams.set("reason", "expired");
    const res = NextResponse.redirect(url, 303);
    clearSessionCookies(res.cookies);
    return res;
  }
  const res = NextResponse.redirect(new URL(next, req.url), 303);
  writeSessionCookies(res.cookies, tokens);
  return res;
}
