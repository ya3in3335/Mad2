# Next Session (concise)
Env: `export DOTNET_ROOT=/opt/dotnet PATH=/opt/dotnet:$PATH; service postgresql start`. Gradle: `bash scripts/gradle-with-backoff.sh <tasks>` from the app dir.
Done ✅: backend (39 tests), Control Center (CI green), Windows app + Setup.exe (CI), Android OS APK (CI), Admin app (local build, 21 tests), Telegram, deployment docs.
Open:
1. Confirm CI "Android" Admin artifact (run after 895f76c); update BUILD_STATUS.md.
2. Control Center: add UI for licenses/demo, /seats, activation-failures, fleet, incidents, telegram (routes exist, see docs/API_ADMIN.md; extend lib/routes.ts allowlist).
3. Step-up on legacy endpoints: /platform/devices/{id}/revoke, tenants/{id}/reactivate, licenses/{id}/reactivate.
4. Release signing (needs secrets), Authenticode for Setup.exe.
