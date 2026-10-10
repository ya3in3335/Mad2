"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useState } from "react";
import { signOutAction } from "@/app/actions";
import { isActivePath, type NavSection } from "@/lib/nav";
import { useI18n } from "./i18n-client";
import { Logo } from "./logo";
import { LocaleSwitcher, ThemeToggle } from "./preferences";

function Icon({ d }: { d: string }) {
  return (
    <svg viewBox="0 0 24 24" aria-hidden className="h-[18px] w-[18px] shrink-0" fill="none" stroke="currentColor" strokeWidth={1.8} strokeLinecap="round" strokeLinejoin="round">
      <path d={d} />
    </svg>
  );
}

export function Sidebar({ nav, user, theme }: { nav: NavSection[]; user: { fullName: string; email: string; mfaEnabled: boolean }; theme: "dark" | "light" }) {
  const { t } = useI18n();
  const pathname = usePathname();
  const [open, setOpen] = useState(false);

  useEffect(() => setOpen(false), [pathname]);

  return (
    <>
      <div className="sticky top-0 z-30 flex items-center justify-between border-b border-line bg-surface/95 px-4 py-3 backdrop-blur lg:hidden">
        <Logo theme={theme} className="h-7 w-auto" />
        <button type="button" className="vx-btn vx-btn-secondary px-2.5" aria-expanded={open} aria-controls="vx-sidebar" onClick={() => setOpen((o) => !o)}>
          <span className="sr-only">{t("nav.menu")}</span>
          <Icon d={open ? "M6 6l12 12M18 6L6 18" : "M4 6h16M4 12h16M4 18h16"} />
        </button>
      </div>
      {open ? <div className="fixed inset-0 z-30 bg-black/50 lg:hidden" onClick={() => setOpen(false)} aria-hidden /> : null}
      <aside
        id="vx-sidebar"
        className={`fixed inset-y-0 start-0 z-40 flex w-64 flex-col border-e border-line bg-surface transition-transform lg:sticky lg:top-0 lg:h-screen lg:translate-x-0 lg:rtl:translate-x-0 ${
          open ? "translate-x-0" : "-translate-x-full rtl:translate-x-full"
        }`}
      >
        <div className="hidden px-5 pb-2 pt-5 lg:block">
          <Logo theme={theme} className="h-8 w-auto" />
          <p className="mt-1 text-[11px] font-medium uppercase tracking-[0.18em] text-accent">{t("app.controlCenter")}</p>
        </div>
        <nav aria-label={t("nav.main")} className="flex-1 overflow-y-auto px-3 py-3">
          {nav.map((section) => (
            <div key={section.key} className="mb-4">
              <p className="px-3 pb-1 text-[11px] font-semibold uppercase tracking-wider text-muted/80">{t(section.key)}</p>
              <ul className="space-y-0.5">
                {section.items.map((item) => {
                  const active = isActivePath(item.href, pathname);
                  return (
                    <li key={item.href}>
                      <Link
                        href={item.href}
                        aria-current={active ? "page" : undefined}
                        className={`flex items-center gap-3 rounded-md px-3 py-2 text-sm transition-colors ${
                          active ? "bg-primary/15 font-medium text-fg shadow-[inset_3px_0_0_var(--vx-accent)] rtl:shadow-[inset_-3px_0_0_var(--vx-accent)]" : "text-muted hover:bg-raised hover:text-fg"
                        }`}
                      >
                        <span className={active ? "text-accent" : undefined}>
                          <Icon d={item.icon} />
                        </span>
                        {t(item.key)}
                      </Link>
                    </li>
                  );
                })}
              </ul>
            </div>
          ))}
        </nav>
        <div className="space-y-2 border-t border-line p-3">
          <div className="px-2">
            <p className="truncate text-sm font-medium">{user.fullName}</p>
            <p className="truncate text-xs text-muted" dir="ltr">
              {user.email}
            </p>
            {!user.mfaEnabled ? <p className="mt-1 text-xs text-warning">{t("shell.mfaOff")}</p> : null}
          </div>
          <LocaleSwitcher className="block" />
          <ThemeToggle theme={theme} />
          <form action={signOutAction}>
            <button type="submit" className="vx-btn vx-btn-ghost w-full justify-start text-danger">
              <Icon d="M15 4h4v16h-4M10 8l-4 4 4 4M6 12h10" />
              {t("action.signOut")}
            </button>
          </form>
        </div>
      </aside>
    </>
  );
}
