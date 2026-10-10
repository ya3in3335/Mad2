# Next Session (concise)
Env: `export DOTNET_ROOT=/opt/dotnet PATH=/opt/dotnet:$PATH; service postgresql start`. Gradle: `bash scripts/gradle-with-backoff.sh <tasks>` from the app dir.
Done ✅: backend (39 tests), Control Center (CI green), Windows app + Setup.exe (CI), Android OS APK (CI), Admin app (local build, 21 tests), Telegram, deployment docs.
Open:
1. ✅ Admin APK built in CI (run #7).
2. ✅ Control Center UI for fleet, incidents, failures, telegram, demo/seats.
3. ✅ Step-up added to device revoke, tenant reactivate, license reactivate.
4. Release signing (needs secrets), Authenticode for Setup.exe.
5. ✅ Single plan "full" (all features) — verified: backend 40/40, CC 31/31 + build, admin-android 22/22 (commit c517b7c).
6. ✅ Offline device-bound licensing (tools/license-issuer) — commit 9026499.
7. ✅ Standalone mode done. Gaps: Win↔Android backup not interchangeable; Android re-login each start; no owner password-change UI; no auto backup.
