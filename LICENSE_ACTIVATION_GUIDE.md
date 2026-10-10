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

## Offline activation (no server)
Activates one device with a code you issue yourself; no VELTRIX server is involved. Offline licenses use the single
plan (`full`, every feature), are bound to exactly one device and stay valid until their expiry date without ever
contacting a server.

### One-time setup (owner)
1. Generate the vendor key pair (once, on a trusted machine):
   `dotnet run --project tools/license-issuer -- keygen --out <secure-dir>`
   It writes `vendor-private-key.pem` (owner-only permissions on Linux/macOS) and `vendor-public-key.pem`, and never overwrites existing files.
2. **Back up `vendor-private-key.pem` offline** (encrypted USB / password manager). Anyone holding it can issue licenses;
   if it is lost, no new codes can be issued for apps built with its public key. Never commit it (`*private-key*.pem` is git-ignored).
3. Copy `vendor-public-key.pem` to `licensing/vendor-public-key.pem` in the repository (this file is safe to commit) and rebuild:
   Windows embeds it at build time, Android as `BuildConfig.VENDOR_PUBLIC_KEY_SPKI`. Builds without this file show
   "Offline activation is not configured". If you also run the server, use the same key pair for it
   (`Licensing__SigningKeyPath`), because an explicitly pinned key (`VeltrixVendorPublicKey` / `VELTRIX_VENDOR_PUBLIC_KEY`) takes precedence.

### Per customer
1. On the device: activation screen → **Offline activation** → the app shows a 64-character **device code**
   (installation id + device-key thumbprint, in blocks of 8). The customer copies or shares it to you.
2. You issue the code:
   `dotnet run --project tools/license-issuer -- issue --key <secure-dir>/vendor-private-key.pem --device-code "<code>" --name "<shop>" --days 365`
   It prints the activation code and writes `<shop>-activation.txt`.
3. The customer pastes the code (or opens the `.txt` file) and taps **Activate offline**. The app verifies the signature
   with the embedded public key and checks that the code matches this device; a code issued for another device is rejected.

### Limitations
- **No remote revocation**: an offline license cannot be revoked or suspended after issue. Use short terms (`--days`) and
  issue a new code to renew.
- Reinstalling the app or resetting the device key changes the device code: issue a new activation code.
- The clock-rollback protection still applies.

## Troubleshooting
| Symptom | Cause / fix |
|---------|-------------|
| "No seats available" | Revoke an old device (Licenses → Devices) or increase seats. |
| "License suspended/expired" | Reactivate or renew from the Control Center / Admin app. |
| Works offline then locks | Offline grace exceeded — connect once to refresh the certificate. |
| Activation failures | Visible to the owner in *Licenses → Activation failures* (`GET /api/v1/platform/licenses/activation-failures`); the attempted key is never stored. |
| "Offline activation is not configured" | The build has no `licensing/vendor-public-key.pem`: add it and rebuild. |
| "Issued for another device" | The code was issued from a different device code (or the app was reinstalled): issue a new one. |
