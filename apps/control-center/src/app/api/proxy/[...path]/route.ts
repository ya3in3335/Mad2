import { NextResponse, type NextRequest } from "next/server";
import { isSameOrigin } from "@/lib/origin";
import { findApiRule, type HttpMethod } from "@/lib/routes";
import { apiRequest, type Query } from "@/lib/server/api";

export const dynamic = "force-dynamic";

const MAX_BODY = 64 * 1024;

/**
 * Same-origin JSON proxy for client components. Only allow-listed /api/v1/platform/*, /api/v1/me* and
 * /api/v1/plans endpoints are forwarded; the bearer token is attached server-side and never reaches the browser.
 */
async function handle(req: NextRequest, ctx: { params: Promise<{ path: string[] }> }, method: HttpMethod) {
  const { path } = await ctx.params;
  const apiPath = `/api/v1/${path.map((p) => p.trim()).join("/")}`;
  const rule = findApiRule(method, apiPath);
  if (!rule) return json({ code: "proxy.not_allowed", title: "Not allowed." }, 404);

  // CSRF: every proxied call must come from our own pages.
  const fetchSite = req.headers.get("sec-fetch-site");
  if (fetchSite && fetchSite !== "same-origin") return json({ code: "proxy.cross_site", title: "Cross-site request rejected." }, 403);
  if (method !== "GET" && !isSameOrigin(req.headers.get("origin"), req.headers.get("x-forwarded-host") ?? req.headers.get("host"), req.headers.get("x-forwarded-proto"))) {
    return json({ code: "proxy.bad_origin", title: "Origin check failed." }, 403);
  }

  const query: Query = {};
  req.nextUrl.searchParams.forEach((v, k) => {
    query[k] = v.slice(0, 200);
  });

  let body: unknown;
  if (method !== "GET") {
    const text = await req.text();
    if (text.length > MAX_BODY) return json({ code: "proxy.too_large", title: "Request too large." }, 413);
    if (text) {
      if (!(req.headers.get("content-type") ?? "").includes("application/json")) {
        return json({ code: "proxy.content_type", title: "JSON expected." }, 415);
      }
      try {
        body = JSON.parse(text);
      } catch {
        return json({ code: "proxy.bad_json", title: "Malformed JSON." }, 400);
      }
    } else body = {};
  }

  const confirmPassword = rule.stepUp ? (req.headers.get("x-confirm-password") ?? undefined) : undefined;
  const result = await apiRequest<unknown>(method, apiPath, { query, body, confirmPassword }, "action");
  if (result.ok) return json(result.data ?? null, 200);
  return json(
    { code: result.error.code, title: result.error.message, status: result.error.status, errors: result.error.fieldErrors ?? null },
    result.error.status || 502,
  );
}

function json(data: unknown, status: number) {
  return NextResponse.json(data, { status, headers: { "Cache-Control": "no-store" } });
}

type Ctx = { params: Promise<{ path: string[] }> };
export const GET = (req: NextRequest, ctx: Ctx) => handle(req, ctx, "GET");
export const POST = (req: NextRequest, ctx: Ctx) => handle(req, ctx, "POST");
export const PUT = (req: NextRequest, ctx: Ctx) => handle(req, ctx, "PUT");
