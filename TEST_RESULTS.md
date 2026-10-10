# Test Results (observed, 2026-10-10)

| Suite | Count | Result | Where |
|-------|-------|--------|-------|
| Backend integration (PostgreSQL 16, WebApplicationFactory) | 39 | ✅ 39 passed | Local + CI "Backend" |
| Windows Core unit tests | 31 | ✅ 31 passed | Local + CI "Windows" |
| Control Center (vitest) | 23 | ✅ 23 passed | Local + CI "Control Center" |
| Android VELTRIX OS (JVM) | 7 | ✅ 7 passed, lint clean | Local + CI "Android" |
| VELTRIX Admin (JVM) | 21 | ✅ 21 passed, lint clean | Local |
| **Total** | **121** | **0 failed** | |

Backend coverage highlights: tenant isolation, balanced journal postings, oversell concurrency, idempotency keys, licensing activation/seat limits/revocation, sync conflicts, subscriptions, encrypted backup + verified restore, device heartbeat/online counts, error-report sanitization and grouping, owner dashboard figures, Telegram webhook auth, outbox dedup and 429/5xx retry, 401/403 on platform routes.

Not covered by automated tests: UI end-to-end in browser, Windows app runtime, Android apps on device, real Telegram API.
