import type { BillingInvoice, Guid } from "./types";

/** Most recent subscription id referenced by a tenant's invoices (newest issue date wins). */
export function latestSubscriptionId(invoices: readonly BillingInvoice[]): Guid | null {
  let best: BillingInvoice | null = null;
  for (const inv of invoices) {
    if (!best || Date.parse(inv.issuedAt) > Date.parse(best.issuedAt)) best = inv;
  }
  return best?.subscriptionId ?? null;
}
