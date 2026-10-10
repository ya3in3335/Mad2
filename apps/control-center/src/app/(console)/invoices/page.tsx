import type { Metadata } from "next";
import Link from "next/link";
import { FilterBar, FilterField } from "@/components/filter-bar";
import { FormDialog } from "@/components/form-dialog";
import { ApiError, Pagination } from "@/components/server-ui";
import { Card, EmptyState, PageHeader, StatusBadge, TableWrap } from "@/components/ui";
import { confirmPaymentFields } from "@/lib/field-sets";
import { daysUntil, formatDate, formatMoney } from "@/lib/format";
import { enumOptions } from "@/lib/options";
import { isGuid, param, parsePage } from "@/lib/paging";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import { INVOICE_STATUSES, type BillingInvoice, type Paged } from "@/lib/types";

export const metadata: Metadata = { title: "Invoices" };
const PAGE_SIZE = 25;

/** Billing invoices. Defaults to "Issued" = manual payments (bank transfer, CCP, cash…) awaiting confirmation. */
export default async function InvoicesPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const sp = await searchParams;
  const { t, locale } = await getT();
  const page = parsePage(sp.page);
  // "all" is an explicit choice, so that paging links do not fall back to the "Issued" default.
  const rawStatus = sp.status === undefined || sp.status === "" ? "Issued" : param(sp.status);
  const status = (INVOICE_STATUSES as readonly string[]).includes(rawStatus) ? rawStatus : "";
  const statusParam = status || "all";
  const tenantId = isGuid(param(sp.tenantId)) ? param(sp.tenantId) : "";
  const res = await apiGet<Paged<BillingInvoice>>("/api/v1/platform/invoices", { page, pageSize: PAGE_SIZE, status, tenantId });

  return (
    <>
      <PageHeader title={t("nav.invoices")} description={t("inv.subtitle")} />
      <Card bodyClassName="">
        <FilterBar action="/invoices">
          <FilterField label={t("lic.status")}>
            <select name="status" defaultValue={statusParam} className="vx-input">
              <option value="all">{t("common.all")}</option>
              {enumOptions(INVOICE_STATUSES, t).map((o) => (
                <option key={o.value} value={o.value}>
                  {o.label}
                </option>
              ))}
            </select>
          </FilterField>
          <FilterField label={t("common.tenantId")} className="w-full sm:w-80">
            <input name="tenantId" defaultValue={tenantId} className="vx-input font-mono text-xs" dir="ltr" maxLength={36} />
          </FilterField>
        </FilterBar>
        {!res.ok ? (
          <div className="p-4">
            <ApiError error={res.error} retryHref="/invoices" />
          </div>
        ) : res.data.items.length === 0 ? (
          <EmptyState title={status === "Issued" ? t("inv.noPending") : t("inv.empty")} />
        ) : (
          <>
            <TableWrap>
              <table className="vx-table">
                <thead>
                  <tr>
                    <th>{t("inv.number")}</th>
                    <th>{t("common.tenant")}</th>
                    <th>{t("subs.period")}</th>
                    <th className="text-end">{t("pos.total")}</th>
                    <th>{t("inv.due")}</th>
                    <th>{t("lic.status")}</th>
                    <th />
                  </tr>
                </thead>
                <tbody>
                  {res.data.items.map((i) => {
                    const overdue = i.status === "Issued" && (daysUntil(i.dueDate) ?? 1) < 0;
                    return (
                      <tr key={i.id}>
                        <td>
                          <div className="font-mono text-xs">{i.number}</div>
                          {i.description ? <div className="text-xs text-muted">{i.description}</div> : null}
                        </td>
                        <td>
                          <Link href={`/tenants/${i.tenantId}`} className="hover:underline">
                            {i.tenantName ?? i.tenantId.slice(0, 8)}
                          </Link>
                        </td>
                        <td className="whitespace-nowrap text-xs">
                          {formatDate(i.periodStart, locale)} → {formatDate(i.periodEnd, locale)}
                        </td>
                        <td className="text-end tabular-nums whitespace-nowrap">{formatMoney(i.total, i.currency, locale)}</td>
                        <td className={`whitespace-nowrap ${overdue ? "text-danger" : ""}`}>{formatDate(i.dueDate, locale)}</td>
                        <td>
                          <StatusBadge value={i.status} t={t} />
                          {i.paidAt ? (
                            <div className="mt-1 text-xs text-muted">
                              {formatDate(i.paidAt, locale)} · {i.paymentMethod ?? ""} {i.paymentReference ?? ""}
                            </div>
                          ) : null}
                        </td>
                        <td className="text-end">
                          {i.status === "Issued" ? (
                            <div className="flex justify-end gap-2">
                              <FormDialog
                                label={t("inv.confirmPayment")}
                                size="sm"
                                variant="primary"
                                method="POST"
                                path={`/api/v1/platform/invoices/${i.id}/confirm-payment`}
                                kind="invoice.confirm"
                                description={t("inv.confirmHelp", { number: i.number, total: formatMoney(i.total, i.currency, locale) })}
                                fields={confirmPaymentFields(t)}
                              />
                              <FormDialog
                                label={t("inv.void")}
                                size="sm"
                                variant="ghost"
                                method="POST"
                                path={`/api/v1/platform/invoices/${i.id}/void`}
                                description={t("inv.voidConfirm", { number: i.number })}
                              />
                            </div>
                          ) : null}
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </TableWrap>
            <Pagination base="/invoices" params={{ status: statusParam, tenantId }} page={res.data.page} pageSize={res.data.pageSize} totalCount={res.data.totalCount} />
          </>
        )}
      </Card>
    </>
  );
}
