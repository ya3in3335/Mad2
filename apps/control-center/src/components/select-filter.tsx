import { FilterField } from "@/components/filter-bar";
import type { FieldOption } from "@/lib/forms";

/** A <select> filter for GET filter bars; the first option clears the filter. */
export function SelectFilter({ name, label, value, options, allLabel, className = "w-full sm:w-44" }: { name: string; label: string; value: string; options: FieldOption[]; allLabel: string; className?: string }) {
  return (
    <FilterField label={label} className={className}>
      <select name={name} defaultValue={value} className="vx-input">
        <option value="">{allLabel}</option>
        {options.map((o) => (
          <option key={o.value} value={o.value}>
            {o.label}
          </option>
        ))}
      </select>
    </FilterField>
  );
}

/** Keeps a query value only when it is one of the allowed values. */
export function oneOf<T extends string>(value: string, allowed: readonly T[]): T | "" {
  return (allowed as readonly string[]).includes(value) ? (value as T) : "";
}
