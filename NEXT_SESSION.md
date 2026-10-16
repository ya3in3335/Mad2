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
13. 🟡 Workforce B–D (Biometric terminals, per "Professional Biometric Time Attendance Integration" prompt) — WIP, UNTESTED.
   Product decision: Android = admin-only (no phone NFC/QR/PIN/kiosk). Windows connector = only punch source besides terminals.
   Saved (Core builds OK; no new tests yet; no real hardware; no vendor SDKs):
   - apps/windows/Veltrix.Desktop.Core/Workforce/Terminals/: TerminalContract.cs, TerminalInfrastructure.cs, SimulatedTerminal.cs,
     HikvisionIsapiAdapter.cs, SupremaBioStar2Adapter.cs (unverified vs real devices — do NOT claim compatibility), TerminalConnectorLoop.cs
   - Workforce/WorkforceService.Terminals.cs, WorkforceService.Access.cs; VMs WfDevicesViewModel.cs, WfAccessViewModel.cs (edited, unchecked)
   Remaining (in order):
   a. Finish coordinator/connector loop; ingestion pipeline (terminal auth, branch check, employee map, tz/drift normalize, provenance,
      idempotent dedupe key, out-of-order/overnight/missing → review queue, never silent drop); encrypted device creds (DPAPI).
   b. New project apps/windows/Veltrix.Connector (Worker Service, Windows Service) + Inno install/uninstall service + add to Veltrix.slnx.
   c. Device Management UI (add/edit/test/sync/events/disable/remove/diagnose); ZKTeco = placeholder until SDK provided.
   d. Tests: simulator-based (dup, out-of-order, overnight, offline buffer, reconnect, unknown employee, clock drift). Door-log dedupe.
   e. docs/HARDWARE_INTEGRATION.md (tested-on: unit/simulator/real-hardware table).
   f. Phase E: Android admin-only rework — revert/strip NFC/QR/PIN/kiosk from WIP commit 2048fdd. Phase F: PDF/Excel/CSV reports.
   Resume: `export DOTNET_ROOT=/opt/dotnet PATH=/opt/dotnet:$PATH; dotnet build Veltrix.slnx; dotnet test tests/unit/Veltrix.Desktop.Tests` (last full run: 220/220 before B–D WIP).
