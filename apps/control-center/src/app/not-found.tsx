import Link from "next/link";
import { getT } from "@/lib/server/i18n";

export default async function NotFound() {
  const { t } = await getT();
  return (
    <main className="flex min-h-screen items-center justify-center px-4">
      <div className="vx-card max-w-md p-8 text-center">
        <h1 className="text-lg font-semibold">{t("err.notFound")}</h1>
        <Link href="/" className="vx-btn vx-btn-secondary mt-4">
          {t("nav.dashboard")}
        </Link>
      </div>
    </main>
  );
}
