import type { Metadata } from "next";
import Link from "next/link";
import { notFound } from "next/navigation";
import { FormDialog } from "@/components/form-dialog";
import { ApiError } from "@/components/server-ui";
import { Badge, Card, DefinitionList, EmptyState, PageHeader, StatusBadge, TableWrap } from "@/components/ui";
import { reasonField } from "@/lib/field-sets";
import { formatDate, formatDateTime, formatNumber, toDateInput } from "@/lib/format";
import { enumOptions } from "@/lib/options";
import { isGuid } from "@/lib/paging";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import { humanize } from "@/lib/status";
import { FEATURES, type Device, type License, type LicenseEvent, type Paged } from "@/lib/types";

export const metadata: Metadata = { title: "License" };

export default async function LicenseDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  if (!isGuid(id)) notFound();
  const { t, locale } = await getT();
  const P = "/api/v1/platform";
  const [licRes, evRes, devRes] = await Promise.all([
    apiGet<License>(`${P}/licenses/${id}`),
    apiGet<LicenseEvent[]>(`${P}/licenses/${id}/events`),
    apiGet<Paged<Device>>(`${P}/devices`, { licenseId: id, pageSize: 100 }),
  ]);
  if (!licRes.ok) {
    if (licRes.error.status === 404) notFound();
    return (
      <>
        <PageHeader title={t("nav.licensing")} />
        <ApiError error={licRes.error} retryHref={`/licenses/${id}`} />
      </>
    );
  }
  const l = licRes.data;
  const revoked = l.status === "Revoked" || l.status === "Transferred";
  const suspended = l.status === "Suspended";

  return (
    <>
      <nav className="mb-2 text-sm">
        <Link href="/licenses" className="text-muted hover:text-fg">
          ← {t("nav.licensing")}
        </Link>
      </nav>
      <PageHeader
        title={`${t("lic.license")} ${l.keyHint}`}
        description={l.tenantName ?? undefined}
        actions={
          <>
            <StatusBadge value={l.effectiveStatus} t={t} />
            {!revoked ? (
              <>
                <FormDialog
                  label={t("action.edit")}
                  method="PUT"
                  path={`${P}/licenses/${l.id}`}
                  kind="license.update"
                  fields={[
                    { name: "maxDevices", label: t("lic.seats"), type: "number", required: true, min: 1, max: 10000, defaultValue: String(l.maxDevices) },
                    { name: "offlineGraceDays", label: t("lic.graceDays"), type: "number", required: true, min: 0, max: 365, defaultValue: String(l.offlineGraceDays) },
                    { name: "expiresAt", label: t("lic.expires"), type: "date", defaultValue: toDateInput(l.expiresAt), help: t("lic.expiresHelp") },
                    { name: "entitlements", label: t("lic.entitlements"), type: "checkboxes", options: enumOptions(FEATURES, t, "feature"), defaultValue: l.entitlements },
                    { name: "notes", label: t("common.notes"), type: "textarea", defaultValue: l.notes ?? "", maxLength: 2000 },
                  ]}
                />
                <FormDialog
                  label={t("lic.extend")}
                  method="POST"
                  path={`${P}/licenses/${l.id}/extend`}
                  kind="license.extend"
                  fields={[
                    { name: "days", label: t("lic.days"), type: "number", required: true, min: 1, max: 3650, defaultValue: "30" },
                    { name: "reason", label: t("common.reason"), type: "text", maxLength: 500 },
                  ]}
                />
                {suspended ? (
                  <FormDialog label={t("lic.reactivate")} variant="primary" method="POST" path={`${P}/licenses/${l.id}/reactivate`} kind="reason" fields={[reasonField(t)]} />
                ) : (
                  <FormDialog
                    label={t("lic.suspend")}
                    method="POST"
                    path={`${P}/licenses/${l.id}/suspend`}
                    kind="reason"
                    description={t("lic.suspendConfirm")}
                    fields={[reasonField(t)]}
                  />
                )}
                <FormDialog
                  label={t("lic.transfer")}
                  method="POST"
                  path={`${P}/licenses/${l.id}/transfer`}
                  kind="license.transfer"
                  description={t("lic.transferConfirm")}
                  fields={[
                    { name: "targetTenantId", label: t("lic.targetTenant"), type: "text", required: true, ltr: true, wide: true, help: t("common.tenantIdHelp") },
                    reasonField(t),
                  ]}
                />
                <FormDialog
                  label={t("lic.revoke")}
                  variant="danger"
                  method="POST"
                  path={`${P}/licenses/${l.id}/revoke`}
                  kind="reason"
                  description={t("lic.revokeConfirm")}
                  fields={[reasonField(t)]}
                  submitLabel={t("lic.revoke")}
                />
              </>
            ) : null}
          </>
        }
      />

      <Card title={t("common.details")}>
        <DefinitionList
          items={[
            [t("common.tenant"), <Link key="t" href={`/tenants/${l.tenantId}`} className="text-primary hover:underline">{l.tenantName ?? l.tenantId}</Link>],
            [t("lic.type"), l.type],
            [t("lic.plan"), <span key="p" className="font-mono">{l.planCode}</span>],
            [t("lic.seats"), `${formatNumber(l.activeDevices, locale)} / ${formatNumber(l.maxDevices, locale)}`],
            [t("lic.startsAt"), formatDate(l.startsAt, locale)],
            [t("lic.expires"), l.expiresAt ? formatDate(l.expiresAt, locale) : t("lic.perpetual")],
            [t("lic.graceDays"), formatNumber(l.offlineGraceDays, locale)],
            [t("common.created"), formatDateTime(l.createdAt, locale)],
            [
              t("lic.entitlements"),
              l.entitlements.length ? (
                <span key="e" className="flex flex-wrap gap-1">
                  {l.entitlements.map((e) => (
                    <Badge key={e} tone="violet">
                      {t(`feature.${e}`) === `feature.${e}` ? humanize(e) : t(`feature.${e}`)}
                    </Badge>
                  ))}
                </span>
              ) : (
                "—"
              ),
            ],
            [t("common.notes"), l.notes ?? "—"],
          ]}
        />
      </Card>

      <Card className="mt-6" bodyClassName="" title={t("nav.devices")}>
        {!devRes.ok ? (
          <div className="p-4">
            <ApiError error={devRes.error} />
          </div>
        ) : devRes.data.items.length === 0 ? (
          <EmptyState title={t("dev.empty")} />
        ) : (
          <TableWrap>
            <table className="vx-table">
              <thead>
                <tr>
                  <th>{t("dev.name")}</th>
                  <th>{t("dev.platform")}</th>
                  <th>{t("app.version")}</th>
                  <th>{t("lic.status")}</th>
                  <th>{t("dev.activated")}</th>
                  <th>{t("dev.lastSeen")}</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {devRes.data.items.map((d) => (
                  <tr key={d.id}>
                    <td>
                      <div className="font-medium">{d.deviceName}</div>
                      <div className="font-mono text-[11px] text-muted" dir="ltr" title={d.keyThumbprint}>
                        {d.keyThumbprint.slice(0, 16)}…
                      </div>
                    </td>
                    <td>{d.platform}</td>
                    <td className="font-mono text-xs">{d.appVersion}</td>
                    <td>
                      <StatusBadge value={d.status} t={t} />
                      {d.revokedReason ? <div className="mt-1 text-xs text-muted">{d.revokedReason}</div> : null}
                    </td>
                    <td className="whitespace-nowrap">{formatDate(d.activatedAt, locale)}</td>
                    <td className="whitespace-nowrap">{formatDateTime(d.lastSeenAt, locale)}</td>
                    <td className="text-end">
                      {d.status === "Active" ? (
                        <FormDialog
                          label={t("dev.revoke")}
                          size="sm"
                          variant="danger"
                          method="POST"
                          path={`${P}/devices/${d.id}/revoke`}
                          kind="reason"
                          description={t("dev.revokeConfirm", { name: d.deviceName })}
                          fields={[reasonField(t)]}
                        />
                      ) : null}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </TableWrap>
        )}
      </Card>

      <Card className="mt-6" bodyClassName="" title={t("lic.events")}>
        {!evRes.ok ? (
          <div className="p-4">
            <ApiError error={evRes.error} />
          </div>
        ) : evRes.data.length === 0 ? (
          <EmptyState title={t("lic.noEvents")} />
        ) : (
          <ol className="divide-y divide-line">
            {evRes.data.map((e) => (
              <li key={e.id} className="flex flex-wrap items-baseline justify-between gap-2 px-5 py-3 text-sm">
                <div className="min-w-0">
                  <span className="font-medium">{humanize(e.type)}</span>
                  {e.details ? <p className="mt-0.5 break-words text-xs text-muted">{e.details}</p> : null}
                </div>
                <time className="whitespace-nowrap text-xs text-muted" dateTime={e.occurredAt}>
                  {formatDateTime(e.occurredAt, locale)}
                </time>
              </li>
            ))}
          </ol>
        )}
      </Card>
    </>
  );
}
