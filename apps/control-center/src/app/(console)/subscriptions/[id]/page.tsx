import type { Metadata } from "next";
import Link from "next/link";
import { notFound } from "next/navigation";
import { FormDialog } from "@/components/form-dialog";
import { ApiError } from "@/components/server-ui";
import { Card, DefinitionList, EmptyState, PageHeader, StatusBadge, TableWrap } from "@/components/ui";
import { formatDate, formatDateTime, formatMoney, formatNumber } from "@/lib/format";
import { singlePlan } from "@/lib/field-sets";
import { enumOptions } from "@/lib/options";
import { isGuid } from "@/lib/paging";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import { humanize } from "@/lib/status";
import { BILLING_CYCLES, type BillingInvoice, type EntitlementChange, type Paged, type Plan, type Subscription } from "@/lib/types";

export const metadata: Metadata = { title: "Subscription" };

export default async function SubscriptionDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  if (!isGuid(id)) notFound();
  const { t, locale } = await getT();
  const P = "/api/v1/platform";
  const [subRes, histRes, plansRes] = await Promise.all([
    apiGet<Subscription>(`${P}/subscriptions/${id}`),
    apiGet<EntitlementChange[]>(`${P}/subscriptions/${id}/history`),
    apiGet<Plan[]>(`${P}/plans`),
  ]);
  if (!subRes.ok) {
    if (subRes.error.status === 404) notFound();
    return (
      <>
        <PageHeader title={t("nav.subscriptions")} />
        <ApiError error={subRes.error} retryHref={`/subscriptions/${id}`} />
      </>
    );
  }
  const s = subRes.data;
  const invRes = await apiGet<Paged<BillingInvoice>>(`${P}/invoices`, { tenantId: s.tenantId, pageSize: 50 });
  const invoices = invRes.ok ? invRes.data.items.filter((i) => i.subscriptionId === s.id) : [];
  const plan = plansRes.ok ? singlePlan(plansRes.data) : undefined;
  const ended = s.status === "Cancelled" || s.status === "Expired";

  return (
    <>
      <nav className="mb-2 text-sm">
        <Link href="/subscriptions" className="text-muted hover:text-fg">
          ← {t("nav.subscriptions")}
        </Link>
      </nav>
      <PageHeader
        title={s.tenantName ?? t("tenants.subscription")}
        description={`${s.planCode} · ${s.billingCycle}`}
        actions={
          <>
            <StatusBadge value={s.status} t={t} />
            {!ended ? (
              <>
                {plan ? (
                  // Single plan: no tier upgrades/downgrades — only switch billing cycle (re-priced from the plan).
                  <FormDialog
                    label={t("subs.changeCycle")}
                    method="POST"
                    path={`${P}/subscriptions/${s.id}/plan`}
                    kind="subscription.plan"
                    description={t("subs.changeCycleHelp")}
                    fields={[
                      { name: "planCode", label: "", type: "hidden", defaultValue: plan.code },
                      { name: "billingCycle", label: t("subs.cycle"), type: "select", required: true, options: enumOptions(BILLING_CYCLES, t), defaultValue: s.billingCycle },
                      { name: "prorateInvoice", label: t("subs.prorate"), type: "checkbox", wide: true, defaultValue: true },
                    ]}
                  />
                ) : null}
                <FormDialog
                  label={t("subs.changeSeats")}
                  method="POST"
                  path={`${P}/subscriptions/${s.id}/seats`}
                  kind="subscription.seats"
                  fields={[{ name: "seats", label: t("subs.seats"), type: "number", required: true, min: 1, max: 10000, defaultValue: String(s.seats) }]}
                />
                <FormDialog
                  label={t("subs.renewalInvoice")}
                  method="POST"
                  path={`${P}/subscriptions/${s.id}/renewal-invoice`}
                  kind="subscription.renewal"
                  description={t("subs.renewalHelp")}
                  fields={[{ name: "taxPercent", label: t("subs.taxPercent"), type: "number", min: 0, max: 100, step: "0.01", help: t("subs.taxPercentHelp") }]}
                />
                <FormDialog
                  label={t("subs.cancel")}
                  variant="danger"
                  method="POST"
                  path={`${P}/subscriptions/${s.id}/cancel`}
                  kind="subscription.cancel"
                  description={t("subs.cancelConfirm")}
                  fields={[
                    { name: "immediately", label: t("subs.cancelNow"), type: "checkbox", wide: true },
                    { name: "reason", label: t("common.reason"), type: "textarea", required: true, maxLength: 500 },
                  ]}
                  submitLabel={t("subs.cancel")}
                />
              </>
            ) : null}
          </>
        }
      />
      <Card title={t("common.details")}>
        <DefinitionList
          items={[
            [t("common.tenant"), <Link key="t" href={`/tenants/${s.tenantId}`} className="text-primary hover:underline">{s.tenantName ?? s.tenantId}</Link>],
            [t("lic.plan"), <span key="p" className="font-mono">{s.planCode}</span>],
            [t("subs.cycle"), s.billingCycle],
            [t("subs.seats"), formatNumber(s.seats, locale)],
            [t("subs.period"), `${formatDate(s.currentPeriodStart, locale)} → ${formatDate(s.currentPeriodEnd, locale)}`],
            [t("subs.price"), formatMoney(s.periodPrice, s.currency, locale)],
            [t("subs.cancelAtEnd"), s.cancelAtPeriodEnd ? t("common.yes") : t("common.no")],
            [t("lic.license"), s.licenseId ? <Link key="l" href={`/licenses/${s.licenseId}`} className="text-primary hover:underline">{t("common.details")}</Link> : "—"],
            [t("common.created"), formatDateTime(s.createdAt, locale)],
          ]}
        />
      </Card>
      <Card className="mt-6" bodyClassName="" title={t("nav.invoices")}>
        {!invRes.ok ? (
          <div className="p-4">
            <ApiError error={invRes.error} />
          </div>
        ) : invoices.length === 0 ? (
          <EmptyState title={t("inv.empty")} />
        ) : (
          <TableWrap>
            <table className="vx-table">
              <thead>
                <tr>
                  <th>{t("inv.number")}</th>
                  <th>{t("subs.period")}</th>
                  <th className="text-end">{t("pos.total")}</th>
                  <th>{t("inv.due")}</th>
                  <th>{t("lic.status")}</th>
                </tr>
              </thead>
              <tbody>
                {invoices.map((i) => (
                  <tr key={i.id}>
                    <td className="font-mono text-xs">{i.number}</td>
                    <td className="whitespace-nowrap">
                      {formatDate(i.periodStart, locale)} → {formatDate(i.periodEnd, locale)}
                    </td>
                    <td className="text-end tabular-nums">{formatMoney(i.total, i.currency, locale)}</td>
                    <td>{formatDate(i.dueDate, locale)}</td>
                    <td>
                      <StatusBadge value={i.status} t={t} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </TableWrap>
        )}
      </Card>
      <Card className="mt-6" bodyClassName="" title={t("subs.history")}>
        {!histRes.ok ? (
          <div className="p-4">
            <ApiError error={histRes.error} />
          </div>
        ) : histRes.data.length === 0 ? (
          <EmptyState title={t("subs.noHistory")} />
        ) : (
          <ol className="divide-y divide-line">
            {histRes.data.map((h) => (
              <li key={h.id} className="flex flex-wrap items-baseline justify-between gap-2 px-5 py-3 text-sm">
                <div className="min-w-0">
                  <span className="font-medium">{humanize(h.change)}</span>
                  {h.details ? <p className="mt-0.5 break-words text-xs text-muted">{h.details}</p> : null}
                </div>
                <time className="whitespace-nowrap text-xs text-muted" dateTime={h.at}>
                  {formatDateTime(h.at, locale)}
                </time>
              </li>
            ))}
          </ol>
        )}
      </Card>
    </>
  );
}
