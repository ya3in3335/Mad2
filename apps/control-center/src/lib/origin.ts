/**
 * CSRF defence for route handlers: a state-changing request must carry an Origin header equal to the
 * site's own origin (derived from the Host / X-Forwarded-* headers set by the reverse proxy).
 */
export function isSameOrigin(originHeader: string | null, host: string | null, forwardedProto?: string | null): boolean {
  if (!originHeader || !host) return false;
  let origin: URL;
  try {
    origin = new URL(originHeader);
  } catch {
    return false;
  }
  if (origin.host.toLowerCase() !== host.toLowerCase()) return false;
  if (forwardedProto) {
    const proto = forwardedProto.split(",")[0]?.trim().toLowerCase();
    if (proto && `${proto}:` !== origin.protocol) return false;
  }
  return origin.protocol === "https:" || origin.protocol === "http:";
}
