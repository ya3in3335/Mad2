import type { FieldSpec } from "./forms";
import type { Translator } from "./i18n-core";
import { enumOptions } from "./options";
import { BILLING_CYCLES, FEATURES, LICENSE_PLAN_CODES, LICENSE_TYPES, PAYMENT_METHODS, type Plan } from "./types";

/** Shared field lists for dialogs that appear on several pages. */
export function licenseCreateFields(t: Translator, tenantId?: string): FieldSpec[] {
  return [
    tenantId
      ? { name: "tenantId", label: "", type: "hidden", defaultValue: tenantId }
      : { name: "tenantId", label: t("common.tenantId"), type: "text", required: true, ltr: true, wide: true, placeholder: "00000000-0000-0000-0000-000000000000", help: t("common.tenantIdHelp") },
    { name: "type", label: t("lic.type"), type: "select", required: true, options: enumOptions(LICENSE_TYPES, t, "licType"), defaultValue: "Annual" },
    { name: "planCode", label: t("lic.plan"), type: "select", required: true, options: LICENSE_PLAN_CODES.map((c) => ({ value: c, label: c })), defaultValue: "professional" },
    { name: "maxDevices", label: t("lic.seats"), type: "number", required: true, min: 1, max: 10000, defaultValue: "1" },
    { name: "offlineGraceDays", label: t("lic.graceDays"), type: "number", min: 0, max: 365, help: t("lic.graceDaysHelp") },
    { name: "startsAt", label: t("lic.startsAt"), type: "date" },
    { name: "expiresAt", label: t("lic.expires"), type: "date", help: t("lic.expiresHelp") },
    { name: "entitlements", label: t("lic.entitlements"), type: "checkboxes", options: enumOptions(FEATURES, t, "feature"), help: t("lic.entitlementsHelp") },
    { name: "notes", label: t("common.notes"), type: "textarea", maxLength: 2000 },
  ];
}

export function subscriptionCreateFields(t: Translator, plans: readonly Plan[], tenantId?: string): FieldSpec[] {
  return [
    tenantId
      ? { name: "tenantId", label: "", type: "hidden", defaultValue: tenantId }
      : { name: "tenantId", label: t("common.tenantId"), type: "text", required: true, ltr: true, wide: true, help: t("common.tenantIdHelp") },
    { name: "planCode", label: t("lic.plan"), type: "select", required: true, options: plans.map((p) => ({ value: p.code, label: `${p.name} (${p.code})` })), defaultValue: plans[0]?.code ?? "" },
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
