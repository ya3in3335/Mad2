import type { Metadata } from "next";
import Link from "next/link";
import { notFound } from "next/navigation";
import { FormDialog } from "@/components/form-dialog";
import { ApiError } from "@/components/server-ui";
import { Card, DefinitionList, EmptyState, PageHeader, StatusBadge } from "@/components/ui";
import { formatDateTime, formatNumber } from "@/lib/format";
import { enumOptions } from "@/lib/options";
import { isGuid } from "@/lib/paging";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import { humanize } from "@/lib/status";
import { INCIDENT_STATUSES, type IncidentDetail } from "@/lib/types";

export const metadata: Metadata = { title: "Incident" };

export default async function IncidentDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  if (!isGuid(id)) notFound();
  const { t, locale } = await getT();
  const P = "/api/v1/platform";
  const res = await apiGet<IncidentDetail>(`${P}/incidents/${id}`);
  if (!res.ok) {
    if (res.error.status === 404) notFound();
    return (
      <>
        <PageHeader title={t("nav.incidents")} />
        <ApiError error={res.error} retryHref={`/incidents/${id}`} />
      </>
    );
  }
  const x = res.data;
  const i = x.incident;
  const n = (v: number) => formatNumber(v, locale);

  return (
    <>
      <nav className="mb-2 text-sm">
        <Link href="/incidents" className="text-muted hover:text-fg">
          ← {t("nav.incidents")}
        </Link>
      </nav>
      <PageHeader
        title={i.title}
        description={`${i.app} ${i.appVersion} · ${i.platform}`}
        actions={
          <>
            <StatusBadge value={i.severity} t={t} />
            <StatusBadge value={i.status} t={t} />
            <FormDialog
              label={t("inc.changeStatus")}
              variant="primary"
              method="POST"
              path={`${P}/incidents/${i.id}/status`}
              kind="incident.status"
              fields={[
                { name: "status", label: t("lic.status"), type: "select", required: true, options: enumOptions(INCIDENT_STATUSES, t), defaultValue: i.status === "open" ? "acknowledged" : i.status },
                { name: "note", label: t("common.notes"), type: "textarea", maxLength: 1000, defaultValue: x.statusNote ?? "" },
              ]}
            />
          </>
        }
      />

      <Card title={t("common.details")}>
        <DefinitionList
          items={[
            [t("common.tenant"), i.tenantId ? <Link key="t" href={`/tenants/${i.tenantId}`} className="text-primary hover:underline">{i.tenantName ?? i.tenantId}</Link> : "—"],
            [t("inc.category"), t(`incCat.${i.category}`) === `incCat.${i.category}` ? humanize(i.category) : t(`incCat.${i.category}`)],
            [t("inc.errorType"), <span key="e" className="font-mono text-xs" dir="ltr">{i.errorType}</span>],
            [t("inc.source"), i.source],
            [t("inc.occurrences"), n(i.occurrenceCount)],
            [t("inc.affectedDevices"), n(x.affectedDevices)],
            [t("inc.firstSeen"), formatDateTime(i.firstSeenAt, locale)],
            [t("inc.lastSeen"), formatDateTime(i.lastSeenAt, locale)],
            [t("inc.acknowledgedAt"), formatDateTime(x.acknowledgedAt, locale)],
            [t("inc.resolvedAt"), formatDateTime(x.resolvedAt, locale)],
            [t("inc.lastDevice"), i.lastDeviceId ? <Link key="d" href={`/fleet/${i.lastDeviceId}`} className="font-mono text-primary hover:underline">{i.lastDeviceId.slice(0, 8)}</Link> : "—"],
            [t("common.notes"), x.statusNote ?? "—"],
            [t("inc.fingerprint"), <span key="f" className="break-all font-mono text-xs" dir="ltr">{i.fingerprint}</span>],
          ]}
        />
      </Card>

      {x.sampleStackTrace ? (
        <Card className="mt-6" title={t("inc.stackTrace")}>
          <pre className="max-h-96 overflow-auto whitespace-pre-wrap break-words font-mono text-xs" dir="ltr">
            {x.sampleStackTrace}
          </pre>
        </Card>
      ) : null}

      <Card className="mt-6" bodyClassName="" title={t("inc.recentEvents")}>
        {x.recentEvents.length === 0 ? (
          <EmptyState title={t("inc.noEvents")} />
        ) : (
          <ol className="divide-y divide-line">
            {x.recentEvents.map((e) => (
              <li key={e.id} className="flex flex-wrap items-baseline justify-between gap-2 px-5 py-3 text-sm">
                <div className="min-w-0">
                  <p className="break-words">{e.message}</p>
                  {e.context ? (
                    <p className="mt-0.5 break-words font-mono text-xs text-muted" dir="ltr">
                      {e.context}
                    </p>
                  ) : null}
                  {e.deviceId ? (
                    <Link href={`/fleet/${e.deviceId}`} className="text-xs text-primary hover:underline">
                      {t("dev.name")} {e.deviceId.slice(0, 8)}
                      {e.osVersion ? ` · ${e.osVersion}` : ""}
                    </Link>
                  ) : null}
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
