# Administrator Guide (platform owner)

## Access
- **Control Center** (web, `https://<admin-domain>`) and **VELTRIX Admin** (Android) use the same platform-admin account.
- Enable MFA (TOTP) for every admin. Sensitive actions ask for your password again (step-up) and are written to the audit log.

## Daily operations
| Task | Where |
|------|-------|
| KPIs: tenants, subscriptions, licenses, devices online, MRR, pending payments, errors | Dashboard |
| Suspend / reactivate a tenant | Tenants → tenant → Suspend |
| Issue demo / commercial license, renew, change seats, suspend, revoke, transfer | Licenses |
| Deactivate / replace / reassign a device | Devices (fleet) |
| Confirm manual payment (idempotent) | Invoices → pending → Confirm |
| Incidents (crashes, sync, license, backup errors) → acknowledge / resolve | Diagnostics |
| Request a diagnostic report from a device (requires tenant consent) | Device → Request report |
| Support tickets | Support |
| Publish a release | Releases |
| Audit trail, security alerts | Audit / Security |
| Backups status, run backup | Health |

## Telegram alerts
Configure server secrets (see `DEPLOYMENT.md`). Only chat IDs in `Telegram__AllowedChatIds` receive alerts or can use `/status /health /stats /alerts /subscriptions /help`. Commands are read-only; all changes are made in the Control Center or Admin app.

## Security rules
- Never share the license signing key, `AUTH_SIGNING_KEY` or `BACKUP_ENCRYPTION_KEY`.
- Disable admins who leave (Admins → Disable); their sessions are revoked.
- Review Security alerts weekly (lockouts, refresh-token reuse).
