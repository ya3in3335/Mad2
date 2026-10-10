import type { Metadata } from "next";
import { LocaleSwitcher } from "@/components/preferences";
import { Logo } from "@/components/logo";
import { param } from "@/lib/paging";
import { safeRedirectPath } from "@/lib/safe-redirect";
import { getT, getTheme } from "@/lib/server/i18n";
import { LoginForm } from "./login-form";

export const metadata: Metadata = { title: "Sign in" };

const REASONS: Record<string, string> = {
  expired: "login.reasonExpired",
  session: "login.reasonExpired",
  forbidden: "login.notAdmin",
};

export default async function LoginPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const sp = await searchParams;
  const { t } = await getT();
  const theme = await getTheme();
  const next = safeRedirectPath(param(sp.next, 512));
  const reason = REASONS[param(sp.reason, 20)];
  return (
    <main className="relative flex min-h-screen items-center justify-center overflow-hidden px-4 py-10">
      <div
        aria-hidden
        className="pointer-events-none absolute inset-0 opacity-60"
        style={{
          background:
            "radial-gradient(60rem 30rem at 15% 10%, color-mix(in srgb, var(--vx-primary) 22%, transparent), transparent 70%), radial-gradient(40rem 30rem at 90% 90%, color-mix(in srgb, var(--vx-violet) 18%, transparent), transparent 70%)",
        }}
      />
      <div className="relative w-full max-w-md">
        <div className="mb-6 flex items-center justify-between gap-4">
          <Logo theme={theme} className="h-10 w-auto" />
          <LocaleSwitcher className="w-36" />
        </div>
        <div className="vx-card p-6 shadow-xl shadow-black/10 sm:p-8">
          <h1 className="text-xl font-semibold">{t("login.title")}</h1>
          <p className="mt-1 text-sm text-muted">{t("login.subtitle")}</p>
          {reason ? (
            <p role="status" className="mt-4 rounded-md border border-warning/40 bg-warning/10 px-3 py-2 text-sm text-warning">
              {t(reason)}
            </p>
          ) : null}
          <LoginForm next={next} />
        </div>
        <p className="mt-6 text-center text-xs text-muted">{t("login.securityNote")}</p>
      </div>
    </main>
  );
}
