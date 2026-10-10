import type { Metadata } from "next";
import Link from "next/link";
import { notFound } from "next/navigation";
import type { ReactNode } from "react";
import { FormDialog } from "@/components/form-dialog";
import { ApiError } from "@/components/server-ui";
import { Badge, Card, DefinitionList, EmptyState, PageHeader, StatusBadge, TableWrap } from "@/components/ui";
import { reasonField } from "@/lib/field-sets";
import { formatDateTime, formatNumber } from "@/lib/format";
import { isGuid } from "@/lib/paging";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import { humanize } from "@/lib/status";
import type { AdminDeviceDetail } from "@/lib/types";

export const metadata: Metadata = { title: "Device" };

export default async function FleetDevicePage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  if (!isGuid(id)) notFound();
  const { t, locale } = await getT();
  const P = "/api/v1/platform";
  const res = await apiGet<AdminDeviceDetail>(`${P}/fleet/devices/${id}`);
  if (!res.ok) {
    if (res.error.status === 404) notFound();
    return (
      <>
        <PageHeader title={t("nav.fleet")} />
        <ApiError error={res.error} retryHref={`/fleet/${id}`} />
      </>
    );
  }
  const x = res.data;
  const d = x.device;
  const active = d.status === "Active";
  const n = (v: number | null) => (v === null ? "—" : formatNumber(v, locale));

  return (
    <>
      <nav className="mb-2 text-sm">
        <Link href="/fleet" className="text-muted hover:text-fg">
          ← {t("nav.fleet")}
        </Link>
      </nav>
      <PageHeader
        title={d.deviceName}
        description={d.tenantName ?? undefined}
        actions={
          <>
            <StatusBadge value={d.status} t={t} />
            {d.online ? <Badge tone="success">{t("fleet.online")}</Badge> : <Badge>{t("fleet.offline")}</Badge>}
            {active ? (
              <>
                <FormDialog
                  label={t("fleet.requestDiagnostic")}
                  method="POST"
                  path={`${P}/fleet/devices/${d.id}/diagnostic-requests`}
                  kind="diagnostic.request"
                  description={x.tenantDiagnosticsConsent ? undefined : t("fleet.noConsent")}
                  fields={[
                    { name: "reason", label: t("common.reason"), type: "text", maxLength: 500, wide: true },
                    { name: "expiresInHours", label: t("fleet.expiresInHours"), type: "number", min: 1, max: 168, defaultValue: "24" },
                  ]}
                />
                <FormDialog
                  label={t("fleet.reassign")}
                  method="POST"
                  path={`${P}/fleet/devices/${d.id}/reassign`}
                  kind="device.reassign"
                  description={t("fleet.reassignHelp")}
                  fields={[{ name: "targetLicenseId", label: t("fleet.targetLicense"), type: "text", required: true, ltr: true, wide: true }, reasonField(t)]}
                />
                <FormDialog
                  label={t("fleet.replace")}
                  method="POST"
                  path={`${P}/fleet/devices/${d.id}/replace`}
                  kind="reason"
                  description={t("fleet.replaceConfirm", { name: d.deviceName })}
                  fields={[reasonField(t)]}
                />
                <FormDialog
                  label={t("fleet.deactivate")}
                  variant="danger"
                  method="POST"
                  path={`${P}/fleet/devices/${d.id}/deactivate`}
                  kind="reason"
                  description={t("dev.revokeConfirm", { name: d.deviceName })}
                  fields={[reasonField(t)]}
                  submitLabel={t("fleet.deactivate")}
                />
              </>
            ) : null}
          </>
        }
      />

      <Card title={t("common.details")}>
        <DefinitionList
          items={[
            [t("common.tenant"), <Link key="t" href={`/tenants/${d.tenantId}`} className="text-primary hover:underline">{d.tenantName ?? d.tenantId}</Link>],
            [t("lic.license"), <Link key="l" href={`/licenses/${d.licenseId}`} className="font-mono text-primary hover:underline">{d.licenseId.slice(0, 8)}</Link>],
            [t("lic.plan"), x.licensePlan ?? "—"],
            [t("lic.status"), x.licenseStatus ? <StatusBadge key="ls" value={x.licenseStatus} t={t} /> : "—"],
            [t("dev.platform"), d.platform],
            [t("app.version"), <span key="v" className="font-mono" dir="ltr">{d.appVersion}</span>],
            [t("fleet.osVersion"), d.osVersion ?? "—"],
            [t("dev.activated"), formatDateTime(d.activatedAt, locale)],
            [t("dev.lastSeen"), formatDateTime(d.lastContactAt, locale)],
            [t("fleet.lastHeartbeat"), formatDateTime(d.lastHeartbeatAt, locale)],
            [t("fleet.lastSync"), formatDateTime(d.lastSyncAt, locale)],
            [t("fleet.syncPending"), n(d.pendingSyncOperations)],
            [t("fleet.syncFailed"), n(d.failedSyncOperations)],
            [t("fleet.reportedErrors"), n(x.reportedErrors)],
            [t("fleet.openIncidents"), <Link key="oi" href={`/incidents?tenantId=${d.tenantId}`} className="text-primary hover:underline">{n(x.openIncidents)}</Link>],
            [t("fleet.consent"), x.tenantDiagnosticsConsent ? t("common.yes") : t("common.no")],
            ...(x.revokedAt ? ([[t("fleet.revokedAt"), `${formatDateTime(x.revokedAt, locale)}${x.revokedReason ? ` — ${x.revokedReason}` : ""}`]] as Array<[string, ReactNode]>) : []),
            ...(x.replacedByDeviceId
              ? ([[t("fleet.replacedBy"), <Link key="rb" href={`/fleet/${x.replacedByDeviceId}`} className="font-mono text-primary hover:underline">{x.replacedByDeviceId.slice(0, 8)}</Link>]] as Array<[string, ReactNode]>)
              : []),
          ]}
        />
      </Card>

      <Card className="mt-6" bodyClassName="" title={t("fleet.diagnostics")}>
        {x.diagnosticRequests.length === 0 ? (
          <EmptyState title={t("fleet.noDiagnostics")} />
        ) : (
          <TableWrap>
            <table className="vx-table">
              <thead>
                <tr>
                  <th>{t("lic.status")}</th>
                  <th>{t("common.reason")}</th>
                  <th>{t("fleet.requestedAt")}</th>
                  <th>{t("fleet.uploadedAt")}</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {x.diagnosticRequests.map((r) => (
                  <tr key={r.id}>
                    <td>
                      <StatusBadge value={r.status} t={t} />
                    </td>
                    <td>{r.reason ?? "—"}</td>
                    <td className="whitespace-nowrap">{formatDateTime(r.requestedAt, locale)}</td>
                    <td className="whitespace-nowrap">{formatDateTime(r.uploadedAt, locale)}</td>
                    <td className="text-end">
                      {r.status === "pending" || r.status === "delivered" ? (
                        <FormDialog label={t("action.cancel")} size="sm" method="POST" path={`${P}/diagnostic-requests/${r.id}/cancel`} kind="empty" immediate />
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
        {x.history.length === 0 ? (
          <EmptyState title={t("lic.noEvents")} />
        ) : (
          <ol className="divide-y divide-line">
            {x.history.map((e) => (
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
