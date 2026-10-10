/** Server-side configuration. VELTRIX_API_URL is never exposed to the browser (no NEXT_PUBLIC_ prefix). */
export function apiBaseUrl(): string {
  const raw = process.env.VELTRIX_API_URL || "http://localhost:5080";
  let url: URL;
  try {
    url = new URL(raw);
  } catch {
    throw new Error("VELTRIX_API_URL is not a valid URL.");
  }
  if (url.protocol !== "http:" && url.protocol !== "https:") throw new Error("VELTRIX_API_URL must use http or https.");
  return url.origin + url.pathname.replace(/\/+$/, "");
}

export const API_TIMEOUT_MS = 20_000;
