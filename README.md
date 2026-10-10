# VELTRIX OS

**Powering Business Intelligence.**

VELTRIX OS is a modular, multilingual (Arabic / French / English), offline-first business management platform —
POS, inventory, purchasing, CRM, sales, accounting, HR and reporting — with device-bound commercial licensing.
It is a business platform that runs **on** Windows and Android; it is not an operating system.

> Status: under active development. See [PROJECT_STATUS.md](docs/PROJECT_STATUS.md) and
> [REQUIREMENTS_MATRIX.md](docs/REQUIREMENTS_MATRIX.md) for what is implemented and verified.

| Component | Path | Stack |
|---|---|---|
| API (modular monolith) | `services/api` | ASP.NET Core 10, EF Core, PostgreSQL |
| Business modules | `modules/*` | .NET class libraries (one per bounded context) |
| Wire contracts | `packages/contracts` | Shared DTOs |
| Windows desktop | `apps/windows` | WPF, MVVM, SQLite |
| Android | `apps/android` | Kotlin, Jetpack Compose, Room, Hilt |
| Control Center | `apps/control-center` | Next.js, TypeScript, Tailwind |
| Brand identity | `branding` | SVG sources + generated assets |

## Quick start (API)

```bash
# PostgreSQL 16 on localhost (user postgres / password postgres) for development
dotnet build Veltrix.slnx
dotnet test tests/integration/Veltrix.Api.IntegrationTests
ASPNETCORE_ENVIRONMENT=Development \
Bootstrap__PlatformAdminEmail=admin@example.com Bootstrap__PlatformAdminPassword='Choose-A-Str0ng-Pass!' \
dotnet run --project services/api/Veltrix.Api
```

See [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) for production configuration.
