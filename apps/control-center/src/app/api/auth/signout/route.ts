import { NextResponse, type NextRequest } from "next/server";
import { cookieNames } from "@/lib/session-config";
import { clearSessionCookies, revokeRefreshToken } from "@/lib/server/tokens";

export const dynamic = "force-dynamic";

/**
 * Forced sign-out (session no longer valid, or account is not a platform administrator).
 * Cookies are SameSite=Strict, so a cross-site link to this URL carries no session to revoke.
 */
export async function GET(req: NextRequest) {
  const reason = req.nextUrl.searchParams.get("reason") === "forbidden" ? "forbidden" : "expired";
  const refresh = req.cookies.get(cookieNames().refresh)?.value;
  if (refresh) await revokeRefreshToken(refresh);
  const url = new URL("/login", req.url);
  url.searchParams.set("reason", reason);
  const res = NextResponse.redirect(url, 303);
  clearSessionCookies(res.cookies);
  return res;
}
