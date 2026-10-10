import type { Metadata } from "next";
import Link from "next/link";
import { FilterBar } from "@/components/filter-bar";
import { SelectFilter, oneOf } from "@/components/select-filter";
import { ApiError } from "@/components/server-ui";
import { Card, EmptyState, PageHeader, StatusBadge, TableWrap } from "@/components/ui";
import { formatDateTime, formatNumber } from "@/lib/format";
import { enumOptions } from "@/lib/options";
import { param } from "@/lib/paging";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import { humanize } from "@/lib/status";
import { INCIDENT_CATEGORIES, INCIDENT_STATUS_FILTERS, type IncidentGroup } from "@/lib/types";

export const metadata: Metadata = { title: "Incident groups" };

export default async function IncidentGroupsPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const sp = await searchParams;
  const { t, locale } = await getT();
  const status = oneOf(param(sp.status), INCIDENT_STATUS_FILTERS);
  const category = oneOf(param(sp.category), INCIDENT_CATEGORIES);
  const res = await apiGet<IncidentGroup[]>("/api/v1/platform/incidents/groups", { status, category, take: 100 });
  const n = (v: number) => formatNumber(v, locale);

  return (
    <>
      <nav className="mb-2 text-sm">
        <Link href="/incidents" className="text-muted hover:text-fg">
          ← {t("nav.incidents")}
        </Link>
      </nav>
      <PageHeader title={t("inc.groups")} description={t("inc.groupsSubtitle")} />
      <Card bodyClassName="">
        <FilterBar action="/incidents/groups">
          <SelectFilter name="status" label={t("lic.status")} value={status} allLabel={t("common.all")} options={enumOptions(INCIDENT_STATUS_FILTERS, t)} />
          <SelectFilter name="category" label={t("inc.category")} value={category} allLabel={t("common.all")} options={enumOptions(INCIDENT_CATEGORIES, t, "incCat")} />
        </FilterBar>
        {!res.ok ? (
          <div className="p-4">
            <ApiError error={res.error} retryHref="/incidents/groups" />
          </div>
        ) : res.data.length === 0 ? (
          <EmptyState title={t("inc.empty")} description={status || category ? t("common.emptyFiltered") : undefined} />
        ) : (
          <TableWrap>
            <table className="vx-table">
              <thead>
                <tr>
                  <th>{t("inc.title")}</th>
                  <th>{t("inc.category")}</th>
                  <th>{t("inc.severity")}</th>
                  <th className="text-end">{t("nav.tenants")}</th>
                  <th className="text-end">{t("inc.openCount")}</th>
                  <th className="text-end">{t("inc.occurrences")}</th>
                  <th>{t("inc.lastSeen")}</th>
                </tr>
              </thead>
              <tbody>
                {res.data.map((g) => (
                  <tr key={g.fingerprint}>
                    <td className="max-w-md">
                      <div className="font-medium">{g.title}</div>
                      <div className="text-xs text-muted" dir="ltr">
                        {g.app} · {g.errorType}
                      </div>
                    </td>
                    <td>{t(`incCat.${g.category}`) === `incCat.${g.category}` ? humanize(g.category) : t(`incCat.${g.category}`)}</td>
                    <td>
                      <StatusBadge value={g.maxSeverity} t={t} />
                    </td>
                    <td className="text-end tabular-nums">{n(g.tenants)}</td>
                    <td className="text-end tabular-nums">
                      {n(g.openIncidents)} / {n(g.incidents)}
                    </td>
                    <td className="text-end tabular-nums">{n(g.occurrences)}</td>
                    <td className="whitespace-nowrap">{formatDateTime(g.lastSeenAt, locale)}</td>
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
