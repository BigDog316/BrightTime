# Handoff

Where the work stands, for picking it up on another machine or in a new session.
User guides and technical documentation for the application as built are in
[README.md](README.md).
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
- **Push through FCM, terms on the web** (D40): `FcmPushSender` sends once
  `Push:Fcm:*` is set, and revokes devices FCM has forgotten. The app's push registration waits
  for the Firebase project's config files. The web has `/terms`.
- **External sign-in from the app** (D41): the provider's flow runs on the web in the
  system browser and hands the app a one-time code, bound to its PKCE challenge, which
  `POST /auth/external/token` redeems. Only accounts already connected to the provider get one.

**M3 has started** (D42): M3a is registration (no money, no offline), M3b is
operations. The new `Enrollment` module holds:
- **The family profile** (D43): a household, its members, their support needs and its
  contacts, under `/api/v1/family-profile`. The owner alone reads or changes it, the youth fields
  are encrypted, and every read, change and refusal is audited.
- **Profile shares** (D44): the family shares chosen members and fields with an
  approved organisation, optionally for one listing and until a date. The organisation's
  staff read exactly that, while the share is live, under `/api/v1/org/{orgId}/family-shares`.
  `profile_share` gained `member_ids`.
- **Registration forms** (D45): an organisation's admins draft typed questions, then
  publish (fixed from then on), revise into new versions, and attach a published form to a
  listing. Families read it in their language at `/api/v1/public/listings/{slug}/form`.
- **Applications and eligibility** (D46):
  - Age, grade and neighbourhood rules, with a provider dry run and a public view.
  - Families draft against the listing's form, then submit once. Submitting checks
    completeness and the hard rules, and shares the applicant as the family chose.
  - Sensitive answers are sealed.
  - `waiver_signature`'s references now restrict deletes.
- **Waivers** (D47):
  - Versioned like forms, and attached to listings as required or optional.
  - A parent signs a child's waiver for the exact text, by its hash. The signature is
    append-only evidence.
  - Submitting needs every required waiver signed.
  - Someone 18 or over signs for themselves, which waits on account linking.
- **Decisions, seats and waitlists** (D48):
  - Staff review, accept, waitlist and decline, singly or in bulk.
  - Seats are taken under a `FOR UPDATE` row lock, so the last seat goes to exactly one
    applicant.
  - Freed seats are offered to the front of the waitlist, and the family takes or declines
    them.
  - The organisation sees who applied only through the family's live share.
  - New permissions: `applications.view` and `applications.decide`.

**M3a is built** (2026-10-21): family profile, shares, forms, eligibility, applications,
waivers, decisions. M3b (attendance with offline sync, messaging, payments, reports) is next.
Rebuild the compose volume first: the schema and the role permissions changed.

**M3b has started** with attendance (D49):
- A new Attendance module: rosters per meeting, records, idempotent offline batch sync, and
  corrections that supersede.
- A family's own view at `/me/attendance`.
- Staff see display names only through the family's share. Every read and write is audited.
- **Check-in by QR** (D50): families show a two-minute single-use badge that staff
  scan, or scan a kiosk's one-minute code. Codes are random, hashed, and name nobody.
- **Devices** (D51): phones and kiosks register to sync. Sync refuses unknown or
  revoked devices. Revoking asks for a wipe, which the device learns on contact without
  signing in.
- **The app's offline roster** (D52): staff take today's roster onto the phone and mark
  it without a signal. Marks are sealed (AES-GCM, with the key in the keychain), synced once,
  and wiped when the server says revoked. The roster locks after 72 hours without contact. The
  Android build compiles; it hasn't been run on a phone yet.
- **Messaging** (D53): staff open conversations with families. A young person is never
  in one with a single adult: an admin observes, or it is refused, and the database decides who
  is a minor. Every route to a pair is tested at the database. Contact-exchange attempts to a
  minor are held. Pushes say only who wrote.
- **Broadcasts** (D54): one announcement to every family with a child enrolled in a
  listing. Families don't reply and never see each other. Young people are included only with
  their consent and an admin observing.
- **Fees and payments** (D55): listings' fees with sliding-scale tiers. Families pay the
  tier their household fits, and no income is kept. Payments go behind `IPaymentGateway`, with a
  manual gateway that admins mark received, or waive with a reason.
- **Stripe** (D56): when `Payments:Stripe:SecretKey` and `:WebhookSecret` are set, payments go to
  Stripe, and the card is entered in Stripe's element. Its webhooks are believed only by signature
  and applied by the jobs host, which now runs the Enrollment context, so a status only moves
  forward. Without both secrets the gateway stays manual.
- **Reports** (D57): an organisation's roster, attendance and participation, as PDF, Excel or
  CSV. They are made when asked for and never stored. Names appear only where the family shares
  them, and participation hides small counts. Each run is recorded and audited.
- **The provider console** (D58) now runs Enrollment and Attendance on the same modules as the
  API. Its first M3 screen is Reports, which downloads a report straight away.
- **Applications in the console** (D59): each activity's queue, with names only as families
  share them. Staff set places, decide singly or in bulk (the page names any left as they were,
  and why), and offer freed places to the waitlist.
- **Attendance in the console** (D60): a week of meetings, each meeting's roster, marks saved for
  the whole roster at once, corrections with a reason, and what was corrected.
- **Fees and payments in the console** (D61): owners and admins set fees and price levels a form at
  a time, and waive a payment with a reason or mark one paid in person received. It also fixed a
  D55 bug that dropped a fee's default flag when the fee was changed.
- **The form builder in the console** (D62): forms started with their first question, questions
  added, edited, moved and translated (labels) a form at a time, and versions published, attached
  to listings, revised and retired. Keys come from the English.
- **Eligibility and waivers in the console** (D63): each activity's rules written out in words,
  added, removed, and tried against a made-up applicant without saving anything. Waivers are
  written, published (then locked), asked for on listings as required or optional, revised and
  retired.
- **Messaging in the console** (D64): staff's conversations with families, opened from an
  application or a roster row, answered on the thread, and announcements to an activity's families.
  Every safety rule is the module's. This finishes D58's list: the provider console now covers
  M3's provider screens.
- **M4 has started** (D65): a new Learning module. Families link a young member to their own
  account, attendance becomes that young person's participation on the worker, and the portfolio
  is theirs: private until they show it, records they can hide, and a page for anyone with the
  link that never names them beyond the display name they chose.
- **Open Badges** (D66): an organisation defines and publishes badges and awards them to young
  people enrolled with it. Each is an OB 3.0 credential signed RS256 under the organisation's own
  `did:web` DID, and an OB 2.0 hosted assertion; revoked with a reason that verifiers see at
  once. Keys live in the vault: production must set `Security:SigningKeys:default` (an RSA PEM) or
  one per organisation in the API host; Development makes one up. The console screens for
  badges, and a check with 1EdTech's validator, are still to do.
- **Playlists** (D67): an organisation's ordered or unordered steps (attend a listing, an
  activity elsewhere, a reflection, a check-in with staff), some reviewed by staff, with a badge
  at the end. Completing one awards the badge, and a listing's eligibility can now ask for a badge
  or a completed playlist, so M4's acceptance path runs end to end. Enrollment asks Learning
  through `IAchievementQueries`, which the Web host now registers too. No console or app screens
  for playlists yet.
- **Portfolio exports** (D68): the young person's printable portfolio (PDF), their badges as OB
  3.0 credentials, and a CLR 2.0 that the county's site signs under its own `did:web` DID, nesting
  the badges' JWTs. Made when asked, never stored.
- **Goals, skills and projects** (D69): private goals; skills the young person lists and skills an
  organisation endorses through an enrolment; projects about an activity they took part in,
  approved by that organisation's staff before they can be shown. The shown portfolio carries
  published projects and skills (through `learning.public_skills`), and the PDF export carries both.
- **CI and the module order** (D70): a new CI job runs the Web and Api suites against a PostGIS
  service, and `CrossSchemaForeignKeyTests` holds cross-schema keys to the module order. One
  wrong-way key, on the unused `notify.broadcast`, was dropped.
- **The household in the app** (D71, proposed): a family keeps its household in the app: its
  details, its people (with a young member's support needs and the account they are linked to),
  its contacts, and who it is shared with, with Stop sharing. No server changes: the screens use
  the family-profile routes D43 and D44 built. Applying, waivers, paying and messages come next.
- **The M4 report** ([m4-report.md](m4-report.md)), not yet signed off: two of three acceptance
  criteria are met; the third needs a validator check on a public host. Evidence uploads (media
  storage) and quizzes remain of M4's scope.
- **M3 was signed off on 2026-10-22** ([m3-report.md](m3-report.md)). Every acceptance criterion is
  met. The funder packet (Q10) and SMS (Q9) carry forward, waiting on answers. Rebuild the compose volume: the schema and the role permissions
  changed.

- What waits on you: push accounts, the app identifiers, Q5 for export and delete, and Q13.

## Waiting on Sam

Every decision through D70 is accepted (D30 and D31 on 2026-10-19, D32 to D46 on 2026-10-20, D47 to D57 on 2026-10-21, D58 to D66 on 2026-10-22, D67 to D70 on 2026-10-25).
D71 (the household screens in the app) is proposed.
D57 leaves two things open: QuestPDF's licence (Community is free only under US$1M revenue) and fonts for names in Bengali, Chinese and Arabic.
D56 leaves two things open: what to do when a waived fee is paid anyway (it is logged now, not changed), and whether organisations must be paid out directly (Stripe Connect).
D46 reports that deleting an organisation, listing or community report would fail against append-only tables; that needs a decision before anything deletes them.
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
- **Random order.** xUnit v3 shuffles tests, within a class too. Two tests that write to the same
  listing or organisation must not count each other's rows: give each its own, or assert only on
  what the test made (D70).
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
