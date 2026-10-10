import type { Metadata } from "next";
import { BarChart, LineChart } from "@/components/charts";
import { Kpi } from "@/components/kpi";
import { ApiError } from "@/components/server-ui";
import { Card, EmptyState, PageHeader } from "@/components/ui";
import { formatMoney, formatNumber } from "@/lib/format";
import { apiGet } from "@/lib/server/api";
import { getT } from "@/lib/server/i18n";
import type { PlatformDashboard } from "@/lib/types";

export const metadata: Metadata = { title: "Dashboard" };

export default async function DashboardPage() {
  const { t, locale } = await getT();
  const res = await apiGet<PlatformDashboard>("/api/v1/platform/dashboard");
  if (!res.ok) {
    return (
      <>
        <PageHeader title={t("nav.dashboard")} />
        <ApiError error={res.error} retryHref="/" />
      </>
    );
  }
  const d = res.data;
  const n = (v: number) => formatNumber(v, locale);
  const money = (v: number) => formatMoney(v, "DZD", locale);
  const hasRevenue = d.revenueByMonth.some((m) => Number(m.value) > 0);
  const hasActivations = d.activationsByMonth.some((m) => Number(m.value) > 0);

  return (
    <>
      <PageHeader title={t("nav.dashboard")} description={t("dash.subtitle")} />
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <Kpi label={t("dash.tenants")} value={n(d.tenants)} hint={t("dash.tenantsHint", { active: n(d.activeTenants), pending: n(d.pendingSetup) })} href="/tenants" />
        <Kpi label={t("dash.mrr")} value={money(d.monthlyRecurringRevenue)} hint={t("dash.revenueThisMonth", { value: money(d.revenueThisMonth) })} tone="violet" />
        <Kpi label={t("dash.activeLicenses")} value={n(d.activeLicenses)} hint={t("dash.expiring14", { count: n(d.expiringLicenses14d) })} tone={d.expiringLicenses14d > 0 ? "warning" : "success"} href="/licenses" />
        <Kpi label={t("dash.activeDevices")} value={n(d.activeDevices)} hint={t("dash.activations30", { count: n(d.activations30d) })} href="/devices" />
        <Kpi label={t("dash.suspended")} value={n(d.suspendedTenants)} tone={d.suspendedTenants > 0 ? "danger" : "neutral"} href="/tenants?status=Suspended" />
        <Kpi label={t("dash.pendingSetup")} value={n(d.pendingSetup)} tone={d.pendingSetup > 0 ? "warning" : "neutral"} href="/tenants?status=Pending" />
        <Kpi label={t("dash.openSupport")} value={n(d.openSupportCases)} tone={d.openSupportCases > 0 ? "info" : "neutral"} href="/support" />
        <Kpi label={t("dash.securityAlerts")} value={n(d.securityAlerts7d)} tone={d.securityAlerts7d > 0 ? "danger" : "success"} href="/security" />
      </div>
      <div className="mt-6 grid grid-cols-1 gap-6 xl:grid-cols-2">
        <Card title={t("dash.revenueByMonth")}>
          {hasRevenue ? <BarChart data={d.revenueByMonth} locale={locale} label={t("dash.revenueByMonth")} formatValue={money} /> : <EmptyState title={t("dash.noRevenue")} />}
        </Card>
        <Card title={t("dash.activationsByMonth")}>
          {hasActivations ? <LineChart data={d.activationsByMonth} locale={locale} label={t("dash.activationsByMonth")} formatValue={n} /> : <EmptyState title={t("dash.noActivations")} />}
        </Card>
      </div>
    </>
  );
}
