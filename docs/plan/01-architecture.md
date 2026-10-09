# 01 — Solution structure and module map

## Targets

| Thing | Choice |
|---|---|
| Runtime | .NET 10, C# 14, `net10.0` everywhere |
| Language settings | `Nullable=enable`, `TreatWarningsAsErrors=true`, `EnforceCodeStyleInBuild=true`, `AnalysisLevel=latest-all` |
| Database | PostgreSQL 17 + PostGIS 3.4+ (minimum 15 for `UNIQUE NULLS NOT DISTINCT`) |
| ORM | EF Core 10 + Npgsql 10, `NetTopologySuite` plugin |
| Web | Blazor Web App — static SSR by default, `InteractiveServer` on the islands that need it |
| Mobile | .NET MAUI Blazor Hybrid (iOS, Android) over a shared Razor class library |
| Reports | QuestPDF, ClosedXML |
| Tests | xUnit v3, Testcontainers (real Postgres+PostGIS), Playwright + axe-core, NetArchTest |

`global.json` pins the SDK to 10.0.2xx with `rollForward: latestFeature`. Central
package management via `Directory.Packages.props`; no `PackageReference` carries a
version outside that file.

## Repository layout

```
BrightTime.sln
Directory.Build.props          analyzers, nullable, warnings-as-errors, deterministic builds
Directory.Packages.props       central package versions
global.json  .editorconfig  .globalconfig

src/
  BrightTime.SharedKernel/              value objects, Result, TenantId, GeoPoint, IClock, AgeBand
  BrightTime.Abstractions/              IEmailSender, ISmsSender, IPushSender, IPaymentGateway,
                                        IGeocoder, ITileProvider, IObjectStore, IScreeningService,
                                        IKeyVault, IFieldCipher
  BrightTime.Infrastructure/            EF base types, outbox dispatcher, job runner, tenant
                                        interceptor, audit interceptor, encryption converters,
                                        S3 store, SMTP/Twilio/FCM/Stripe adapters, Nominatim/
                                        Pelias geocoder, MapLibre tile proxy

  BrightTime.Modules.Platform/          + .Contracts
  BrightTime.Modules.Identity/          + .Contracts
  BrightTime.Modules.Directory/         + .Contracts
  BrightTime.Modules.Opportunities/     + .Contracts
  BrightTime.Modules.Enrollment/        + .Contracts
  BrightTime.Modules.Attendance/        + .Contracts
  BrightTime.Modules.Learning/          + .Contracts
  BrightTime.Modules.Community/         + .Contracts
  BrightTime.Modules.Workforce/         + .Contracts
  BrightTime.Modules.Landscape/         + .Contracts
  BrightTime.Modules.Analytics/         + .Contracts
  BrightTime.Modules.Notifications/     + .Contracts
  BrightTime.Modules.Integrations/      + .Contracts

  BrightTime.Api/                       host: minimal-API endpoint groups per module,
                                        SignalR hubs, OpenAPI, rate limiting, problem details
  BrightTime.Jobs/                      worker host (same modules, no HTTP surface)
  BrightTime.Web/                       Blazor Web App (SSR + interactive islands)
  BrightTime.Mobile/                    MAUI Blazor Hybrid
  BrightTime.Ui.Shared/                 Razor class library: components, view models, API client
  BrightTime.Ui.Design/                 design tokens, CSS (logical properties for RTL), icons

tests/
  BrightTime.<Module>.Tests/            unit, per module
  BrightTime.IntegrationTests/          Testcontainers: real Postgres, real RLS, real migrations
  BrightTime.Architecture.Tests/        NetArchTest: module boundary enforcement
  BrightTime.E2E/                       Playwright + axe, web and mobile-web
  BrightTime.Load/                      k6 or NBomber scripts for the finder

db/
  schema/      the DDL in this plan, as reviewable SQL
  queries/     the finder's reference query
  explain/     plan harness + committed plan output
  seed/        Kettle County seed data

deploy/        Dockerfiles, compose.yml, compose.prod.yml, nginx/, systemd units
docs/          this plan, ADRs, runbooks
.github/workflows/ci.yml
```

### Why a `.Contracts` project per module

26 projects instead of 13 is the price of being able to lift a module out
later without a rewrite. A module project references only:

- `SharedKernel`, `Abstractions`, `Infrastructure`
- the `.Contracts` of modules it talks to — **never** another module's
  implementation project

`BrightTime.Architecture.Tests` fails the build on any other edge. That test is
the module boundary; prose in a README is not.

## Module map

```
                      ┌──────────────┐
                      │   Platform   │  tenancy, geography, taxonomy, media,
                      │              │  audit, outbox, jobs, retention, API clients
                      └──────┬───────┘
                             │ (everything depends on Platform.Contracts)
          ┌──────────────────┼──────────────────┐
          │                  │                  │
   ┌──────▼─────┐     ┌──────▼──────┐    ┌──────▼──────┐
   │  Identity  │     │  Directory  │    │Notifications│
   │ users,     │◄────┤ orgs, sites,│    │ templates,  │
   │ guardians, │     │ staff,      │    │ delivery,   │
   │ consent    │     │ clearances  │    │ preferences │
   └──────┬─────┘     └──────┬──────┘    └──────▲──────┘
          │                  │                  │
          │           ┌──────▼──────────┐       │
          │           │  Opportunities  │───────┤
          │           │ listings, search│       │
          │           │ collections     │       │
          │           └──────┬──────────┘       │
          │                  │                  │
   ┌──────▼──────────────────▼──────┐           │
   │          Enrollment            │───────────┤
   │ family profile, applications,  │           │
   │ seats, waivers, fees           │           │
   └──────┬─────────────────────────┘           │
          │                                     │
   ┌──────▼──────┐  ┌───────────┐  ┌──────────┐ │
   │ Attendance  │  │ Learning  │  │Community │─┤
   │ check-in,   │─►│ portfolio,│◄─│ spaces,  │ │
   │ offline sync│  │ badges,   │  │moderation│ │
   └──────┬──────┘  │ playlists │  └────┬─────┘ │
          │         └─────┬─────┘       │       │
          │               │             │       │
   ┌──────▼───────┐ ┌─────▼──────┐ ┌────▼─────┐ │
   │  Workforce   │ │ Landscape  │ │Analytics │ │
   │ mentors,     │ │ assets,    │ │ rollups, │ │
   │ training,    │ │ field data,│ │suppressed│ │
   │ gigs         │ │ surveys    │ │ views    │ │
   └──────────────┘ └─────┬──────┘ └────┬─────┘ │
                          │             │       │
                   ┌──────▼─────────────▼───────▼──┐
                   │         Integrations          │
                   │ OneRoster/Ed-Fi, Clever/      │
                   │ ClassLink, HSDS, public API,  │
                   │ widgets, iCal                 │
                   └───────────────────────────────┘
```

### Allowed dependencies, explicitly

| Module | May reference the Contracts of |
|---|---|
| Platform | — |
| Identity | Platform |
| Directory | Platform, Identity |
| Opportunities | Platform, Identity, Directory |
| Enrollment | Platform, Identity, Directory, Opportunities |
| Attendance | Platform, Identity, Directory, Opportunities, Enrollment |
| Learning | Platform, Identity, Directory, Opportunities, Enrollment, Attendance |
| Community | Platform, Identity, Directory, Opportunities, Learning |
| Workforce | Platform, Identity, Directory, Opportunities, Learning |
| Landscape | Platform, Identity, Directory, Opportunities |
| Analytics | Platform, Identity (pseudonym only) — reads **events**, not other modules' tables |
| Notifications | Platform, Identity |
| Integrations | Platform, Identity, Directory, Opportunities, Enrollment, Analytics |

Cycles are impossible by construction: the table above is a DAG and the
architecture test asserts it. Where a lower module needs to react to a higher
one, it subscribes to a contract message instead of taking a reference — e.g.
`Notifications` never references `Enrollment`; it handles
`ApplicationDecided` from the outbox.

### How modules communicate

1. **Query across modules** → call the other module's `I<Module>Queries`
   interface, implemented in that module, registered in DI. Returns contract
   DTOs. No `DbContext` crosses a module line.
2. **React to something** → the originating module writes a contract message
   to `platform.outbox_message` in the same transaction as its state change;
   the dispatcher fans it out in-process to `IMessageHandler<T>`. Same code
   path works if a module later moves out of process.
3. **Never** join across module schemas in application SQL. The one
   exception is the search projection, which is a module-owned read model
   fed by messages.

### Database boundaries

One schema per module; one `DbContext` per module with
`HasDefaultSchema(...)` and its own migrations history table
(`__ef_migrations_opportunity`, etc.), so modules migrate independently.

Cross-schema foreign keys are allowed **only** to this explicit allowlist of
stable reference aggregates:

- `platform.tenant`, `platform.neighbourhood`, `platform.school`,
  `platform.school_district`, `platform.taxonomy_term`, `platform.media_object`
- `identity.app_user`
- `directory.organisation`, `directory.site`

Everything else references by id with no FK and is validated through contracts.
Rationale: those nine tables are the ones whose integrity is worth a hard
constraint and whose lifecycle is genuinely shared; letting `community.post`
FK into `enrollment.enrollment` would weld two modules together for no gain.

`BrightTime.Architecture.Tests` includes a test that reads
`information_schema.table_constraints` from a migrated Testcontainer and fails
on any cross-schema FK outside the allowlist.

## Request pipeline

```
nginx (TLS, HSTS, brotli, static cache)
  └─► ASP.NET Core
        ├─ TenantResolutionMiddleware     host → platform.tenant_domain → TenantContext
        ├─ Authentication                 cookie (web) | JWT+refresh (mobile) | API key (public)
        ├─ RequestIdentityMiddleware      actor id, org memberships, capability flags
        ├─ RateLimiting                   per tenant, per client, stricter for anonymous + public API
        ├─ DbConnectionInterceptor        set_config('brighttime.tenant_id', …, true)
        │                                 set_config('brighttime.actor_id',  …, true)
        ├─ AuditInterceptor               one audit_event per operation touching youth records
        ├─ Authorization                  policy handlers (see 04-roles-and-permissions.md)
        └─ Endpoint
```

### Five database principals

| Role | Used by | RLS posture |
|---|---|---|
| `brighttime_owner` | owns every object | bound by `FORCE ROW LEVEL SECURITY` |
| `brighttime_migrator` | DDL and backfills | `BYPASSRLS` — the only cross-tenant principal |
| `brighttime_app` | the request pipeline | tenant policy + actor guard |
| `brighttime_auth` | **the authentication path only** | tenant policy, exempt from the actor guard, granted on the Identity stores and nothing else |
| `brighttime_jobs` | the worker host | tenant policy, no actor guard (no human actor exists) |
| `brighttime_readonly` | civic/research reads | suppressed views only; no grant on any fact table |

`brighttime_auth` exists because login, registration and password reset have
to read `identity.app_user` before any actor exists. Rather than leaving that
table out of the actor guard — which would expose every user row to an
unauthenticated request through any other code path — the authentication path
gets its own principal with a narrow grant.

`set_config(..., is_local => true)` runs inside the request's transaction, never
at session level — a pooled connection must not carry tenant context forward.
`DbConnectionInterceptor` opens an explicit transaction for every request that
touches the database, including read-only ones, precisely so `SET LOCAL` has a
scope. Reads that genuinely need no transaction use a dedicated
`IPublicReadConnection` that sets the tenant and nothing else.

## Low-bandwidth and no-JavaScript mode

This is a hard requirement, not a nicety: the finder must work on a borrowed
phone on a weak connection.

- The whole public finder is **static SSR with a GET form**. Filters are query
  string parameters; results are server-rendered HTML; pagination is links.
  No WebSocket, no WASM download, no JS required for search, listing detail,
  collections, neighbourhood pages, or badge verification.
- Interactive islands (`InteractiveServer`) are additive: the map, typeahead,
  saved-item toggles, the provider console, the moderation queue.
- `?lowbw=1` (and a cookie) drops map tiles for a static list, serves 240px
  images, and skips the font download.
- Server-rendered pages carry `Cache-Control: public, max-age=60,
  stale-while-revalidate=600` for anonymous requests; the tenant is part of the
  cache key via `Vary: Host`.

## Mobile sharing model

`BrightTime.Ui.Shared` holds components and view models and knows nothing about
render mode. Platform differences go through `IAppPlatform`
(`ILocationProvider`, `ICameraProvider`, `IQrScanner`, `IOfflineStore`,
`IPushRegistrar`), implemented once for web (mostly no-ops) and once for MAUI.

Offline store is SQLite via `sqlite-net` with SQLCipher, key in the OS keychain
/ Android Keystore. Two offline domains only — attendance rosters and field
observations — each with a client-generated `client_record_id` that makes
replay idempotent at the database level
(`ux_attendance_client_record`, `ux_field_obs_client_record`).

## Infrastructure

- `deploy/compose.yml` — postgis/postgis:17-3.4, minio, mailpit, the API, the
  jobs worker, the web app, nginx. One command to a working local stack.
- `deploy/compose.prod.yml` — same services, external managed Postgres and S3,
  nginx terminating TLS, no mailpit.
- Background jobs: Postgres-backed, `FOR UPDATE SKIP LOCKED` claim with a
  `LISTEN/NOTIFY` wakeup. See decision D2.
- Observability: OpenTelemetry traces + metrics, OTLP exporter; Serilog with a
  destructuring policy that refuses to serialise any `*Enc` property, any type
  marked `[Pii]`, and any `DateOnly` named like a birth date. A unit test
  asserts the redaction policy against a sample of every DTO.
- Health: `/health/live`, `/health/ready` (db, storage, outbox lag, job lag).

## CI pipeline

```yaml
# .github/workflows/ci.yml  (outline; written in M1)
jobs:
  build:      dotnet restore/build -warnaserror, dotnet format --verify-no-changes
  unit:       dotnet test tests/**/*.Tests
  arch:       BrightTime.Architecture.Tests (module boundaries, cross-schema FKs)
  db:         apply db/schema/*.sql to postgis:17-3.4, then EF migrations,
              then assert the two are identical (schema drift gate)
  integration: Testcontainers; includes the cross-tenant isolation matrix
  e2e:        Playwright, Chromium + WebKit, axe-core on every public route,
              plus a JS-disabled pass over the finder
  explain:    db/explain/run-explain.sh against the seeded container;
              fails if any committed plan gains a Seq Scan on listing_search
  pack:       container images, SBOM (syft), trivy scan
```

The `explain` job is the unusual one and the one I most want: a PR that
regresses the finder's plan fails CI with the plan diff attached.
