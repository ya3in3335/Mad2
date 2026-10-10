import type { Metadata } from "next";
import Link from "next/link";
import { FilterBar, FilterField } from "@/components/filter-bar";
import { FormDialog } from "@/components/form-dialog";
import { ApiError, Pagination } from "@/components/server-ui";
import { Card, EmptyState, PageHeader, StatusBadge, TableWrap } from "@/components/ui";
import { formatDate, formatNumber } from "@/lib/format";
import { enumOptions } from "@/lib/options";
import { param, parsePage } from "@/lib/paging";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import { TENANT_STATUSES, type Paged, type TenantSummary } from "@/lib/types";

export const metadata: Metadata = { title: "Tenants" };
const PAGE_SIZE = 25;

export default async function TenantsPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const sp = await searchParams;
  const { t, locale } = await getT();
  const page = parsePage(sp.page);
  const search = param(sp.search, 100);
  const status = (TENANT_STATUSES as readonly string[]).includes(param(sp.status)) ? param(sp.status) : "";
  const res = await apiGet<Paged<TenantSummary>>("/api/v1/platform/tenants", { page, pageSize: PAGE_SIZE, search, status });

  return (
    <>
      <PageHeader
        title={t("nav.tenants")}
        description={t("tenants.subtitle")}
        actions={
          <FormDialog
            label={t("tenants.new")}
            variant="primary"
            method="POST"
            path="/api/v1/platform/tenants"
            kind="tenant.create"
            resultView="setupToken"
            description={t("tenants.newHelp")}
            fields={[
              { name: "name", label: t("tenants.name"), type: "text", required: true, maxLength: 200, wide: true },
              { name: "email", label: t("auth.email"), type: "email", ltr: true },
              { name: "phone", label: t("crm.phone"), type: "text", ltr: true, maxLength: 50 },
              { name: "contactName", label: t("tenants.contact"), type: "text", maxLength: 200 },
              { name: "wilaya", label: t("onb.wilaya"), type: "text", maxLength: 100 },
              { name: "notes", label: t("common.notes"), type: "textarea", maxLength: 2000 },
            ]}
          />
        }
      />
      <Card bodyClassName="">
        <FilterBar action="/tenants">
          <FilterField label={t("action.search")} className="w-full sm:w-72">
            <input name="search" defaultValue={search} className="vx-input" placeholder={t("tenants.searchHint")} maxLength={100} />
          </FilterField>
          <FilterField label={t("lic.status")}>
            <select name="status" defaultValue={status} className="vx-input">
              <option value="">{t("common.all")}</option>
              {enumOptions(TENANT_STATUSES, t).map((o) => (
                <option key={o.value} value={o.value}>
                  {o.label}
                </option>
              ))}
            </select>
          </FilterField>
        </FilterBar>
        {!res.ok ? (
          <div className="p-4">
            <ApiError error={res.error} retryHref="/tenants" />
          </div>
        ) : res.data.items.length === 0 ? (
          <EmptyState title={t("tenants.empty")} description={search || status ? t("common.emptyFiltered") : undefined} />
        ) : (
          <>
            <TableWrap>
              <table className="vx-table">
                <thead>
                  <tr>
                    <th>{t("tenants.name")}</th>
                    <th>{t("lic.status")}</th>
                    <th>{t("lic.plan")}</th>
                    <th>{t("tenants.subscription")}</th>
                    <th className="text-end">{t("nav.devices")}</th>
                    <th className="text-end">{t("tenants.users")}</th>
                    <th>{t("onb.wilaya")}</th>
                    <th>{t("common.created")}</th>
                  </tr>
                </thead>
                <tbody>
                  {res.data.items.map((x) => (
                    <tr key={x.id}>
                      <td>
                        <Link href={`/tenants/${x.id}`} className="font-medium text-primary hover:underline">
                          {x.name}
                        </Link>
                        {x.email ? (
                          <div className="text-xs text-muted" dir="ltr">
                            {x.email}
                          </div>
                        ) : null}
                      </td>
                      <td>
                        <StatusBadge value={x.status} t={t} />
                        {!x.setupCompleted ? <div className="mt-1 text-xs text-warning">{t("tenants.setupPending")}</div> : null}
                      </td>
                      <td className="font-mono text-xs">{x.planCode ?? "—"}</td>
                      <td>
                        <StatusBadge value={x.subscriptionStatus} t={t} />
                        {x.subscriptionEndsAt ? <div className="mt-1 text-xs text-muted">{formatDate(x.subscriptionEndsAt, locale)}</div> : null}
                      </td>
                      <td className="text-end tabular-nums">{formatNumber(x.activeDevices, locale)}</td>
                      <td className="text-end tabular-nums">{formatNumber(x.users, locale)}</td>
                      <td>{x.wilaya ?? "—"}</td>
                      <td className="whitespace-nowrap">{formatDate(x.createdAt, locale)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </TableWrap>
            <Pagination base="/tenants" params={{ search, status }} page={res.data.page} pageSize={res.data.pageSize} totalCount={res.data.totalCount} />
          </>
        )}
      </Card>
    </>
  );
}
