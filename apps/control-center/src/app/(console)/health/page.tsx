import type { Metadata } from "next";
import { FormDialog } from "@/components/form-dialog";
import { ApiError } from "@/components/server-ui";
import { Badge, Card, DefinitionList, EmptyState, PageHeader, StatusBadge, TableWrap } from "@/components/ui";
import { formatBytes, formatDateTime } from "@/lib/format";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import { humanize } from "@/lib/status";
import { JOB_NAMES, type BackupRecord, type SystemHealth } from "@/lib/types";

export const metadata: Metadata = { title: "System health" };

export default async function HealthPage() {
  const { t, locale } = await getT();
  const [healthRes, backupsRes] = await Promise.all([apiGet<SystemHealth>("/api/v1/platform/health"), apiGet<BackupRecord[]>("/api/v1/platform/backups")]);

  return (
    <>
      <PageHeader
        title={t("nav.health")}
        description={t("health.subtitle")}
        actions={
          <FormDialog
            label={t("health.runBackup")}
            variant="primary"
            method="POST"
            path="/api/v1/platform/backups/run"
            description={t("health.runBackupConfirm")}
          />
        }
      />
      {!healthRes.ok ? (
        <ApiError error={healthRes.error} retryHref="/health" />
      ) : (
        <div className="grid grid-cols-1 gap-6 xl:grid-cols-3">
          <Card title={t("health.overview")}>
            <DefinitionList
              items={[
                [t("lic.status"), <StatusBadge key="s" value={healthRes.data.status} t={t} />],
                [
                  t("health.database"),
                  <Badge key="d" tone={healthRes.data.databaseReachable ? "success" : "danger"}>
                    {healthRes.data.databaseReachable ? t("health.reachable") : t("health.unreachable")}
                  </Badge>,
                ],
                [t("app.version"), <span key="v" className="font-mono">{healthRes.data.version}</span>],
                [t("health.serverTime"), formatDateTime(healthRes.data.serverTime, locale)],
                [
                  t("health.migrations"),
                  healthRes.data.pendingMigrations.length ? (
                    <span key="m" className="text-warning">
                      {healthRes.data.pendingMigrations.join(", ")}
                    </span>
                  ) : (
                    t("health.upToDate")
                  ),
                ],
                [
                  t("health.lastBackup"),
                  healthRes.data.lastBackup ? `${formatDateTime(healthRes.data.lastBackup.startedAt, locale)} · ${healthRes.data.lastBackup.status}` : t("health.noBackup"),
                ],
              ]}
            />
          </Card>
          <Card title={t("health.workers")} className="xl:col-span-2" bodyClassName="">
            {healthRes.data.workers.length === 0 ? (
              <EmptyState title={t("health.noWorkers")} />
            ) : (
              <TableWrap>
                <table className="vx-table">
                  <thead>
                    <tr>
                      <th>{t("health.job")}</th>
                      <th>{t("health.lastRun")}</th>
                      <th>{t("lic.status")}</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    {healthRes.data.workers.map((w) => (
                      <tr key={w.name}>
                        <td className="font-mono text-xs" dir="ltr">
                          {w.name}
                        </td>
                        <td className="whitespace-nowrap text-xs">{formatDateTime(w.lastRunAt, locale)}</td>
                        <td>
                          <div className="flex flex-wrap gap-1">
                            <StatusBadge value={w.lastStatus} t={t} />
                            {w.stale ? <Badge tone="warning">{t("health.stale")}</Badge> : null}
                          </div>
                          {w.lastError ? <p className="mt-1 max-w-md break-words text-xs text-danger">{w.lastError}</p> : null}
                        </td>
                        <td className="text-end">
                          {(JOB_NAMES as readonly string[]).includes(w.name) ? (
                            <FormDialog label={t("health.runNow")} size="sm" method="POST" path={`/api/v1/platform/jobs/${w.name}/run`} description={t("health.runJobConfirm", { name: w.name })} />
                          ) : null}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </TableWrap>
            )}
          </Card>
        </div>
      )}
      <Card className="mt-6" bodyClassName="" title={t("health.backups")}>
        {!backupsRes.ok ? (
          <div className="p-4">
            <ApiError error={backupsRes.error} />
          </div>
        ) : backupsRes.data.length === 0 ? (
          <EmptyState title={t("health.noBackup")} />
        ) : (
          <TableWrap>
            <table className="vx-table">
              <thead>
                <tr>
                  <th>{t("health.started")}</th>
                  <th>{t("health.file")}</th>
                  <th className="text-end">{t("health.size")}</th>
                  <th>{t("lic.status")}</th>
                  <th>{t("health.verified")}</th>
                  <th>SHA-256</th>
                </tr>
              </thead>
              <tbody>
                {backupsRes.data.map((b) => (
                  <tr key={b.id}>
                    <td className="whitespace-nowrap text-xs">{formatDateTime(b.startedAt, locale)}</td>
                    <td className="font-mono text-xs" dir="ltr">
                      {b.fileName}
                    </td>
                    <td className="text-end tabular-nums">{formatBytes(b.sizeBytes)}</td>
                    <td>
                      <StatusBadge value={b.status} t={t} />
                      {b.error ? <p className="mt-1 max-w-xs break-words text-xs text-danger">{b.error}</p> : null}
                    </td>
                    <td>
                      <Badge tone={b.verified ? "success" : "warning"}>{b.verified ? t("common.yes") : t("common.no")}</Badge>
                      {b.verificationDetails ? <p className="mt-1 max-w-xs text-xs text-muted">{humanize(b.verificationDetails)}</p> : null}
                    </td>
                    <td className="font-mono text-[11px] text-muted" dir="ltr" title={b.sha256}>
                      {b.sha256 ? `${b.sha256.slice(0, 12)}…` : "—"}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </TableWrap>
        )}
      </Card>
      <div className="mt-6 flex flex-wrap gap-2">
        {JOB_NAMES.filter((j) => !(healthRes.ok && healthRes.data.workers.some((w) => w.name === j))).map((j) => (
          <FormDialog key={j} label={`${t("health.runNow")}: ${j}`} size="sm" variant="ghost" method="POST" path={`/api/v1/platform/jobs/${j}/run`} description={t("health.runJobConfirm", { name: j })} />
        ))}
      </div>
    </>
  );
}
