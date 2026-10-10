/** Only allow same-site relative paths as post-login redirect targets (prevents open redirects). */
export function safeRedirectPath(next: string | null | undefined, fallback = "/"): string {
  if (!next || typeof next !== "string") return fallback;
  if (!next.startsWith("/") || next.startsWith("//") || next.startsWith("/\\")) return fallback;
  if (/[\u0000-\u001f\\]/.test(next)) return fallback;
  if (next.startsWith("/login") || next.startsWith("/api/")) return fallback;
  return next.length > 512 ? fallback : next;
}
