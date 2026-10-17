# Attendance terminal integration (Windows)

Punching happens **only on physical terminals**. Identity is matched on the terminal. VELTRIX imports the event: user id or card, time, method and result.
It never imports fingerprint or face images, or biometric templates. The Android app is admin-only.

## Architecture
- `Veltrix.Desktop.Core/Workforce/Terminals/`:
  - `ITerminalAdapter` is the vendor-neutral contract.
  - The adapters are `SimulatedTerminalAdapter`, `HikvisionIsapiAdapter`, `SupremaBioStar2Adapter` and `ZkTecoSdkAdapter` (placeholder).
  - `TerminalConnectorLoop` handles polling, streams, exponential backoff and the heartbeat.
  - `TerminalSyncCoordinator` keeps a single writer through the machine-wide mutex `Global\VeltrixOS.TerminalIngestion`.
- **Windows service `VeltrixConnector`** (`apps/windows/Veltrix.Connector`):
  - Runs as LocalSystem with delayed auto-start and restarts itself on failure. It works without the app open.
  - It reads `%ProgramData%\VELTRIX\Connector\connector.json`, which the app writes. The file holds the database path and no secrets.
  - It accepts only `…\Users\<name>\AppData\Local\VELTRIX OS\data\veltrix.db`, with no links in the path.
- **The app runs the same loop as a fallback.** It stays idle while the service heartbeat is younger than 2 minutes.
- **Pipeline** (`WorkforceService.Terminals.cs`):
  1. Authenticate with the DPAPI-encrypted password and check the pinned serial.
  2. Measure clock drift.
  3. Import pages into the persisted inbox. The cursor moves in the same transaction (offline-safe).
  4. Ingest in chronological order. The idempotency key is the vendor id, or a SHA-256 of the event when there is none.
  5. Map the employee, check the branch, normalize time zone and drift, and keep the provenance (local time, offset, raw data).
- **Review queue:** unknown user, drift beyond tolerance, future time, too old, conflicting duplicate, other branch, not verified, inactive employee.
  Nothing is dropped silently. A rejection keeps the event with a mandatory reason.
- **Out-of-order events:** the following automatically typed punches are re-derived through compensating events. Raw events are never edited.
  Overtime stays payable only after approval.

## Verification status (honest)
| Item | Unit tests | Simulator | Real hardware |
|---|---|---|---|
| Ingestion pipeline: dedupe, conflict, drift, out-of-order, overnight, offline, review, mapping, removal | ✅ `WorkforceTerminalTests` | ✅ | — |
| Connector loop: backoff, heartbeat, defer to service, single-writer lock | ✅ | ✅ | — |
| Hikvision ISAPI adapter (written from the published ISAPI documentation) | ❌ | ❌ | ❌ **not verified** |
| Suprema BioStar 2 API adapter (written from the published API documentation) | ❌ | ❌ | ❌ **not verified** |
| ZKTeco: official Standalone SDK 6.2.4.11 through the 32-bit bridge `VeltrixZkBridge.exe` | ✅ adapter + pipeline (fake bridge) | — | ❌ **not verified** |
| Windows service install (Inno `sc.exe`), DPAPI, named mutex | builds only | — | ❌ not run on Windows yet |

No vendor or model is claimed compatible until it is verified on a real device.

## ZKTeco setup
1. Get the SDK from ZKTeco at https://github.com/ZKTeco/Standalone-SDK (folder `Communication Protocol SDK(32Bit Ver6.2.4.11)\sdk`). It is not bundled because the repository carries no license.
2. As administrator, run `"C:\Program Files\VELTRIX OS\ZkBridge\Install-ZktecoSdk.ps1" -SdkDir <sdk folder>`.
   The script registers the 32-bit DLL in SysWOW64 with the 32-bit regsvr32.
3. Add the terminal in VELTRIX:
   - Manufacturer: ZKTeco.
   - IP address and port (4370 by default).
   - Password: the terminal's numeric Comm Key.
   - The machine number is 1.
4. How the integration behaves:
   - The bridge calls only the methods listed in the SDK type library: Connect_Net, SetCommPassword, GetSerialNumber, GetFirmwareVersion, GetProductCode, GetDeviceTime, EnableDevice, ReadGeneralLogData, SSR_GetGeneralLogData and Disconnect.
   - It is read-only: the terminal log is never cleared.
   - In/out keys are kept only as raw data; the attendance engine derives the direction.

## Pending work
- ZKTeco: verify on a real terminal (verify-mode codes other than 0, 1, 2 and 15 are shown as "unknown", with the raw code kept).
- Validate the Hikvision and Suprema adapters on real devices.
- Door-log dedupe after a crash.
