# M1 report: foundation and the opportunity finder

The end-of-milestone report that [plan/09](plan/09-milestones.md) promises.
Written 2026-10-19 at `cec1ed6`. M1 ran from the plan's approval on 2026-10-09.

**Signed off by Sam on 2026-10-19: M1 is closed.** Every acceptance criterion is met and
gated in CI. The open items below are decisions or later-milestone work, not M1 defects.

## Acceptance

| Criterion | Status | Kept true by |
|---|---|---|
| `docker compose up` → browse, search and filter 420 listings in six locales, with JavaScript disabled | Met. An empty volume reaches the seeded site in about 30 seconds. All 396 published listings page through; the other 24 are drafts or in review, by design. | Run by hand, and `FinderWithoutJavaScriptTests` (both engines) |
| Cross-tenant matrix green across every module and role | Met | `TenantIsolationMatrixTests`, `ConsoleRouteMatrixTests` |
| Committed `EXPLAIN` plans for all seven scenarios, no `Seq Scan` on `listing_search`, estimates within 10× | Met, at 50,000 synthetic listings | `FinderPlanTests` (CI job `explain`); plans in `db/explain/measured/shipped/` |
| axe-core clean on every public route; keyboard-only pass documented | Met. 18 routes in Chromium and WebKit, plus six locales; [keyboard pass](accessibility/keyboard.md) | `AccessibilityTests`, `KeyboardTests` (CI job `e2e`) |
| A provider goes application → approved → drafted → submitted → approved → published → in search | Met | `ConsoleTests`, `ProviderManagementTests` |

CI is green on all four jobs at `7408cb4` (build and tests, schema, plans, e2e), and running
for the merge at `cec1ed6`.

## What was built

**Platform**
- Solution skeleton: .NET 10, central packages, analyzers at `latest-all`, warnings as
  errors, architecture tests on the module boundaries.
- **Tenancy by host name** (D18). Row-level security is live on every module table, and the
  finder reads outside it through a scoped role (D13).
- **Identity:** cookie sign-in for the consoles; password, passkey and external OIDC sign-in
  (D17); step-up for sensitive actions; development sign-in (D14).
- **API host:** 10-minute JWTs and rotating, family-chained refresh tokens (D28), sharing
  tenancy, principal and audit with the web host through `BrightTime.Hosting`.
- **Audit log:** one row per operation (D11, D19), partitioned by month and sealed.
- **Outbox and job runner:** messages published in the caller's transaction, one delivery job
  per handler (D27), on a hand-rolled Postgres queue (D2).
- **Health:** `/health/live` and `/health/ready` in both hosts, reporting queue lag without a
  tenant.
- **Localisation:** six locales from JSON catalogues with ICU plurals (D20), Arabic right to
  left, per-tenant wording overrides.

**The finder**
- Static server rendering that works with JavaScript off (D7): search, every filter, list and
  map views, listing pages, collections, neighbourhood and organisation pages.
- `.ics` calendar files, and saved items kept on the device (D22).
- **Search:** English and Spanish stemming, a trigram arm for the other four languages (D6),
  and typo tolerance by strict word similarity.
- **Map:** a server-drawn SVG of the tenant's neighbourhoods (D21), with no tiles and no
  coordinates.

**Consoles**
- **Provider:** apply, sites, staff, a listing editor with schedules and occurrences, drafts,
  seasonal cloning, and CSV and HSDS import with a dry run.
- **Organiser:** the vetting checklist (D25), listing review with a revision diff (D15),
  taxonomy (D26), tenant wording and the audit explorer.

**Data and operations**
- Kettle County seed: 60 organisations, 85 sites and 420 listings across four kinds, with
  six-locale content on a subset.
- **Local stack:** `deploy/compose.yml` (D29) runs PostGIS 18-3.6, a one-shot seed, mailpit,
  the API, the jobs worker, the web app and nginx.
- **CI:** build, format, all tests, schema apply and RLS checks, the finder plan gate, and
  the accessibility and e2e gate.

## How to run it

```bash
docker compose -f deploy/compose.yml up --build
```

Then browse `http://kettle.localhost:8000`. Sign in at `/dev/sign-in`; mail goes to mailpit at
`http://localhost:8025`. Running the tests and the app outside containers is in
[HANDOFF.md](HANDOFF.md).

## Test results

1031 tests, all passing against PostgreSQL 18.6 + PostGIS 3.6.4:

| Project | Tests | | Project | Tests |
|---|---:|---|---|---:|
| Web | 269 | | Directory module | 59 |
| Opportunities module | 230 | | Infrastructure | 58 |
| Integration | 127 | | Seed | 18 |
| SharedKernel | 87 | | API | 17 |
| E2E (Playwright, axe) | 86 | | Architecture | 10 |
| Identity module | 61 | | Platform module | 9 |

The gates found real bugs that unit tests didn't:
- **Paging drift:** relevance drifted with the clock between pages, so paging skipped
  listings.
- **Broken in-page links:** `<base href="/">` sent the skip link and every error link to the
  home page.
- **Dead typo search:** typo tolerance matched nothing.
- **Schema drift:** the first drift check found 75 differences between EF and the DDL,
  including a real runtime bug.

**Finder timings at 50,000 synthetic listings, single runs:**
- Most scenarios: under 25 ms (browse 0.2, facets plus distance 22, date window 5.6,
  everything 4.4, map counts 7.4).
- Text search: 44 ms. Typo search: 56–77 ms.
- The synthetic load is pessimistic for text: a tenth of the table matches each word, and
  ranking that many rows is most of the cost.
- Kettle County, at 420 listings, wasn't timed separately.

## Schema changes during M1

The DDL in `db/schema/` stayed the source of truth (Q12): it has 15 files, 178 tables, 231 RLS
policies and 442 indexes, counting those behind constraints. Against the plan's version,
M1 changed 234 lines in five files:
- **New table:** `opportunity.listing_revision`, append-only, with a trigger that refuses
  updates (D15).
- **Columns:** `listing.publish_at` (scheduling); `listing_search.is_primary`, so a
  query with no location reads one row per listing; `refresh_token.auth_method` (D28).
- **Indexes:** `ux_listing_source_record` (import idempotency), `ix_listing_session_expand`
  (occurrence expansion), `ux_listing_search_primary` and `ix_refresh_token_user`.
- **Roles and grants:** `brighttime_finder` with select on `listing_search` only, and a
  migration that fails if it ever gains more (D13). The public schema is locked down to
  usage.
- **Queue access:** RLS policies `job_worker_claims` and `outbox_dispatch`, so the worker
  can claim across tenants on the queue tables and nowhere else.
- **Functions:** `platform.queue_health()` (security definer, counts only), and the audit
  partition functions `seal_audit_partition`, `ensure_audit_partitions` and
  `audit_default_partition_rows`, with execute granted only to the jobs role.

## Known gaps

As planned for M1: no mobile app, registration, attendance, badges or community, and no
dashboards beyond provider listing counts.

Found or left during M1:
- **Translations:** the five non-English catalogues are machine drafts (Q13), and
  native-speaker review should gate launch.
- **Geocoding:** new sites are placed at their neighbourhood's centre until geocoding is
  decided (Q7). Distance search skips them.
- **Media storage:** undecided, so the compose stack has no object storage.
- **Edits to live listings** go live at once and are reviewed after (D15). Pending revisions
  are a later slice.
- **API:** token sign-in and `/me` only. Public read endpoints, and passkey or external
  sign-in through the API, are still to build; the mobile app in M2 needs them.
- **Accessibility:** no screen-reader audit yet. The keyboard pass doesn't cover the
  interactive islands or 400% zoom.
- **Typo search index estimate:** the fuzzy arm's GIN index reports about twice the
  candidates it returns, because trigram counts can't see word boundaries. The plan gate
  judges the heap scan above it, which is within 30%.
- **One e2e flake, now traced:** a Chromium test failed once in 85 runs after its wait was
  fixed, and its message wasn't captured. Any future failure leaves a Playwright trace.

## Decisions needed

None block closing M1. These shape M2 and launch:

| | Question | Needed by |
|---|---|---|
| Q3 | Verifiable parental consent method. The recommendation is email-plus, with organiser-attested signed forms in person. | M2: the consent flow |
| — | Whether a youth's 30-minute refresh idle (D28) suits their own phone, or passkeys or device binding replace it | M2 |
| Q4 | Retention periods, including 30 days for processed outbox rows | Before production data |
| Q5 | Deletion versus audit. The proposal is to erase the record and pseudonymise its audit entries. | M2: subject deletion |
| Q7 | Geocoding: self-hosted Nominatim (proposed) or a hosted geocoder; and map tiles | Before distance search on new sites |
| Q13 | Who reviews the translations | Launch |
| — | Where media is stored | When listings get images |
| Q1, Q2, Q6, Q8, Q9, Q10 | County, privacy pack, payments, screening, SMS and reporting consumers | Their milestones (M3 onward); Q9 is weeks of lead time if SMS ships in M3 |
