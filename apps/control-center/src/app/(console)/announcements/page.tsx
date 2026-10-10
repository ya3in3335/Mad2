import type { Metadata } from "next";
import Link from "next/link";
import { FormDialog } from "@/components/form-dialog";
import { ApiError } from "@/components/server-ui";
import { Badge, Card, EmptyState, PageHeader, StatusBadge } from "@/components/ui";
import type { FieldSpec } from "@/lib/forms";
import { formatDateTime } from "@/lib/format";
import type { Translator } from "@/lib/i18n-core";
import { enumOptions } from "@/lib/options";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import { ANNOUNCEMENT_SEVERITIES, type Announcement } from "@/lib/types";

export const metadata: Metadata = { title: "Announcements" };

/** "2026-05-01T10:30:00Z" -> "2026-05-01T10:30" for <input type="datetime-local"> (UTC). */
function toLocalInput(iso: string | null | undefined): string {
  if (!iso) return "";
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? "" : d.toISOString().slice(0, 16);
}

function announcementFields(t: Translator, a?: Announcement): FieldSpec[] {
  return [
    { name: "title", label: t("ann.title"), type: "text", required: true, maxLength: 200, wide: true, defaultValue: a?.title ?? "" },
    { name: "body", label: t("support.message"), type: "textarea", required: true, maxLength: 4000, defaultValue: a?.body ?? "" },
    { name: "severity", label: t("security.severity"), type: "select", required: true, options: enumOptions(ANNOUNCEMENT_SEVERITIES, t), defaultValue: a?.severity ?? "Info" },
    { name: "tenantId", label: t("ann.tenantOptional"), type: "text", ltr: true, defaultValue: a?.tenantId ?? "", help: t("ann.tenantHelp") },
    { name: "startsAt", label: t("ann.startsAt"), type: "datetime", required: true, defaultValue: toLocalInput(a?.startsAt ?? new Date().toISOString()), help: t("common.utc") },
    { name: "endsAt", label: t("ann.endsAt"), type: "datetime", defaultValue: toLocalInput(a?.endsAt), help: t("common.utc") },
    { name: "published", label: t("ann.published"), type: "checkbox", wide: true, defaultValue: a?.published ?? false },
  ];
}

export default async function AnnouncementsPage() {
  const { t, locale } = await getT();
  const res = await apiGet<Announcement[]>("/api/v1/platform/announcements");
  return (
    <>
      <PageHeader
        title={t("nav.announcements")}
        description={t("ann.subtitle")}
        actions={<FormDialog label={t("ann.new")} variant="primary" method="POST" path="/api/v1/platform/announcements" kind="announcement.upsert" fields={announcementFields(t)} />}
      />
      {!res.ok ? (
        <ApiError error={res.error} retryHref="/announcements" />
      ) : res.data.length === 0 ? (
        <Card>
          <EmptyState title={t("ann.empty")} />
        </Card>
      ) : (
        <ul className="space-y-4">
          {res.data.map((a) => (
            <li key={a.id} className="vx-card p-5">
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div className="min-w-0">
                  <div className="flex flex-wrap items-center gap-2">
                    <h2 className="font-semibold">{a.title}</h2>
                    <StatusBadge value={a.severity} t={t} />
                    <Badge tone={a.published ? "success" : "neutral"}>{a.published ? t("ann.published") : t("ann.draft")}</Badge>
                  </div>
                  <p className="mt-1 text-xs text-muted">
                    {formatDateTime(a.startsAt, locale)} → {a.endsAt ? formatDateTime(a.endsAt, locale) : t("ann.noEnd")} ·{" "}
                    {a.tenantId ? (
                      <Link href={`/tenants/${a.tenantId}`} className="text-primary hover:underline">
                        {t("ann.oneTenant")}
                      </Link>
                    ) : (
                      t("ann.allTenants")
                    )}
                  </p>
                </div>
                <FormDialog label={t("action.edit")} size="sm" method="PUT" path={`/api/v1/platform/announcements/${a.id}`} kind="announcement.upsert" fields={announcementFields(t, a)} />
              </div>
              <p className="mt-3 whitespace-pre-wrap break-words text-sm">{a.body}</p>
            </li>
          ))}
        </ul>
      )}
    </>
  );
}
