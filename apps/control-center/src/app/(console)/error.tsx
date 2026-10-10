"use client";

import { useEffect } from "react";
import { useI18n } from "@/components/i18n-client";

export default function ConsoleError({ error, reset }: { error: Error & { digest?: string }; reset: () => void }) {
  const { t } = useI18n();
  useEffect(() => {
    console.error(error);
  }, [error]);
  return (
    <div role="alert" className="vx-card border-danger/40 p-8">
      <h1 className="text-lg font-semibold text-danger">{t("err.generic")}</h1>
      <p className="mt-2 text-sm text-muted">{t("err.unexpected")}</p>
      {error.digest ? <p className="mt-2 font-mono text-xs text-muted">ref: {error.digest}</p> : null}
      <button type="button" className="vx-btn vx-btn-secondary mt-4" onClick={reset}>
        {t("action.retry")}
      </button>
    </div>
  );
}
