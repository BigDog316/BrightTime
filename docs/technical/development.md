# Development

- [Tools](#tools)
- [Building](#building)
- [Tests](#tests)
- [CI](#ci)
- [Adding a feature slice](#adding-a-feature-slice)
- [Conventions the build enforces](#conventions-the-build-enforces)

Machine-specific setup, including the Windows scratch container on port 5434 and running the app
on the Android emulator, is in [HANDOFF.md](../HANDOFF.md#setting-up-a-machine).

## Tools

- The .NET SDK named in `global.json` (10.0.200, rolling forward to the latest feature band).
- PostgreSQL 18 with PostGIS 3.6, or Docker (`postgis/postgis:18-3.6`).
- Docker, for the compose stack and for Testcontainers.
- On Windows, Git Bash or WSL for `db/apply-schema.sh`.
- For the app: the MAUI Android workload (`dotnet workload install maui-android`), the Android SDK
  and a JDK; a Mac for iOS.

## Building

```bash
dotnet build
```

Analysers run at `latest-all` with warnings as errors, and package versions are central
(`Directory.Packages.props`). The solution file is `BrightTime.slnx`.

## Tests

Tests are xUnit v3 on Microsoft.Testing.Platform (`global.json` selects the runner), with Shouldly.

| Project | Covers | Needs |
|---|---|---|
| `BrightTime.SharedKernel.Tests` | Ids, results, paging, text | nothing |
| `BrightTime.Localisation.Tests` | Every key in all six catalogues, ICU syntax, plurals | nothing |
| `BrightTime.Architecture.Tests` | The module dependency order; contracts depend only on the shared kernel; only clocks read the time | nothing |
| `BrightTime.Ui.Shared.Tests` | The app's screens and offline roster, without a device | nothing |
| `BrightTime.Infrastructure.Tests` | Key vault, field encryption, outbox, job runner, audit | a database for some |
| `BrightTime.Modules.*.Tests` | Each module's rules | a database for most |
| `BrightTime.Seed.Tests` | The seed | a database |
| `BrightTime.IntegrationTests` | The schema, RLS, the tenant-isolation matrix, schema drift, the database rules (the conversation safety trigger and others) | a database, or Docker for Testcontainers |
| `BrightTime.Web.Tests` | Pages and consoles end to end in process, the console route matrix, the youth-record guard | `BRIGHTTIME_TEST_CONNECTION` |
| `BrightTime.Api.Tests` | Every API area end to end in process, including D65 and D66 | `BRIGHTTIME_TEST_CONNECTION` |
| `BrightTime.E2E.Tests` | Playwright in Chromium and WebKit: axe-core on every public route in six locales, keyboard-only use, the finder with JavaScript off | `BRIGHTTIME_TEST_CONNECTION` and installed browsers |

**Running them.** Point the tests at a cluster of their own, never one holding real data: the
fixtures create cluster-wide roles, and drop and recreate `brighttime_*` databases.

```bash
BRIGHTTIME_TEST_CONNECTION="Host=127.0.0.1;Port=5433;Database=brighttime;Username=postgres;Trust Server Certificate=true" dotnet test
```

- Apply the schema to that database first (`db/apply-schema.sh`), and again after any DDL
  change.
- **Without the variable, database tests skip silently and the run still passes.** The integration
  project starts its own container through Testcontainers instead; the Web, Api and E2E tests
  skip.
- One suite per database server at a time: the fixtures use fixed database names
  (`brighttime_apitest`, `brighttime_consoletest`, `brighttime_e2etest`, …).
- Run one class with `dotnet test --project tests/BrightTime.Api.Tests -- --filter-class "*BadgeApiTests"`.
- Install the E2E browsers once per machine, after a build:
  `pwsh artifacts/bin/BrightTime.E2E.Tests/debug/playwright.ps1 install chromium webkit`.
  A failure leaves a trace in `artifacts/e2e-traces/`.

The full suite is 1227 tests (at D66), all passing against PostgreSQL 18.6 and PostGIS 3.6.4.

## CI

`.github/workflows/ci.yml` runs on every push to `main` and every pull request:

| Job | Does |
|---|---|
| build and unit tests | Restore, a Release build, `dotnet format --verify-no-changes`, and `dotnet test` |
| Android app builds | Installs the MAUI Android workload and builds the app, with its format check |
| schema | Applies the DDL in order, checks every module table has row-level security, and checks the app role can't bypass it |
| finder query plans | Runs the seven finder scenarios at 50,000 listings, fails on a sequential scan of `listing_search` or estimates off by more than 10×, and keeps the plans |
| accessibility | The E2E gate, keeping traces of failures |

The build job sets no `BRIGHTTIME_TEST_CONNECTION`, so in CI the Web and Api suites skip and the
integration suite runs on Testcontainers. Run the Web and Api suites locally before pushing.

**Line endings.** On Windows, a CRLF checkout fails `dotnet format --verify-no-changes` on every
file. Check formatting as CI does, in a clone made with `core.autocrlf=false`.

## Adding a feature slice

The order that has worked through D66:

1. **Decide.** Write the decision in [plan/08-decisions.md](../plan/08-decisions.md) as
   *proposed*: what, why, what deviates from the plan, and what isn't done yet.
2. **Schema.** Change `db/schema/` (and `99-rls-and-grants.sql` for any new table's policies and
   grants). A youth-record table goes in the actor guard's list. Rebuild your test database.
3. **Mapping.** Map the tables in the module's `DbContext` exactly as the DDL has them, including
   `jsonb` and `citext` column types and the indexes. `SchemaDriftTests` holds the two together.
4. **Module.** Write the service in the module, reading inside `db.InTransactionAsync`. Talk to
   other modules only through their contracts; publish changes with `db.PublishAsync`. Return
   `Result<T>` with an `Error` code for every refusal.
5. **Permissions.** Add any new permission to `Permissions` and its roles to `BrightTimeRoles`.
   Check organisation scope with `OrganisationScopeRequirement`.
6. **Audit.** Record youth-record access with `AuditTrail.RecordInTransactionAsync` in the same
   transaction. Mark endpoints and pages `[YouthRecord(action)]` and DTOs `[YouthData]`.
7. **Endpoints or pages.** API routes map to `ApiProblems.From(error)` on failure. Console pages
   are static SSR forms: post, then redirect with `?done=`.
8. **Words.** Every new string, including each `error.<code>`, goes into all six catalogues. Mark
   machine-drafted text in `@@drafts`.
9. **Tests.** At least one end-to-end test through the API or the console, as the real login
   (not the superuser), so RLS and the actor guard are exercised.
10. **Docs.** Update [HANDOFF.md](../HANDOFF.md), the *Built* note in
    [plan/05](../plan/05-api-surface.md), and these documents where the slice changes them.

## Conventions the build enforces

| Rule | Enforced by |
|---|---|
| Modules reference only the contracts they're allowed | `ModuleBoundaryTests` |
| Only clocks read the system time (`IClock`, `TimeProvider`) | An architecture test |
| EF mapping equals the DDL, both ways | `SchemaDriftTests` |
| Every module table has RLS; the app role can't bypass it | CI's schema job, `TenantIsolationMatrixTests` |
| Every console route refuses other tenants and other organisations | `ConsoleRouteMatrixTests` |
| An endpoint that can return youth data declares `[YouthRecord]` | The start-up guard, `YouthRecordGuardTests` |
| Every string exists in all six catalogues | `CatalogueTests` |
| A new public page is in `PublicRoutes`, so axe and the keyboard gate see it | `AccessibilityTests`, `KeyboardTests` |
| In-page links use `SamePageLink`, never a bare `#id` (`<base href="/">` sends it home) | Review, and the keyboard gate |
| Formatting, including `using` order (System first) | `dotnet format` in CI |
| No sequential scan in the finder's plans | CI's plans job |

More of the traps, such as the static SSR form rule, draining queues in tests and shared test
accounts, are in [HANDOFF.md](../HANDOFF.md#conventions-that-bite).
