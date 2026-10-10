import type { Metadata } from "next";
import Link from "next/link";
import { ApiError } from "@/components/server-ui";
import { Card, EmptyState, PageHeader, StatusBadge, TableWrap } from "@/components/ui";
import { formatDateTime, formatNumber, shortId } from "@/lib/format";
import { param } from "@/lib/paging";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import { toneFor } from "@/lib/status";
import type { SecurityAlert } from "@/lib/types";

export const metadata: Metadata = { title: "Security alerts" };
const WINDOWS = [1, 7, 30, 90] as const;

export default async function SecurityPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const sp = await searchParams;
  const { t, locale } = await getT();
  const requested = Number(param(sp.days));
  const days = (WINDOWS as readonly number[]).includes(requested) ? requested : 7;
  const res = await apiGet<SecurityAlert[]>("/api/v1/platform/security-alerts", { days });
  const counts = res.ok
    ? res.data.reduce<Record<string, number>>((acc, a) => {
        const tone = toneFor(a.severity);
        acc[tone] = (acc[tone] ?? 0) + 1;
        return acc;
      }, {})
    : {};

  return (
    <>
      <PageHeader
        title={t("nav.security")}
        description={t("security.subtitle")}
        actions={
          <div className="flex rounded-md border border-line p-0.5" role="group" aria-label={t("security.window")}>
            {WINDOWS.map((d) => (
              <Link
                key={d}
                href={`/security?days=${d}`}
                aria-current={d === days ? "true" : undefined}
                className={`rounded px-3 py-1 text-sm ${d === days ? "bg-primary text-white" : "text-muted hover:text-fg"}`}
              >
                {t("security.days", { days: d })}
              </Link>
            ))}
          </div>
        }
      />
      {res.ok && res.data.length > 0 ? (
        <div className="mb-4 flex flex-wrap gap-3 text-sm">
          <span className="text-danger">{t("security.high", { count: formatNumber(counts.danger ?? 0, locale) })}</span>
          <span className="text-warning">{t("security.medium", { count: formatNumber(counts.warning ?? 0, locale) })}</span>
          <span className="text-muted">{t("security.other", { count: formatNumber((counts.info ?? 0) + (counts.neutral ?? 0), locale) })}</span>
        </div>
      ) : null}
      <Card bodyClassName="">
        {!res.ok ? (
          <div className="p-4">
            <ApiError error={res.error} retryHref="/security" />
          </div>
        ) : res.data.length === 0 ? (
          <EmptyState title={t("security.empty")} description={t("security.emptyHelp")} />
        ) : (
          <TableWrap>
            <table className="vx-table">
              <thead>
                <tr>
                  <th>{t("audit.when")}</th>
                  <th>{t("security.severity")}</th>
                  <th>{t("security.kind")}</th>
                  <th>{t("audit.details")}</th>
                  <th>{t("common.tenant")}</th>
                  <th>{t("audit.user")}</th>
                </tr>
              </thead>
              <tbody>
                {res.data.map((a, i) => (
                  <tr key={`${a.kind}-${a.occurredAt}-${i}`}>
                    <td className="whitespace-nowrap text-xs">{formatDateTime(a.occurredAt, locale)}</td>
                    <td>
                      <StatusBadge value={a.severity} t={t} />
                    </td>
                    <td className="font-mono text-xs" dir="ltr">
                      {a.kind}
                    </td>
                    <td className="text-sm">{a.description}</td>
                    <td className="font-mono text-xs">
                      {a.tenantId ? (
                        <Link href={`/tenants/${a.tenantId}`} className="text-primary hover:underline">
                          {shortId(a.tenantId)}
                        </Link>
                      ) : (
                        "—"
                      )}
                    </td>
                    <td className="font-mono text-xs">{shortId(a.userId)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </TableWrap>
        )}
      </Card>
    </>
  );
}
