import type { Metadata } from "next";
import Link from "next/link";
import { FilterBar, FilterField } from "@/components/filter-bar";
import { FormDialog } from "@/components/form-dialog";
import { ApiError } from "@/components/server-ui";
import { Card, EmptyState, PageHeader, StatusBadge } from "@/components/ui";
import { formatDateTime } from "@/lib/format";
import { enumOptions } from "@/lib/options";
import { isGuid, param, withParams } from "@/lib/paging";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import { SUPPORT_STATUSES, type SupportCase } from "@/lib/types";

export const metadata: Metadata = { title: "Support" };

export default async function SupportPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const sp = await searchParams;
  const { t, locale } = await getT();
  const status = (SUPPORT_STATUSES as readonly string[]).includes(param(sp.status)) ? param(sp.status) : "";
  const selectedId = isGuid(param(sp.case)) ? param(sp.case) : "";
  const res = await apiGet<SupportCase[]>("/api/v1/platform/support", { status });
  const selected = res.ok && selectedId ? res.data.find((c) => c.id === selectedId) : undefined;

  return (
    <>
      <PageHeader title={t("nav.support")} description={t("support.subtitle")} />
      <div className="grid grid-cols-1 gap-6 xl:grid-cols-5">
        <Card bodyClassName="" className="xl:col-span-2">
          <FilterBar action="/support">
            <FilterField label={t("lic.status")}>
              <select name="status" defaultValue={status} className="vx-input">
                <option value="">{t("common.all")}</option>
                {enumOptions(SUPPORT_STATUSES, t).map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </select>
            </FilterField>
          </FilterBar>
          {!res.ok ? (
            <div className="p-4">
              <ApiError error={res.error} retryHref="/support" />
            </div>
          ) : res.data.length === 0 ? (
            <EmptyState title={t("support.empty")} />
          ) : (
            <ul className="max-h-[70vh] divide-y divide-line overflow-y-auto">
              {res.data.map((c) => {
                const active = c.id === selectedId;
                return (
                  <li key={c.id}>
                    <Link
                      href={withParams("/support", { status }, { case: c.id })}
                      aria-current={active ? "true" : undefined}
                      className={`block px-4 py-3 hover:bg-raised ${active ? "bg-primary/10" : ""}`}
                    >
                      <div className="flex items-start justify-between gap-2">
                        <p className="min-w-0 truncate font-medium">{c.subject}</p>
                        <StatusBadge value={c.status} t={t} />
                      </div>
                      <div className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted">
                        <span>{c.tenantName ?? t("support.noTenant")}</span>
                        <StatusBadge value={c.priority} t={t} />
                        <span>{formatDateTime(c.updatedAt, locale)}</span>
                        <span>{t("support.messages", { count: c.messages.length })}</span>
                      </div>
                    </Link>
                  </li>
                );
              })}
            </ul>
          )}
        </Card>
        <div className="xl:col-span-3">
          {selected ? (
            <Card
              title={selected.subject}
              actions={
                <FormDialog
                  label={t("support.reply")}
                  size="sm"
                  variant="primary"
                  method="POST"
                  path={`/api/v1/platform/support/${selected.id}/reply`}
                  kind="support.reply"
                  fields={[
                    { name: "body", label: t("support.message"), type: "textarea", required: true, maxLength: 8000 },
                    { name: "status", label: t("support.setStatus"), type: "select", options: enumOptions(SUPPORT_STATUSES, t), help: t("support.setStatusHelp") },
                  ]}
                />
              }
            >
              <div className="mb-4 flex flex-wrap gap-3 text-sm text-muted">
                {selected.tenantId ? (
                  <Link href={`/tenants/${selected.tenantId}`} className="text-primary hover:underline">
                    {selected.tenantName ?? selected.tenantId}
                  </Link>
                ) : null}
                <StatusBadge value={selected.status} t={t} />
                <StatusBadge value={selected.priority} t={t} />
                <span>{formatDateTime(selected.createdAt, locale)}</span>
              </div>
              {selected.messages.length === 0 ? (
                <EmptyState title={t("support.noMessages")} />
              ) : (
                <ol className="space-y-3">
                  {selected.messages.map((m) => (
                    <li key={m.id} className={`flex ${m.fromVendor ? "justify-end" : "justify-start"}`}>
                      <div className={`max-w-[85%] rounded-lg border px-4 py-3 text-sm ${m.fromVendor ? "border-primary/30 bg-primary/10" : "border-line bg-raised"}`}>
                        <p className="mb-1 text-xs font-medium text-muted">
                          {m.fromVendor ? t("support.vendor") : t("support.customer")} · {formatDateTime(m.createdAt, locale)}
                        </p>
                        <p className="whitespace-pre-wrap break-words">{m.body}</p>
                      </div>
                    </li>
                  ))}
                </ol>
              )}
            </Card>
          ) : (
            <Card>
              <EmptyState title={selectedId ? t("err.notFound") : t("support.select")} />
            </Card>
          )}
        </div>
      </div>
    </>
  );
}
