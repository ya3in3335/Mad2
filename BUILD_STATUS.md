# Build Status

Only results actually observed are recorded here. "Local" = Linux cloud dev container. "CI" = GitHub Actions on branch `ccr-c065d702-4m28p8`.

| Component | Command | Where | Result |
|-----------|---------|-------|--------|
| Backend API | `dotnet test tests/integration/Veltrix.Api.IntegrationTests` | Local (PostgreSQL 16) + CI "Backend" run #2 | ✅ 39/39 passed |
| Windows Core | `dotnet test tests/unit/Veltrix.Desktop.Tests` | Local + CI | ✅ 31/31 passed |
| Windows WPF app | `dotnet build apps/windows/Veltrix.Desktop -c Release` | Local (EnableWindowsTargeting) | ✅ 0 errors, 0 warnings |
| Windows installer | publish win-x64 + Inno Setup | CI "Windows" run #4 (windows-2022) | ✅ `VELTRIX-OS-Windows-0.1.4-Setup.exe` — https://github.com/ya3in3335/Mad2/actions/runs/38084589855 |
| Android OS | testDebugUnitTest lintDebug assembleDebug assembleStaging | Local | ✅ 7 tests, lint clean, APKs built |
| VELTRIX Admin | testDebugUnitTest lintDebug assembleDebug assembleStaging | Local | ✅ 21 tests, lint clean, APKs built |
| Android OS (CI) | same | CI "Android" run #7 | ✅ `VELTRIX-OS-Android-0.1.7` — https://github.com/ya3in3335/Mad2/actions/runs/38086451289 |
| VELTRIX Admin (CI) | same | CI "Android" run #7 | ✅ `VELTRIX-Admin-Android-0.1.7` — https://github.com/ya3in3335/Mad2/actions/runs/38086451289 |
| Control Center | lint / typecheck / test / build | Local + CI "Control Center" run #3 | ✅ 23/23 tests, build OK |
| Security | gitleaks + keystore check | CI "Security" | ⚠️ run #5 flagged 2 fake test tokens → allowlisted exact values in `.gitleaks.toml` |

## Not verified
- The Windows app has not been launched on a real Windows desktop by the developer (CI compiles + packages only).
- Control Center mutation forms and MFA login not exercised in a browser.
- VELTRIX Admin not launched on a device/emulator.
- No production signing: APKs are debug/test-signed; Setup.exe is not Authenticode-signed (SmartScreen warning expected).
