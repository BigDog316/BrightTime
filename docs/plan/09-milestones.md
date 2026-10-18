# 09 — Milestones

Each milestone is runnable, tested and demonstrable against the Kettle County
seed before the next starts. Each ends with the report you asked for: what was
built, how to run it, test results, schema changes, known gaps, decisions
needed.

Estimates assume one engineer working steadily. They are ranges because the
unknowns are real, and I would rather hand you a range than a number I will
revise.

## M1 — Foundation + the opportunity finder (4–6 weeks)

**Blocked on:** Q11 (Docker), Q12 (migration strategy), D6/D7/D8 confirmation.

- Solution skeleton, `Directory.Build.props`, central packages, `global.json`,
  analyzers, warnings-as-errors.
- `deploy/compose.yml`: postgis:17-3.4, minio, mailpit, nominatim, api, jobs,
  web, nginx. `docker compose up` to a seeded, working site.
- `db/schema/*.sql` applied; EF contexts mapped; CI drift gate both ways.
- Tenancy: host resolution, `TenantContext`, connection interceptor, RLS live
  and verified by the cross-tenant matrix test.
- Identity: ASP.NET Core Identity on `identity.app_user`, cookie + JWT,
  passkeys, external OIDC, role/permission seeding, policy handlers.
- Audit log: interceptor, partitioning, the startup test that fails boot on a
  youth-data endpoint missing `[YouthRecord]`.
- Localisation: six locales wired, RTL layout verified for `ar`, resource
  override table, reading-level check in CI.
- Outbox + job runner.
- **Finder**: search projection, the composed query, all filters, list and map,
  listing detail, collections, neighbourhood pages, org pages, `.ics`,
  saved items, reminders.
- **Provider listing management**: org application, sites, staff, listing
  editor with RRULE builder and occurrence expansion, drafts, scheduling,
  seasonal cloning, CSV + HSDS import with dry-run.
- **Organiser review**: vetting queue, listing approval with reasons, taxonomy.
- Seed: Kettle County — 11 municipalities, 18 neighbourhoods, 4 districts,
  28 schools, 1 transit agency, 60 organisations, 85 sites, 420 listings
  across all four kinds, 6-locale content on a representative subset.
- CI: build, unit, arch, db-drift, integration, e2e+axe, explain.

**Acceptance**
- `docker compose up` → browse, search and filter 420 listings in six
  locales, with JavaScript disabled.
- Cross-tenant matrix test green across every module and role.
- Committed `EXPLAIN` plans for all seven scenarios, no `Seq Scan` on
  `listing_search`, estimates within 10× of actuals.
- axe-core clean on every public route; keyboard-only pass documented.
- A provider can go application → approved → listing drafted → submitted →
  approved → published → visible in search, end to end.

**Known gaps at M1:** no mobile app, no registration, no attendance, no
badges, no community, no dashboards beyond provider listing counts.

*Acceptance status (2026-10-17):*
- **Cross-tenant matrix:** green. `TenantIsolationMatrixTests` covers the catalogue and
  every role against every populated table; `ConsoleRouteMatrixTests` covers every
  console page.
- **Plans:** committed (`db/explain/measured/shipped/`) and gated in CI by
  `FinderPlanTests`.
- **Provider end to end:** covered by `ConsoleTests` and `ProviderManagementTests`.
- **axe-core and the e2e run:** not started. They need Playwright and axe-core
  downloaded, which needs your approval.
- **`docker compose up`:** not verified. There's no container runtime here (Q11), and
  the compose and CI images are PostGIS 17-3.4 while only 18.6 / 3.6.4 has been
  verified.

## M2 — Mobile + parental consent (3–4 weeks)

- MAUI Blazor Hybrid, iOS + Android, over `BrightTime.Ui.Shared`.
- Onboarding (age, interests, anchor), feed with `why`, search, filter sheet,
  map, listing detail, saved items, schedule.
- Push notifications (FCM/APNs behind `IPushSender`), local notifications for
  deadlines, notification preferences and quiet hours.
- Location on demand only; no background location in either store listing.
- **Parental consent flow**: guardian registration, child profile creation,
  guardian-link verification (Q3), scoped consent grant and revoke, consent
  centre, `youth_capability` recomputation, under-13 capability floor.
- Subject export/delete requests.

**Acceptance:** app runs on a physical iOS and Android device; a guardian can
create a child profile, grant and revoke consent, and the revoke immediately
removes the child's ability to do the consented thing; `/me/capabilities`
drives UI gating rather than client-side guesses.

## M3 — Registration, family profile, attendance, messaging, reports (6–8 weeks)

The largest milestone. Candidate for splitting.

- Family profile, members, contacts, support needs, `profile_share`.
- Form builder, eligibility rules with dry-run preview, waivers with
  e-signature, fees and sliding scale behind `IPaymentGateway` (Q6).
- Applications: draft, submit (idempotent), eligibility evaluation, decisions,
  bulk decide, seat claim under `FOR UPDATE`, waitlist and offers.
- Attendance: roster check-in, rotating QR, kiosk mode, offline capture and
  idempotent batch sync, correction-by-supersede, device enrolment and remote
  purge.
- Messaging: org-to-family threads, broadcasts, email + push + SMS (Q9), the
  conversation safety invariant live and tested.
- Reports: QuestPDF + ClosedXML, provider participation/attendance/roster
  reports, funder packet (Q10), suppression envelope on every aggregate.

**Acceptance:** a family applies once and uses the profile for a second
program with explicit per-org sharing; a staff phone records attendance in
airplane mode for an hour and syncs with zero duplicates on replay; an
attempted adult↔minor 1:1 conversation fails at the database in every test
route.

## M4 — Learner profile, badges, playlists (4–5 weeks)

- Portfolio with visibility per item, participation records derived from
  attendance, skills, projects with staff approval.
- Open Badges: issuer `did:web`, Ed25519 keys in the vault, OB 3.0 VC-JOSE
  issuance, public verification endpoint, revocation, and OB 2.0 hosted
  assertions if D3 is confirmed.
- Playlists: authoring, ordered/unordered, required and optional steps,
  progress, evidence upload, mentor review, completion badge, unlock rules.
- Portfolio export: PDF, OB 3.0 JSON, CLR 2.0.

**Acceptance:** a third-party OB 3.0 validator verifies an issued credential;
a revoked badge reports revoked at the public endpoint; completing a playlist
issues the badge and unlocks a gated listing.

## M5 — Social learning space with moderation (4–5 weeks)

- Spaces, membership with the ≥2-adults invariant, posts, comments,
  challenges, submissions, rubrics, mentor feedback, showcase with consent.
- Screening pipeline (Q8): PII detection and abuse classification before
  publication, hold/redact/block, screening results recorded.
- Moderation: report from anywhere, severity queue with a critical lane for
  `grooming_concern` and `self_harm`, triage, actions, append-only history,
  user suspension, escalation path.
- Media safety: scanning, EXIF stripping, re-encoding, SVG rejection.

**Acceptance:** nothing youth-authored reaches another user before screening;
the moderation audit trail reconstructs every decision; a mandated-reporter
escalation produces a documented, testable workflow.

## M6 — Mentor workforce and gig board (3–4 weeks)

- Mentor profiles, availability, searchability gated on clearance currency.
- Training catalogue, units, completion, expiry, credentials, blocking
  training requirements per role.
- Curriculum library with organiser vetting and provider adoption.
- Gig board: posting, matching with an explainable score, applications,
  decisions, assignment gated on clearance + training, hours logging and
  approval.

**Acceptance:** an assignment cannot go active without both verification
timestamps; a mentor whose clearance expires overnight loses youth-facing
access by morning without manual intervention.

## M7 — Landscaping, field collection, ecosystem dashboards (5–6 weeks)

- Asset inventory, features, photos with review, merge and dedupe.
- Field campaigns, area assignment with mandatory supervisor, offline bundle
  download, offline observation capture with photo + GPS, idempotent sync,
  review and validation queue.
- Resident and youth surveys, barrier capture.
- Census import (ACS 5-year), GTFS import, travel-time precomputation
  (transit and walk), supply-vs-need metrics, opportunity-desert scoring with
  published driver breakdown.
- Dashboards: youth, guardian, provider, organiser, civic — all through
  suppressed views.
- Research export pipeline with approval, k-anonymity and DUA.

**Acceptance:** a youth collector records 30 assets offline across a
neighbourhood and syncs cleanly; the desert score for a neighbourhood can be
explained from its drivers without reading code; `brighttime_readonly` has no
grant on any fact table, asserted by test.

## M8 — Integrations, public API, hardening, launch readiness (4–6 weeks)

- OneRoster and Ed-Fi connectors, Clever and ClassLink SSO — all flag-gated
  and disabled, with the DPA constraint live.
- Public read API with scoped clients and rate limits, iCal feeds, embeddable
  search/collection/map/calendar widgets with origin allowlists.
- Hardening: dependency and container scanning, SBOM, CSP, security headers,
  secret rotation runbook, backup and restore drill, PITR verification.
- Load testing: the finder at 50× expected concurrent search; registration
  admission control under a simulated August opening.
- Accessibility: full manual audit, screen-reader pass, published conformance
  statement.
- Store submission: Apple privacy manifest, Google Data Safety, kids-category
  review, both checklists.
- External penetration test and privacy review (vendor; schedule from M6).
- Runbooks: incident response, breach notification within the Part 121 7-day
  window, data-subject request fulfilment, tenant onboarding.

**Acceptance:** a partner embeds a working search widget; the public API
cannot be granted a youth-data scope (constraint-tested); restore from backup
into a clean environment verified; pen-test findings triaged with fixes or
accepted risks documented.

## Sequencing note

M3 is 6–8 weeks and carries the most risk: it is the first milestone handling
real youth records at volume, the first with money in it, and the first with
offline sync. If you want to de-risk the schedule I would split it:

- **M3a** — family profile, forms, eligibility, applications, waivers,
  decisions, waitlists (no money, no offline).
- **M3b** — attendance with offline sync, messaging, payments, reports.

That also gets a usable registration flow in front of providers a month
earlier, which is where the requirements feedback actually lives.
