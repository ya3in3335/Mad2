import type { Metadata } from "next";
import Link from "next/link";
import { notFound } from "next/navigation";
import { FormDialog } from "@/components/form-dialog";
import { ApiError } from "@/components/server-ui";
import { Card, DefinitionList, EmptyState, PageHeader, StatusBadge, TableWrap } from "@/components/ui";
import { licenseCreateFields, reasonField, subscriptionCreateFields } from "@/lib/field-sets";
import { daysUntil, formatDate, formatDateTime, formatMoney, formatNumber } from "@/lib/format";
import { isGuid } from "@/lib/paging";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import { latestSubscriptionId } from "@/lib/tenant-detail";
import type { BillingInvoice, Company, Device, License, Paged, Plan, Subscription } from "@/lib/types";

export const metadata: Metadata = { title: "Tenant" };

export default async function TenantDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  if (!isGuid(id)) notFound();
  const { t, locale } = await getT();
  const P = "/api/v1/platform";

  const [tenantRes, licRes, devRes, invRes, plansRes] = await Promise.all([
    apiGet<Company>(`${P}/tenants/${id}`),
    apiGet<Paged<License>>(`${P}/licenses`, { tenantId: id, pageSize: 50 }),
    apiGet<Paged<Device>>(`${P}/devices`, { tenantId: id, pageSize: 50 }),
    apiGet<Paged<BillingInvoice>>(`${P}/invoices`, { tenantId: id, pageSize: 20 }),
    apiGet<Plan[]>(`${P}/plans`),
  ]);
  if (!tenantRes.ok) {
    if (tenantRes.error.status === 404) notFound();
    return (
      <>
        <PageHeader title={t("nav.tenants")} />
        <ApiError error={tenantRes.error} retryHref={`/tenants/${id}`} />
      </>
    );
  }
  const c = tenantRes.data;
  let subId = invRes.ok ? latestSubscriptionId(invRes.data.items) : null;
  if (!subId) {
    // No invoice yet (e.g. a trial): the subscriptions endpoint has no tenant filter, so scan the most recent ones.
    const recent = await apiGet<Paged<Subscription>>(`${P}/subscriptions`, { pageSize: 100 });
    if (recent.ok) subId = recent.data.items.find((s) => s.tenantId === c.id)?.id ?? null;
  }
  const subRes = subId ? await apiGet<Subscription>(`${P}/subscriptions/${subId}`) : null;
  const plans = plansRes.ok ? plansRes.data.filter((p) => p.isActive) : [];
  const suspended = c.status === "Suspended";

  return (
    <>
      <nav className="mb-2 text-sm">
        <Link href="/tenants" className="text-muted hover:text-fg">
          ← {t("nav.tenants")}
        </Link>
      </nav>
      <PageHeader
        title={c.name}
        description={c.legalName ?? undefined}
        actions={
          <>
            <StatusBadge value={c.status} t={t} />
            {suspended ? (
              <FormDialog
                label={t("tenants.reactivate")}
                variant="primary"
                method="POST"
                path={`${P}/tenants/${c.id}/reactivate`}
                description={t("tenants.reactivateConfirm", { name: c.name })}
              />
            ) : (
              <FormDialog
                label={t("tenants.suspend")}
                variant="danger"
                method="POST"
                path={`${P}/tenants/${c.id}/suspend`}
                kind="reason"
                description={t("tenants.suspendConfirm", { name: c.name })}
                fields={[reasonField(t)]}
                submitLabel={t("tenants.suspend")}
              />
            )}
          </>
        }
      />

      <div className="grid grid-cols-1 gap-6 xl:grid-cols-3">
        <Card title={t("tenants.company")} className="xl:col-span-2">
          <DefinitionList
            items={[
              [t("common.id"), <span key="id" className="font-mono text-xs" dir="ltr">{c.id}</span>],
              [t("tenants.setup"), c.setupCompleted ? t("common.yes") : <span key="s" className="text-warning">{t("tenants.setupPending")}</span>],
              [t("auth.email"), c.email ? <span key="e" dir="ltr">{c.email}</span> : "—"],
              [t("crm.phone"), c.phone ? <span key="p" dir="ltr">{c.phone}</span> : "—"],
              [t("onb.address"), [c.address, c.city, c.wilaya, c.country].filter(Boolean).join(", ") || "—"],
              [t("onb.taxId"), c.taxId ?? "—"],
              [t("onb.rc"), c.tradeRegisterNo ?? "—"],
              [t("tenants.nis"), c.statisticalId ?? "—"],
              [t("tenants.article"), c.articleNo ?? "—"],
              [t("onb.currency"), c.currency],
              [t("set.language"), c.locale],
              [t("tenants.timeZone"), c.timeZone],
            ]}
          />
        </Card>

        <Card
          title={t("tenants.subscription")}
          actions={
            !subRes || !subRes.ok ? (
              plans.length ? (
                <FormDialog
                  label={t("subs.new")}
                  size="sm"
                  method="POST"
                  path={`${P}/subscriptions`}
                  kind="subscription.create"
                  fields={subscriptionCreateFields(t, plans, c.id)}
                />
              ) : null
            ) : (
              <Link href={`/subscriptions/${subRes.data.id}`} className="text-sm text-primary hover:underline">
                {t("common.details")}
              </Link>
            )
          }
        >
          {subRes && subRes.ok ? (
            <DefinitionList
              items={[
                [t("lic.plan"), <span key="p" className="font-mono">{subRes.data.planCode}</span>],
                [t("lic.status"), <StatusBadge key="s" value={subRes.data.status} t={t} />],
                [t("subs.cycle"), subRes.data.billingCycle],
                [t("subs.seats"), formatNumber(subRes.data.seats, locale)],
                [t("subs.periodEnd"), formatDate(subRes.data.currentPeriodEnd, locale)],
                [t("subs.price"), formatMoney(subRes.data.periodPrice, subRes.data.currency, locale)],
              ]}
            />
          ) : subRes && !subRes.ok ? (
            <ApiError error={subRes.error} />
          ) : (
            <EmptyState title={t("tenants.noSubscription")} description={t("tenants.noSubscriptionHelp")} />
          )}
        </Card>
      </div>

      <Card
        className="mt-6"
        bodyClassName=""
        title={t("nav.licensing")}
        actions={<FormDialog label={t("lic.issue")} size="sm" variant="primary" method="POST" path={`${P}/licenses`} kind="license.create" resultView="licenseKey" fields={licenseCreateFields(t, c.id)} />}
      >
        {!licRes.ok ? (
          <div className="p-4">
            <ApiError error={licRes.error} />
          </div>
        ) : licRes.data.items.length === 0 ? (
          <EmptyState title={t("lic.empty")} />
        ) : (
          <TableWrap>
            <table className="vx-table">
              <thead>
                <tr>
                  <th>{t("lic.key")}</th>
                  <th>{t("lic.type")}</th>
                  <th>{t("lic.status")}</th>
                  <th className="text-end">{t("lic.seats")}</th>
                  <th>{t("lic.expires")}</th>
                </tr>
              </thead>
              <tbody>
                {licRes.data.items.map((l) => {
                  const days = daysUntil(l.expiresAt);
                  return (
                    <tr key={l.id}>
                      <td>
                        <Link href={`/licenses/${l.id}`} className="font-mono text-primary hover:underline" dir="ltr">
                          {l.keyHint}
                        </Link>
                      </td>
                      <td>{l.type}</td>
                      <td>
                        <StatusBadge value={l.effectiveStatus} t={t} />
                      </td>
                      <td className="text-end tabular-nums">
                        {formatNumber(l.activeDevices, locale)} / {formatNumber(l.maxDevices, locale)}
                      </td>
                      <td className={days !== null && days <= 14 ? "text-warning" : undefined}>{l.expiresAt ? formatDate(l.expiresAt, locale) : t("lic.perpetual")}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </TableWrap>
        )}
      </Card>

      <Card className="mt-6" bodyClassName="" title={t("nav.devices")}>
        {!devRes.ok ? (
          <div className="p-4">
            <ApiError error={devRes.error} />
          </div>
        ) : devRes.data.items.length === 0 ? (
          <EmptyState title={t("dev.empty")} />
        ) : (
          <TableWrap>
            <table className="vx-table">
              <thead>
                <tr>
                  <th>{t("dev.name")}</th>
                  <th>{t("dev.platform")}</th>
                  <th>{t("app.version")}</th>
                  <th>{t("lic.status")}</th>
                  <th>{t("dev.lastSeen")}</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {devRes.data.items.map((d) => (
                  <tr key={d.id}>
                    <td className="font-medium">{d.deviceName}</td>
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
                          path={`${P}/devices/${d.id}/revoke`}
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
        )}
      </Card>

      <Card className="mt-6" bodyClassName="" title={t("nav.invoices")}>
        {!invRes.ok ? (
          <div className="p-4">
            <ApiError error={invRes.error} />
          </div>
        ) : invRes.data.items.length === 0 ? (
          <EmptyState title={t("inv.empty")} />
        ) : (
          <TableWrap>
            <table className="vx-table">
              <thead>
                <tr>
                  <th>{t("inv.number")}</th>
                  <th>{t("inv.issued")}</th>
                  <th>{t("inv.due")}</th>
                  <th className="text-end">{t("pos.total")}</th>
                  <th>{t("lic.status")}</th>
                </tr>
              </thead>
              <tbody>
                {invRes.data.items.map((i) => (
                  <tr key={i.id}>
                    <td className="font-mono text-xs">{i.number}</td>
                    <td>{formatDate(i.issuedAt, locale)}</td>
                    <td>{formatDate(i.dueDate, locale)}</td>
                    <td className="text-end tabular-nums">{formatMoney(i.total, i.currency, locale)}</td>
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
    </>
  );
}
