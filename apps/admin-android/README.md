# VELTRIX Admin (Android)

Owner-only mobile command center for the VELTRIX platform. It is a thin client: every number and every action comes from the VELTRIX backend (`/api/v1/platform/**`), which enforces authorization, step-up confirmation and audit logging. The app has no licensing database, no signing keys, no Telegram token and no built-in account.

- Package: `com.veltrix.admin` · minSdk 26 · targetSdk 36
- Stack: Kotlin, Jetpack Compose (Material 3), MVVM + StateFlow, Hilt, Retrofit/OkHttp, Navigation Compose, AndroidX Biometric
- Languages: Arabic (RTL), French, English

## Build
```bash
cd apps/admin-android
bash ../../scripts/gradle-with-backoff.sh testDebugUnitTest lintDebug assembleDebug assembleStaging
```
Outputs:
- `app/build/outputs/apk/debug/VELTRIX-Admin-Android-<version>-debug.apk`
- `app/build/outputs/apk/staging/VELTRIX-Admin-Android-<version>-test.apk` (minified, debug-signed — for testing only)

A release APK is signed only when the signing environment variables are provided by CI secrets; none are stored in the repository.

## First launch
1. Install the APK (allow "install unknown apps" for the file manager/browser you use).
2. Enter the server address (HTTPS required; debug builds also accept `http://10.0.2.2` for the emulator).
3. Sign in with a **platform administrator** account, then enter the authenticator (TOTP) code if MFA is enabled. Accounts without platform permissions are refused by the server.
4. Optionally enable biometric unlock in Settings.

## Security model
- Access/refresh tokens are encrypted with an AES-GCM key held in the Android Keystore; refresh tokens rotate on every refresh (server-side reuse detection).
- Biometric / device-credential gate on cold start and after 15 s in background.
- `FLAG_SECURE` blocks screenshots and the recents preview.
- Sensitive actions (suspend/revoke/transfer/seat changes, device deactivation, test Telegram message) re-prompt for the password, sent once as `X-Confirm-Password`; the server verifies it and writes the audit record.
- Logout revokes the session on the server and wipes local tokens.

## Status / limitations
- Built and unit-tested (21 JVM tests) and lint-clean; **not yet exercised on a physical device or emulator against a live server.**
- Endpoint list: `docs/API_ADMIN.md`.
