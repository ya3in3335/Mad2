import type { Metadata } from "next";
import { FormDialog } from "@/components/form-dialog";
import { ApiError } from "@/components/server-ui";
import { Badge, Card, EmptyState, PageHeader, TableWrap } from "@/components/ui";
import { formatMoney, formatNumber } from "@/lib/format";
import { planFields } from "@/lib/plan-fields";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import type { Plan } from "@/lib/types";

export const metadata: Metadata = { title: "Plans" };

export default async function PlansPage() {
  const { t, locale } = await getT();
  const res = await apiGet<Plan[]>("/api/v1/platform/plans");
  return (
    <>
      <PageHeader
        title={t("nav.plans")}
        description={t("plans.subtitle")}
        actions={<FormDialog label={t("plans.new")} variant="primary" method="POST" path="/api/v1/platform/plans" kind="plan.upsert" fields={planFields(t)} />}
      />
      <Card bodyClassName="">
        {!res.ok ? (
          <div className="p-4">
            <ApiError error={res.error} retryHref="/plans" />
          </div>
        ) : res.data.length === 0 ? (
          <EmptyState title={t("plans.empty")} />
        ) : (
          <TableWrap>
            <table className="vx-table">
              <thead>
                <tr>
                  <th>{t("tenants.name")}</th>
                  <th className="text-end">{t("plans.monthlyPrice")}</th>
                  <th className="text-end">{t("plans.annualPrice")}</th>
                  <th className="text-end">{t("plans.limits")}</th>
                  <th>{t("plans.features")}</th>
                  <th>{t("lic.status")}</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {res.data.map((p) => (
                  <tr key={p.id}>
                    <td>
                      <div className="font-medium">{p.name}</div>
                      <div className="font-mono text-xs text-muted" dir="ltr">
                        {p.code}
                      </div>
                    </td>
                    <td className="text-end tabular-nums whitespace-nowrap">{formatMoney(p.monthlyPrice, p.currency, locale)}</td>
                    <td className="text-end tabular-nums whitespace-nowrap">{formatMoney(p.annualPrice, p.currency, locale)}</td>
                    <td className="text-end text-xs text-muted whitespace-nowrap">
                      {t("plans.limitsValue", { devices: formatNumber(p.maxDevices, locale), users: formatNumber(p.maxUsers, locale), branches: formatNumber(p.maxBranches, locale) })}
                    </td>
                    <td className="max-w-xs text-xs text-muted">{p.features.join(", ") || "—"}</td>
                    <td>
                      <div className="flex flex-wrap gap-1">
                        <Badge tone={p.isActive ? "success" : "neutral"}>{p.isActive ? t("status.active") : t("plans.inactive")}</Badge>
                        {p.isPublic ? <Badge tone="info">{t("plans.public")}</Badge> : null}
                      </div>
                    </td>
                    <td className="text-end">
                      <FormDialog label={t("action.edit")} size="sm" method="PUT" path={`/api/v1/platform/plans/${p.id}`} kind="plan.upsert" title={`${t("action.edit")} · ${p.name}`} fields={planFields(t, p)} />
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
