# 07 — Threat model: youth safety and data privacy

Scope: the platform as designed in this plan. Framing is abuse-case-first
rather than STRIDE-first, because the dominant risks here are not an attacker
breaking in — they are an authorised adult doing something harmful, and
ordinary data doing harm by existing.

Each threat has a control, and each control names where it is *enforced*.
"Enforced in the schema" means a constraint or trigger; "enforced in policy"
means an authorisation requirement; "verified by" names the test.

## Assets, ranked by harm if exposed

| # | Asset | Why it ranks here |
|---|---|---|
| A1 | Adult↔youth communication channel | A messaging channel is the grooming vector. Not data at rest — a capability. |
| A2 | `attendance_record` | Says a named child was at a named place at a named hour. Weaponisable by a stalker or a non-custodial parent. |
| A3 | `family_member_support_need` | Health, disability, IEP/504. Highest sensitivity per record. |
| A4 | Youth identity + DOB + guardian contacts | Identity theft, and the key to impersonating a guardian. |
| A5 | Youth home location | Which is why **no column for it exists** anywhere in the schema. |
| A6 | Youth-authored content and photos | Deanonymisation, bullying, sexualisation. |
| A7 | `field_observation` by youth collectors | Reveals where trained youth were working, on a schedule. |
| A8 | District-sourced records | FERPA / Ed Law 2-d exposure, plus contractual breach. |
| A9 | Aggregate dashboards | Small cells re-identify in a county of 18 neighbourhoods. |
| A10 | Staff clearance status | Employment harm. (The report itself is out of scope by design.) |

## Threats and controls

### T1 — Grooming through platform messaging
**Scenario:** a mentor or staff member opens a private channel with one minor
and moves the relationship off-platform.

**Controls**
- There is no data shape for it. `community.conversation` has no pair form;
  `ck_conversation_minor_safety` requires `is_org_visible AND
  adult_participant_count >= 2` whenever `includes_minor`, and
  `enforce_safe_conversation()` recomputes those columns on every participant
  change and raises if the invariant breaks. *Enforced in the schema.*
- A space containing minors requires ≥2 non-minor members. *Enforced in policy,
  indexed by `ix_space_member_adults`.*
- Org admins are implicit readers of every thread with a minor
  (`is_implicit_reader`), and the UI says so on the thread.
- `messages.send_to_youth_1to1` is a permanent `never` in the permission
  matrix with no grantable row.
- Automated screening flags contact-exchange patterns (phone, handle, "text
  me", external platform names) in adult→youth text before delivery.
- **Verified by** an integration test that attempts every route to a 1:1
  adult-minor channel — direct insert, participant removal to leave a pair,
  guardian removal, mentor added to a two-person space — and asserts each
  fails at the database.

**Residual:** a determined adult can exchange contact details in person. The
platform reduces, cannot eliminate. Mitigation is the mandated-reporter
escalation path on `grooming_concern` reports.

### T2 — Adult poses as a youth
**Controls:** youth self-registration yields `youth_capability` with posting,
uploads and messaging off until an organisation verifies the account via
enrollment or roster. Community participation requires space membership,
which only staff can grant. *Enforced in schema + policy.*

### T3 — Youth or unvetted adult poses as a mentor
**Controls:** `mentor_profile.is_searchable` requires `status='active'`;
`gig_assignment` cannot be `active` without both
`clearance_verified_at` and `training_verified_at`
(`ck_gig_assignment_gates`); `ClearanceCurrentRequirement` re-checks expiry on
every youth-facing action, not only at assignment. `ck_gig_min_age >= 16`,
with 18 the default for youth-facing roles.

### T4 — Attendance data leak reveals a child's location in time
**Controls**
- `youth_access_grant` time-boxes provider access with a basis and an end
  date; after the grace window a provider sees only suppressed aggregates.
- Every read writes an `audit_event` with `subject_ids` — enforced by the
  startup test that fails boot if a youth-data endpoint lacks `[YouthRecord]`.
- `note_enc` is application-encrypted; free-text staff notes are the field
  most likely to contain health or behaviour detail.
- Retention default: 400 days, then aggregate-and-delete, configurable per
  tenant with a legal basis recorded.
- Export requires `PA` scope and is itself audited and rate-limited.
- RESTRICTIVE RLS policy means an unauthenticated request cannot reach the
  table at all.

### T5 — Deanonymisation through the public showcase or portfolio
**Controls:** display name is distinct from legal name and validated as such;
EXIF (including GPS) stripped at upload, with
`ck_media_exif_before_public` making camera-origin media unpublishable until
`exif_stripped_at` is set; public items carry no school and no neighbourhood;
under-13 work needs a recorded `showcase_publication` consent
(`ck_showcase_minor_consent`); staff approval is required
(`ck_project_public_needs_approval`); faces in asset photos are a rejection
reason in field review.

### T6 — Scraping the public finder to find where children gather
**Controls:** the public API's scopes are constrained by
`ck_api_client_scopes` to listing/org/taxonomy/geography data — there is no
youth-data scope to grant. Rate limits per client and per IP, stricter for
anonymous. Aggregates suppressed below the tenant threshold with
complementary suppression. Sites may set `is_address_public = false` and
serve only a neighbourhood. No endpoint ever returns a count of youth at a
site on a date.

*Implemented in M1: the public map and organisation pages (D21).*
- **The map shows a count per neighbourhood and nothing finer.** It is drawn from area
  boundaries, never a site's location. A test fails if a coordinate reaches the page.
- **Public site records have no coordinates field at all.** `PublicSite` carries no location,
  and for a private site no street or transit note either.
- **Unapproved organisations have no page.** An applying, suspended or rejected
  organisation's slug answers 404, exactly as one that never existed.

### T7 — Cross-tenant data access
**Controls:** RLS tenant policy on every table (layer 1), parent-derived
policy on child tables (layer 2), `FORCE ROW LEVEL SECURITY` so the owner is
bound, app role without `BYPASSRLS`, tenant set with `set_config(...,
is_local => true)` inside the request transaction so a pooled connection
cannot carry context forward. The 99-file ends with a migration-time assertion
that **fails deployment** if any module table lacks RLS.
**Verified by** the cross-tenant matrix test: for each module × role, a
request in tenant A reads exactly zero tenant-B rows, both through the API and
through a hand-forged `DbContext` query.

*Implemented (2026-10-17), `TenantIsolationMatrixTests` and `ConsoleRouteMatrixTests`:*
- **The catalogue half:** for every table in every module schema, populated or not, RLS is
  on and forced. The tenant policy (or the parent-derived one, or `tenant_self`) is a
  permissive policy for all commands, covering every application role, with
  `tenant_id = platform.current_tenant_id()` for reads and writes alike. No second
  permissive policy widens it, except two that are named with their reasons:
  - host lookup on `tenant_domain`, read-only;
  - the worker's claim on `job`, for `brighttime_jobs` only.
  A new table is covered without being named.
- **The behaviour half:** on a database built from `db/schema` with the Kettle seed and an
  empty second tenant, each of the four application roles was run against every populated
  table.
  - The other tenant, and a request with no tenant, see none of Kettle's rows.
  - Kettle never sees more than its own rows, and the application sees all of them in the
    core tables.
  - The other tenant's updates and deletes reach nothing.
- **The console half:** every page under `/provider/orgs/…` and `/organiser/…`, found from
  the application's own endpoint table:
  - refuses another organisation's owner, and providers on the organiser pages;
  - opens for the person it is for, so a refusal is never the test's own bad id.
- **What it found:** `notify.preference` carried both its own tenant policy and a
  parent-derived one. The two were OR-ed, so a row whose `tenant_id` disagreed with its
  user's tenant would have shown to the user's tenant. The parent-derived policy is removed.
- **How it was checked:** an injected `using (true)` policy fails the catalogue half by
  name.

**One deliberate exception (decision D13).** The public finder reads
`opportunity.listing_search` as `brighttime_finder`, which bypasses RLS, because the
tenant policy stopped the planner using the search indexes. On that path, isolation
is the tenant predicate `SearchQueryBuilder` always emits, and the table holds only
published, public listing data with no youth data. The role cannot read or write any
other table. The migration fails if that ever changes, and the integration tests
check it independently.

### T8 — Provider over-reach: browsing youth they never served
**Controls:** `youth_record.read` is `◐ grant` only.
`YouthRecordRequirement` resolves `youth_access_grant` for (org, youth) and
denies with a reason if absent — a 403 with an audit entry, not an empty list,
because a silent empty result hides the probe.

### T9 — Non-custodial parent or unsafe adult gains access
**Controls:** `guardian_link` requires an explicit verification method and
records it; `restricted_contact` blocks visibility and communication in both
directions and is honoured by `NoRestrictedContactRequirement`; a 13+ youth
can see every guardian link on their account and request review; revocation is
immediate and cascades to `youth_capability`.

**Residual:** the platform cannot adjudicate custody. It can make access
legible and revocable, and give an organiser a lever.

### T10 — Insider abuse by platform staff
**Controls:** `PX` has **no** youth-record read permission. Break-glass is a
separate time-boxed grant requiring a second approver and a mandatory
`reason` (`ck_audit_reason_required`), which notifies the tenant's organiser.
Audit is append-only by both grant revocation and trigger. Production
database access is via a bastion with session recording; `brighttime_app`
credentials are short-lived.

*Implemented: the audit log (D11, D19).*
- **Append-only, on every partition:**
  - Postgres copies neither a statement trigger nor the parent's revoked grants to a
    partition, so each partition is sealed separately (`platform.seal_audit_partition`).
  - Each partition gets its own trigger refusing UPDATE, DELETE and TRUNCATE, which holds even
    for the owner.
  - No application role has any grant on a partition. Rows go in through
    `platform.audit_event`.
- **Partitions:**
  - The worker creates them four months ahead, once at start and then daily.
  - Only `brighttime_jobs` may call the upkeep functions.
  - A row in the default partition is logged as critical.
- **What is recorded today:**
  - every sign-in and its refusals, with a reason the person is never shown;
  - sign-out;
  - password reset requested and completed;
  - passkeys added and removed;
  - external accounts connected and disconnected;
  - step-up confirmations;
  - organisation status changes;
  - listing creates, edits, moves and reviews;
  - each search of the audit log itself.
- **Client details:** only keyed hashes of the client address and user agent. A typed address
  with no account is never stored.
- **Explorer:**
  - `/organiser/audit` is for the roles whose `audit.read` is tenant-wide: organiser,
    moderator and platform admin.
  - The scoped forms for youth, guardians and provider admins come with the records they
    cover (M2).
- **Still open:**
  - youth-record reads, which arrive with the youth-record endpoints (M2/M3);
  - break-glass;
  - organiser notification of break-glass;
  - pseudonymising `subject_ids` on erasure.

### T11 — Prompt injection / content injection via listing or post text
**Scenario:** an imported HSDS listing or a youth post contains text crafted
to steer the screening model or a future LLM-assisted summariser.

**Controls:** all provider- and user-supplied text is treated as data, never
as instruction. The screening service receives content in a
clearly-delimited data channel with a fixed system prompt, and its output is
constrained to an enum decision plus label scores — a free-text model response
can never become an action. Translation and summarisation outputs are marked
`translation_source = 'machine'` and are never auto-published without review
when the target is youth-facing. Stored HTML is sanitised to an allowlist on
write *and* on render.

*Implemented for import (M1):* an imported row becomes a draft and never publishes itself.
An update to an already-published listing goes live as an editor's change does, and enters
the organiser's "changed since review" lane (D15). Imported text is treated like typed text:
the same length limits, and plain Markdown rendered with HTML escaped. An HSDS file's
organisation ids are ignored, so everything lands in the uploading organisation. Codes it
doesn't know are dropped rather than trusted.

### T12 — Offline data on staff and youth devices
**Controls:** SQLCipher with the key in Keychain / Android Keystore; roster
packs carry the minimum fields; TTL purge; `kiosk_device.wipe_requested_at`
triggers a purge on next contact and the client refuses to open after 72h
offline; sync is idempotent via `client_record_id` so a stolen device
replaying its queue changes nothing.

### T13 — SMS and email as a leak channel
**Controls:** `ck_template_sms_length` and
`ck_template_sms_no_youth_name` make a youth-identifying SMS template a
constraint violation. SMS and push carry a prompt and a deep link, never
detail. Destination addresses are stored only as hashes
(`delivery.address_hash`); suppression and bounce handling work on hashes.
Phone numbers are verified before any send
(`notify.phone_verification`), and under-13 accounts cannot receive SMS at all
(`ck_youth_capability_under13`).

### T14 — Shared-device session leakage
**Controls:** youth sessions get a 30-minute idle timeout and no default
"remember me"; `DELETE /me/sessions` revokes everywhere; refresh tokens are
rotating and family-chained, so a replayed token kills the family
(`identity.refresh_token.family_id`).

*Implemented for the API's tokens (D28):*
- **Refresh tokens:** rotating and family-chained. A spent token presented again revokes its
  whole family and is audited as `auth.token.reused`.
- **Youth sessions:** end after 30 minutes unused; adults' after 30 days. No session outlives
  90 days.
- **Sign out everywhere:** `DELETE /api/v1/me/sessions` revokes every family and changes the
  security stamp, which ends web sessions too.

*M1, saved listings (D22).* Saved listings live in a cookie in the browser, so on a shared
device the next person can see what was saved. A saved support group can say something
about someone. The saved page says this plainly, the list can be cleared in one press, and
the cookie holds listing ids and nothing else.

### T15 — Youth field collectors put at risk
**Controls:** `field_assignment` cannot leave `assigned` without a
supervisor; `min_collector_age` per campaign; only the *asset* location is
recorded, never the collector's track or idle position; photos are reviewed
before visibility; assignment areas are published to the supervising
organisation, not publicly.

### T16 — Discriminatory or opaque recommendations
**Controls:** the ranking feature set is a closed list (text match, chosen
distance, time to start, declared interests, popularity). The feature
extractor is a single class with a unit test that asserts it rejects a
sensitive-attribute input, and `application_answer.is_sensitive` marks the
fields that may never enter reporting, export or ranking. Every recommendation
ships its `why` payload and the UI renders it. "Less like this" edits declared
interests rather than feeding a hidden profile.

### T17 — Small-cell re-identification in dashboards
**Controls:** `analytics.suppress()` on every aggregate, threshold per tenant
(≥5 floor, default 10), complementary suppression on, zero published but
1..n−1 suppressed. Civic and research roles are granted **views only** at the
database level — `brighttime_readonly` has no grant on any fact table.
Research exports require a named approver, a DUA reference, a purpose of at
least 40 characters and k ≥ 5, enforced by `ck_research_approval`,
`ck_research_purpose` and `ck_research_k`.

### T18 — District integration over-collection
**Controls:** connections are created disabled and
`ck_district_enabled_needs_dpa` makes enabling without a DPA reference, signed
date, purpose and non-empty field allowlist a constraint violation.
`ck_district_data_elements` caps what may even be requested. The sync job
drops any payload field not in the allowlist and records it in
`roster_sync_run.fields_dropped`, so a district widening its feed cannot
widen our collection. District identifiers are stored only as keyed hashes
(`external_identity_map.external_id_hash`).

### T19 — Media as a malware or CSAM vector
**Controls:** uploads are quarantined until `scan_state='clean'`; nothing
unscanned is servable; content-type sniffed server-side, not trusted from the
client; images re-encoded (which also destroys embedded payloads); SVG upload
disallowed; CSAM detection and the legal reporting path are an M5 requirement
with a named responsible person at the tenant, not a technical control we can
claim to have solved.

### T20 — Payment data scope creep
**Controls:** no cardholder data anywhere. `payment_intent` holds amount,
status and a provider reference; the card is collected by the provider's
hosted element. Webhooks are signature-verified and idempotent. This keeps the
application out of PCI DSS scope beyond SAQ-A.

### T21 — Account takeover
**Controls:** passkeys first-class and preferred for youth; Argon2id password
hashing; breached-password check at set time; lockout; step-up authentication
for consent grants, guardian-link verification and youth-record export; rate
limiting on every auth route; no account enumeration in any response or
timing difference.

*Implemented for the API (D28):*
- **Sign-in:** `POST /api/v1/auth/token` uses the same password check as the web: the
  same lockout, and the same answer and work for every refusal. It is rate limited per
  client address.
- **Access tokens:** live 10 minutes. They carry who and when, never permissions. Every
  request rebuilds the principal from the database and refuses the token if the account
  is no longer active or its security stamp has changed, so a suspension or a sign-out
  everywhere applies at the next request.
- **Tenancy:** the issuer is the tenant's own address, so another tenant refuses the token.
- **Storage:** refresh tokens are kept only as SHA-256 hashes. A password reset revokes
  them all.

*Implemented for passwords (web):*
- **Hashing:** Argon2id at the OWASP minimum (19 MiB, t=2, p=1), in PHC format.
  Weaker hashes are upgraded at the next sign-in.
- **Lockout:** five failures lock the account for 15 minutes. The count is kept under
  `FOR UPDATE`, so parallel guesses can't race it.
- **No enumeration:** unknown email, wrong password, locked, inactive and child accounts
  get the same message after the same Argon2 work. "Forgot password" queues the address
  and answers at once; the account lookup, token and email happen after the response.
- **Reset tokens:** 256 random bits, stored as SHA-256 with a 60-minute expiry. They are
  single-use, one per account, and resending is throttled. A reset changes the security
  stamp, which ends existing sessions at their next revalidation (at most 5 minutes).
- **Reset links:** built from the tenant's primary domain (D18), never the Host header. A
  host that is no tenant's never reaches the reset page. The reset page sends no Referer.
- **Rate limiting:** a fixed window per client address on every sign-in route.
- **Password policy:** NIST 800-63B. At least 12 characters, no composition rules, and a
  common-password check. The breached-password check against the HIBP range API is
  available behind `Auth:CheckBreachedPasswords`, off by default because it is an
  outbound call.
*Implemented for provider sign-up and invitations (D23):*
- **Same answer every time:** an application or an invitation gets the same response
  whether the address is new, already has an account, or belongs to a youth or child.
  Mail goes out after the response.
- **Typed addresses grant nothing:** an account made from one has no password until its
  owner sets one through the emailed link. An existing account only gains an invitation,
  and its roles change only when its owner accepts.
- **Rate limiting:** the application form shares sign-in's per-address limit.
*Implemented for passkeys (web):*
- **Ceremonies:** WebAuthn with discoverable credentials and user verification required,
  and no attestation. The user handle is the account id, never an email.
- **Phishing resistance:** the relying-party id and origin are the tenant's primary domain
  (D18), so a passkey can't be used on another site or another tenant.
- **Storage:** credentials live in `identity.user_credential`, accessed only as
  `brighttime_auth`. Tenant isolation comes through the account's RLS.
- **Challenges:** held in an encrypted, HttpOnly, SameSite=Strict cookie for five minutes.
  The cookie is cleared on use, so an answer cannot be replayed.
- **Registration binding:** registration options are bound to the account that requested
  them.
- **Cloned keys:** a signature counter that goes backwards is refused.
- **Revocation:** a removed passkey is revoked, not deleted.
- **Lockout:** passkey sign-in ignores password lockout, because a passkey cannot be
  guessed. Inactive and child accounts are still refused.
- **Endpoints:** antiforgery token in a header, and rate limited with the other sign-in
  routes.
*Implemented for external OIDC sign-in (decision D17):*
- **Protocol:** authorization code with PKCE. The handler validates state, nonce,
  signature, issuer, audience and lifetime.
- **Identity:** validated issuer plus subject only. Email never finds or connects an account.
- **Connecting:** only from a signed-in session, and finished only by the session that
  started it.
- **Unconnected accounts:** sign in to nothing, and the message reveals nothing about
  BrightTime accounts.
- **Callback carried to another browser:** fails on the correlation cookie.

*Implemented: step-up.*
- **Session claims:** the session carries `auth_time` and how the person authenticated.
  Cookie revalidation keeps them, so refreshing a session never counts as authenticating.
- **The policy:** `RecentAuthenticationRequirement` (`StepUp.Policy`, 10 minutes by
  default, `Auth:StepUpMinutes`).
  - Applied today to the account security page and its actions: adding or removing a
    passkey, and connecting or disconnecting an external account.
  - Consent grants, guardian verification and youth-record export should declare the same
    policy as they are built.
- **Failure handling:** a page request is redirected to `/account/confirm` and returned
  afterwards. A script call gets a 403 naming the confirm page.
- **Confirming:**
  - with the account's own password, which shares sign-in's lockout;
  - or with one of its own passkeys; another account's passkey is refused.
  - An external-only account sets a password first: `prompt=login` re-authentication is
    honoured unevenly by providers.

*Implemented: the client address behind the proxy.*
- **Trust:** `X-Forwarded-For` and `X-Forwarded-Proto` are believed only from proxies in
  `Network:TrustedProxies` or `Network:TrustedNetworks`.
  - Only the right-most `ForwardLimit` entries are read, which a trusted hop appended
    itself.
  - With nothing configured, forwarded headers are off, because the middleware reads empty
    trust lists as "trust everyone".
  - A one-time warning names a peer whose forwarded headers were ignored.
- **Rate-limit key:** the IPv4 address, or the IPv6 /64, so a host cannot rotate through its
  prefix for a fresh allowance.
- **Rejections:** they carry `Retry-After` and a message, and are not re-executed through the
  not-found page, which had turned them into "does not exist" or a 400.

- **Still open:**
  - applying step-up to consent, guardian verification and export when those exist;
  - passkeys for youth onboarding (M2);
  - the mobile token flow.

### T22 — Availability as a safety issue
Enrollment deadlines and summer registration windows mean an outage has real
consequences for families. Controls: the public finder is static-SSR and
cacheable, so it survives a backend degradation; health checks gate
deployment; load test the finder at 50× expected concurrent search in M8;
registration windows get a queue-based admission control rather than
collapsing.

## Compliance mapping

| Requirement | Where it lives |
|---|---|
| COPPA verifiable parental consent | `identity.parental_consent` with `method`, `consent_text_hash`, evidence; `enforce_child_consent()` blocks an active child account without it |
| COPPA data minimisation | No home address column; DOB encrypted with only `birth_month` in clear; under-13 capability floor in `ck_youth_capability_under13` |
| COPPA parental access, deletion, revocation | `platform.subject_request`, consent centre, immediate cascade on revoke |
| FERPA | District data isolated behind `integration.district_connection`; field allowlist; purpose recorded; no re-disclosure path |
| **NY Education Law 2-d / Part 121** | `tenant.student_privacy_pack = 'ny-ed-law-2d'` selects: parents' bill of rights surfaced at every consent point, encryption in transit (TLS 1.3) and at rest (volume + field-level), 7-day breach notification workflow, DPA required before any district connection, annual data-security training recorded in `workforce.course_enrollment`, and the prohibition on commercial use — which is why there is no advertising, no third-party tracker and no analytics vendor anywhere in the stack |
| Finder usable with no account | Every public route is anonymous and SSR; `search_event` is pseudonymous with a daily-rotating session hash |
| Audit of every youth-record read and write | `platform.audit_event`, one row per operation with `subject_ids`; append-only by grant and trigger; partitioned monthly |
| Configurable retention with automated deletion | `platform.retention_policy` per data class, enforced by a nightly job |
| Self-service export and deletion | `platform.subject_request` + `/api/v1/me/export`, `/api/v1/me/delete` |
| No advertising, trackers, data sale | First-party analytics only; tile and geocode requests proxied so the client never contacts a third party; CSP with no third-party script origins |

## What I am not claiming

- **Right to be forgotten vs. the audit log.** These conflict. The design
  deletes the record and keeps the audit entry with the subject id
  pseudonymised, because an audit log you can erase is not an audit log. That
  is a defensible reading, not a settled one — see open question Q5.
- **CSAM detection** is a vendor and a legal process, not a schema.
- **Background-check adjudication** is deliberately out of scope. We hold
  status and expiry; who decides "conditional" is the organisation's
  responsibility and liability.
- **Penetration testing and a third-party privacy review** are M8 line items.
  Nothing here substitutes for them.
