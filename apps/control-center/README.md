# VELTRIX Control Center

Vendor super-admin web portal for the VELTRIX platform: tenants, licences and devices, subscriptions,
plans and manual payments, platform administrators, support, audit, security alerts, system health and
backups, app releases, announcements and platform settings.

Next.js 15 (App Router, React Server Components), React 19, Tailwind CSS 4, TypeScript. No UI or chart
libraries: charts are plain SVG built with `src/lib/chart.ts`.

## Running

```bash
npm ci
cp .env.example .env.local      # adjust VELTRIX_API_URL
npm run dev                     # http://localhost:3000
```

Production:

```bash
npm run build                   # VELTRIX_API_URL is not needed at build time
node .next/standalone/server.js # output: "standalone"; copy .next/static and public/ next to it
```

(`next start` also works for a quick local check but prints a warning because of `output: "standalone"`.)

Quality gates: `npm run lint`, `npm run typecheck`, `npm test` (vitest), `npm run build`.

### Environment variables

| Variable | Required | Description |
| --- | --- | --- |
| `VELTRIX_API_URL` | runtime | Base URL of the VELTRIX API (default `http://localhost:5080`). Server-side only. |
| `NODE_ENV` | no | `production` enables `Secure` + `__Host-` cookies and HSTS. HTTPS is then mandatory. |
| `NEXT_TELEMETRY_DISABLED` | no | Set to `1` to disable Next.js telemetry. |

Only platform administrators (`isPlatformAdmin` on `/api/v1/me`) can sign in.

## Pages

| Route | Backend endpoints |
| --- | --- |
| `/login` | `POST /api/v1/auth/login` (password, then TOTP code when MFA is enabled) |
| `/` dashboard | `GET /platform/dashboard` (KPIs, revenue and activation charts) |
| `/tenants`, `/tenants/[id]` | `GET/POST /platform/tenants`, `POST …/{id}/suspend` (step-up), `…/reactivate`; licences, devices, invoices and subscription of the tenant |
| `/licenses`, `/licenses/[id]` | `GET/POST/PUT /platform/licenses`, `extend`, `reactivate`, `suspend`/`revoke`/`transfer` (step-up), `events` |
| `/devices` | `GET /platform/devices`, `POST …/{id}/revoke` |
| `/subscriptions`, `/subscriptions/[id]` | `GET/POST /platform/subscriptions`, `plan`, `seats`, `renewal-invoice`, `cancel` (step-up), `history` |
| `/plans` | `GET/POST/PUT /platform/plans` |
| `/invoices` | `GET /platform/invoices` (defaults to `Issued` = manual payments awaiting confirmation), `confirm-payment` (step-up), `void` |
| `/admins` | `GET/POST /platform/admins` (step-up), `disable`/`enable` (step-up) |
| `/support` | `GET /platform/support`, `POST …/{id}/reply` |
| `/audit` | `GET /platform/audit` (action prefix, tenant, date range, paging) |
| `/security` | `GET /platform/security-alerts?days=` |
| `/health` | `GET /platform/health`, `GET /platform/backups`, `POST /platform/backups/run` (step-up), `POST /platform/jobs/{name}/run` |
| `/releases` | `GET/POST/PUT /platform/releases` |
| `/announcements` | `GET/POST/PUT /platform/announcements` |
| `/settings` | `GET /platform/settings`, `PUT /platform/settings/{key}` |

(`/platform` = `/api/v1/platform`.) Every page has loading (`loading.tsx`), empty and error states; API
errors (RFC 7807 problem details) are shown with their `code`.

## Security model

- **Tokens never reach the browser.** Login runs in a server action; the access token, refresh token and
  expiry are stored in `httpOnly`, `SameSite=Strict` cookies (`__Host-` prefixed and `Secure` in
  production). Server components call the API with the bearer token server-side (`src/lib/server/api.ts`).
- **Token rotation.** `src/middleware.ts` refreshes the access token shortly before expiry; concurrent
  refreshes with the same refresh token are coalesced because the API revokes the whole token family on
  reuse. A 401 during rendering goes through `/api/auth/refresh`. Sign-out revokes the refresh token on
  the API and clears the cookies.
- **Allow-listed API surface.** Mutations from the browser go through one server action (`apiMutation`
  in `src/app/actions.ts`) and the `/api/proxy/*` route; both only accept method/path pairs listed in
  `src/lib/routes.ts` (GUID-shaped ids, no traversal/encoded characters). Everything else is rejected
  before a request leaves the server. The API still enforces permissions on every endpoint; the sidebar
  merely hides entries the administrator lacks permissions for.
- **Step-up confirmation.** Privileged operations (suspend tenant; suspend/revoke/transfer licence;
  create/disable/enable administrator; cancel subscription; confirm payment; run backup) prompt for the
  password, which is sent once as `X-Confirm-Password` and never stored. A rejected password re-prompts.
- **CSRF.** Cookies are `SameSite=Strict`; Next.js server actions check `Origin` against `Host`; the proxy
  route additionally checks `Sec-Fetch-Site` and `Origin`.
- **CSP.** Per-request nonce-based Content-Security-Policy (`src/lib/csp.ts`), `frame-ancestors 'none'`,
  no third-party origins (fonts are system fonts), plus HSTS, `X-Content-Type-Options`, COOP/CORP.
- **Open redirects.** Post-login targets go through `safeRedirectPath`.
- One-time secrets (tenant setup token, licence key) are displayed once in a dialog and not persisted.

## Internationalisation and theming

English, French and Arabic (RTL). Shared strings come from `src/i18n/{en,fr,ar}.json` (generated);
Control Center strings live in `src/i18n/control-center/*.json` (hand-maintained). The language and
theme (dark by default, light available) are stored in the non-sensitive `vx_lang` / `vx_theme` cookies.
Brand colours come from `src/app/tokens.css` (generated from `packages/design-tokens/tokens.json`) and
are exposed to Tailwind as `bg-surface`, `text-muted`, `text-accent`, etc. (`src/app/globals.css`).
Logos are copied from `branding/logo` into `public/brand/`.

## Known limitations

- The subscriptions API has no tenant filter: the tenant page finds the subscription through the
  tenant's invoices, falling back to the 100 most recent subscriptions.
- The support API returns at most 500 cases and has no per-case endpoint or paging.
- There is no browser end-to-end test suite yet; vitest covers the pure logic in `src/lib`.
