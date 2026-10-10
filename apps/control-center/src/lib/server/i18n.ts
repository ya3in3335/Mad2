import "server-only";
import { cookies, headers } from "next/headers";
import { cache } from "react";
import { createTranslator, LOCALE_COOKIE, resolveLocale, resolveTheme, THEME_COOKIE, type Locale, type Theme, type Translator } from "../i18n-core";
import { DICTIONARIES } from "../i18n-dictionaries";

export const getLocale = cache(async (): Promise<Locale> => {
  const jar = await cookies();
  const h = await headers();
  return resolveLocale(jar.get(LOCALE_COOKIE)?.value, h.get("accept-language"));
});

export const getTheme = cache(async (): Promise<Theme> => resolveTheme((await cookies()).get(THEME_COOKIE)?.value));

export const getT = cache(async (): Promise<{ t: Translator; locale: Locale }> => {
  const locale = await getLocale();
  return { t: createTranslator(DICTIONARIES[locale], DICTIONARIES.en), locale };
});
