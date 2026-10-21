# Handoff

Where the work stands, for picking it up on another machine or in a new session.
Written 2026-10-18 at commit c97444d. Updated on the Windows machine through 2026-10-20 (M2's
server work: D30 to D41; M3 from D42).

## State

M1 feature slices are built; the full suite passes (2026-10-20, after D35) against PostgreSQL 18.6 + PostGIS 3.6.4.
They cover tenancy, the finder, consoles, sign-in, audit, localisation, provider management,
organiser review, the tenant-isolation matrix, the finder plan gate, the outbox, the API host
with token auth, and health endpoints.

`docker compose -f deploy/compose.yml up --build` is green (D29). It reaches the seeded site
at `http://kettle.localhost:8000` from an empty volume. Compose, CI and the Testcontainers
fixture now pin PostGIS 18-3.6.

The axe-core and e2e gate is green as well (`tests/BrightTime.E2E.Tests`, CI job `e2e`), on
Chromium and WebKit. It found and fixed a paging bug: relevance drifted with the clock between
pages.

The keyboard-only pass is done ([accessibility/keyboard.md](accessibility/keyboard.md)) and
gated by `KeyboardTests`, so every M1 acceptance item is now green. The typo-tolerance fix
is merged (`cec1ed6`).

**M1 was signed off on 2026-10-19** ([m1-report.md](m1-report.md)). Read
[plan/08-decisions.md](plan/08-decisions.md) for every decision and open question.

**M2 is under way** (mobile and parental consent). It is server-side first:
- **Slice 1, the public read API** (D30): `/api/v1/public/**` serves the finder to the app.
- **Slice 2, the consent core** (D31): child profiles, scoped grant and revoke, organiser
  attestation of signed forms, and capability recomputation with `/me/capabilities`.
- **Personal data and reminders** (D33): saved items, interests, a neighbourhood anchor, the
  personalised feed, and reminders booked as jobs.
- **Notifications** (D33): the new `notify` module holds encrypted push devices, preferences
  with one quiet window, the inbox, and delivery from `NotificationRequested`. Push goes
  through `LoggingPushSender` until the Firebase and Apple accounts exist.
- **Keys** (D32): `IKeyVault` with HKDF tenant data keys. The worker also runs the job that
  recomputes capabilities on birthdays.
- **Shared words and mail** (D34): the catalogues and `Text` are in
  `BrightTime.Localisation` (no ASP.NET, so the app can use them), account mail is in
  `BrightTime.Hosting/Mail`, and the API host sends mail. On top of that: the email-plus round
  trip (consent email, `POST /guardian-links/{id}/verify`, confirmatory email) and
  `POST /auth/register`.
- **Youth accounts and the web's side of the app's links** (D35):
  - Registration takes a birth month. Under 13 is refused, and 13 to 17 makes a youth account.
  - A guardian hands a 13-year-old their profile: the claim email, then `/account/claim` or
    `POST /auth/claim`.
  - The consent link works on the web (`/app/guardian-links/{id}/verify`, with
    `consent-link.js`).
  - The app-link files are served from `AppLinks:*`.
  - The server words pushes and the inbox (`notify.*`).
- **The app** (D36): `BrightTime.Ui.Shared` (screens, client, session; tested
  anywhere), `BrightTime.Ui.Design` (styles) and `BrightTime.Mobile` (the MAUI host).
  - The Android build runs on the emulator against the compose stack.
  - The iOS build is written but not compiled; it needs the Mac.
  - Still to do: push, the map (Q7), the schedule (M3), a run on physical devices, and the
    release identifiers.
- **Terms and passkeys** (D37, D38):
  - A young person agrees to the terms (versioned, pinned by hash) and may then sign up for
    programs themselves.
  - Passkeys work through the API, sharing the web's store and relying party.
  - In the app: a terms page, "Sign in with a passkey", and a passkeys page. Android uses
    Credential Manager (compiled, not run on a device).
- **Shared key ring, terms sweep, passwords from the app** (D39):
  - The data protection keys are in `platform.data_protection_key`, encrypted under
    `Security:Keys:data-protection`, which production must set in both hosts.
  - The worker recomputes capabilities left stale by a terms change.
  - `/auth/password/forgot` and `/auth/password/reset`, with the breached-password check.
- **Push through FCM, terms on the web** (D40, proposed): `FcmPushSender` sends once
  `Push:Fcm:*` is set, and revokes devices FCM has forgotten. The app's push registration waits
  for the Firebase project's config files. The web has `/terms`.
- **External sign-in from the app** (D41, proposed): the provider's flow runs on the web in the
  system browser and hands the app a one-time code, bound to its PKCE challenge, which
  `POST /auth/external/token` redeems. Only accounts already connected to the provider get one.

**M3 has started** (D42, proposed): M3a is registration (no money, no offline), M3b is
operations. The new `Enrollment` module holds:
- **The family profile** (D43, proposed): a household, its members, their support needs and its
  contacts, under `/api/v1/family-profile`. The owner alone reads or changes it, the youth fields
  are encrypted, and every read, change and refusal is audited.
- **Profile shares** (D44, proposed): the family shares chosen members and fields with an
  approved organisation, optionally for one listing and until a date. The organisation's
  staff read exactly that, while the share is live, under `/api/v1/org/{orgId}/family-shares`.
  `profile_share` gained `member_ids`. Next come form definitions and the listing's form.

- What waits on you: push accounts, the app identifiers, Q5 for export and delete, and Q13.

## Waiting on Sam

Every decision through D39 is accepted (D30 and D31 on 2026-10-19, D32 to D39 on 2026-10-20).
**Proposed:** D40 (push through FCM, terms on the web), D41 (external sign-in from the app), D42 (the M3 split), D43 (the family profile) and D44 (profile shares).
Counsel should also review the terms' draft text (`AccountTerms.Current`).
Q3 is answered (email plus, with a signed-form fallback), and the youth 30-minute refresh idle stays for now.

- **Counsel:** confirm D31's rule that email plus covers only scopes that don't disclose a
  child's information. Also review the draft consent texts (`ConsentDisclosures`) and the
  consent, confirmation and registration emails (each catalogue's `@@drafts`) before launch.
- **Open questions:**
  - Q7 (map tiles and geocoding) and Q13 (translations).
  - Q4 retention; 30 days is proposed for processed outbox rows.
  - Q5 (deletion versus audit), needed for M2's export and delete requests.
  - Where media is stored.
  - Firebase and Apple developer accounts, for push in M2, and the app identifiers and
    signing fingerprints for `AppLinks:*`.

## Candidate next work

These have not been asked for yet:

- D15 pending revisions (edits currently go live and are recorded as revisions);
- curation screens;
- finder speed: text 44 ms and typo 56–77 ms on the synthetic load, mostly ranking (see
  [plan/03](plan/03-indexing-and-query-plans.md));
- the app on iOS (the Mac), and on physical phones, which M2's acceptance asks for.

## Setting up a machine

1. **Install the tools.**
   - The .NET SDK named in `global.json`.
   - PostgreSQL 18 with PostGIS 3.6.
   - On Windows, run `db/apply-schema.sh` from Git Bash or WSL.
2. **Give the tests their own cluster, never a server holding real data.** The schema creates
   cluster-wide roles, and the fixtures create and drop `brighttime_*` databases and `bt_*_login`
   roles.
   - **Mac:** a cluster on 127.0.0.1:5433 with trust auth.
   - **Windows:** port 5433 there is Sam's own PostgreSQL service, so leave it alone. The
     scratch cluster is a container on 5434. Use these commands, and the same connection
     string below with `Port=5434`:
     ```bash
     docker run -d --name brighttime-scratch -p 127.0.0.1:5434:5432 -e POSTGRES_HOST_AUTH_METHOD=trust postgis/postgis:18-3.6 -c max_connections=300
     ```
     ```bash
     PATH="/c/Program Files/PostgreSQL/18/bin:$PATH" db/apply-schema.sh brighttime 127.0.0.1 5434 postgres
     ```
   - **Two checkouts, one cluster:** the other Windows session changes `brighttime`'s
     schema, so this checkout tests against its own database, `brighttime_main`. Apply the
     schema to it the same way, and set `Database=brighttime_main`.
   - **API test accounts:** each API test signs in as its own seeded admin, chosen by index,
     so parallel classes don't collide. Indices 20–29 are consent, 30–37 are email plus and
     registration, 40–44 are the personal finder, 45–49 are notifications, 50–53 are youth
     accounts, 54 and 57 are the app's contract tests, and 55, 56 and 58 are terms, passkeys and password resets. Pick a new range for a new class. The web tests' `YouthAccountPagesTests` use
     the admins at indices 40–43 of the web fixture's own database.
   - **No connection string:** without `BRIGHTTIME_TEST_CONNECTION`, the integration project
     starts its own PostGIS container through Testcontainers, and the web and API tests skip.
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
6. **Or run the whole stack in containers.** This needs nothing but Docker:
   ```bash
   docker compose -f deploy/compose.yml up --build
   ```
   - Browse `http://kettle.localhost:8000`. Mailpit is at `http://localhost:8025`.
   - After a change under `db/schema/`, run `down -v` first, because the schema is applied
     on first boot only.
   - It publishes Postgres on 127.0.0.1:5432, so stop it before using that port for
     anything else.
7. **Run the app on the Android emulator.** Visual Studio's Android SDK and JDK are enough.
   Start the compose stack (step 6), boot an emulator, then deploy with the build's `Install`
   target. A plain `adb install` of a Debug APK crashes on start, because Debug uses fast
   deployment and keeps the assemblies outside the APK.
   ```bash
   dotnet build src/BrightTime.Mobile -t:Install -f net10.0-android -p:JavaSdkDirectory="C:\Program Files\Android\openjdk\jdk-21.0.8" -p:AndroidSdkDirectory="C:\Program Files (x86)\Android\android-sdk"
   ```
   - Debug talks to `http://10.0.2.2:8000/api/v1/` as `kettle.localhost`.
   - A long checkout path breaks aapt2 (Windows path length). The repository's
     `artifacts/` paths are short enough.
8. **Install the browsers for the e2e tests.** Do this once per machine, after a build. Without
   them, `BrightTime.E2E.Tests` skips locally and fails under CI:
   ```bash
   pwsh artifacts/bin/BrightTime.E2E.Tests/debug/playwright.ps1 install chromium webkit
   ```

## Conventions that bite

- **Transactions.** Module EF contexts are read inside `InTransactionAsync`, because
  RLS tenancy is set per transaction.
- **Schema drift.** `SchemaDriftTests` fails if a mapped table differs from the DDL in
  either direction.
- **UI text.** New UI and email text goes into all six catalogues under
  `src/BrightTime.Localisation/Strings/`, or `CatalogueTests` fails. Copy that needs legal
  review goes in each catalogue's `@@drafts` as well.
- **Mail in tests.** Web and API fixtures capture mail with `CapturedMail`. It is sent after
  the response, so wait with `WaitForAsync(address, alreadySeen)`.
- **API test accounts.** API tests each sign in as their own seeded admin:
  `TokenAuthTests` uses 0 to 8, `ConsentApiTests` 20 to 29, `EmailPlusTests` 30 to 36, and
  `RegistrationTests` 37.
- **Static SSR forms.** A form's handler runs only if that form renders during its own POST.
- **Draining queues in tests.** Tests that publish module messages drain with
  `TestHost.DrainAsync`, not `JobRunner.DrainAsync` alone.
- **Shared test databases.** Web and API tests share one seeded database per project. A
  test that changes an account must use a seed account that no other test uses.
- **A new public page** belongs in `PublicRoutes`, or neither the axe nor the keyboard gate sees it.
- **Links to a spot on the same page** use `SamePageLink`, never a bare `href="#id"`, which
  `<base href="/">` sends to the home page.
- **E2E failures leave a trace.** Each one leaves `artifacts/e2e-traces/<test>.zip` (CI
  uploads them). Open it with `playwright.ps1 show-trace <zip>`. Browser steps go through
  `SiteFixture.RunAsync`.
- **One suite per database server at a time.** The fixtures drop and recreate fixed names
  (`brighttime_e2etest`, `brighttime_consoletest`, `brighttime_apitest`, …), so two checkouts
  testing against the same server at once break each other.
- **The finder's instant.** `SearchQueryBuilder.Build` needs `SearchQuery.AsOf`, or a cursor
  that carries one. Finders take it from `IClock` on the first page. Only clocks may read
  the system time (an architecture test).
- **Line endings on Windows.** `dotnet format --verify-no-changes` reports every file in a
  CRLF checkout. To check as CI does, run it in a clone made with `core.autocrlf=false`.
- **Commits.** Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  `.claude/` is never committed.
