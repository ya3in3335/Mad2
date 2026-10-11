# VELTRIX Admin (Android)

Standalone, **offline** owner tool: it issues VELTRIX OS activation codes on the owner's phone. There is no server, no account, no login and no `INTERNET` permission. It replaces `tools/license-issuer` for day-to-day use and produces byte-for-byte compatible codes (same payload, ES256 DER signature, `base64url(JSON).base64url(signature)`).

- Package: `com.veltrix.admin` · minSdk 26 · targetSdk 36
- Stack: Kotlin, Jetpack Compose (Material 3), Hilt, kotlinx.serialization, AndroidX Biometric, Android Keystore
- Languages: Arabic (RTL), French, English

## Build
```bash
cd apps/admin-android
bash ../../scripts/gradle-with-backoff.sh testDebugUnitTest lintDebug assembleDebug
```
Output: `app/build/outputs/apk/debug/VELTRIX-Admin-Android-<version>-debug.apk` (`assembleStaging` gives a minified, debug-signed test APK). Release signing comes only from CI environment variables.

## Use
1. Open the app: biometric / screen-lock prompt (required; a phone without a screen lock cannot use the app).
2. **Create signing key** (first run): EC P-256 generated inside the Android Keystore (alias `veltrix_vendor_sign_v1`, StrongBox when available, non-exportable, user authentication required for each use).
3. **Public key** tab: copy/share the base64 SPKI or PEM and put it in `licensing/vendor-public-key.pem`, then rebuild the VELTRIX OS apps. Apps built with another key reject this phone's codes.
4. **Issue** tab: paste the shop's device code (64 chars, spaces ignored), shop name, duration (30/90/365/custom days), optional note → confirm with biometrics → copy, share as text, or save as `.txt`. Codes grant plan `full`, all features, 1 device.
5. **History** tab: search past issues; **Renew** re-issues for the same device with a new duration.

## Key loss
The private key never leaves the phone and has **no backup**. If the phone is lost or reset, or the screen lock is removed (Android then destroys the key), no further license can be issued for apps built with its public key: a new key means rebuilding and redistributing every VELTRIX OS app. Deleting the key needs two confirmations plus biometrics.

## Security
`FLAG_SECURE` on all screens; lock on cold start and after 15 s in background; no backups (`allowBackup=false`); history is a local JSON file in app-private storage. No QR output (no QR library in the dependencies).

## Tests
JVM tests sign with a software EC key through the same `LicenseSigner` path and verify with a copy of the customer app's `LicenseVerifier` (valid, wrong device, wrong key/tampered, expiry), plus device-code parsing, expiry math, form validation and history search. Not yet exercised on a physical device.
