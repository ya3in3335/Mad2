"use client";

import { createContext, useContext, useMemo, type ReactNode } from "react";
import { createTranslator, type Dictionary, type Locale, type Translator } from "@/lib/i18n-core";

interface I18nValue {
  locale: Locale;
  t: Translator;
}

const I18nContext = createContext<I18nValue | null>(null);

export function I18nProvider({ locale, dict, fallback, children }: { locale: Locale; dict: Dictionary; fallback: Dictionary; children: ReactNode }) {
  const value = useMemo(() => ({ locale, t: createTranslator(dict, fallback) }), [locale, dict, fallback]);
  return <I18nContext.Provider value={value}>{children}</I18nContext.Provider>;
}

export function useI18n(): I18nValue {
  const v = useContext(I18nContext);
  if (!v) throw new Error("useI18n must be used inside <I18nProvider>.");
  return v;
}
