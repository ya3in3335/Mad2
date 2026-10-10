import Link from "next/link";
import { totalPages, pageWindow, withParams } from "@/lib/paging";
import type { ActionError } from "@/lib/problem";
import { getT } from "@/lib/server/i18n";
import { formatNumber } from "@/lib/format";
import { ErrorPanel } from "./ui";

/** Error state for a failed API read (RFC 7807 problem already normalised by lib/problem). */
export async function ApiError({ error, retryHref }: { error: ActionError; retryHref?: string }) {
  const { t } = await getT();
  const title = error.status === 403 ? t("err.permission") : error.status === 0 ? t("err.network") : t("err.loadFailed");
  return <ErrorPanel title={title} message={error.message} code={error.code} retryHref={retryHref} retryLabel={t("action.retry")} />;
}

export async function Pagination({
  base,
  params,
  page,
  pageSize,
  totalCount,
}: {
  base: string;
  params: Record<string, string | undefined>;
  page: number;
  pageSize: number;
  totalCount: number;
}) {
  const { t, locale } = await getT();
  const pages = totalPages(totalCount, pageSize);
  const from = totalCount === 0 ? 0 : (page - 1) * pageSize + 1;
  const to = Math.min(totalCount, page * pageSize);
  return (
    <nav aria-label={t("paging.label")} className="flex flex-wrap items-center justify-between gap-3 border-t border-line px-4 py-3 text-sm">
      <p className="text-muted">
        {t("paging.summary", { from: formatNumber(from, locale), to: formatNumber(to, locale), total: formatNumber(totalCount, locale) })}
      </p>
      {pages > 1 ? (
        <ul className="flex items-center gap-1">
          <li>
            {page > 1 ? (
              <Link className="vx-btn vx-btn-ghost px-2 py-1" href={withParams(base, params, { page: page - 1 })} rel="prev">
                {t("paging.prev")}
              </Link>
            ) : null}
          </li>
          {pageWindow(page, pages).map((p, i) =>
            p === "…" ? (
              <li key={`e${i}`} className="px-1 text-muted">
                …
              </li>
            ) : (
              <li key={p}>
                <Link
                  href={withParams(base, params, { page: p })}
                  aria-current={p === page ? "page" : undefined}
                  className={`vx-btn px-2.5 py-1 ${p === page ? "bg-primary text-white" : "vx-btn-ghost"}`}
                >
                  {formatNumber(p, locale)}
                </Link>
              </li>
            ),
          )}
          <li>
            {page < pages ? (
              <Link className="vx-btn vx-btn-ghost px-2 py-1" href={withParams(base, params, { page: page + 1 })} rel="next">
                {t("paging.next")}
              </Link>
            ) : null}
          </li>
        </ul>
      ) : null}
    </nav>
  );
}
