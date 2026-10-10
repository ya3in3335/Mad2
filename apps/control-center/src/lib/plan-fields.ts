import type { FieldSpec } from "./forms";
import type { Translator } from "./i18n-core";
import { FULL_PLAN_CODE, type Plan } from "./types";

/**
 * Field list for the single plan's edit dialog (price, limits, availability). The plan always includes every feature and its
 * code is fixed, so neither is editable.
 */
export function planFields(t: Translator, plan?: Plan): FieldSpec[] {
  const s = (v: number | undefined, fallback: string) => (v === undefined ? fallback : String(v));
  return [
    { name: "code", label: "", type: "hidden", defaultValue: plan?.code ?? FULL_PLAN_CODE },
    { name: "name", label: t("tenants.name"), type: "text", required: true, maxLength: 200, defaultValue: plan?.name ?? "" },
    { name: "description", label: t("plans.description"), type: "textarea", maxLength: 2000, defaultValue: plan?.description ?? "" },
    { name: "monthlyPrice", label: t("plans.monthlyPrice"), type: "number", required: true, min: 0, step: "0.01", defaultValue: s(plan?.monthlyPrice, "0") },
    { name: "annualPrice", label: t("plans.annualPrice"), type: "number", required: true, min: 0, step: "0.01", defaultValue: s(plan?.annualPrice, "0") },
    { name: "currency", label: t("onb.currency"), type: "text", required: true, maxLength: 3, ltr: true, defaultValue: plan?.currency ?? "DZD" },
    { name: "trialDays", label: t("plans.trialDays"), type: "number", required: true, min: 0, max: 365, defaultValue: s(plan?.trialDays, "14") },
    { name: "maxDevices", label: t("plans.maxDevices"), type: "number", required: true, min: 0, defaultValue: s(plan?.maxDevices, "1") },
    { name: "maxUsers", label: t("plans.maxUsers"), type: "number", required: true, min: 0, defaultValue: s(plan?.maxUsers, "5") },
    { name: "maxBranches", label: t("plans.maxBranches"), type: "number", required: true, min: 0, defaultValue: s(plan?.maxBranches, "1") },
    { name: "offlineGraceDays", label: t("lic.graceDays"), type: "number", required: true, min: 0, max: 365, defaultValue: s(plan?.offlineGraceDays, "7") },
    { name: "isActive", label: t("plans.isActive"), type: "checkbox", defaultValue: plan?.isActive ?? true },
    { name: "isPublic", label: t("plans.isPublic"), type: "checkbox", defaultValue: plan?.isPublic ?? true },
  ];
}
