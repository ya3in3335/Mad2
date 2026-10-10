import type { Metadata } from "next";
import { FormDialog } from "@/components/form-dialog";
import { ApiError } from "@/components/server-ui";
import { Badge, Card, EmptyState, PageHeader, TableWrap } from "@/components/ui";
import { formatDateTime } from "@/lib/format";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import { getCurrentUser } from "@/lib/server/user";
import type { PlatformAdmin } from "@/lib/types";

export const metadata: Metadata = { title: "Administrators" };

export default async function AdminsPage() {
  const { t, locale } = await getT();
  const me = await getCurrentUser();
  const res = await apiGet<PlatformAdmin[]>("/api/v1/platform/admins");
  return (
    <>
      <PageHeader
        title={t("nav.admins")}
        description={t("admins.subtitle")}
        actions={
          <FormDialog
            label={t("admins.new")}
            variant="primary"
            method="POST"
            path="/api/v1/platform/admins"
            kind="admin.create"
            description={t("admins.newHelp")}
            fields={[
              { name: "fullName", label: t("onb.fullName"), type: "text", required: true, maxLength: 200 },
              { name: "email", label: t("auth.email"), type: "email", required: true, ltr: true },
              { name: "password", label: t("admins.initialPassword"), type: "password", required: true, maxLength: 128, wide: true, help: t("admins.passwordHelp") },
            ]}
          />
        }
      />
      <Card bodyClassName="">
        {!res.ok ? (
          <div className="p-4">
            <ApiError error={res.error} retryHref="/admins" />
          </div>
        ) : res.data.length === 0 ? (
          <EmptyState title={t("admins.empty")} />
        ) : (
          <TableWrap>
            <table className="vx-table">
              <thead>
                <tr>
                  <th>{t("onb.fullName")}</th>
                  <th>{t("auth.email")}</th>
                  <th>{t("lic.status")}</th>
                  <th>MFA</th>
                  <th>{t("admins.lastLogin")}</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {res.data.map((a) => (
                  <tr key={a.id}>
                    <td className="font-medium">
                      {a.fullName}
                      {a.id === me.id ? <span className="ms-2 text-xs text-muted">({t("admins.you")})</span> : null}
                    </td>
                    <td dir="ltr" className="text-start">
                      {a.email}
                    </td>
                    <td>
                      <Badge tone={a.isActive ? "success" : "neutral"}>{a.isActive ? t("status.active") : t("admins.disabled")}</Badge>
                    </td>
                    <td>
                      <Badge tone={a.mfaEnabled ? "success" : "warning"}>{a.mfaEnabled ? t("admins.mfaOn") : t("admins.mfaOff")}</Badge>
                    </td>
                    <td className="whitespace-nowrap">{a.lastLoginAt ? formatDateTime(a.lastLoginAt, locale) : t("admins.never")}</td>
                    <td className="text-end">
                      {a.id === me.id ? null : a.isActive ? (
                        <FormDialog
                          label={t("admins.disable")}
                          size="sm"
                          variant="danger"
                          method="POST"
                          path={`/api/v1/platform/admins/${a.id}/disable`}
                          description={t("admins.disableConfirm", { name: a.fullName })}
                          submitLabel={t("admins.disable")}
                        />
                      ) : (
                        <FormDialog
                          label={t("admins.enable")}
                          size="sm"
                          method="POST"
                          path={`/api/v1/platform/admins/${a.id}/enable`}
                          description={t("admins.enableConfirm", { name: a.fullName })}
                        />
                      )}
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
