# License Activation Guide

## Concepts
- **License**: issued by the platform owner to a tenant (company) with a type (Trial/demo, Monthly, Quarterly, Annual, Perpetual, Enterprise), an expiry and a **seat count** (max active devices).
- **Device certificate**: after activation, each device holds a certificate signed by the server's ECDSA P-256 key. Apps verify it **offline** with the public key (`GET /api/v1/licensing/public-key`), so they keep working without internet until the offline grace period ends (max 30 days by default).
- The private signing key exists only on the server (`Licensing__SigningKeyPath`). It is never shipped in an app.

## Issuing (owner)
Control Center → Licenses, or VELTRIX Admin → Licenses:
- **Demo**: tenant + days (1–90) + seats → `POST /api/v1/platform/licenses/demo`
- **Commercial**: tenant + plan/term + seats → `POST /api/v1/platform/licenses`
- Renew/extend, change seats, suspend, revoke, transfer: actions on the license page (suspend/revoke/transfer ask for your password).
The license key is shown **once** — send it to the customer through a secure channel.

## Activating a device (customer)
1. Install VELTRIX OS (Windows Setup.exe or Android APK).
2. Enter the server address and the license key.
3. The app requests a challenge (`POST /api/v1/licensing/challenge`), signs it with a key generated on the device, and calls `POST /api/v1/licensing/activate`. The server checks the key, the license status and the free seats, then returns the signed certificate.
4. Reinstalling on the same machine replaces the previous registration instead of consuming a new seat.

## Troubleshooting
| Symptom | Cause / fix |
|---------|-------------|
| "No seats available" | Revoke an old device (Licenses → Devices) or increase seats. |
| "License suspended/expired" | Reactivate or renew from the Control Center / Admin app. |
| Works offline then locks | Offline grace exceeded — connect once to refresh the certificate. |
| Activation failures | Visible to the owner in *Licenses → Activation failures* (`GET /api/v1/platform/licenses/activation-failures`); the attempted key is never stored. |
