# M4 report: learner portfolio, Open Badges, playlists and exports

The end-of-milestone report that [plan/09](plan/09-milestones.md) promises.
Written 2026-10-25 at `5f65730`. M4 ran from M3's sign-off on 2026-10-22 to 2026-10-25, as four
slices: the portfolio (D65), Open Badges (D66), playlists (D67) and exports (D68), then goals,
skills and projects (D69), added after the report was first written. The user guides, technical
documentation and their PDF editions were written alongside.

**Not yet signed off.** Two of the three acceptance criteria are met and tested. The third, a
third-party validator verifying a credential, needs a check by hand on a host the internet can
reach, and there is none yet. One item of plan/09's M4 scope isn't built: evidence uploads on
playlist steps, which wait on media storage. The sections below give the detail.

## Acceptance

| Criterion | Status | Kept true by |
|---|---|---|
| A third-party OB 3.0 validator verifies an issued credential | **Not yet checked.** Our own checks pass: a credential's RS256 signature verifies against the key in the issuer's DID document; it has the OB 3.0 contexts, types, schema and status; the hashed recipient matches. 1EdTech's validator must resolve the `did:web` DID over HTTPS on the public internet, which `kettle.localhost` can't offer | `BadgeApiTests` (our checks); a hand check on a public host (to do) |
| A revoked badge reports revoked at the public endpoint | Met. After revocation `/public/badges/{slug}` reports `revoked` with the reason, the OB 2.0 assertion answers `410 Gone`, and the issuer's revocation list carries it at once (`no-store`) | `BadgeApiTests` |
| Completing a playlist issues the badge and unlocks a gated listing | Met. A young person's application to a listing gated on a badge is refused (`application.ineligible`). They complete the playlist, one step sent back by staff and done again, and are awarded the badge, signed by the organisation. The same application then submits, and the badge also opens a second playlist that asks for it | `PlaylistApiTests` |

To close the first criterion: deploy to a host with a real domain and HTTPS, award a badge there,
and run one credential through 1EdTech's OB 3.0 validator. While there, run an exported CLR
through its CLR 2.0 validator, and an OB 2.0 hosted assertion through an OB 2.0 validator.

## What was built

**The portfolio** (D65), a new Learning module:
- **Linking accounts.** A family links a household member to their own account, or to a child's
  account they are a verified guardian of. The member's enrolments follow.
- **Participation from attendance.** Each attendance change publishes a message; the worker
  rebuilds that enrolment's participation record whole, so corrections and redeliveries leave one
  row.
- **The young person's own portfolio.** It is private until they show it, to anyone with the link
  or to everyone. They can hide any record. Showing it needs the showcase capability (nobody under
  13), a link name, and a display name without their family name.
- **The shown page.** It is read through a security-definer function that returns only the
  unhidden records of a shown portfolio.

**Open Badges** (D66):
- **Defining and awarding.** Organisations define, publish and retire badges, award them to young
  people enrolled with them, and revoke them with a reason.
- **Two formats.** Each award is an OB 3.0 credential, signed RS256 as a VC-JWT, and an OB 2.0
  hosted assertion.
- **Issuer identity.** Each organisation issues under its own `did:web` DID. Its keys are held in
  the vault and published in its DID document. Rotation retires the old key, and the DID document
  keeps it, so earlier credentials still verify.
- **Privacy.** The recipient appears only as a salted hash of an account identifier.
- **Public documents.** The DID document, the revocation list, the Achievement, the OB 2.0
  BadgeClass and Issuer, a generated image, and the verification page are anonymous and
  rate-limited.

**Playlists** (D67):
- **Authoring.** An organisation's ordered or unordered steps: attend a listing, an activity
  elsewhere, a reflection, or a check-in with staff. Published playlists are locked, and archived
  ones take nobody new.
- **Progress.** Young people start a playlist if their account may sign up, and if they hold any
  badge it asks for. They mark steps done in order. Staff approve submitted steps or send them back
  with a note, seeing the young person only by portfolio display name.
- **Completion** awards the playlist's badge in the same transaction.
- **Gating a listing.** Eligibility gains `badge_required` and `playlist_completed`. Enrollment asks
  Learning through `IAchievementQueries`, an interface in Enrollment's own contracts that Learning
  implements, so the module order holds.

**Exports** (D68):
- `POST /me/portfolio/export` returns the file at once and stores nothing.
- `pdf` is a printable portfolio in the young person's language.
- `json_ob3` gives their badges as OB 3.0 credentials with their JWTs.
- `clr2` is a CLR 2.0 `ClrCredential`. The county's site signs it under its own DID. It nests the
  badges' JWTs and records activities as co-curricular achievements.

**Goals, skills and projects** (D69):
- **Goals** are private: never shown, never exported.
- **Skills** come from the county's skill vocabulary. Each is one the young person lists at a
  level, or one an organisation endorses through their enrolment. They can remove either.
- **Projects** concern an activity they took part in. That activity's organisation approves a
  project, or sends it back with a note, before the young person can show it. Changing a shown
  project takes it back to a private draft.
- A shown portfolio carries published projects, and skills marked endorsed or not; the PDF export
  carries both.

**Documentation:**
- User guides for families, young people, organisations and network organisers.
- A technical reference: architecture, data and security, every API route, configuration,
  operations and development.
- PDF editions of both, rebuilt by `tools/docs-pdf/build.py`.

## How to run it

```bash
docker compose -f deploy/compose.yml down -v
```

```bash
docker compose -f deploy/compose.yml up --build
```

Rebuild the volume: the schema and the role permissions changed in M4. Everything M4 added is in
the API; no console or app screens yet.
- The API's routes are listed in [technical/api.md](technical/api.md), and in Development it serves
  its OpenAPI document at `http://kettle.localhost:8000/api/v1/openapi.json`.
- The flows above are scripted end to end in `PortfolioApiTests`, `BadgeApiTests`,
  `PlaylistApiTests`, `PortfolioExportApiTests` and `LearnerRecordApiTests`.
- Outside Development, set `Security:SigningKeys:default` (an RSA PEM) in the API before anyone
  awards a badge or exports a CLR.

## Test results

1231 tests, all passing against PostgreSQL 18.6 + PostGIS 3.6.4, up from 1225 at M3's close
(1224 in its report, plus the acceptance test it added). M4's five API test classes each run a
whole flow end to end, as the real login, with row-level security and the actor guard in force.

| Project | Methods | | Project | Methods |
|---|---:|---|---|---:|
| Web | 193 | | Infrastructure | 39 |
| Opportunities module | 182 | | SharedKernel | 35 |
| Integration | 107 | | Ui.Shared | 23 |
| API | 101 | | Localisation | 20 |
| Directory module | 40 | | Seed | 18 |
| Identity module | 40 | | E2E (Playwright, axe) | 11 |
| Architecture | 10 | | Platform module | 5 |

Found by tests and runs, not by reading:
- **A shown portfolio was never found** (D65). The definer function's `search_path` doesn't include
  the schema that defines `citext`, so comparing the link name failed. It compares as text now.

Found while writing the documentation, and since fixed (D70):
- **CI skipped the Web and Api suites.** The build job sets no `BRIGHTTIME_TEST_CONNECTION`. A new
  job runs both suites against a PostGIS service.
- **Nothing enforced the cross-schema foreign key rule.** `CrossSchemaForeignKeyTests` now holds
  every cross-schema key to a shared reference table or to a module the owner may reference. It
  found one key pointing up the order, `notify.broadcast` → `opportunity.listing` on an unused
  table, which was dropped.

## Schema changes during M4

The DDL stayed the source of truth. M4 changed 28 lines in two files:
- **`learning.issuer_key`** (D66): `ck_issuer_key_alg` allows `RS256`, the algorithm OB 3.0's JWT
  proofs require issuers to support.
- **`learning.badge_assertion`** (D66):
  - `enrollment_id`, so an award names its recipient to the organisation only as the family's
    share does;
  - the recipient hash's comment corrected to `sha256(identifier || salt)`, as both OB versions
    specify.
- **`learning.public_participation(tenant, slug)`** (D65): a security-definer function, owned by
  the migrator and executable by the app role, returning only the unhidden records of a shown
  portfolio.

- **`learning.project`** (D69): `review_note`, what staff asked to change when they sent a project
  back.
- **`learning.public_skills(tenant, slug)`** (D69): a second definer function, returning a shown
  portfolio's skills (term, level, endorsed or not).

Playlists, unlock rules and exports (D67, D68) used tables the approved schema already had.

## Known gaps

M4 scope not built:
- **Evidence uploads on playlist steps**, and **images on projects**, which wait on media storage;
  and **quizzes**, which have no design yet. Those step kinds are refused.

Left for later, by decision:
- **Screens.** No console or app screens for portfolios, badges, playlists, goals, skills or projects. The console's
  eligibility page shows badge and playlist rules but doesn't add them.
- **A guardian's view** of a child's portfolio, and exports on a child's behalf.
- **Completing an *attend a listing* step automatically** when attendance arrives; today the young
  person marks it, and the server checks the participation record.
- **Mentors as reviewers**, which waits for M6.
- **Issuer key management routes**: rotation happens in the vault, and marking a key compromised is
  a database change.
- **The `zip` export.**

## Decisions needed

| | Question | Needed by |
|---|---|---|
| — | A host the internet can reach (domain, HTTPS), for the validator check that closes M4's first criterion | Signing off M4 |
| — | Where media and generated files are stored | Evidence uploads, projects, listing images |
| Q4, Q5 | Retention, including `portfolio_export` rows; deletion versus audit for portfolios and badges | Before production data |
| Q13 | Who reviews the translations (M4's new strings are machine drafts in `@@drafts`) | Launch |
| D56, D57 | Carried from M3: a waived fee paid anyway, Stripe Connect; QuestPDF's licence and fonts | Live payments; reports and PDF exports in production |
