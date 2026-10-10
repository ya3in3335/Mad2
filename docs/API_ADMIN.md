# VELTRIX Admin API (owner app + Telegram bot)

Server endpoints used by the owner-only **VELTRIX Admin** Android app, the Telegram notification bot, and the client
telemetry they rely on. All routes are under the API host; JSON uses camelCase, enums are strings, paged results are
`PagedResult<T> { items, page, pageSize, totalCount }` with `page`, `pageSize`, `search`, `sort` query parameters.

**Legend**
- **Permission**: platform permission required (held by the `platform_super_admin` role). Tenant users never hold
  `platform.*` permissions, even with `*`, so every `/api/v1/platform/**` route returns 403 to them and 401 when anonymous.
- **Step-up**: the request must carry `X-Confirm-Password: <current password>`; otherwise 403 `auth.step_up_required`.
- **New** = added in this change; **Reused** = existing endpoint the Admin app calls.
- DTO names refer to `packages/contracts/Veltrix.Contracts` (namespaces `Operations`, `Telegram`, `Licensing`, `Platform`,
  `Subscriptions`, `Tenants`, `Identity`).
- Responses never contain license keys, key hashes, device public keys/thumbprints, the Telegram bot token or webhook secret.
  The only time a license key is returned is the one-time `CreateLicenseResponse.LicenseKey` on issue/transfer.

## 1. Authentication (Reused)

| Method | Route | Permission | Step-up | Request | Response |
|---|---|---|---|---|---|
| POST | `/api/v1/auth/login` | anonymous (rate limited) | – | `LoginRequest` | `TokenResponse` or `MfaRequiredResponse` |
| POST | `/api/v1/auth/refresh` | anonymous (rate limited) | – | `RefreshRequest` | `TokenResponse` |
| POST | `/api/v1/auth/logout` | anonymous | – | `LogoutRequest` | 204 |
| GET | `/api/v1/me` | authenticated | – | – | `UserProfileDto` |
| GET | `/api/v1/me/sessions` | authenticated | – | – | `SessionDto[]` |
| POST | `/api/v1/me/mfa/setup` / `enable` / `disable` | authenticated | – | – / `MfaVerifyRequest` | `MfaSetupResponse` / 204 |

## 2. Owner dashboard

| Method | Route | Permission | Step-up | Request | Response | |
|---|---|---|---|---|---|---|
| GET | `/api/v1/platform/dashboard/admin?onlineWithinMinutes=` | `platform.tenants` | – | – | `OwnerDashboardDto` | **New** |
| GET | `/api/v1/platform/dashboard` | `platform.tenants` | – | – | `PlatformDashboardDto` (revenue/activation charts) | Reused |
| GET | `/api/v1/platform/health` | `platform.config` | – | – | `SystemHealthDto` | Reused |

`OwnerDashboardDto` is computed from live data: tenants by status; CRM customers (aggregate count across tenants, anonymized
excluded); subscriptions by status; usable/suspended/expiring licenses; active devices, online devices (heartbeat within
`onlineWithinMinutes`, default `Diagnostics:OnlineWindowMinutes` = 5), Android/Windows split; MRR (active subscriptions, annual / 12);
issued-unpaid invoices (count + amount); new (7 d) and open support tickets; unresolved and critical incidents; sync operations
rejected server-side in the last 24 h and failed operations reported by device heartbeats; backend health status; latest active
release per platform/channel.

## 3. Tenants / customers

| Method | Route | Permission | Step-up | Request | Response | |
|---|---|---|---|---|---|---|
| GET | `/api/v1/platform/tenants?status=&search=` | `platform.tenants` | – | – | `PagedResult<TenantSummaryDto>` | Reused |
| GET | `/api/v1/platform/tenants/{id}` | `platform.tenants` | – | – | `CompanyDto` | Reused |
| GET | `/api/v1/platform/tenants/{id}/overview` | `platform.tenants` | – | – | `TenantOverviewDto` | **New** |
| POST | `/api/v1/platform/tenants` | `platform.tenants` | – | `CreateTenantRequest` | `CreateTenantResponse` | Reused |
| POST | `/api/v1/platform/tenants/{id}/suspend` | `platform.tenants` | **yes** | `SuspendTenantRequest` | 204 | Reused |
| POST | `/api/v1/platform/tenants/{id}/reactivate` | `platform.tenants` | no (existing) | – | 204 | Reused |

`TenantOverviewDto` aggregates company, latest subscription, licenses, devices (`AdminDeviceDto`), activation history
(license events), billing invoices, support case summaries, user/customer counts, open incidents and the diagnostics-consent flag.

## 4. Subscriptions & payments (Reused)

| Method | Route | Permission | Step-up | Request | Response |
|---|---|---|---|---|---|
| GET | `/api/v1/platform/plans` | `platform.subscriptions` | – | – | `PlanDto[]` |
| POST/PUT | `/api/v1/platform/plans[/{id}]` | `platform.subscriptions` | – | `UpsertPlanRequest` | `PlanDto` |
| GET | `/api/v1/platform/subscriptions?status=` | `platform.subscriptions` | – | – | `PagedResult<SubscriptionDto>` |
| GET | `/api/v1/platform/subscriptions/{id}` | `platform.subscriptions` | – | – | `SubscriptionDto` |
| POST | `/api/v1/platform/subscriptions` | `platform.subscriptions` | – | `CreateSubscriptionRequest` | `SubscriptionDto` |
| POST | `/api/v1/platform/subscriptions/{id}/renewal-invoice` | `platform.subscriptions` | – | `IssueInvoiceRequest` | `BillingInvoiceDto` |
| POST | `/api/v1/platform/subscriptions/{id}/plan` | `platform.subscriptions` | – | `ChangePlanRequest` | `SubscriptionDto` |
| POST | `/api/v1/platform/subscriptions/{id}/seats` | `platform.subscriptions` | – | `ChangeSeatsRequest` | `SubscriptionDto` |
| POST | `/api/v1/platform/subscriptions/{id}/cancel` | `platform.subscriptions` | **yes** | `CancelSubscriptionRequest` | `SubscriptionDto` |
| GET | `/api/v1/platform/subscriptions/{id}/history` | `platform.subscriptions` | – | – | `EntitlementChangeDto[]` |
| GET | `/api/v1/platform/invoices?tenantId=&status=Issued` | `platform.subscriptions` | – | – | `PagedResult<BillingInvoiceDto>` (pending manual payments = `status=Issued`) |
| POST | `/api/v1/platform/invoices/{id}/confirm-payment` | `platform.subscriptions` | **yes** | `ConfirmPaymentRequest` | `BillingInvoiceDto` |
| POST | `/api/v1/platform/invoices/{id}/void` | `platform.subscriptions` | – | – | `BillingInvoiceDto` |

## 5. Licensing

| Method | Route | Permission | Step-up | Request | Response | |
|---|---|---|---|---|---|---|
| GET | `/api/v1/platform/licenses?tenantId=&search=` | `platform.licenses` | – | – | `PagedResult<LicenseDto>` | Reused |
| GET | `/api/v1/platform/licenses/{id}` | `platform.licenses` | – | – | `LicenseDto` | Reused |
| POST | `/api/v1/platform/licenses` (commercial issue) | `platform.licenses` | – | `CreateLicenseRequest` | `CreateLicenseResponse` | Reused |
| POST | `/api/v1/platform/licenses/demo` | `platform.licenses` | – | `IssueDemoLicenseRequest` (1–60 days, 1–10 devices) | `CreateLicenseResponse` | **New** |
| PUT | `/api/v1/platform/licenses/{id}` | `platform.licenses` | – | `UpdateLicenseRequest` | `LicenseDto` | Reused |
| POST | `/api/v1/platform/licenses/{id}/extend` (renew) | `platform.licenses` | – | `ExtendLicenseRequest` | `LicenseDto` | Reused |
| POST | `/api/v1/platform/licenses/{id}/seats` | `platform.licenses` | – | `ChangeLicenseSeatsRequest` (refused below active devices) | `LicenseDto` | **New** |
| POST | `/api/v1/platform/licenses/{id}/suspend` | `platform.licenses` | **yes** | `LicenseStatusRequest` | `LicenseDto` | Reused |
| POST | `/api/v1/platform/licenses/{id}/reactivate` | `platform.licenses` | no (existing) | `LicenseStatusRequest` | `LicenseDto` | Reused |
| POST | `/api/v1/platform/licenses/{id}/revoke` | `platform.licenses` | **yes** | `LicenseStatusRequest` | `LicenseDto` | Reused |
| POST | `/api/v1/platform/licenses/{id}/transfer` | `platform.licenses` | **yes** | `TransferLicenseRequest` | `CreateLicenseResponse` | Reused |
| GET | `/api/v1/platform/licenses/{id}/events` (activation history) | `platform.licenses` | – | – | `LicenseEventDto[]` | Reused |
| GET | `/api/v1/platform/licenses/activation-failures?tenantId=&licenseId=` | `platform.licenses` | – | – | `PagedResult<ActivationFailureDto>` | **New** |

Failed activations (bad/unknown key, expired/suspended license, exhausted seats, invalid challenge/signature) are now recorded
in the audit trail (`licensing.activation_failed`, with tenant/license when the key is recognized, never the key), as a
`device.activation_failed` license event, as a `device_registration` incident, and raised as a Telegram alert. All admin
operations are audited (existing `IAuditWriter`).

## 6. Device fleet & telemetry

| Method | Route | Permission | Step-up | Request | Response | |
|---|---|---|---|---|---|---|
| GET | `/api/v1/platform/fleet/devices?tenantId=&platform=&status=&online=&onlineWithinMinutes=&appVersion=&search=` | `platform.licenses` | – | – | `PagedResult<AdminDeviceDto>` | **New** |
| GET | `/api/v1/platform/fleet/devices/{id}` | `platform.licenses` | – | – | `AdminDeviceDetailDto` | **New** |
| POST | `/api/v1/platform/fleet/devices/{id}/deactivate` | `platform.licenses` | **yes** | `DeviceActionRequest` | `AdminDeviceDetailDto` | **New** (reuses `LicenseAdminService.RevokeDeviceAsync`) |
| POST | `/api/v1/platform/fleet/devices/{id}/replace` | `platform.licenses` | **yes** | `DeviceActionRequest` | `AdminDeviceDetailDto` | **New** (frees the seat, status `Replaced`) |
| POST | `/api/v1/platform/fleet/devices/{id}/reassign` | `platform.licenses` | **yes** | `ReassignDeviceRequest` (same tenant, seat check) | `AdminDeviceDetailDto` | **New** |
| GET | `/api/v1/platform/devices?tenantId=&licenseId=` | `platform.licenses` | – | – | `PagedResult<DeviceDto>` | Reused (legacy list) |
| POST | `/api/v1/platform/devices/{id}/revoke` | `platform.licenses` | no (existing) | `RevokeDeviceRequest` | 204 | Reused |

Client side (used by the POS apps, not the Admin app):

| Method | Route | Auth | Request | Response | |
|---|---|---|---|---|---|
| POST | `/api/v1/devices/heartbeat` | user session bound to an active device (`did` claim); `InstallationId` must match; rate limited (`RateLimits:TelemetryPerMinute`, default 60) | `DeviceHeartbeatRequest` | `DeviceHeartbeatResponse` | **New** |

The heartbeat updates `LastSeenAt` (last contact), `LastHeartbeatAt`, app/OS version, last sync time and pending/failed sync
counters on the existing `licensing.devices` row. Online = active device with a heartbeat inside the window. Heartbeats and error
reports stay accepted for expired (read-only) tenants; suspended tenants are rejected.

## 7. Diagnostics / incidents

| Method | Route | Permission | Step-up | Request | Response | |
|---|---|---|---|---|---|---|
| GET | `/api/v1/platform/incidents?status=open\|acknowledged\|resolved\|unresolved&category=&severity=&tenantId=&platform=&app=&source=client\|server&since=&search=&sort=-occurrences` | `platform.support` | – | – | `PagedResult<IncidentDto>` | **New** |
| GET | `/api/v1/platform/incidents/groups?status=&category=&take=` | `platform.support` | – | – | `IncidentGroupDto[]` (cross-tenant per fingerprint) | **New** |
| GET | `/api/v1/platform/incidents/{id}` | `platform.support` | – | – | `IncidentDetailDto` | **New** |
| POST | `/api/v1/platform/incidents/{id}/status` | `platform.support` | – | `UpdateIncidentStatusRequest` | `IncidentDetailDto` | **New** |
| POST | `/api/v1/platform/fleet/devices/{id}/diagnostic-requests` | `platform.licenses` | – | `CreateDiagnosticRequestRequest` | `DiagnosticRequestDto` | **New** |
| GET | `/api/v1/platform/diagnostic-requests/{id}` | `platform.licenses` | – | – | `DiagnosticReportDto` | **New** |
| POST | `/api/v1/platform/diagnostic-requests/{id}/cancel` | `platform.licenses` | – | – | `DiagnosticRequestDto` | **New** |

Client side:

| Method | Route | Auth | Request | Response | |
|---|---|---|---|---|---|
| POST | `/api/v1/diagnostics/errors` | any authenticated user (tenant + device taken from the token); rate limited | `ErrorReportBatchRequest` (1–50 items) | `ErrorReportBatchResponse` | **New** |
| POST | `/api/v1/devices/diagnostic-requests/{id}/report` | device-bound session of the target device; tenant setting `diagnostics.remoteConsent` must be `true` (else 403 `diagnostics.consent_required`) | `DiagnosticReportUploadRequest` | 204 | **New** |

- Categories: `crash, api, sync, license, migration, backup, device_registration, version_incompat, auth_failures, outage`;
  severities `info, warning, error, critical`; incident status `open, acknowledged, resolved` (a resolved incident reopens on recurrence).
- Fingerprint = SHA-256 of `app | appVersion | category | errorType | normalized(sanitized message)` (ids, numbers, quoted literals
  replaced by placeholders). Incidents are per (fingerprint, tenant); `/groups` aggregates across tenants.
- Server-side sanitization (always, regardless of client): emails, JWTs, Bearer/Basic credentials, `key=value` secrets
  (password/token/secret/api key/…), Telegram bot tokens, VELTRIX license keys, PEM blocks, Luhn-valid card numbers and long
  hex/base64 runs are replaced with `[redacted]`; context keys naming credentials are dropped. Limits: message 2 000, stack 16 000,
  30 context entries × 500 chars, 100 report facts, 500 log lines × 500 chars.
- Server events recorded automatically: backup failures, account lockouts (repeated failed sign-ins), transient sync failures,
  failed license activations.
- Remote diagnostics: the vendor creates a request; the device receives it in the heartbeat response only if the tenant enabled
  consent (`PUT /api/v1/settings/diagnostics.remoteConsent` with value `true`, permission `settings.manage`). The device uploads
  pre-defined, sanitized facts/log lines. There is no remote command execution.
- Retention: job `diagnostics-retention` (daily) deletes resolved incidents/events and diagnostic requests older than
  `Diagnostics:RetentionDays` (default 90) and delivered/failed Telegram outbox rows older than the same period. Run on demand with
  `POST /api/v1/platform/jobs/diagnostics-retention/run` (`platform.config`).

## 8. Support, releases, audit (Reused)

| Method | Route | Permission | Step-up | Request | Response |
|---|---|---|---|---|---|
| GET | `/api/v1/platform/support?status=` | `platform.support` | – | – | `SupportCaseDto[]` |
| POST | `/api/v1/platform/support/{id}/reply` | `platform.support` | – | `ReplySupportCaseRequest` | `SupportCaseDto` |
| GET | `/api/v1/platform/releases` | `platform.releases` | – | – | `AppReleaseDto[]` |
| POST/PUT | `/api/v1/platform/releases[/{id}]` | `platform.releases` | – | `UpsertReleaseRequest` | `AppReleaseDto` |
| GET | `/api/v1/platform/security-alerts?days=` | `platform.audit` | – | – | `SecurityAlertDto[]` |
| GET | `/api/v1/platform/audit?tenantId=&action=&from=&to=` | `platform.audit` | – | – | `PagedResult<AuditLogDto>` |
| GET | `/api/v1/platform/backups` | `platform.config` | – | – | `BackupRecordDto[]` |
| POST | `/api/v1/platform/backups/run` | `platform.config` | **yes** | – | `BackupRecordDto` |

## 9. Telegram

| Method | Route | Permission | Step-up | Request | Response | |
|---|---|---|---|---|---|---|
| GET | `/api/v1/platform/telegram/status` | `platform.config` | – | – | `TelegramStatusDto` | **New** |
| GET | `/api/v1/platform/telegram/deliveries?status=pending\|sent\|failed\|skipped&type=` | `platform.config` | – | – | `PagedResult<TelegramDeliveryDto>` | **New** |
| POST | `/api/v1/platform/telegram/test` | `platform.config` | **yes** | `TelegramTestMessageRequest` | `TelegramTestMessageResponse` | **New** |
| POST | `/api/v1/telegram/webhook` | anonymous + `X-Telegram-Bot-Api-Secret-Token` | – | Telegram `Update` | 200 (`{method:"sendMessage",…}` inline reply) / 401 / 404 when disabled | **New** |

Configuration (`Telegram` section; secrets via environment only, e.g. `Telegram__BotToken`, `Telegram__WebhookSecret`,
`Telegram__AllowedChatIds__0`): `Enabled` (default false), `BotToken` (default empty), `AllowedChatIds[]`, `WebhookSecret`,
`DedupWindowMinutes` (60), `MaxMessagesPerMinute` (20), `PerChatIntervalMilliseconds` (1000), `MaxAttempts` (8). Register the
webhook with Telegram's `setWebhook` using the same `secret_token`.

- Webhook: secret compared in constant time (SHA-256 + `FixedTimeEquals`); updates from chats not in `AllowedChatIds` are
  acknowledged with 200 and ignored. Commands (read-only, aggregates only): `/status /health /stats /alerts /subscriptions /help`.
  No mutation is possible through Telegram.
- Outbox (`telegram.outbox`): one row per alert and allowed chat; dedup by key within the window; alerts are still recorded as
  `skipped` when the bot is disabled. The sender (`TelegramSenderService`, every 2 s, only when `Workers:Enabled`) honours
  ≤ 1 msg/s per chat and ≤ 20/min overall, retries 429 after `retry_after` and 5xx/network errors with exponential backoff
  (5 s · 2^n, max 1 h, `MaxAttempts`), and fails other 4xx permanently. The bot token is redacted from stored errors and HttpClient
  request logging is disabled for this client (the URL contains the token).
- Alerts raised: new tenant registration and onboarding, new device activation, failed license activation, subscription expiring
  (≤ 7 days) / expired (daily job `subscription-alerts`), invoice awaiting manual payment, backend degraded/outage (job
  `health-monitor`, every 5 min; sent directly if the database is unreachable), repeated crashes (incident thresholds 5/25/100/500),
  sync failures (new sync incident or a device reporting failed operations), backup failure/verification failure, new support
  ticket (from tenants), account lockout and refresh-token reuse (security), new active release.
