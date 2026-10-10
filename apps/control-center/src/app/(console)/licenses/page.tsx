import type { Metadata } from "next";
import Link from "next/link";
import { FilterBar, FilterField } from "@/components/filter-bar";
import { FormDialog } from "@/components/form-dialog";
import { ApiError, Pagination } from "@/components/server-ui";
import { Card, EmptyState, PageHeader, StatusBadge, TableWrap } from "@/components/ui";
import { licenseCreateFields } from "@/lib/field-sets";
import { daysUntil, formatDate, formatNumber } from "@/lib/format";
import { isGuid, param, parsePage } from "@/lib/paging";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import type { License, Paged } from "@/lib/types";

export const metadata: Metadata = { title: "Licenses" };
const PAGE_SIZE = 25;

export default async function LicensesPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const sp = await searchParams;
  const { t, locale } = await getT();
  const page = parsePage(sp.page);
  const search = param(sp.search, 100);
  const tenantId = isGuid(param(sp.tenantId)) ? param(sp.tenantId) : "";
  const res = await apiGet<Paged<License>>("/api/v1/platform/licenses", { page, pageSize: PAGE_SIZE, search, tenantId });

  return (
    <>
      <PageHeader
        title={t("nav.licensing")}
        description={t("lic.subtitle")}
        actions={
          <FormDialog
            label={t("lic.issue")}
            variant="primary"
            method="POST"
            path="/api/v1/platform/licenses"
            kind="license.create"
            resultView="licenseKey"
            fields={licenseCreateFields(t, tenantId || undefined)}
          />
        }
      />
      <Card bodyClassName="">
        <FilterBar action="/licenses">
          <FilterField label={t("action.search")} className="w-full sm:w-72">
            <input name="search" defaultValue={search} className="vx-input" placeholder={t("lic.searchHint")} maxLength={100} />
          </FilterField>
          <FilterField label={t("common.tenantId")} className="w-full sm:w-80">
            <input name="tenantId" defaultValue={tenantId} className="vx-input font-mono text-xs" dir="ltr" maxLength={36} />
          </FilterField>
        </FilterBar>
        {!res.ok ? (
          <div className="p-4">
            <ApiError error={res.error} retryHref="/licenses" />
          </div>
        ) : res.data.items.length === 0 ? (
          <EmptyState title={t("lic.empty")} description={search || tenantId ? t("common.emptyFiltered") : undefined} />
        ) : (
          <>
            <TableWrap>
              <table className="vx-table">
                <thead>
                  <tr>
                    <th>{t("lic.key")}</th>
                    <th>{t("common.tenant")}</th>
                    <th>{t("lic.type")}</th>
                    <th>{t("lic.plan")}</th>
                    <th>{t("lic.status")}</th>
                    <th className="text-end">{t("lic.seats")}</th>
                    <th>{t("lic.expires")}</th>
                  </tr>
                </thead>
                <tbody>
                  {res.data.items.map((l) => {
                    const days = daysUntil(l.expiresAt);
                    return (
                      <tr key={l.id}>
                        <td>
                          <Link href={`/licenses/${l.id}`} className="font-mono text-primary hover:underline" dir="ltr">
                            {l.keyHint}
                          </Link>
                        </td>
                        <td>
                          <Link href={`/tenants/${l.tenantId}`} className="hover:underline">
                            {l.tenantName ?? l.tenantId.slice(0, 8)}
                          </Link>
                        </td>
                        <td>{l.type}</td>
                        <td className="font-mono text-xs">{l.planCode}</td>
                        <td>
                          <StatusBadge value={l.effectiveStatus} t={t} />
                        </td>
                        <td className="text-end tabular-nums">
                          {formatNumber(l.activeDevices, locale)} / {formatNumber(l.maxDevices, locale)}
                        </td>
                        <td className={`whitespace-nowrap ${days !== null && days <= 14 ? "text-warning" : ""}`}>
                          {l.expiresAt ? formatDate(l.expiresAt, locale) : t("lic.perpetual")}
                          {days !== null && days >= 0 && days <= 14 ? <div className="text-xs">{t("lic.daysLeft", { days })}</div> : null}
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </TableWrap>
            <Pagination base="/licenses" params={{ search, tenantId }} page={res.data.page} pageSize={res.data.pageSize} totalCount={res.data.totalCount} />
          </>
        )}
      </Card>
    </>
  );
}
