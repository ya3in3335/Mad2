import "server-only";
import { cookies } from "next/headers";
import { cookieNames } from "../session-config";

export interface SessionTokens {
  accessToken: string | null;
  refreshToken: string | null;
  expiresAt: number | null;
}

export async function readSession(): Promise<SessionTokens> {
  const jar = await cookies();
  const names = cookieNames();
  const exp = Number(jar.get(names.expires)?.value);
  return {
    accessToken: jar.get(names.access)?.value || null,
    refreshToken: jar.get(names.refresh)?.value || null,
    expiresAt: Number.isFinite(exp) && exp > 0 ? exp : null,
  };
}
