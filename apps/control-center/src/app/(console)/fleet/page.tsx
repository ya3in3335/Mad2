import type { Metadata } from "next";
import Link from "next/link";
import { FilterBar, FilterField } from "@/components/filter-bar";
import { SelectFilter, oneOf } from "@/components/select-filter";
import { ApiError, Pagination } from "@/components/server-ui";
import { Badge, Card, EmptyState, PageHeader, StatusBadge, TableWrap } from "@/components/ui";
import { formatDateTime, formatNumber } from "@/lib/format";
import { enumOptions } from "@/lib/options";
import { isGuid, param, parsePage } from "@/lib/paging";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import { FLEET_DEVICE_STATUSES, FLEET_PLATFORMS, type AdminDevice, type Paged } from "@/lib/types";

export const metadata: Metadata = { title: "Device fleet" };
const PAGE_SIZE = 50;

export default async function FleetPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const sp = await searchParams;
  const { t, locale } = await getT();
  const page = parsePage(sp.page);
  const search = param(sp.search, 100);
  const tenantId = isGuid(param(sp.tenantId)) ? param(sp.tenantId) : "";
  const platform = oneOf(param(sp.platform), FLEET_PLATFORMS);
  const status = oneOf(param(sp.status), FLEET_DEVICE_STATUSES);
  const online = oneOf(param(sp.online), ["true", "false"] as const);
  const res = await apiGet<Paged<AdminDevice>>("/api/v1/platform/fleet/devices", { page, pageSize: PAGE_SIZE, search, tenantId, platform, status, online });
  const filtered = Boolean(search || tenantId || platform || status || online);

  return (
    <>
      <PageHeader title={t("nav.fleet")} description={t("fleet.subtitle")} />
      <Card bodyClassName="">
        <FilterBar action="/fleet">
          <FilterField label={t("action.search")} className="w-full sm:w-64">
            <input name="search" defaultValue={search} className="vx-input" placeholder={t("dev.searchHint")} maxLength={100} />
          </FilterField>
          <FilterField label={t("common.tenantId")} className="w-full sm:w-80">
            <input name="tenantId" defaultValue={tenantId} className="vx-input font-mono text-xs" dir="ltr" maxLength={36} />
          </FilterField>
          <SelectFilter name="platform" label={t("dev.platform")} value={platform} allLabel={t("common.all")} options={FLEET_PLATFORMS.map((p) => ({ value: p, label: t(`fleet.platform.${p}`) }))} />
          <SelectFilter name="status" label={t("lic.status")} value={status} allLabel={t("common.all")} options={enumOptions(FLEET_DEVICE_STATUSES, t)} />
          <SelectFilter
            name="online"
            label={t("fleet.connectivity")}
            value={online}
            allLabel={t("common.all")}
            options={[
              { value: "true", label: t("fleet.online") },
              { value: "false", label: t("fleet.offline") },
            ]}
          />
        </FilterBar>
        {!res.ok ? (
          <div className="p-4">
            <ApiError error={res.error} retryHref="/fleet" />
          </div>
        ) : res.data.items.length === 0 ? (
          <EmptyState title={t("dev.empty")} description={filtered ? t("common.emptyFiltered") : undefined} />
        ) : (
          <>
            <TableWrap>
              <table className="vx-table">
                <thead>
                  <tr>
                    <th>{t("dev.name")}</th>
                    <th>{t("common.tenant")}</th>
                    <th>{t("dev.platform")}</th>
                    <th>{t("app.version")}</th>
                    <th>{t("lic.status")}</th>
                    <th>{t("fleet.connectivity")}</th>
                    <th className="text-end">{t("fleet.syncFailed")}</th>
                    <th>{t("dev.lastSeen")}</th>
                  </tr>
                </thead>
                <tbody>
                  {res.data.items.map((d) => (
                    <tr key={d.id}>
                      <td>
                        <Link href={`/fleet/${d.id}`} className="font-medium text-primary hover:underline">
                          {d.deviceName}
                        </Link>
                      </td>
                      <td>
                        <Link href={`/tenants/${d.tenantId}`} className="hover:underline">
                          {d.tenantName ?? d.tenantId.slice(0, 8)}
                        </Link>
                      </td>
                      <td>{d.platform}</td>
                      <td className="font-mono text-xs" dir="ltr">
                        {d.appVersion}
                      </td>
                      <td>
                        <StatusBadge value={d.status} t={t} />
                      </td>
                      <td>{d.online ? <Badge tone="success">{t("fleet.online")}</Badge> : <Badge>{t("fleet.offline")}</Badge>}</td>
                      <td className={`text-end tabular-nums ${d.failedSyncOperations ? "text-danger" : ""}`}>{d.failedSyncOperations === null ? "—" : formatNumber(d.failedSyncOperations, locale)}</td>
                      <td className="whitespace-nowrap">{formatDateTime(d.lastContactAt, locale)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </TableWrap>
            <Pagination base="/fleet" params={{ search, tenantId, platform, status, online }} page={res.data.page} pageSize={res.data.pageSize} totalCount={res.data.totalCount} />
          </>
        )}
      </Card>
    </>
  );
}
