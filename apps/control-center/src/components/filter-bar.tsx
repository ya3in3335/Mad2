import Link from "next/link";
import type { ReactNode } from "react";
import { getT } from "@/lib/server/i18n";

/** Plain GET form: works without JavaScript and keeps filters in the URL (shareable, back-button friendly). */
export async function FilterBar({ action, children }: { action: string; children: ReactNode }) {
  const { t } = await getT();
  return (
    <form method="get" action={action} role="search" className="flex flex-wrap items-end gap-3 border-b border-line p-4">
      {children}
      <div className="flex gap-2">
        <button type="submit" className="vx-btn vx-btn-primary">
          {t("action.filter")}
        </button>
        <Link href={action} className="vx-btn vx-btn-ghost">
          {t("action.reset")}
        </Link>
      </div>
    </form>
  );
}

export function FilterField({ label, children, className = "w-full sm:w-48" }: { label: string; children: ReactNode; className?: string }) {
  return (
    <label className={className}>
      <span className="vx-label">{label}</span>
      {children}
    </label>
  );
}
