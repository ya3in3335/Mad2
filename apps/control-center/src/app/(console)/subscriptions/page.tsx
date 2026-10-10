import type { Metadata } from "next";
import Link from "next/link";
import { FilterBar, FilterField } from "@/components/filter-bar";
import { FormDialog } from "@/components/form-dialog";
import { ApiError, Pagination } from "@/components/server-ui";
import { Card, EmptyState, PageHeader, StatusBadge, TableWrap } from "@/components/ui";
import { subscriptionCreateFields } from "@/lib/field-sets";
import { formatDate, formatMoney, formatNumber } from "@/lib/format";
import { enumOptions } from "@/lib/options";
import { param, parsePage } from "@/lib/paging";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import { SUBSCRIPTION_STATUSES, type Paged, type Plan, type Subscription } from "@/lib/types";

export const metadata: Metadata = { title: "Subscriptions" };
const PAGE_SIZE = 25;

export default async function SubscriptionsPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const sp = await searchParams;
  const { t, locale } = await getT();
  const page = parsePage(sp.page);
  const status = (SUBSCRIPTION_STATUSES as readonly string[]).includes(param(sp.status)) ? param(sp.status) : "";
  const [res, plansRes] = await Promise.all([
    apiGet<Paged<Subscription>>("/api/v1/platform/subscriptions", { page, pageSize: PAGE_SIZE, status }),
    apiGet<Plan[]>("/api/v1/platform/plans"),
  ]);
  const plans = plansRes.ok ? plansRes.data.filter((p) => p.isActive) : [];

  return (
    <>
      <PageHeader
        title={t("nav.subscriptions")}
        description={t("subs.subtitle")}
        actions={
          plans.length ? (
            <FormDialog label={t("subs.new")} variant="primary" method="POST" path="/api/v1/platform/subscriptions" kind="subscription.create" fields={subscriptionCreateFields(t, plans)} />
          ) : null
        }
      />
      <Card bodyClassName="">
        <FilterBar action="/subscriptions">
          <FilterField label={t("lic.status")}>
            <select name="status" defaultValue={status} className="vx-input">
              <option value="">{t("common.all")}</option>
              {enumOptions(SUBSCRIPTION_STATUSES, t).map((o) => (
                <option key={o.value} value={o.value}>
                  {o.label}
                </option>
              ))}
            </select>
          </FilterField>
        </FilterBar>
        {!res.ok ? (
          <div className="p-4">
            <ApiError error={res.error} retryHref="/subscriptions" />
          </div>
        ) : res.data.items.length === 0 ? (
          <EmptyState title={t("subs.empty")} description={status ? t("common.emptyFiltered") : undefined} />
        ) : (
          <>
            <TableWrap>
              <table className="vx-table">
                <thead>
                  <tr>
                    <th>{t("common.tenant")}</th>
                    <th>{t("lic.plan")}</th>
                    <th>{t("subs.cycle")}</th>
                    <th>{t("lic.status")}</th>
                    <th className="text-end">{t("subs.seats")}</th>
                    <th className="text-end">{t("subs.price")}</th>
                    <th>{t("subs.periodEnd")}</th>
                  </tr>
                </thead>
                <tbody>
                  {res.data.items.map((s) => (
                    <tr key={s.id}>
                      <td>
                        <Link href={`/subscriptions/${s.id}`} className="font-medium text-primary hover:underline">
                          {s.tenantName ?? s.tenantId.slice(0, 8)}
                        </Link>
                      </td>
                      <td className="font-mono text-xs">{s.planCode}</td>
                      <td>{s.billingCycle}</td>
                      <td>
                        <StatusBadge value={s.status} t={t} />
                        {s.cancelAtPeriodEnd ? <div className="mt-1 text-xs text-warning">{t("subs.cancelAtEnd")}</div> : null}
                      </td>
                      <td className="text-end tabular-nums">{formatNumber(s.seats, locale)}</td>
                      <td className="text-end tabular-nums whitespace-nowrap">{formatMoney(s.periodPrice, s.currency, locale)}</td>
                      <td className="whitespace-nowrap">{formatDate(s.currentPeriodEnd, locale)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </TableWrap>
            <Pagination base="/subscriptions" params={{ status }} page={res.data.page} pageSize={res.data.pageSize} totalCount={res.data.totalCount} />
          </>
        )}
      </Card>
    </>
  );
}
