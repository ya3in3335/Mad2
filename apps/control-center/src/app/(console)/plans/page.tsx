import type { Metadata } from "next";
import { FormDialog } from "@/components/form-dialog";
import { ApiError } from "@/components/server-ui";
import { Badge, Card, DefinitionList, EmptyState, PageHeader } from "@/components/ui";
import { formatMoney, formatNumber } from "@/lib/format";
import { singlePlan } from "@/lib/field-sets";
import { planFields } from "@/lib/plan-fields";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import { FULL_PLAN_CODE, type Plan } from "@/lib/types";

export const metadata: Metadata = { title: "Plan" };

/** VELTRIX OS is sold as one plan that includes every feature: this page edits its price, limits and availability. */
export default async function PlansPage() {
  const { t, locale } = await getT();
  const res = await apiGet<Plan[]>("/api/v1/platform/plans");
  const plan = res.ok ? (res.data.find((p) => p.code === FULL_PLAN_CODE) ?? singlePlan(res.data)) : undefined;
  return (
    <>
      <PageHeader
        title={t("nav.plans")}
        description={t("plans.subtitle")}
        actions={
          plan ? (
            <FormDialog label={t("action.edit")} variant="primary" method="PUT" path={`/api/v1/platform/plans/${plan.id}`} kind="plan.upsert" title={`${t("action.edit")} · ${plan.name}`} fields={planFields(t, plan)} />
          ) : null
        }
      />
      {!res.ok ? (
        <ApiError error={res.error} retryHref="/plans" />
      ) : !plan ? (
        <Card>
          <EmptyState title={t("plans.empty")} />
        </Card>
      ) : (
        <Card
          title={
            <span className="flex items-center gap-2">
              {plan.name}
              <span className="font-mono text-xs text-muted" dir="ltr">
                {plan.code}
              </span>
            </span>
          }
          actions={
            <div className="flex flex-wrap gap-1">
              <Badge tone={plan.isActive ? "success" : "neutral"}>{plan.isActive ? t("status.active") : t("plans.inactive")}</Badge>
              {plan.isPublic ? <Badge tone="info">{t("plans.public")}</Badge> : null}
            </div>
          }
        >
          {plan.description ? <p className="mb-4 text-sm text-muted">{plan.description}</p> : null}
          <DefinitionList
            items={[
              [t("plans.monthlyPrice"), <span key="m" className="tabular-nums">{formatMoney(plan.monthlyPrice, plan.currency, locale)}</span>],
              [t("plans.annualPrice"), <span key="a" className="tabular-nums">{formatMoney(plan.annualPrice, plan.currency, locale)}</span>],
              [t("plans.limits"), t("plans.limitsValue", { devices: formatNumber(plan.maxDevices, locale), users: formatNumber(plan.maxUsers, locale), branches: formatNumber(plan.maxBranches, locale) })],
              [t("plans.trialDays"), formatNumber(plan.trialDays, locale)],
              [t("lic.graceDays"), formatNumber(plan.offlineGraceDays, locale)],
              [t("plans.features"), t("plans.allFeatures")],
            ]}
          />
        </Card>
      )}
    </>
  );
}
