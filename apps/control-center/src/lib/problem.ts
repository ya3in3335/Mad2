import type { Problem } from "./types";

export interface ActionError {
  status: number;
  code: string;
  message: string;
  fieldErrors?: Record<string, string[]>;
}

export type ActionResult<T> = { ok: true; data: T } | { ok: false; error: ActionError };

/** Turns an RFC 7807 problem (or anything else the API returned) into a safe, displayable error. */
export function problemToError(status: number, body: unknown): ActionError {
  const p = (body && typeof body === "object" ? body : {}) as Problem;
  const fieldErrors = p.errors && typeof p.errors === "object" ? p.errors : undefined;
  const firstField = fieldErrors ? Object.values(fieldErrors).flat()[0] : undefined;
  const message =
    (typeof p.detail === "string" && p.detail) ||
    firstField ||
    (typeof p.title === "string" && p.title) ||
    defaultMessage(status);
  return {
    status,
    code: (typeof p.code === "string" && p.code) || `http.${status}`,
    message: message.slice(0, 500),
    ...(fieldErrors ? { fieldErrors } : {}),
  };
}

export function defaultMessage(status: number): string {
  if (status === 0) return "The VELTRIX API could not be reached.";
  if (status === 400) return "The request was invalid.";
  if (status === 401) return "Your session has expired. Sign in again.";
  if (status === 403) return "You are not allowed to perform this operation.";
  if (status === 404) return "Not found.";
  if (status === 409) return "The operation conflicts with the current state.";
  if (status === 423) return "The account is temporarily locked.";
  if (status === 429) return "Too many requests. Try again shortly.";
  if (status >= 500) return "The server encountered an error.";
  return "The request failed.";
}

export const STEP_UP_CODES = new Set(["auth.step_up_required"]);
export function isStepUpError(e: ActionError): boolean {
  return STEP_UP_CODES.has(e.code);
}
