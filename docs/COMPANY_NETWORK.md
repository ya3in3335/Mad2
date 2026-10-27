# VELTRIX Link: the company network (LAN)

One company PC is the **hub**; the other PCs are **employee PCs**. Every PC keeps a full working copy, keeps selling if the network drops, and synchronises automatically every 3 seconds.

## Setup
1. **On the administrator's PC:** open Company network → Turn on the hub.
   - The VELTRIX Connector service then serves HTTPS on TCP 47800 and answers discovery on UDP 47801.
   - The installer opens both ports for private and domain networks only.
2. **Create a code** for each new PC. Codes are 10 symbols, valid 15 minutes, single use, 5 attempts.
3. **On the employee PC:** open Company network → Search the network → type the code → Join, then restart VELTRIX OS.

## What replicates
| Data | Hub → employee PC | Employee PC → hub |
|---|---|---|
| Products, customers, registers, shifts, sales, returns, cash movements | ✅ | ✅ |
| Stock | ✅ (as quantity changes: concurrent sales add up) | ✅ |
| Journal entries of the employee PC's own sales | ❌ (the company's books stay on the hub) | ✅ |
| Employees, salaries, payroll, attendance, terminals, cameras, purchases, suppliers, expenses | ❌ never | ❌ refused by the hub |
| Sign-in accounts | ✅ (PBKDF2 hashes, every minute) | ❌ |

- Documents created on an employee PC carry its code, for example `JE-B1-000001`, and receipts use `B1-POS-…`, so numbers never collide.
- Employee PCs cannot delete replicated rows.
- When the hub refuses a change, the change is kept on the employee PC with the reason.

## Security
- **Pairing:** an HMAC proof binds the code to both random nonces and to the hub's TLS certificate. A PC without the code, or a fake hub (different certificate), is refused.
- **After pairing:** the hub certificate (self-signed ECDSA, stored DPAPI-encrypted in ProgramData) is pinned. Each PC has its own bearer token; the hub stores only its SHA-256, and the owner can revoke a PC at any time.
- **Employee edition:** HR, workforce, cameras and accounting pages are hidden. The hub also refuses those tables, so the restriction holds even against a modified client.

## Verification status
- **Tests (in-process and real HTTPS on loopback):**
  - the replication engine, including concurrent stock changes, replays, pending local edits, scopes and prefixes;
  - pairing, wrong or reused codes, certificate pinning and revocation.
- **Not yet verified:** two real Windows PCs on a LAN (service start, firewall, discovery broadcast).

## Limits (v1)
- Only the *Employee* role exists for joined PCs. A second full-admin PC is not supported yet.
- Joining replaces the PC's shop data with the company's (the PC is warned first).
- The hub log is never pruned yet.
