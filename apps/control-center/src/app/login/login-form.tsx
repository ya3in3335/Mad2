"use client";

import { useActionState, useState } from "react";
import { useI18n } from "@/components/i18n-client";
import { INITIAL_LOGIN_STATE } from "@/lib/login";
import { loginAction } from "./actions";

export function LoginForm({ next }: { next: string }) {
  const { t } = useI18n();
  const [state, action, pending] = useActionState(loginAction, INITIAL_LOGIN_STATE);
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const mfa = state.step === "mfa";

  return (
    <form action={action} className="mt-6 space-y-4" noValidate>
      <input type="hidden" name="next" value={next} />
      {mfa ? (
        <>
          <input type="hidden" name="email" value={email} />
          <input type="hidden" name="password" value={password} />
          <p className="text-sm text-muted">{t("login.mfaPrompt")}</p>
          <div>
            <label htmlFor="mfaCode" className="vx-label">
              {t("auth.mfaCode")}
            </label>
            <input
              id="mfaCode"
              name="mfaCode"
              className="vx-input text-center font-mono text-lg tracking-[0.4em]"
              inputMode="numeric"
              autoComplete="one-time-code"
              pattern="[0-9]*"
              maxLength={8}
              required
              autoFocus
            />
          </div>
        </>
      ) : (
        <>
          <div>
            <label htmlFor="email" className="vx-label">
              {t("auth.email")}
            </label>
            <input
              id="email"
              name="email"
              type="email"
              className="vx-input"
              autoComplete="username"
              required
              maxLength={256}
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              autoFocus
            />
          </div>
          <div>
            <label htmlFor="password" className="vx-label">
              {t("auth.password")}
            </label>
            <input
              id="password"
              name="password"
              type="password"
              className="vx-input"
              autoComplete="current-password"
              required
              maxLength={128}
              value={password}
              onChange={(e) => setPassword(e.target.value)}
            />
          </div>
        </>
      )}
      {state.error ? (
        <p role="alert" className="rounded-md border border-danger/40 bg-danger/10 px-3 py-2 text-sm text-danger">
          {t(state.error)}
        </p>
      ) : null}
      <button type="submit" className="vx-btn vx-btn-primary w-full py-2.5" disabled={pending}>
        {pending ? t("state.loading") : mfa ? t("login.verify") : t("action.signIn")}
      </button>
      {mfa ? (
        <button
          type="button"
          className="vx-btn vx-btn-ghost w-full"
          onClick={() => {
            setPassword("");
            window.location.reload();
          }}
        >
          {t("action.back")}
        </button>
      ) : null}
    </form>
  );
}
