"use client";

import { useRouter } from "next/navigation";
import { useTransition } from "react";
import { setLocaleAction, setThemeAction } from "@/app/actions";
import { LOCALES, type Locale } from "@/lib/i18n-core";
import { useI18n } from "./i18n-client";

const LABELS: Record<Locale, string> = { en: "English", fr: "Français", ar: "العربية" };

export function LocaleSwitcher({ className }: { className?: string }) {
  const { locale, t } = useI18n();
  const router = useRouter();
  const [pending, start] = useTransition();
  return (
    <label className={className}>
      <span className="sr-only">{t("set.language")}</span>
      <select
        className="vx-input py-1.5"
        value={locale}
        disabled={pending}
        onChange={(e) => {
          const next = e.target.value;
          start(async () => {
            await setLocaleAction(next);
            router.refresh();
          });
        }}
      >
        {LOCALES.map((l) => (
          <option key={l} value={l} lang={l}>
            {LABELS[l]}
          </option>
        ))}
      </select>
    </label>
  );
}

export function ThemeToggle({ theme }: { theme: "dark" | "light" }) {
  const { t } = useI18n();
  const router = useRouter();
  const [pending, start] = useTransition();
  const next = theme === "dark" ? "light" : "dark";
  return (
    <button
      type="button"
      className="vx-btn vx-btn-ghost w-full justify-start"
      disabled={pending}
      onClick={() =>
        start(async () => {
          await setThemeAction(next);
          router.refresh();
        })
      }
    >
      <span aria-hidden>{theme === "dark" ? "☀" : "☾"}</span>
      {t(next === "light" ? "set.themeLight" : "set.themeDark")}
    </button>
  );
}
