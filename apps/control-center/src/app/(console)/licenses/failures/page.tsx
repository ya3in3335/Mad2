import type { Metadata } from "next";
import Link from "next/link";
import { FilterBar, FilterField } from "@/components/filter-bar";
import { ApiError, Pagination } from "@/components/server-ui";
import { Card, EmptyState, PageHeader, TableWrap } from "@/components/ui";
import { formatDateTime } from "@/lib/format";
import { isGuid, param, parsePage } from "@/lib/paging";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import { humanize } from "@/lib/status";
import type { ActivationFailure, Paged } from "@/lib/types";

export const metadata: Metadata = { title: "Activation failures" };
const PAGE_SIZE = 50;

export default async function ActivationFailuresPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const sp = await searchParams;
  const { t, locale } = await getT();
  const page = parsePage(sp.page);
  const tenantId = isGuid(param(sp.tenantId)) ? param(sp.tenantId) : "";
  const licenseId = isGuid(param(sp.licenseId)) ? param(sp.licenseId) : "";
  const res = await apiGet<Paged<ActivationFailure>>("/api/v1/platform/licenses/activation-failures", { page, pageSize: PAGE_SIZE, tenantId, licenseId });

  return (
    <>
      <nav className="mb-2 text-sm">
        <Link href="/licenses" className="text-muted hover:text-fg">
          ← {t("nav.licensing")}
        </Link>
      </nav>
      <PageHeader title={t("lic.failures")} description={t("lic.failuresSubtitle")} />
      <Card bodyClassName="">
        <FilterBar action="/licenses/failures">
          <FilterField label={t("common.tenantId")} className="w-full sm:w-80">
            <input name="tenantId" defaultValue={tenantId} className="vx-input font-mono text-xs" dir="ltr" maxLength={36} />
          </FilterField>
          <FilterField label={t("lic.licenseId")} className="w-full sm:w-80">
            <input name="licenseId" defaultValue={licenseId} className="vx-input font-mono text-xs" dir="ltr" maxLength={36} />
          </FilterField>
        </FilterBar>
        {!res.ok ? (
          <div className="p-4">
            <ApiError error={res.error} retryHref="/licenses/failures" />
          </div>
        ) : res.data.items.length === 0 ? (
          <EmptyState title={t("lic.failuresEmpty")} description={tenantId || licenseId ? t("common.emptyFiltered") : undefined} />
        ) : (
          <>
            <TableWrap>
              <table className="vx-table">
                <thead>
                  <tr>
                    <th>{t("lic.failureCode")}</th>
                    <th>{t("common.tenant")}</th>
                    <th>{t("lic.license")}</th>
                    <th>{t("dev.platform")}</th>
                    <th>{t("lic.ipAddress")}</th>
                    <th>{t("lic.occurredAt")}</th>
                  </tr>
                </thead>
                <tbody>
                  {res.data.items.map((f) => (
                    <tr key={f.id}>
                      <td className="font-mono text-xs" dir="ltr" title={f.code}>
                        {humanize(f.code)}
                      </td>
                      <td>
                        {f.tenantId ? (
                          <Link href={`/tenants/${f.tenantId}`} className="font-mono text-xs hover:underline">
                            {f.tenantId.slice(0, 8)}
                          </Link>
                        ) : (
                          "—"
                        )}
                      </td>
                      <td>
                        {f.licenseId ? (
                          <Link href={`/licenses/${f.licenseId}`} className="font-mono text-xs text-primary hover:underline">
                            {f.licenseId.slice(0, 8)}
                          </Link>
                        ) : (
                          "—"
                        )}
                      </td>
                      <td>{f.platform ?? "—"}</td>
                      <td className="font-mono text-xs" dir="ltr">
                        {f.ipAddress ?? "—"}
                      </td>
                      <td className="whitespace-nowrap">{formatDateTime(f.occurredAt, locale)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </TableWrap>
            <Pagination base="/licenses/failures" params={{ tenantId, licenseId }} page={res.data.page} pageSize={res.data.pageSize} totalCount={res.data.totalCount} />
          </>
        )}
      </Card>
    </>
  );
}
