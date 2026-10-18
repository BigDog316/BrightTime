# Handoff

Where the work stands, for picking it up on another machine or in a new session.
Written 2026-10-18 at commit c97444d.

## State

M1 feature slices are built, with 932 tests passing against PostgreSQL 18.6 + PostGIS 3.6.4.
They cover tenancy, the finder, consoles, sign-in, audit, localisation, provider management,
organiser review, the tenant-isolation matrix, the finder plan gate, the outbox, the API host
with token auth, and health endpoints.

Read [plan/08-decisions.md](plan/08-decisions.md) for every decision and open question, then
[plan/09-milestones.md](plan/09-milestones.md) for M1 acceptance status. The status note in
[plan/README.md](plan/README.md) predates the code.

## Waiting on Sam

- **Accept or reject D27** (outbox: publish in the caller's transaction, one job per handler)
  **and D28** (API tokens: 10-minute JWTs, rotating family-chained refresh tokens).
- **Approve the Playwright and axe-core download** for the accessibility and e2e gate.
- **Decide on Docker (Q11)** for the `docker compose up` gate. `deploy/compose.yml` and
  `.github/workflows/ci.yml` still pin PostGIS 17-3.4, and only 18.6 / 3.6.4 has been verified.
- **Check GitHub Actions** for the last few pushes. CI results have not been seen.
- **Open questions:**
  - Q3, Q7 (map tiles) and Q13 (translations).
  - Q4 retention; 30 days is proposed for processed outbox rows.
  - Whether the youth 30-minute refresh idle suits mobile.
  - Where media is stored.

## Candidate next work

These have not been asked for yet:

- public API read endpoints;
- passkey and external sign-in through the API;
- D15 pending revisions (edits currently go live and are recorded as revisions);
- curation screens;
- a keyboard-only pass;
- finder speed (facets plus distance is about 27 ms; see [plan/03](plan/03-indexing-and-query-plans.md));
- the compose fixes.

## Setting up a machine

1. **Install the tools.**
   - The .NET SDK named in `global.json`.
   - PostgreSQL 18 with PostGIS 3.6.
   - On Windows, run `db/apply-schema.sh` from Git Bash or WSL.
2. **Give the tests their own cluster, never a server holding real data.** The schema creates
   cluster-wide roles, and the fixtures create and drop `brighttime_*` databases and `bt_*_login`
   roles. The Mac setup used a cluster on 127.0.0.1:5433 with trust auth.
3. **Apply the schema:**
   ```bash
   db/apply-schema.sh brighttime 127.0.0.1 5433 postgres
   ```
   Re-apply it after any change under `db/schema/`, or the new schema tests fail with 42883.
4. **Run the tests.** Set the connection inline on every run. Without it, every database test
   skips silently and the run still passes:
   ```bash
   BRIGHTTIME_TEST_CONNECTION="Host=127.0.0.1;Port=5433;Database=brighttime;Username=postgres;Trust Server Certificate=true" dotnet test
   ```
5. **Run the web app** (`.claude/launch.json` is not committed, so recreate it).
   - Start `src/BrightTime.Web` with these settings:
     - `Finder:Mode=Sql`.
     - `Finder:ConnectionString` and `Auth:ConnectionString` pointing at `brighttime_consoletest`
       as `bt_web_login` and `bt_auth_login`. The web tests seed that database, and their
       fixture holds the test-only password.
     - `Tenancy:PublicScheme=http` and `Tenancy:PublicPort=5175`.
     - `Auth:DevelopmentSignIn=true`.
   - Browse `http://kettle.localhost:5175`; plain `localhost` returns 404. Sign in at `/dev/sign-in`.

## Conventions that bite

- **Transactions.** Module EF contexts are read inside `InTransactionAsync`, because
  RLS tenancy is set per transaction.
- **Schema drift.** `SchemaDriftTests` fails if a mapped table differs from the DDL in
  either direction.
- **UI text.** New UI text goes into all six catalogues under
  `src/BrightTime.Web/Localisation/Strings/`, or `CatalogueTests` fails.
- **Static SSR forms.** A form's handler runs only if that form renders during its own POST.
- **Draining queues in tests.** Tests that publish module messages drain with
  `TestHost.DrainAsync`, not `JobRunner.DrainAsync` alone.
- **Shared test databases.** Web and API tests share one seeded database per project. A
  test that changes an account must use a seed account that no other test uses.
- **Commits.** Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  `.claude/` is never committed.
