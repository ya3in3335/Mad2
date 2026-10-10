# VELTRIX — Task Board

Status legend: ✅ done & verified · 🟡 in progress · ⬜ not started · ⛔ blocked

| # | Phase | Task | Owner area | Status |
|---|-------|------|-----------|--------|
| 1 | P1 | Recover interrupted session, inventory repo | lead | ✅ |
| 2 | P2 | Android client (Compose) — debug + test APK | apps/android | ✅ (built locally; CI pending) |
| 3 | P3 | Windows WPF app UI (Veltrix.Desktop) | apps/windows | ✅ compiles; ⏳ manual run on Windows |
| 4 | P3 | Windows Core library + 31 unit tests | apps/windows/Veltrix.Desktop.Core | ✅ |
| 5 | P3 | Inno Setup installer script + build.ps1 | apps/windows/installer | ✅ Setup.exe built in CI |
| 6 | P4 | Control Center pages (Next.js) | apps/control-center | ✅ CI green |
| 7 | P5 | Backend core modules + 31 integration tests | modules, services | ✅ |
| 8 | P5 | Admin APIs: device heartbeat, diagnostics, owner dashboard | modules, services | ✅ 39 tests |
| 9 | P6 | GitHub Actions workflows | .github/workflows | 🟡 Android run pending |
| 10 | P7 | VELTRIX Admin Android app | apps/admin-android | 🟡 builds + 21 tests; README pending |
| 11 | P8 | Telegram notifier (outbox, webhook, read-only commands) | modules/telegram | ✅ tested with fake transport |
| 12 | P9 | Integration/security/release verification | all | ⬜ |
| 13 | P10 | Final docs (deployment, admin guide, license guide, test results) | docs | ⬜ |
| 14 | P5 | Deployment: Dockerfile, docker-compose, env templates | deploy | ⬜ |
