# License Guide — device binding (no activation codes)

VELTRIX OS customer apps (Windows and Android) run **standalone only**: one device, a local owner account, local data, no
server and no sync. There are **no license keys, device codes or activation codes**. Anti-piracy relies on two things:

1. **The vendor installs the app personally** on the customer's computer or phone, and **deletes the installer** afterwards
   (never leave the setup file or APK with the customer).
2. **Automatic device binding on first run** (trust-on-first-use): the installed copy binds itself to that device and refuses
   to open anywhere else.

## Installing for a customer (vendor)

### Windows
1. Run `VELTRIX-OS-Windows-<version>-Setup.exe` as administrator on the customer's PC.
2. The installer writes a one-time token `%ProgramData%\VELTRIX\install.token` (new random value on every install) and grants
   the Users group modify rights on `%ProgramData%\VELTRIX` and `HKLM\SOFTWARE\VELTRIX` so the (non-elevated) app can bind.
3. Start the app once. It binds silently, then shows: language → privacy notice → create the local owner account.
4. **Delete the installer** from the PC, USB stick, Downloads folder and recycle bin.

### Android
1. Install the APK yourself (USB / `adb install`, or from your own device), then delete the APK from the phone.
2. Open the app once: it binds silently, then shows language → privacy → create the local owner account.

## How the binding works

### Windows
- First start, no binding and `install.token` present → a record `{v, iid (new installation id), fingerprint, boundAt, tok}` is
  created. The fingerprint is a salted SHA-256 of the Windows **MachineGuid** (machine name only if MachineGuid is unreadable);
  OS version and CPU count are deliberately not used so Windows updates do not break a legitimate install.
- The record is encrypted with **DPAPI LocalMachine** and authenticated with an **HMAC-SHA256** whose random key is itself
  DPAPI-LocalMachine protected. It is stored in two places:
  - `%ProgramData%\VELTRIX\binding.dat` (fallback `%LocalAppData%\VELTRIX OS\binding.dat` if not writable),
  - `HKLM\SOFTWARE\VELTRIX` value `Binding` (fallback `HKCU\Software\VELTRIX` if not writable).
  The token is then deleted.
- Every start, **before any local data is opened**: at least one location must exist, decrypt on this machine, pass the HMAC,
  match the fingerprint, and both locations must agree when both exist. A missing location is repaired from the valid one.
  Otherwise the app shows **"This copy is not licensed for this computer — contact your vendor"** and stops.
- No binding and no token (program folder copied to another PC) → refused. A binding copied to another PC cannot be decrypted
  there (DPAPI machine key) → refused.
- **Re-install by the vendor** writes a fresh token: on the same PC the existing binding is kept; if the binding is unusable
  (e.g. motherboard/Windows reinstall changed MachineGuid) the app re-binds.

### Android
- First launch (no binding and no "bound" marker): the record `{iid, keyThumbprint, boundAt}` is created, where
  `keyThumbprint` is the SHA-256 of the existing non-exportable Keystore device identity key. It is authenticated with an
  **HMAC by a Keystore key** and stored in the Keystore-encrypted secure store; a plain marker records that binding happened.
- Every launch verifies it. Keystore keys never leave the phone, so data restored or copied to another phone fails
  verification → "not licensed" screen. `android:allowBackup="false"` and `dataExtractionRules` exclude every domain from cloud
  backup and device-to-device transfer.
- Moving to a new phone: the vendor installs the app there; the customer moves business data with the app's own
  password-encrypted backup (Settings → Backup), not with Android backup.

## Local data

- Windows: secrets (owner credentials, tokens) are DPAPI current-user protected. The SQLite business database is **not**
  encrypted at rest in this version; copying it to another PC lets someone read it with SQLite tools (the app itself will
  not open on that PC). Wrapping the database key with the binding requires an encrypted SQLite provider (SQLCipher) and a
  data migration — planned, not done.
- Android: the Room database lives in app-private storage, excluded from backup/transfer.

## Limits (be honest with customers)

This **deters casual copying** (copying the program folder, cloning app data, restoring a backup onto another device). It is
**not** unbreakable: someone with administrator/root access and reverse-engineering skills can patch the app or forge a
binding on a machine they control. Keeping the installer out of customers' hands is the most important control.

## Troubleshooting

| Screen / symptom | Cause | Fix |
| --- | --- | --- |
| "This copy is not licensed for this computer" (reference `NotInstalled`) | Started without a vendor installation (no token, no binding) | Vendor re-installs on this PC |
| … reference `WrongDevice` | Binding from another PC, or MachineGuid changed | Vendor re-installs (fresh token re-binds) |
| … reference `Tampered` | Binding edited or the two locations disagree | Vendor re-installs |
| … reference `StorageFailed` | Neither binding location writable | Check `%ProgramData%\VELTRIX` permissions, re-install as admin |
| Android "not licensed for this device" | Data moved/restored from another phone, or binding cleared | Uninstall, vendor re-installs, restore the app's encrypted backup |
