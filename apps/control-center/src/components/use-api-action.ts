"use client";

import { useRouter } from "next/navigation";
import { useCallback, useState, useTransition } from "react";
import { apiMutation } from "@/app/actions";
import { isStepUpError, type ActionError, type ActionResult } from "@/lib/problem";
import { requiresStepUp, type HttpMethod } from "@/lib/routes";
import { useStepUp } from "./step-up";

export interface RunOptions {
  /** Re-render the server components after success (default true). */
  refresh?: boolean;
  /** Text shown in the password confirmation dialog. */
  stepUpReason?: string;
}

/**
 * Runs an allow-listed mutation through the apiMutation server action. Privileged endpoints prompt for
 * the administrator's password first; a rejected password re-prompts once.
 */
export function useApiAction() {
  const router = useRouter();
  const promptPassword = useStepUp();
  const [pending, startTransition] = useTransition();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ActionError | null>(null);

  const run = useCallback(
    async <T = unknown>(method: HttpMethod, path: string, body?: unknown, opts: RunOptions = {}): Promise<ActionResult<T> | null> => {
      setError(null);
      let password: string | undefined;
      const needsStepUp = requiresStepUp(method, path);
      if (needsStepUp) {
        const pw = await promptPassword(opts.stepUpReason);
        if (pw === null) return null;
        password = pw;
      }
      setBusy(true);
      try {
        let res = await apiMutation<T>(method, path, body, password);
        if (!res.ok && needsStepUp && isStepUpError(res.error)) {
          const pw = await promptPassword(opts.stepUpReason);
          if (pw === null) return null;
          res = await apiMutation<T>(method, path, body, pw);
        }
        if (!res.ok) {
          setError(res.error);
          if (res.error.status === 401) router.push("/login?reason=expired");
          return res;
        }
        if (opts.refresh !== false) startTransition(() => router.refresh());
        return res;
      } catch {
        const e: ActionError = { status: 0, code: "client.network", message: "The request could not be sent." };
        setError(e);
        return { ok: false, error: e };
      } finally {
        setBusy(false);
      }
    },
    [promptPassword, router],
  );

  return { run, pending: pending || busy, error, setError };
}
