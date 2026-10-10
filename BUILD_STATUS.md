# Build Status

Only results actually observed are recorded here. "Local" = this cloud dev container (Linux).

| Component | Command | Where | Result | Date |
|-----------|---------|-------|--------|------|
| Backend API | `dotnet test tests/integration/Veltrix.Api.IntegrationTests` | Local (PostgreSQL 16) | ✅ 31/31 passed | 2026-10-10 |
| Windows Core | `dotnet test tests/unit/Veltrix.Desktop.Tests` | Local | ✅ 31/31 passed | 2026-10-10 |
| Windows WPF app | `dotnet build apps/windows/Veltrix.Desktop` | Local | ⬜ project being created | — |
| Windows installer | ISCC VeltrixOS.iss | Requires Windows runner | ⬜ not yet run | — |
| Android OS | `assembleDebug assembleStaging testDebugUnitTest lintDebug` | Local | ✅ APKs built, 7 unit tests passed, lint clean | 2026-10-10 |
| VELTRIX Admin | — | — | ⬜ not started | — |
| Control Center | lint / typecheck / test / build | Local | ⬜ pages in progress | — |
| GitHub Actions | all workflows | GitHub | ⬜ workflows committed, no run observed yet | — |

## Local artifacts (not published, gitignored `artifacts-out/`)
- `VELTRIX-OS-Android-0.1.0-debug.apk`
- `VELTRIX-OS-Android-0.1.0-test.apk` (minified, debug-signed — test only)

No production-signed APK and no code-signed Windows installer exist. Signing requires secrets that are not in the repository.
