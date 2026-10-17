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
8. ✅ Auto device binding (elevated installer --bind), barcode labels, splash motion, local ERP (101 tests), modern UI restyle (52cf3f5, build OK, not yet viewed on Windows).
9. ⬜ Phone↔PC Wi-Fi sync; ⬜ Arabic user guide + cleanup of server-era parts.
10. ✅ Glass-neon silver/gray restyle (Windows). NOTE: Themes/Colors.*.xaml are now hand-edited — do NOT re-run scripts/gen-i18n.py for colors without first porting the palette into packages/design-tokens/tokens.json.
11. ✅ Workforce M1 (Core engine, schema v5, 211 tests): employees/attendance/shifts/leave/payroll-prep/devices(virtual,HID,serial,TCP)/access control/RBAC. Next: M2 Windows UI screens; M3 Android (kiosk + self-service); vendor SDK adapters pending; door-log dedupe after crash; employer contributions posting.
12. ✅ Workforce M2 Windows UI (13 screens, 218 tests). TODO next: Android M3; door-log dedupe; employer contributions posting.
13. ✅ Workforce B–D Windows connector (commit fff5408; 233/233 tests; simulator only — NO real hardware; Hikvision/Suprema adapters unverified; ZKTeco = "SDK not installed").
   Done: Terminals/* adapters + simulator + TerminalConnectorLoop; ingestion pipeline + review queue (WorkforceService.Terminals.cs);
   apps/windows/Veltrix.Connector (Windows service VeltrixConnector, reads %ProgramData%\VELTRIX\Connector\connector.json, validates DB path);
   app fallback loop (App.xaml.cs StartTerminalConnector); screens wf.terminals + wf.review (WfTerminalsViewModel.cs, WfTerminalsView/WfReviewView.xaml);
   Inno installs/stops/deletes the service; windows.yml publishes publish-connector; docs/HARDWARE_INTEGRATION.md; tests WorkforceTerminalTests.cs.
   Not yet run on Windows: service install, DPAPI, named mutex, new screens (only compiled + resource keys checked).
   Next: E) Android admin-only (strip NFC/QR/PIN/kiosk from WIP 2048fdd); F) PDF/Excel/CSV reports; door-log dedupe; employer contributions posting.
   Resume: export DOTNET_ROOT=/opt/dotnet PATH=/opt/dotnet:$PATH; dotnet test tests/unit/Veltrix.Desktop.Tests
14. ✅ HR section password (SectionLock.cs, SectionLockViewModel/View, ShellViewModel gate): separate PBKDF2 password in SecureStore, set by owner at first opening of wf.*, erp.hr, cameras; 15-min idle relock, Ctrl+Shift+L lock, lockout backoff, owner change/reset. 238 tests. Not yet run on Windows.
15. 🟡 ZKTeco via official SDK 6.2.4.11: Veltrix.ZkBridge (x86, reg-free COM manifest, STA) + ZkTecoSdkAdapter; SDK auto-downloaded by setup from ZKTeco GitHub (13 files SHA-256 pinned in ZkTecoSdkFiles.cs + VeltrixOS.iss [Code]) into {app}\ZkBridge, fallback button = VeltrixOS.exe --install-zk-sdk (UAC). 241 tests (fake bridge). NOT verified on a real terminal or real Windows.
