import type { Metadata } from "next";
import Link from "next/link";
import { FilterBar, FilterField } from "@/components/filter-bar";
import { ApiError, Pagination } from "@/components/server-ui";
import { Card, EmptyState, PageHeader, TableWrap } from "@/components/ui";
import { formatDateTime, fromDateInput, shortId } from "@/lib/format";
import { isGuid, param, parsePage } from "@/lib/paging";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import type { AuditLog, Paged } from "@/lib/types";

export const metadata: Metadata = { title: "Audit log" };
const PAGE_SIZE = 50;
const DATE = /^\d{4}-\d{2}-\d{2}$/;

export default async function AuditPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const sp = await searchParams;
  const { t, locale } = await getT();
  const page = parsePage(sp.page);
  const tenantId = isGuid(param(sp.tenantId)) ? param(sp.tenantId) : "";
  const action = param(sp.action, 100).replace(/[^A-Za-z0-9._-]/g, "");
  const from = DATE.test(param(sp.from)) ? param(sp.from) : "";
  const to = DATE.test(param(sp.to)) ? param(sp.to) : "";
  const res = await apiGet<Paged<AuditLog>>("/api/v1/platform/audit", {
    page,
    pageSize: PAGE_SIZE,
    tenantId,
    action,
    from: fromDateInput(from, false),
    to: fromDateInput(to, true),
  });
  const filtered = Boolean(tenantId || action || from || to);

  return (
    <>
      <PageHeader title={t("nav.audit")} description={t("audit.subtitle")} />
      <Card bodyClassName="">
        <FilterBar action="/audit">
          <FilterField label={t("audit.action")} className="w-full sm:w-56">
            <input name="action" defaultValue={action} className="vx-input font-mono text-xs" dir="ltr" placeholder="auth.login" maxLength={100} />
          </FilterField>
          <FilterField label={t("common.tenantId")} className="w-full sm:w-80">
            <input name="tenantId" defaultValue={tenantId} className="vx-input font-mono text-xs" dir="ltr" maxLength={36} />
          </FilterField>
          <FilterField label={t("rep.from")} className="w-full sm:w-40">
            <input type="date" name="from" defaultValue={from} className="vx-input" />
          </FilterField>
          <FilterField label={t("rep.to")} className="w-full sm:w-40">
            <input type="date" name="to" defaultValue={to} className="vx-input" />
          </FilterField>
        </FilterBar>
        {!res.ok ? (
          <div className="p-4">
            <ApiError error={res.error} retryHref="/audit" />
          </div>
        ) : res.data.items.length === 0 ? (
          <EmptyState title={t("audit.empty")} description={filtered ? t("common.emptyFiltered") : undefined} />
        ) : (
          <>
            <TableWrap>
              <table className="vx-table">
                <thead>
                  <tr>
                    <th>{t("audit.when")}</th>
                    <th>{t("audit.action")}</th>
                    <th>{t("audit.entity")}</th>
                    <th>{t("common.tenant")}</th>
                    <th>{t("audit.user")}</th>
                    <th>IP</th>
                    <th>{t("audit.details")}</th>
                  </tr>
                </thead>
                <tbody>
                  {res.data.items.map((a) => (
                    <tr key={a.id}>
                      <td className="whitespace-nowrap text-xs">{formatDateTime(a.occurredAt, locale)}</td>
                      <td className="font-mono text-xs" dir="ltr">
                        {a.action}
                      </td>
                      <td className="text-xs">
                        {a.entityType}
                        {a.entityId ? (
                          <span className="ms-1 font-mono text-muted" title={a.entityId}>
                            {shortId(a.entityId)}
                          </span>
                        ) : null}
                      </td>
                      <td className="font-mono text-xs">
                        {a.tenantId ? (
                          <Link href={`/tenants/${a.tenantId}`} className="text-primary hover:underline" title={a.tenantId}>
                            {shortId(a.tenantId)}
                          </Link>
                        ) : (
                          "—"
                        )}
                      </td>
                      <td className="font-mono text-xs" title={a.userId ?? undefined}>
                        {shortId(a.userId)}
                      </td>
                      <td className="font-mono text-xs" dir="ltr">
                        {a.ipAddress ?? "—"}
                      </td>
                      <td className="max-w-md">
                        {a.details ? (
                          <details>
                            <summary className="cursor-pointer text-xs text-primary">{t("audit.show")}</summary>
                            <pre dir="ltr" className="mt-1 max-h-48 overflow-auto whitespace-pre-wrap break-all rounded bg-raised p-2 text-[11px]">
                              {a.details}
                            </pre>
                          </details>
                        ) : (
                          "—"
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </TableWrap>
            <Pagination base="/audit" params={{ tenantId, action, from, to }} page={res.data.page} pageSize={res.data.pageSize} totalCount={res.data.totalCount} />
          </>
        )}
      </Card>
    </>
  );
}
