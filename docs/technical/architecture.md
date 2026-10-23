# Architecture

- [Hosts](#hosts)
- [Solution layout](#solution-layout)
- [Modules](#modules)
- [How modules talk](#how-modules-talk)
- [Tenancy](#tenancy)
- [The request pipeline](#the-request-pipeline)
- [The outbox and the job queue](#the-outbox-and-the-job-queue)
- [The finder](#the-finder)
- [The app](#the-app)
- [Localisation](#localisation)

## Hosts

```
                 ┌───────────────── nginx (TLS, host names) ─────────────────┐
                 │                                                           │
        ┌────────▼────────┐                                         ┌────────▼────────┐
        │  BrightTime.Web │  finder (static SSR), consoles,          │  BrightTime.Api │  /api/v1 for the app
        │                 │  account pages, app-link files           │                 │  and integrations
        └────────┬────────┘                                         └────────┬────────┘
                 │                 ┌──────────────────┐                      │
                 └────────────────►│ PostgreSQL 18 +  │◄─────────────────────┘
                                   │ PostGIS 3.6      │
                                   └────────▲─────────┘
                                            │
                                   ┌────────┴─────────┐
                                   │ BrightTime.Jobs  │  outbox dispatch, delivery jobs,
                                   │  (worker)        │  scheduled sweeps
                                   └──────────────────┘
```

| Host | Project | What it serves | Database logins |
|---|---|---|---|
| Web | `src/BrightTime.Web` | The public finder (works with JavaScript off, D7), listing, collection, neighbourhood and organisation pages, account pages (sign-in, reset, claim, security, terms), the provider and organiser consoles, `/.well-known` app-link files | `Finder:ConnectionString` (a member of `brighttime_app`; enters `brighttime_finder` for search, D13), `Auth:ConnectionString` (`brighttime_auth`) |
| Api | `src/BrightTime.Api` | `/api/v1`: JSON for the app, public reads, webhooks, Open Badges documents | `ConnectionStrings:App`, `ConnectionStrings:Auth` |
| Jobs | `src/BrightTime.Jobs` | No HTTP surface. Dispatches the outbox, runs delivery and scheduled jobs. Web's and Api's readiness probes report its queue lag | `ConnectionStrings:Jobs` (`brighttime_jobs`) |

The three hosts are composed from the same modules, so a module's rules hold whichever host runs
it. `BrightTime.Hosting` holds what Web and Api share: tenancy, the principal, audit, health,
localisation middleware, mail and the data-protection key ring.

A one-shot **seed** container (`src/BrightTime.Seed`) loads the Kettle County tenant
([seed.md](../seed.md)).

## Solution layout

| Path | Contents |
|---|---|
| `src/BrightTime.SharedKernel` | Ids, `Result` and `Error`, paging cursors, `IClock`, privacy attributes (`[YouthData]`, `[YouthRecord]`), text helpers |
| `src/BrightTime.Abstractions` | Interfaces for things outside the process: `IKeyVault`, payments, geocoding, storage, screening, reporting, messaging |
| `src/BrightTime.Infrastructure` | EF base context and tenancy interceptor, outbox, job runner, audit trail, field encryption, key vault, email |
| `src/BrightTime.Hosting` | Shared host plumbing (above) |
| `src/BrightTime.Localisation` | The six catalogues (`Strings/*.json`) and `Text`, with no ASP.NET dependency so the app can use them |
| `src/BrightTime.Modules.<Name>` and `.Contracts` | One module each (below) |
| `src/BrightTime.Reporting` | Report rendering (PDF with QuestPDF, Excel, CSV) and the provider report runner |
| `src/BrightTime.Ui.Shared`, `.Ui.Design`, `.Mobile` | The app: Razor screens and API client, styles, and the MAUI host |
| `src/BrightTime.Seed` | The Kettle County seed |
| `db/schema/` | The DDL, applied in file order by `db/apply-schema.sh` |
| `db/queries/`, `db/explain/` | The finder's reference query, and its measured plans |
| `deploy/` | Dockerfiles, `compose.yml`, nginx and Postgres init |
| `tests/` | One project per area (see [development.md](development.md)) |

## Modules

Each module owns one PostgreSQL schema and one EF Core `DbContext` (`SchemaName`, its own
migrations-history table name). Other modules see it only through its `.Contracts` assembly.

| Module | Schema | Owns | State |
|---|---|---|---|
| Platform | `platform` | Tenants and domains, geography (neighbourhoods, schools), taxonomy, wording overrides, the outbox and job queue, audit, data-protection keys | Built |
| Identity | `identity` | Accounts, credentials and passkeys, refresh tokens, roles, guardian links, consent, youth capabilities, terms | Built |
| Directory | `directory` | Organisations, sites, hours, memberships and invitations, clearances (status only), vetting | Built |
| Opportunities | `opportunity` | Listings, sessions and occurrences, revisions, the search projection, collections, saved items, interests, reminders | Built |
| Enrollment | `enrollment` | Family profile, shares, forms, eligibility, applications, waivers and signatures, seats and waitlists, enrolments, fees and payments | Built |
| Attendance | `attendance` | Attendance records, sync batches, check-in tokens, devices | Built |
| Notifications | `notify` | Push devices, preferences, the inbox, deliveries | Built |
| Community | `community` | Conversations, messages, broadcasts, screening holds (M3). Spaces, posts and moderation are M5 | Messaging built |
| Learning | `learning` | Learner profiles, participation, issuers and keys, badge classes and assertions (D65, D66). Playlists, goals, skills and projects to come | Partly built |
| Workforce, Landscape, Analytics, Integrations | `workforce`, `landscape`, `analytics`, `integration` | Mentors and gigs; asset mapping and surveys; suppressed rollups; external systems | Schema only |

**Dependencies.** A module may reference the contracts of the modules beneath it in this order,
and never the other way:

| Module | May reference the contracts of |
|---|---|
| Platform | none |
| Identity | Platform |
| Directory | Platform, Identity |
| Opportunities | Platform, Identity, Directory |
| Notifications | Platform, Identity |
| Enrollment | Platform, Identity, Directory, Opportunities |
| Attendance | Platform, Identity, Directory, Opportunities, Enrollment |
| Learning | Platform, Identity, Directory, Opportunities, Enrollment, Attendance |
| Community | Platform, Identity, Directory, Opportunities, Learning |

`ModuleBoundaryTests` in `BrightTime.Architecture.Tests` fails the build on any other reference,
and on a module referencing another module's implementation rather than its contracts.

## How modules talk

1. **Asking.** A module calls another's query interface (`IDirectoryQueries`,
   `IEnrolmentQueries`, `IAttendanceQueries`, …), implemented in the owning module and
   registered by the host. It returns contract records. No `DbContext` crosses a module line, and
   application SQL never joins across schemas. The one exception is the finder's search
   projection, `opportunity.listing_search`, a read model fed by messages.
2. **Reacting.** The originating module publishes a contract message in the same transaction as
   its change:

   ```csharp
   await db.PublishAsync(tenant.TenantId, new AttendanceChanged(enrollmentId), ct);
   ```

   The message lands in `platform.outbox_message`. The worker's dispatcher fans it out to one
   delivery job per handler (`IModuleMessageHandler<T>`, registered with
   `services.AddMessageHandler<TMessage, THandler>()`), so one failing handler retries alone (D27).

Messages and their handlers today:

| Message | Published by | Handled by |
|---|---|---|
| `SiteChanged`, `OrganisationStatusChanged`, `TaxonomyTermChanged` | Directory, Platform | Opportunities: re-projects affected listings into search |
| `NotificationRequested` | Any module | Notifications: the inbox and push, under the person's preferences and quiet hours. Email delivery of notifications isn't built yet |
| `AttendanceChanged` | Attendance (every write path) | Learning: rebuilds that enrolment's participation record |
| `EnrolmentAccountChanged` | Enrollment (linking a member to an account) | Learning: moves participation to or from the account |

## Tenancy

A tenant is a county or region. Every request belongs to exactly one:

- **By host name** (`Tenancy:Mode=Host`, the default). The request's host is looked up in
  `platform.tenant_domain`, the only table readable before a tenant is known. An unknown host is
  a 404, which is why `localhost` alone shows nothing in development: use `kettle.localhost`.
- **Fixed** (`Tenancy:Mode=Fixed` with `Tenancy:TenantId`), for a single-tenant install.

The tenant's own address (`SiteContext.BaseUrl`) is built from its primary domain, never from the
request's `Host` header. Every link BrightTime puts in an email, a calendar file, a passkey relying
party or a badge comes from it.

Inside the database, the tenant is a transaction-local setting (`brighttime.tenant_id`) that
every module context sets at the start of each transaction. Module code therefore reads inside
`db.InTransactionAsync(...)`. See [data-and-security.md](data-and-security.md) for the policies
that use it.

## The request pipeline

Api (`src/BrightTime.Api/Program.cs`), in order:

1. Exception handler and status-code pages; HSTS and HTTPS redirection outside Development
   (health probes are exempt).
2. `ApiTenantMiddleware`: host → tenant.
3. Rate limiting: the `public` policy (300 requests a minute per client by default) and the
   sign-in policy (10 a minute).
4. Authentication: bearer JWT.
5. `ActorMiddleware`: the signed-in person, their memberships and capabilities; sets
   `brighttime.actor_id` for the database.
6. `ApiLocaleMiddleware`: the response language (`?locale=`, then the person's choice, then
   `Accept-Language`, then the tenant default), reported in `Content-Language`.
7. Authorization: policies and requirement handlers.
8. `AuditFlushMiddleware`: the request's audit rows are written before the response.
9. Endpoints, under `/api/v1`.

The Web host runs the same sequence with cookie authentication, a remembered language cookie,
antiforgery, and Blazor's static server rendering.

## The outbox and the job queue

- **Queue.** `platform.job`, a hand-rolled Postgres queue (D2): `JobRunner` claims due jobs with
  `FOR UPDATE SKIP LOCKED`, holds a lease (`Jobs:LockDuration`), retries with backoff, and marks a
  job `dead` after its last attempt.
- **Outbox.** `OutboxDispatcher` polls `platform.outbox_message` every second and enqueues one
  delivery job per registered handler.
- **The worker's role.** `brighttime_jobs` has a permissive policy on the queue and outbox tables
  only, so it can claim across tenants there and nowhere else. Every job runs under its message's
  tenant.
- **Scheduled work** runs as jobs or background services in the worker: reminders, capability
  recomputation on birthdays and after a terms change (`TermsSweep`), Stripe events, listing
  projection, and audit partition upkeep (`AuditPartitionMaintenance`).
- **Health.** `/health/ready` reports the oldest undispatched message and the oldest due job,
  degraded past one and five minutes (`Health:*`).

## The finder

- Public search reads only `opportunity.listing_search`, the projection, through the
  `brighttime_finder` role (`BYPASSRLS`, select on that one table, D13), entered with
  `SET LOCAL ROLE`. The query is `db/queries/search-listings.sql`.
- Text search uses English and Spanish stemming, a trigram arm for the other four languages, and
  typo tolerance by strict word similarity (D6).
- The map is a server-drawn SVG of the tenant's neighbourhoods: no tiles, no coordinates (D21).
- Paging is by cursor, and every page of one search is computed at the same instant
  (`SearchQuery.AsOf`), so relevance can't drift between pages.
- Measured plans for seven scenarios at 50,000 listings are committed under `db/explain/`, and CI
  fails on a sequential scan of `listing_search`.

## The app

`BrightTime.Mobile` is a .NET MAUI Blazor Hybrid host for Android and iOS. Its screens and API
client are in `BrightTime.Ui.Shared`, which runs and is tested without a device.

- It talks to the Api host only, with 10-minute access tokens and rotating refresh tokens kept in
  the platform's secure storage.
- Passkeys use Credential Manager on Android and the system API on iOS. External sign-in runs in
  the system browser and hands the app a one-time code bound to its PKCE challenge (D41).
- The offline roster (D52) seals marks with AES-GCM under a key in the keychain, syncs them once
  through `/org/{orgId}/attendance/batch`, locks after 72 hours without contact, and wipes itself
  when the server says the device is revoked.
- Debug builds talk to `http://10.0.2.2:8000/api/v1/` as `kettle.localhost`, which is the
  compose stack seen from the Android emulator.

## Localisation

- Six locales: `en`, `es`, `zh-Hans`, `bn`, `ht`, `ar`. Arabic is right to left throughout.
- Catalogues are JSON with ICU plurals (D20) in `src/BrightTime.Localisation/Strings/`. Every key
  must exist in all six (`CatalogueTests`). Text awaiting review is listed by key prefix in each
  catalogue's `@@drafts`.
- A tenant overrides any string per locale (**Wording** in the organiser console), stored in
  `platform.resource_override` and loaded per request.
- The API returns error codes rather than prose. Clients show `error.<code>` from the catalogue
  in the person's language (see [api.md](api.md#errors)).
- Listing content is translated per listing (`listing_translation`). Machine translations are
  marked as such on the page.
