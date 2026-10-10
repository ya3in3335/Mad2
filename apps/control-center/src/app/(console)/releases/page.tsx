import type { Metadata } from "next";
import { FormDialog } from "@/components/form-dialog";
import { ApiError } from "@/components/server-ui";
import { Badge, Card, EmptyState, PageHeader, TableWrap } from "@/components/ui";
import type { FieldSpec } from "@/lib/forms";
import { formatDateTime } from "@/lib/format";
import type { Translator } from "@/lib/i18n-core";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import type { AppRelease } from "@/lib/types";

export const metadata: Metadata = { title: "Releases" };

const PLATFORMS = ["windows", "android"];
const CHANNELS = ["stable", "beta"];

function releaseFields(t: Translator, r?: AppRelease): FieldSpec[] {
  return [
    { name: "platform", label: t("dev.platform"), type: "select", required: true, options: PLATFORMS.map((p) => ({ value: p, label: p })), defaultValue: r?.platform ?? "windows" },
    { name: "channel", label: t("rel.channel"), type: "select", required: true, options: CHANNELS.map((c) => ({ value: c, label: c })), defaultValue: r?.channel ?? "stable" },
    { name: "version", label: t("app.version"), type: "text", required: true, ltr: true, maxLength: 50, defaultValue: r?.version ?? "", placeholder: "1.4.0" },
    { name: "minSupportedVersion", label: t("rel.minVersion"), type: "text", required: true, ltr: true, maxLength: 50, defaultValue: r?.minSupportedVersion ?? "", help: t("rel.minVersionHelp") },
    { name: "downloadUrl", label: t("rel.downloadUrl"), type: "url", ltr: true, wide: true, defaultValue: r?.downloadUrl ?? "", placeholder: "https://" },
    { name: "sha256", label: "SHA-256", type: "text", ltr: true, wide: true, maxLength: 64, defaultValue: r?.sha256 ?? "" },
    { name: "releaseNotes", label: t("rel.notes"), type: "textarea", maxLength: 8000, defaultValue: r?.releaseNotes ?? "" },
    { name: "isActive", label: t("rel.isActive"), type: "checkbox", defaultValue: r?.isActive ?? true },
  ];
}

export default async function ReleasesPage() {
  const { t, locale } = await getT();
  const res = await apiGet<AppRelease[]>("/api/v1/platform/releases");
  return (
    <>
      <PageHeader
        title={t("nav.releases")}
        description={t("rel.subtitle")}
        actions={<FormDialog label={t("rel.new")} variant="primary" method="POST" path="/api/v1/platform/releases" kind="release.upsert" fields={releaseFields(t)} />}
      />
      <Card bodyClassName="">
        {!res.ok ? (
          <div className="p-4">
            <ApiError error={res.error} retryHref="/releases" />
          </div>
        ) : res.data.length === 0 ? (
          <EmptyState title={t("rel.empty")} />
        ) : (
          <TableWrap>
            <table className="vx-table">
              <thead>
                <tr>
                  <th>{t("dev.platform")}</th>
                  <th>{t("app.version")}</th>
                  <th>{t("rel.channel")}</th>
                  <th>{t("rel.minVersion")}</th>
                  <th>{t("lic.status")}</th>
                  <th>{t("rel.published")}</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {res.data.map((r) => (
                  <tr key={r.id}>
                    <td>{r.platform}</td>
                    <td className="font-mono" dir="ltr">
                      {r.version}
                      {r.downloadUrl ? (
                        <a href={r.downloadUrl} rel="noopener noreferrer" target="_blank" className="ms-2 text-xs text-primary hover:underline">
                          ↓
                        </a>
                      ) : null}
                    </td>
                    <td>
                      <Badge tone={r.channel === "stable" ? "success" : "violet"}>{r.channel}</Badge>
                    </td>
                    <td className="font-mono text-xs">{r.minSupportedVersion}</td>
                    <td>
                      <Badge tone={r.isActive ? "success" : "neutral"}>{r.isActive ? t("status.active") : t("plans.inactive")}</Badge>
                    </td>
                    <td className="whitespace-nowrap text-xs">{formatDateTime(r.publishedAt, locale)}</td>
                    <td className="text-end">
                      <FormDialog label={t("action.edit")} size="sm" method="PUT" path={`/api/v1/platform/releases/${r.id}`} kind="release.upsert" fields={releaseFields(t, r)} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </TableWrap>
        )}
      </Card>
    </>
  );
}
