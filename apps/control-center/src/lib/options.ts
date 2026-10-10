import type { Translator } from "./i18n-core";
import type { FieldOption } from "./forms";
import { humanize, statusKey } from "./status";

/** Select options for enum-like values, translated via status.<value> / <prefix>.<value> when available. */
export function enumOptions(values: readonly string[], t: Translator, prefix?: string): FieldOption[] {
  return values.map((v) => {
    const key = prefix ? `${prefix}.${v}` : statusKey(v);
    const label = t(key);
    return { value: v, label: label === key ? humanize(v) : label };
  });
}
