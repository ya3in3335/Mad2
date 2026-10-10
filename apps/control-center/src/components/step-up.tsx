"use client";

import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from "react";
import { useI18n } from "./i18n-client";

type Prompt = (reason?: string) => Promise<string | null>;
const StepUpContext = createContext<Prompt | null>(null);

/**
 * Asks the administrator to re-enter their password before a privileged operation. The password lives
 * only in this component's state until it is handed to the caller, and is cleared immediately after.
 */
export function StepUpProvider({ children }: { children: ReactNode }) {
  const { t } = useI18n();
  const dialogRef = useRef<HTMLDialogElement>(null);
  const resolver = useRef<((v: string | null) => void) | null>(null);
  const [password, setPassword] = useState("");
  const [reason, setReason] = useState<string | undefined>();
  const [open, setOpen] = useState(false);

  const finish = useCallback((value: string | null) => {
    resolver.current?.(value);
    resolver.current = null;
    setPassword("");
    setOpen(false);
  }, []);

  const prompt = useCallback<Prompt>((r) => {
    resolver.current?.(null);
    setReason(r);
    setPassword("");
    setOpen(true);
    return new Promise<string | null>((resolve) => {
      resolver.current = resolve;
    });
  }, []);

  useEffect(() => {
    const d = dialogRef.current;
    if (!d) return;
    if (open && !d.open) d.showModal();
    if (!open && d.open) d.close();
  }, [open]);

  return (
    <StepUpContext.Provider value={prompt}>
      {children}
      <dialog
        ref={dialogRef}
        onCancel={(e) => {
          e.preventDefault();
          finish(null);
        }}
        className="m-auto w-[min(92vw,26rem)] rounded-lg border border-line bg-surface p-0 text-fg shadow-2xl backdrop:bg-black/60"
      >
        <form
          className="space-y-4 p-6"
          onSubmit={(e) => {
            e.preventDefault();
            if (password) finish(password);
          }}
        >
          <div>
            <h2 className="text-lg font-semibold">{t("stepUp.title")}</h2>
            <p className="mt-1 text-sm text-muted">{reason ?? t("stepUp.body")}</p>
          </div>
          <div>
            <label htmlFor="vx-step-up" className="vx-label">
              {t("auth.password")}
            </label>
            <input
              id="vx-step-up"
              type="password"
              className="vx-input"
              autoComplete="current-password"
              value={password}
              maxLength={128}
              onChange={(e) => setPassword(e.target.value)}
              autoFocus
              required
            />
          </div>
          <div className="flex justify-end gap-2">
            <button type="button" className="vx-btn vx-btn-secondary" onClick={() => finish(null)}>
              {t("action.cancel")}
            </button>
            <button type="submit" className="vx-btn vx-btn-primary" disabled={!password}>
              {t("action.confirm")}
            </button>
          </div>
        </form>
      </dialog>
    </StepUpContext.Provider>
  );
}

export function useStepUp(): Prompt {
  const p = useContext(StepUpContext);
  if (!p) throw new Error("useStepUp must be used inside <StepUpProvider>.");
  return p;
}
