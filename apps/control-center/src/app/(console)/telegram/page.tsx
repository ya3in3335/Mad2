import type { Metadata } from "next";
import { FilterBar } from "@/components/filter-bar";
import { FormDialog } from "@/components/form-dialog";
import { Kpi } from "@/components/kpi";
import { SelectFilter, oneOf } from "@/components/select-filter";
import { ApiError, Pagination } from "@/components/server-ui";
import { Badge, Card, DefinitionList, EmptyState, PageHeader, StatusBadge, TableWrap } from "@/components/ui";
import { formatDateTime, formatNumber } from "@/lib/format";
import { enumOptions } from "@/lib/options";
import { param, parsePage } from "@/lib/paging";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import { TELEGRAM_DELIVERY_STATUSES, type Paged, type TelegramDelivery, type TelegramStatus } from "@/lib/types";

export const metadata: Metadata = { title: "Telegram" };
const PAGE_SIZE = 50;

export default async function TelegramPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const sp = await searchParams;
  const { t, locale } = await getT();
  const page = parsePage(sp.page);
  const status = oneOf(param(sp.status), TELEGRAM_DELIVERY_STATUSES);
  const P = "/api/v1/platform/telegram";
  const [stRes, dlRes] = await Promise.all([apiGet<TelegramStatus>(`${P}/status`), apiGet<Paged<TelegramDelivery>>(`${P}/deliveries`, { page, pageSize: PAGE_SIZE, status })]);
  const n = (v: number) => formatNumber(v, locale);
  const yesNo = (v: boolean) => (v ? <Badge tone="success">{t("common.yes")}</Badge> : <Badge tone="warning">{t("common.no")}</Badge>);

  return (
    <>
      <PageHeader
        title={t("nav.telegram")}
        description={t("tg.subtitle")}
        actions={
          <FormDialog
            label={t("tg.sendTest")}
            variant="primary"
            method="POST"
            path={`${P}/test`}
            kind="telegram.test"
            description={t("tg.sendTestHelp")}
            fields={[{ name: "text", label: t("tg.message"), type: "textarea", maxLength: 1000, wide: true }]}
          />
        }
      />
      {!stRes.ok ? (
        <ApiError error={stRes.error} retryHref="/telegram" />
      ) : (
        <>
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
            <Kpi label={t("tg.pending")} value={n(stRes.data.pending)} tone={stRes.data.pending > 0 ? "warning" : "neutral"} />
            <Kpi label={t("tg.failed")} value={n(stRes.data.failed)} tone={stRes.data.failed > 0 ? "danger" : "success"} href="/telegram?status=failed" />
            <Kpi label={t("tg.sent24h")} value={n(stRes.data.sent24h)} tone="success" />
            <Kpi label={t("tg.skipped24h")} value={n(stRes.data.skipped24h)} tone="neutral" />
          </div>
          <Card className="mt-6" title={t("tg.configuration")}>
            <DefinitionList
              items={[
                [t("tg.enabled"), yesNo(stRes.data.enabled)],
                [t("tg.botToken"), yesNo(stRes.data.botTokenConfigured)],
                [t("tg.webhookSecret"), yesNo(stRes.data.webhookSecretConfigured)],
                [t("tg.allowedChats"), n(stRes.data.allowedChats)],
                [t("tg.lastSent"), formatDateTime(stRes.data.lastSentAt, locale)],
                [t("tg.lastError"), stRes.data.lastError ? <span key="le" className="break-words text-danger">{stRes.data.lastError}</span> : "—"],
              ]}
            />
          </Card>
        </>
      )}

      <Card className="mt-6" bodyClassName="" title={t("tg.deliveries")}>
        <FilterBar action="/telegram">
          <SelectFilter name="status" label={t("lic.status")} value={status} allLabel={t("common.all")} options={enumOptions(TELEGRAM_DELIVERY_STATUSES, t)} />
        </FilterBar>
        {!dlRes.ok ? (
          <div className="p-4">
            <ApiError error={dlRes.error} retryHref="/telegram" />
          </div>
        ) : dlRes.data.items.length === 0 ? (
          <EmptyState title={t("tg.empty")} description={status ? t("common.emptyFiltered") : undefined} />
        ) : (
          <>
            <TableWrap>
              <table className="vx-table">
                <thead>
                  <tr>
                    <th>{t("tg.type")}</th>
                    <th>{t("inc.severity")}</th>
                    <th>{t("tg.message")}</th>
                    <th>{t("lic.status")}</th>
                    <th className="text-end">{t("tg.attempts")}</th>
                    <th>{t("common.created")}</th>
                  </tr>
                </thead>
                <tbody>
                  {dlRes.data.items.map((d) => (
                    <tr key={d.id}>
                      <td className="font-mono text-xs" dir="ltr">
                        {d.type}
                      </td>
                      <td>
                        <StatusBadge value={d.severity} t={t} />
                      </td>
                      <td className="max-w-md">
                        <p className="line-clamp-3 whitespace-pre-wrap break-words text-xs">{d.text}</p>
                        {d.lastError ? <p className="mt-1 break-words text-xs text-danger">{d.lastError}</p> : null}
                      </td>
                      <td>
                        <StatusBadge value={d.status} t={t} />
                      </td>
                      <td className="text-end tabular-nums">{n(d.attempts)}</td>
                      <td className="whitespace-nowrap">
                        {formatDateTime(d.createdAt, locale)}
                        {d.sentAt ? <div className="text-xs text-muted">{formatDateTime(d.sentAt, locale)}</div> : null}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </TableWrap>
            <Pagination base="/telegram" params={{ status }} page={dlRes.data.page} pageSize={dlRes.data.pageSize} totalCount={dlRes.data.totalCount} />
          </>
        )}
      </Card>
    </>
  );
}
