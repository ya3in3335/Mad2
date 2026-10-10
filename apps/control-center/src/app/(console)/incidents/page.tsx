import type { Metadata } from "next";
import Link from "next/link";
import { FilterBar, FilterField } from "@/components/filter-bar";
import { SelectFilter, oneOf } from "@/components/select-filter";
import { ApiError, Pagination } from "@/components/server-ui";
import { Card, EmptyState, PageHeader, StatusBadge, TableWrap } from "@/components/ui";
import { formatDateTime, formatNumber } from "@/lib/format";
import { enumOptions } from "@/lib/options";
import { isGuid, param, parsePage } from "@/lib/paging";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import { humanize } from "@/lib/status";
import { FLEET_PLATFORMS, INCIDENT_CATEGORIES, INCIDENT_SEVERITIES, INCIDENT_STATUS_FILTERS, type Incident, type Paged } from "@/lib/types";

export const metadata: Metadata = { title: "Incidents" };
const PAGE_SIZE = 50;

export default async function IncidentsPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const sp = await searchParams;
  const { t, locale } = await getT();
  const page = parsePage(sp.page);
  const search = param(sp.search, 100);
  const tenantId = isGuid(param(sp.tenantId)) ? param(sp.tenantId) : "";
  const status = oneOf(param(sp.status), INCIDENT_STATUS_FILTERS);
  const category = oneOf(param(sp.category), INCIDENT_CATEGORIES);
  const severity = oneOf(param(sp.severity), INCIDENT_SEVERITIES);
  const platform = oneOf(param(sp.platform), FLEET_PLATFORMS);
  const source = oneOf(param(sp.source), ["client", "server"] as const);
  const res = await apiGet<Paged<Incident>>("/api/v1/platform/incidents", { page, pageSize: PAGE_SIZE, search, tenantId, status, category, severity, platform, source });
  const filtered = Boolean(search || tenantId || status || category || severity || platform || source);

  return (
    <>
      <PageHeader
        title={t("nav.incidents")}
        description={t("inc.subtitle")}
        actions={
          <Link href="/incidents/groups" className="vx-btn vx-btn-secondary">
            {t("inc.groups")}
          </Link>
        }
      />
      <Card bodyClassName="">
        <FilterBar action="/incidents">
          <FilterField label={t("action.search")} className="w-full sm:w-64">
            <input name="search" defaultValue={search} className="vx-input" maxLength={100} />
          </FilterField>
          <FilterField label={t("common.tenantId")} className="w-full sm:w-80">
            <input name="tenantId" defaultValue={tenantId} className="vx-input font-mono text-xs" dir="ltr" maxLength={36} />
          </FilterField>
          <SelectFilter name="status" label={t("lic.status")} value={status} allLabel={t("common.all")} options={enumOptions(INCIDENT_STATUS_FILTERS, t)} />
          <SelectFilter name="severity" label={t("inc.severity")} value={severity} allLabel={t("common.all")} options={enumOptions(INCIDENT_SEVERITIES, t)} />
          <SelectFilter name="category" label={t("inc.category")} value={category} allLabel={t("common.all")} options={enumOptions(INCIDENT_CATEGORIES, t, "incCat")} />
          <SelectFilter name="platform" label={t("dev.platform")} value={platform} allLabel={t("common.all")} options={FLEET_PLATFORMS.map((p) => ({ value: p, label: t(`fleet.platform.${p}`) }))} />
          <SelectFilter
            name="source"
            label={t("inc.source")}
            value={source}
            allLabel={t("common.all")}
            options={[
              { value: "client", label: t("inc.source.client") },
              { value: "server", label: t("inc.source.server") },
            ]}
          />
        </FilterBar>
        {!res.ok ? (
          <div className="p-4">
            <ApiError error={res.error} retryHref="/incidents" />
          </div>
        ) : res.data.items.length === 0 ? (
          <EmptyState title={t("inc.empty")} description={filtered ? t("common.emptyFiltered") : undefined} />
        ) : (
          <>
            <TableWrap>
              <table className="vx-table">
                <thead>
                  <tr>
                    <th>{t("inc.title")}</th>
                    <th>{t("common.tenant")}</th>
                    <th>{t("inc.category")}</th>
                    <th>{t("inc.severity")}</th>
                    <th>{t("lic.status")}</th>
                    <th className="text-end">{t("inc.occurrences")}</th>
                    <th>{t("inc.lastSeen")}</th>
                  </tr>
                </thead>
                <tbody>
                  {res.data.items.map((i) => (
                    <tr key={i.id}>
                      <td className="max-w-md">
                        <Link href={`/incidents/${i.id}`} className="font-medium text-primary hover:underline">
                          {i.title}
                        </Link>
                        <div className="text-xs text-muted" dir="ltr">
                          {i.app} {i.appVersion} · {i.platform}
                        </div>
                      </td>
                      <td>
                        {i.tenantId ? (
                          <Link href={`/tenants/${i.tenantId}`} className="hover:underline">
                            {i.tenantName ?? i.tenantId.slice(0, 8)}
                          </Link>
                        ) : (
                          "—"
                        )}
                      </td>
                      <td>{t(`incCat.${i.category}`) === `incCat.${i.category}` ? humanize(i.category) : t(`incCat.${i.category}`)}</td>
                      <td>
                        <StatusBadge value={i.severity} t={t} />
                      </td>
                      <td>
                        <StatusBadge value={i.status} t={t} />
                      </td>
                      <td className="text-end tabular-nums">{formatNumber(i.occurrenceCount, locale)}</td>
                      <td className="whitespace-nowrap">{formatDateTime(i.lastSeenAt, locale)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </TableWrap>
            <Pagination base="/incidents" params={{ search, tenantId, status, category, severity, platform, source }} page={res.data.page} pageSize={res.data.pageSize} totalCount={res.data.totalCount} />
          </>
        )}
      </Card>
    </>
  );
}
