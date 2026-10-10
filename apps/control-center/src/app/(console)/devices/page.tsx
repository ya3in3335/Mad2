import type { Metadata } from "next";
import Link from "next/link";
import { FilterBar, FilterField } from "@/components/filter-bar";
import { FormDialog } from "@/components/form-dialog";
import { ApiError, Pagination } from "@/components/server-ui";
import { Card, EmptyState, PageHeader, StatusBadge, TableWrap } from "@/components/ui";
import { reasonField } from "@/lib/field-sets";
import { formatDateTime } from "@/lib/format";
import { isGuid, param, parsePage } from "@/lib/paging";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import type { Device, Paged } from "@/lib/types";

export const metadata: Metadata = { title: "Devices" };
const PAGE_SIZE = 50;

export default async function DevicesPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const sp = await searchParams;
  const { t, locale } = await getT();
  const page = parsePage(sp.page);
  const search = param(sp.search, 100);
  const tenantId = isGuid(param(sp.tenantId)) ? param(sp.tenantId) : "";
  const res = await apiGet<Paged<Device>>("/api/v1/platform/devices", { page, pageSize: PAGE_SIZE, search, tenantId });

  return (
    <>
      <PageHeader title={t("nav.devices")} description={t("dev.subtitle")} />
      <Card bodyClassName="">
        <FilterBar action="/devices">
          <FilterField label={t("action.search")} className="w-full sm:w-72">
            <input name="search" defaultValue={search} className="vx-input" placeholder={t("dev.searchHint")} maxLength={100} />
          </FilterField>
          <FilterField label={t("common.tenantId")} className="w-full sm:w-80">
            <input name="tenantId" defaultValue={tenantId} className="vx-input font-mono text-xs" dir="ltr" maxLength={36} />
          </FilterField>
        </FilterBar>
        {!res.ok ? (
          <div className="p-4">
            <ApiError error={res.error} retryHref="/devices" />
          </div>
        ) : res.data.items.length === 0 ? (
          <EmptyState title={t("dev.empty")} description={search || tenantId ? t("common.emptyFiltered") : undefined} />
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
                    <th>{t("dev.lastSeen")}</th>
                    <th />
                  </tr>
                </thead>
                <tbody>
                  {res.data.items.map((d) => (
                    <tr key={d.id}>
                      <td>
                        <div className="font-medium">{d.deviceName}</div>
                        <Link href={`/licenses/${d.licenseId}`} className="text-xs text-primary hover:underline">
                          {t("lic.license")}
                        </Link>
                      </td>
                      <td>
                        <Link href={`/tenants/${d.tenantId}`} className="hover:underline">
                          {d.tenantName ?? d.tenantId.slice(0, 8)}
                        </Link>
                      </td>
                      <td>{d.platform}</td>
                      <td className="font-mono text-xs">{d.appVersion}</td>
                      <td>
                        <StatusBadge value={d.status} t={t} />
                      </td>
                      <td className="whitespace-nowrap">{formatDateTime(d.lastSeenAt, locale)}</td>
                      <td className="text-end">
                        {d.status === "Active" ? (
                          <FormDialog
                            label={t("dev.revoke")}
                            size="sm"
                            variant="danger"
                            method="POST"
                            path={`/api/v1/platform/devices/${d.id}/revoke`}
                            kind="reason"
                            description={t("dev.revokeConfirm", { name: d.deviceName })}
                            fields={[reasonField(t)]}
                          />
                        ) : null}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </TableWrap>
            <Pagination base="/devices" params={{ search, tenantId }} page={res.data.page} pageSize={res.data.pageSize} totalCount={res.data.totalCount} />
          </>
        )}
      </Card>
    </>
  );
}
