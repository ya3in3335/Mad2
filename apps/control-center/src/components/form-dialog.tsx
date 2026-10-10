"use client";

import { useEffect, useRef, useState, type ReactNode } from "react";
import { buildRequest, initialValues, resolvePath, type FieldSpec, type FormKind, type FormValues } from "@/lib/forms";
import type { ActionError } from "@/lib/problem";
import type { HttpMethod } from "@/lib/routes";
import { useI18n } from "./i18n-client";
import { useApiAction } from "./use-api-action";

type Variant = "primary" | "secondary" | "danger" | "ghost";
type ResultView = "setupToken" | "licenseKey";

export interface FormDialogProps {
  label: string;
  title?: string;
  description?: string;
  fields?: FieldSpec[];
  kind?: FormKind;
  method: HttpMethod;
  /** May contain {field} placeholders, filled from the form values. */
  path: string;
  submitLabel?: string;
  variant?: Variant;
  size?: "sm" | "md";
  /** Show the one-time secret returned by the API instead of closing. */
  resultView?: ResultView;
  /** Run immediately without a dialog (only for harmless, reversible operations). */
  immediate?: boolean;
}

export function ErrorBanner({ error }: { error: ActionError | null }) {
  const { t } = useI18n();
  if (!error) return null;
  const fields = error.fieldErrors ? Object.entries(error.fieldErrors) : [];
  return (
    <div role="alert" className="rounded-md border border-danger/40 bg-danger/10 px-3 py-2 text-sm text-danger">
      <p>{error.code === "auth.step_up_required" ? t("stepUp.failed") : error.message}</p>
      {fields.length > 1 ? (
        <ul className="mt-1 list-disc ps-5 text-xs">
          {fields.map(([k, v]) => (
            <li key={k}>
              {k}: {v.join(" ")}
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}

function extractSecret(data: unknown, view: ResultView): string | null {
  if (!data || typeof data !== "object") return null;
  const d = data as Record<string, unknown>;
  const v = view === "setupToken" ? d.setupToken : d.licenseKey;
  return typeof v === "string" ? v : null;
}

export function FormDialog({
  label,
  title,
  description,
  fields = [],
  kind = "empty",
  method,
  path,
  submitLabel,
  variant = "secondary",
  size = "md",
  resultView,
  immediate,
}: FormDialogProps) {
  const { t } = useI18n();
  const dialogRef = useRef<HTMLDialogElement>(null);
  const [open, setOpen] = useState(false);
  const [values, setValues] = useState<FormValues>(() => initialValues(fields));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [secret, setSecret] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const { run, pending, error, setError } = useApiAction();

  useEffect(() => {
    const d = dialogRef.current;
    if (!d) return;
    if (open && !d.open) d.showModal();
    if (!open && d.open) d.close();
  }, [open]);

  const btnClass = `vx-btn vx-btn-${variant} ${size === "sm" ? "px-2.5 py-1 text-xs" : ""}`;

  async function submit() {
    const built = buildRequest(kind, values);
    if (!built.ok) {
      setErrors(built.errors);
      return;
    }
    setErrors({});
    const res = await run(method, resolvePath(path, values), built.body, { stepUpReason: title ?? label });
    if (!res || !res.ok) return;
    const s = resultView ? extractSecret(res.data, resultView) : null;
    if (s) setSecret(s);
    else close();
  }

  function close() {
    setOpen(false);
    setSecret(null);
    setCopied(false);
    setErrors({});
    setError(null);
    setValues(initialValues(fields));
  }

  if (immediate) {
    return (
      <span className="inline-flex flex-col items-start gap-1">
        <button type="button" className={btnClass} disabled={pending} onClick={() => void submit()}>
          {pending ? t("state.loading") : label}
        </button>
        {error ? <span className="text-xs text-danger">{error.message}</span> : null}
      </span>
    );
  }

  const set = (name: string, v: FormValues[string]) => setValues((prev) => ({ ...prev, [name]: v }));

  return (
    <>
      <button type="button" className={btnClass} onClick={() => setOpen(true)}>
        {label}
      </button>
      <dialog
        ref={dialogRef}
        onCancel={(e) => {
          e.preventDefault();
          if (!pending) close();
        }}
        className="m-auto max-h-[90vh] w-[min(94vw,40rem)] overflow-y-auto rounded-lg border border-line bg-surface p-0 text-fg shadow-2xl backdrop:bg-black/60"
      >
        {open ? (
          secret ? (
            <div className="space-y-4 p-6">
              <h2 className="text-lg font-semibold">{t(resultView === "setupToken" ? "secret.setupTokenTitle" : "secret.licenseKeyTitle")}</h2>
              <p className="text-sm text-warning">{t("secret.once")}</p>
              <code dir="ltr" className="block break-all rounded-md border border-line bg-raised p-3 font-mono text-sm select-all">
                {secret}
              </code>
              <div className="flex justify-end gap-2">
                <button
                  type="button"
                  className="vx-btn vx-btn-secondary"
                  onClick={() => {
                    void navigator.clipboard?.writeText(secret).then(() => setCopied(true));
                  }}
                >
                  {copied ? t("secret.copied") : t("secret.copy")}
                </button>
                <button type="button" className="vx-btn vx-btn-primary" onClick={close}>
                  {t("action.close")}
                </button>
              </div>
            </div>
          ) : (
            <form
              className="space-y-5 p-6"
              noValidate
              onSubmit={(e) => {
                e.preventDefault();
                void submit();
              }}
            >
              <div>
                <h2 className="text-lg font-semibold">{title ?? label}</h2>
                {description ? <p className="mt-1 text-sm text-muted">{description}</p> : null}
              </div>
              {fields.length ? (
                <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                  {fields.map((f) => (
                    <Field key={f.name} field={f} value={values[f.name]} error={errors[f.name] ? t(errors[f.name] as string) : undefined} onChange={(v) => set(f.name, v)} />
                  ))}
                </div>
              ) : null}
              <ErrorBanner error={error} />
              <div className="flex justify-end gap-2">
                <button type="button" className="vx-btn vx-btn-secondary" onClick={close} disabled={pending}>
                  {t("action.cancel")}
                </button>
                <button type="submit" className={`vx-btn ${variant === "danger" ? "vx-btn-danger" : "vx-btn-primary"}`} disabled={pending}>
                  {pending ? t("state.loading") : (submitLabel ?? t("action.confirm"))}
                </button>
              </div>
            </form>
          )
        ) : null}
      </dialog>
    </>
  );
}

function Field({ field: f, value, error, onChange }: { field: FieldSpec; value: FormValues[string]; error?: string; onChange: (v: FormValues[string]) => void }) {
  const id = `f-${f.name}`;
  if (f.type === "hidden") return null;
  const wide = f.wide || f.type === "textarea" || f.type === "checkboxes";
  const common = {
    id,
    name: f.name,
    required: f.required,
    "aria-invalid": error ? true : undefined,
    "aria-describedby": error || f.help ? `${id}-hint` : undefined,
    dir: f.ltr ? ("ltr" as const) : undefined,
  };
  let control: ReactNode;
  switch (f.type) {
    case "textarea":
      control = <textarea {...common} className="vx-input min-h-28" maxLength={f.maxLength ?? 8000} placeholder={f.placeholder} value={String(value ?? "")} onChange={(e) => onChange(e.target.value)} />;
      break;
    case "select":
      control = (
        <select {...common} className="vx-input" value={String(value ?? "")} onChange={(e) => onChange(e.target.value)}>
          {!f.required ? <option value="">—</option> : null}
          {f.options?.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
      );
      break;
    case "checkbox":
      return (
        <label className={`flex items-center gap-2 text-sm ${wide ? "sm:col-span-2" : ""}`}>
          <input type="checkbox" id={id} name={f.name} className="h-4 w-4 accent-[var(--vx-primary)]" checked={value === true} onChange={(e) => onChange(e.target.checked)} />
          {f.label}
        </label>
      );
    case "checkboxes": {
      const selected = Array.isArray(value) ? value : [];
      control = (
        <div className="grid grid-cols-2 gap-2 rounded-md border border-line p-3 sm:grid-cols-3" role="group" aria-labelledby={`${id}-label`}>
          {f.options?.map((o) => (
            <label key={o.value} className="flex items-center gap-2 text-sm">
              <input
                type="checkbox"
                className="h-4 w-4 accent-[var(--vx-primary)]"
                checked={selected.includes(o.value)}
                onChange={(e) => onChange(e.target.checked ? [...selected, o.value] : selected.filter((x) => x !== o.value))}
              />
              {o.label}
            </label>
          ))}
        </div>
      );
      break;
    }
    default: {
      const type = f.type === "datetime" ? "datetime-local" : f.type;
      control = (
        <input
          {...common}
          type={type}
          className="vx-input"
          min={f.min}
          max={f.max}
          step={f.step}
          maxLength={f.maxLength}
          placeholder={f.placeholder}
          autoComplete={f.type === "password" ? "new-password" : "off"}
          value={String(value ?? "")}
          onChange={(e) => onChange(e.target.value)}
        />
      );
    }
  }
  return (
    <div className={wide ? "sm:col-span-2" : undefined}>
      <label htmlFor={f.type === "checkboxes" ? undefined : id} id={`${id}-label`} className="vx-label">
        {f.label}
        {f.required ? <span className="text-danger"> *</span> : null}
      </label>
      {control}
      {error ? (
        <p id={`${id}-hint`} className="mt-1 text-xs text-danger">
          {error}
        </p>
      ) : f.help ? (
        <p id={`${id}-hint`} className="mt-1 text-xs text-muted">
          {f.help}
        </p>
      ) : null}
    </div>
  );
}
