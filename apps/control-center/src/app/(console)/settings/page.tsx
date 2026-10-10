import type { Metadata } from "next";
import { FormDialog } from "@/components/form-dialog";
import { ApiError } from "@/components/server-ui";
import { Card, DefinitionList, EmptyState, PageHeader, TableWrap } from "@/components/ui";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import { getCurrentUser } from "@/lib/server/user";
import type { PlatformSetting } from "@/lib/types";

export const metadata: Metadata = { title: "Settings" };

export default async function SettingsPage() {
  const { t } = await getT();
  const me = await getCurrentUser();
  const res = await apiGet<PlatformSetting[]>("/api/v1/platform/settings");
  return (
    <>
      <PageHeader
        title={t("nav.settings")}
        description={t("settings.subtitle")}
        actions={
          <FormDialog
            label={t("settings.new")}
            variant="primary"
            method="PUT"
            path="/api/v1/platform/settings/{key}"
            kind="setting.set"
            fields={[
              { name: "key", label: t("settings.key"), type: "text", required: true, ltr: true, maxLength: 100, wide: true, placeholder: "billing.payment_instructions", help: t("settings.keyHelp") },
              { name: "value", label: t("settings.value"), type: "textarea", maxLength: 4000 },
            ]}
          />
        }
      />
      <Card bodyClassName="" title={t("settings.platform")}>
        {!res.ok ? (
          <div className="p-4">
            <ApiError error={res.error} retryHref="/settings" />
          </div>
        ) : res.data.length === 0 ? (
          <EmptyState title={t("settings.empty")} />
        ) : (
          <TableWrap>
            <table className="vx-table">
              <thead>
                <tr>
                  <th>{t("settings.key")}</th>
                  <th>{t("settings.value")}</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {res.data.map((s) => (
                  <tr key={s.key}>
                    <td className="font-mono text-xs" dir="ltr">
                      {s.key}
                    </td>
                    <td className="max-w-xl whitespace-pre-wrap break-words text-sm">{s.value || <span className="text-muted">—</span>}</td>
                    <td className="text-end">
                      <FormDialog
                        label={t("action.edit")}
                        size="sm"
                        method="PUT"
                        path="/api/v1/platform/settings/{key}"
                        kind="setting.set"
                        title={s.key}
                        fields={[
                          { name: "key", label: "", type: "hidden", defaultValue: s.key },
                          { name: "value", label: t("settings.value"), type: "textarea", maxLength: 4000, defaultValue: s.value },
                        ]}
                      />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </TableWrap>
        )}
      </Card>
      <Card className="mt-6" title={t("settings.account")}>
        <DefinitionList
          items={[
            [t("onb.fullName"), me.fullName],
            [t("auth.email"), <span key="e" dir="ltr">{me.email}</span>],
            ["MFA", me.mfaEnabled ? t("admins.mfaOn") : <span key="m" className="text-warning">{t("admins.mfaOff")}</span>],
            [t("settings.roles"), me.roles.join(", ") || "—"],
          ]}
        />
        {!me.mfaEnabled ? <p className="mt-4 text-sm text-warning">{t("settings.mfaAdvice")}</p> : null}
      </Card>
    </>
  );
}
