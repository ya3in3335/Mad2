export const LOCALES = ["en", "fr", "ar"] as const;
export type Locale = (typeof LOCALES)[number];
export const DEFAULT_LOCALE: Locale = "en";
export const LOCALE_COOKIE = "vx_lang";
export const THEME_COOKIE = "vx_theme";
export type Theme = "dark" | "light";

export type Dictionary = Record<string, string>;

export function isLocale(v: unknown): v is Locale {
  return typeof v === "string" && (LOCALES as readonly string[]).includes(v);
}

export function isRtl(locale: Locale): boolean {
  return locale === "ar";
}

/** Cookie wins; otherwise the first supported language in Accept-Language; otherwise English. */
export function resolveLocale(cookieValue: string | undefined | null, acceptLanguage?: string | null): Locale {
  if (isLocale(cookieValue)) return cookieValue;
  if (acceptLanguage) {
    const prefs = acceptLanguage
      .split(",")
      .map((part) => {
        const [tag, ...params] = part.trim().split(";");
        const q = params.find((p) => p.trim().startsWith("q="));
        return { lang: (tag ?? "").toLowerCase().split("-")[0] ?? "", q: q ? Number(q.trim().slice(2)) || 0 : 1 };
      })
      .sort((a, b) => b.q - a.q);
    for (const p of prefs) if (isLocale(p.lang)) return p.lang;
  }
  return DEFAULT_LOCALE;
}

export function resolveTheme(cookieValue: string | undefined | null): Theme {
  return cookieValue === "light" ? "light" : "dark";
}

/** Looks a key up in the dictionary, falling back to English, then the key itself; supports {name} placeholders. */
export function translate(
  dict: Dictionary,
  fallback: Dictionary,
  key: string,
  vars?: Record<string, string | number>,
): string {
  const template = dict[key] ?? fallback[key] ?? key;
  if (!vars) return template;
  return template.replace(/\{(\w+)\}/g, (m, name: string) => (name in vars ? String(vars[name]) : m));
}

export type Translator = (key: string, vars?: Record<string, string | number>) => string;

export function createTranslator(dict: Dictionary, fallback: Dictionary): Translator {
  return (key, vars) => translate(dict, fallback, key, vars);
}
