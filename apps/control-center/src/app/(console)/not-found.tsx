import Link from "next/link";
import { getT } from "@/lib/server/i18n";

export default async function NotFound() {
  const { t } = await getT();
  return (
    <div className="vx-card p-8">
      <h1 className="text-lg font-semibold">{t("err.notFound")}</h1>
      <Link href="/" className="vx-btn vx-btn-secondary mt-4">
        {t("nav.dashboard")}
      </Link>
    </div>
  );
}
