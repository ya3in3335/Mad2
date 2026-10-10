"use server";

import { cookies } from "next/headers";
import { redirect } from "next/navigation";
import { safeRedirectPath } from "@/lib/safe-redirect";
import { authPost } from "@/lib/server/api";
import { revokeRefreshToken, writeSessionCookies } from "@/lib/server/tokens";
import { loginErrorKey, parseLoginResponse, validateLoginInput, type LoginState } from "@/lib/login";

/** Password sign-in, with a second round-trip carrying the TOTP code when the account has MFA enabled. */
export async function loginAction(_prev: LoginState, form: FormData): Promise<LoginState> {
  const input = validateLoginInput({
    email: form.get("email"),
    password: form.get("password"),
    mfaCode: form.get("mfaCode"),
  });
  if (!input.ok) return { step: input.mfaStep ? "mfa" : "password", error: input.error, email: input.email };

  const res = await authPost<unknown>("/api/v1/auth/login", {
    email: input.email,
    password: input.password,
    mfaCode: input.mfaCode ?? null,
  });
  if (!res.ok) {
    return { step: input.mfaCode ? "mfa" : "password", error: loginErrorKey(res.error.code, res.error.message), email: input.email };
  }

  const parsed = parseLoginResponse(res.data);
  if (parsed.kind === "mfa") return { step: "mfa", email: input.email };
  if (parsed.kind === "invalid") return { step: "password", error: "err.generic", email: input.email };
  if (!parsed.tokens.user.isPlatformAdmin) {
    await revokeRefreshToken(parsed.tokens.refreshToken);
    return { step: "password", error: "login.notAdmin", email: input.email };
  }

  writeSessionCookies(await cookies(), parsed.tokens);
  redirect(safeRedirectPath(typeof form.get("next") === "string" ? (form.get("next") as string) : null));
}
