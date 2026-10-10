import type { FieldSpec } from "./forms";
import type { Translator } from "./i18n-core";
import { enumOptions } from "./options";
import { BILLING_CYCLES, FULL_PLAN_CODE, LICENSE_TYPES, PAYMENT_METHODS, type Plan } from "./types";

/** Shared field lists for dialogs that appear on several pages. */
export function licenseCreateFields(t: Translator, tenantId?: string): FieldSpec[] {
  return [
    tenantId
      ? { name: "tenantId", label: "", type: "hidden", defaultValue: tenantId }
      : { name: "tenantId", label: t("common.tenantId"), type: "text", required: true, ltr: true, wide: true, placeholder: "00000000-0000-0000-0000-000000000000", help: t("common.tenantIdHelp") },
    { name: "type", label: t("lic.type"), type: "select", required: true, options: enumOptions(LICENSE_TYPES, t, "licType"), defaultValue: "Annual" },
    // Single plan: every license is on the "full" plan (all features); seats are the commercial lever.
    { name: "planCode", label: "", type: "hidden", defaultValue: FULL_PLAN_CODE },
    { name: "maxDevices", label: t("lic.seats"), type: "number", required: true, min: 1, max: 10000, defaultValue: "1" },
    { name: "offlineGraceDays", label: t("lic.graceDays"), type: "number", min: 0, max: 365, help: t("lic.graceDaysHelp") },
    { name: "startsAt", label: t("lic.startsAt"), type: "date" },
    { name: "expiresAt", label: t("lic.expires"), type: "date", help: t("lic.expiresHelp") },
    { name: "notes", label: t("common.notes"), type: "textarea", maxLength: 2000 },
  ];
}

export function licenseDemoFields(t: Translator, tenantId?: string): FieldSpec[] {
  return [
    tenantId
      ? { name: "tenantId", label: "", type: "hidden", defaultValue: tenantId }
      : { name: "tenantId", label: t("common.tenantId"), type: "text", required: true, ltr: true, wide: true, placeholder: "00000000-0000-0000-0000-000000000000", help: t("common.tenantIdHelp") },
    { name: "days", label: t("lic.days"), type: "number", required: true, min: 1, max: 60, defaultValue: "14", help: t("lic.demoDaysHelp") },
    { name: "maxDevices", label: t("lic.seats"), type: "number", required: true, min: 1, max: 10, defaultValue: "1" },
    { name: "notes", label: t("common.notes"), type: "textarea", maxLength: 2000 },
  ];
}

/** The single sellable plan ("full"), falling back to the first active plan. */
export function singlePlan(plans: readonly Plan[]): Plan | undefined {
  return plans.find((p) => p.code === FULL_PLAN_CODE && p.isActive) ?? plans.find((p) => p.isActive);
}

export function subscriptionCreateFields(t: Translator, plans: readonly Plan[], tenantId?: string): FieldSpec[] {
  return [
    tenantId
      ? { name: "tenantId", label: "", type: "hidden", defaultValue: tenantId }
      : { name: "tenantId", label: t("common.tenantId"), type: "text", required: true, ltr: true, wide: true, help: t("common.tenantIdHelp") },
    { name: "planCode", label: "", type: "hidden", defaultValue: singlePlan(plans)?.code ?? FULL_PLAN_CODE },
    { name: "billingCycle", label: t("subs.cycle"), type: "select", required: true, options: enumOptions(BILLING_CYCLES, t), defaultValue: "Monthly" },
    { name: "seats", label: t("subs.seats"), type: "number", min: 1, max: 10000 },
    { name: "customPrice", label: t("subs.customPrice"), type: "number", min: 0, step: "0.01", help: t("subs.customPriceHelp") },
    { name: "startWithTrial", label: t("subs.startWithTrial"), type: "checkbox", wide: true },
  ];
}

export function reasonField(t: Translator): FieldSpec {
  return { name: "reason", label: t("common.reason"), type: "textarea", required: true, maxLength: 500 };
}

export function confirmPaymentFields(t: Translator): FieldSpec[] {
  return [
    { name: "method", label: t("inv.method"), type: "select", required: true, options: enumOptions(PAYMENT_METHODS, t, "payment"), defaultValue: "bank_transfer" },
    { name: "reference", label: t("inv.reference"), type: "text", maxLength: 200, ltr: true },
    { name: "paidAt", label: t("inv.paidAt"), type: "date", help: t("inv.paidAtHelp") },
  ];
}
