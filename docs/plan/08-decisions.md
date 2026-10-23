# 08 — Where I disagree, and open questions

You asked to be told when I disagree with something in the prompt. Nothing
here is changed in the plan; each item states what I would do instead and
waits for your answer.

## Disagreements and recommendations

### D1 — Split functional area C into four modules (**already reflected in the plan; say if you want it merged**)
The prompt says "one module per functional area". Area C (provider tools) is
organisation management + registration + attendance + messaging + reporting —
four different write patterns, three different consistency requirements, and
attendance alone needs offline sync. I split it into `Directory`,
`Enrollment`, `Attendance` and `Notifications`. This is the one place I
deviated from the prompt's structure in the plan itself, because the module
map was undeliverable otherwise. Revert if you disagree.

### D2 — Hand-rolled Postgres job queue, not Hangfire
The prompt says "background jobs backed by Postgres", which Hangfire can do.
I recommend against it: Hangfire's Postgres storage is community-maintained,
owns its own schema, and knows nothing about tenancy — every job would need
tenant context threaded manually, and its dashboard would be a second
authorisation surface over our data. A `platform.job` table with
`FOR UPDATE SKIP LOCKED` and `LISTEN/NOTIFY` is ~400 lines, gives us
tenant-aware jobs, audit integration, a `unique_key` for debouncing
reprojection, and one schema to review. **Decision needed:** confirm, or name
Hangfire/Quartz if you want an off-the-shelf scheduler.

*Implemented (approved with the plan).* `JobStore` claims with `FOR UPDATE SKIP LOCKED`,
completes and fails only jobs the same worker still holds, retries with exponential
backoff (5 s doubling to an hour), dead-letters after `max_attempts`, and reaps jobs
whose worker stopped responding. Two schema corrections came out of building it:

- The debounce index covered running jobs as well as waiting ones, so an edit made while
  a projection ran would have collapsed into it and been lost. It now covers waiting
  jobs only.
- `platform.job` was tenant-isolated with no worker exception, so a worker serving every
  tenant could claim nothing. `job_worker_claims` lets `brighttime_jobs` alone see every
  job; requests still enqueue only for their own tenant.

Not yet built: `LISTEN/NOTIFY` (the worker polls every 2 s), and the `job_schedule` cron
runner. Until the latter exists, nothing re-projects a listing whose `next_start_at` has
passed, so the soonest sort drifts until the listing is next edited.

### D3 — Open Badges 3.0 is more expensive than it looks
OB 3.0 is W3C Verifiable Credentials 2.0. That means DID management, Ed25519
key custody and rotation, a proof format choice, and a verifier. My
recommendation:
- Issuer identity: `did:web`, served from `/.well-known/did.json` on the
  tenant's own host. Simplest thing that is actually resolvable, no ledger.
- Keys: Ed25519, private material **only** as a vault reference
  (`issuer_key.private_key_ref`), never in the database.
- Proof: `VC-JOSE` (compact JWT) as primary, since more verifiers handle it.
- **Also emit OB 2.0 hosted assertions.** A meaningful share of consumers —
  LinkedIn, several LMSs, some state systems — still do not read 3.0. A badge
  a young person cannot show anywhere is not a credential.
**Decision needed:** confirm dual 2.0/3.0 issuance. It is roughly a week of
extra work in M4 and I think it is the difference between a badge and a
picture.

### D4 — RLS for tenancy; application for everything else
Confirming the prompt's intent and stating the boundary explicitly, because
it is the kind of thing that drifts. RLS carries tenant isolation plus one
coarse RESTRICTIVE "is there an authenticated actor" guard. Per-record
authorisation stays in the application. Reasons: a five-way join in a policy
predicate produces plans nobody can reason about; RLS cannot return *why*
access was denied; and a youth should be told "your guardian hasn't verified
the link yet", not shown an empty page. Note the trap this avoids: multiple
PERMISSIVE policies are **OR**-ed, so a second permissive policy weakens
isolation — the actor guard must be RESTRICTIVE, which it is.

### D5 — `text` + `CHECK` instead of Postgres enum types
The prompt doesn't specify. I used `text` with named `CHECK` constraints
throughout. `ALTER TYPE ... ADD VALUE` cannot be used in the same transaction
that adds it, values cannot be removed or reordered, and the EF mapping is
less portable. A `CHECK` constraint is altered transactionally in a migration
like anything else. Cost: ~2 bytes per row versus a 4-byte enum, and no
ordering guarantee. Tenant-configurable sets (categories, interests,
accommodations) are lookup tables, not either.

### D6 — Postgres full-text search will not carry four of your six languages
This is the technical point I most want you to see before M1.

Core Postgres has Snowball stemmers for English and Spanish. For Chinese,
Bengali, Haitian Creole and Arabic it has nothing. Specifically, Chinese has
no whitespace tokenisation, so `to_tsvector` produces one giant token per run
of characters and `@@` effectively stops working.

Plan: real stemming for en/es; trigram n-grams as the **primary** path for
zh/bn/ht/ar via `ix_ls_fuzzy`; `unaccent` everywhere. This is adequate for
launch. `ISearchProvider` is a seam so Meilisearch or OpenSearch is a
provider swap, and I would pull that trigger when zh/ar volume becomes
material or a tenant passes ~100k published listings.
The alternatives I rejected: `zhparser`/`pg_bigm` are often unavailable on
managed Postgres, and committing to them constrains your hosting.
**Decision needed:** accept the trigram-based approach for M1, or budget a
search engine from the start.

### D7 — The finder must be static SSR, not interactive Blazor
The prompt says "server-rendered public pages for SEO and fast first load,
interactive where needed". I want to go further and be explicit: the entire
public finder — search, results, listing detail, collections, neighbourhood
pages, badge verification — is a **GET form with server-rendered HTML and
link-based pagination, working with JavaScript disabled**. Interactive
islands are additive, for the map and typeahead only.

Three reasons: the low-bandwidth requirement is real for the population this
serves; a static page is cacheable at nginx, which makes the finder survive a
backend degradation during the one week in August when registration opens;
and it is the most reliable route to WCAG 2.2 AA.

### D8 — Never persist a youth's home address or coordinates
Stronger than the prompt's "collect the least data". The search anchor is a
*neighbourhood or a school*, both of which are public geography. A typed
address is geocoded in-request and discarded; nginx strips `nearAddress` from
logs; there is no column for it. A home address for a minor is the one piece
of data whose breach is unrecoverable, and the feature works without it.
This does mean "distance from my house" is approximate. I think that is the
right trade and want you to agree before M1.

### D9 — Users are per-tenant, not global
A white-label region is a separate data controller with its own DPA, its own
retention policy and its own privacy pack. A global user table would mean one
breach spans every region and a subject-deletion request has ambiguous scope.
Cost: a family moving from one county to another re-registers. I think that is
acceptable and the right default; flag it if you want cross-tenant identity.

### D10 — Family profile sharing must be per-organisation and revocable
"One application serves many programs" (discoverDYCD) is a real benefit and a
real risk: it tends to mean every provider sees everything. The plan has
`enrollment.profile_share` with an explicit field list, per organisation, per
listing, revocable, plus `family_member_support_need.share_with_providers` as
a separate opt-in. Slightly more friction at application time; far less
standing exposure.

### D11 — Audit one row per operation, not per record
"An audit log of every read and write of youth records" read literally means a
roster view of 40 youth writes 40 rows. At scale the audit table becomes the
largest and hottest thing in the database. One row per operation with
`subject_ids uuid[]` and `subject_count` satisfies the requirement — you can
still answer "who read child X and when" via the GIN index on `subject_ids` —
at ~1/40th the write volume. Confirm this reading is acceptable to your
counsel.

### D12 — Where I think the prompt under-specifies the hard part
Not a disagreement, a flag. The two things most likely to sink this product
are not in the technical requirements:
1. **Provider data entry.** Everything downstream — search quality,
   landscaping, dashboards — depends on small organisations keeping listings
   current. The schema has seasonal cloning, bulk import, broken-link
   detection and `expires_at`; I would add a provider-facing "your listing
   goes stale in 14 days" nudge and an organiser freshness dashboard in M1
   rather than M7.
2. **Moderation staffing.** A moderation queue with a 1-hour critical SLA
   implies a rota. The software cannot supply that.

### D13 — The public finder reads listing_search outside row-level security (**decided: option 2, implemented**)

**What was measured.** With RLS on, the finder's search table is read by a sequential
scan for keyword, date, fuzzy and combined queries. With RLS bypassed, the same
queries use the index and take well under a millisecond, except fuzzy matching at
17 ms. At 50,000 rows the RLS cost is 12–65 ms. The cost grows with the table, so
it will be larger at the 220,000 rows in the capacity plan.
Details: `docs/plan/03-indexing-and-query-plans.md`, "Measured plans".

**Why it happens.** The tenant policy is a security barrier. PostgreSQL will not
move the application's non-leakproof filters below it, so the index cannot serve
them. Three ways to make the function leakproof, or to stop it being inlined,
were tried and did not change the plan.

**Options.**

1. **Keep RLS on the search table (the status quo).** Isolation is enforced by
   the database. Cost: the sequential scans above, growing with the table.
2. **Read the public finder through a dedicated role that bypasses RLS.** The role
   is granted `SELECT` on `opportunity.listing_search` and nothing else. Isolation
   for this one surface then rests on the explicit tenant predicate that
   `SearchQueryBuilder` always emits, plus the fact that the table holds only
   published, public data and no youth data. RLS stays on every other table and
   on every write path. Measured ceiling: the no-RLS column above.
3. **Leakproof wrappers.** Superuser-created, leakproof functions and an operator
   class over them, so the containment and distance checks can run below the
   barrier. Keeps RLS, but is fragile, needs a DBA, and is hard to review.

**Recommendation: option 2**, with an integration test that a query through the
finder's role cannot return another tenant's rows, and a documented rule that no
youth-data table may ever be read through a bypassing role.

**Why it needs a decision.** Option 2 narrows the defence-in-depth described in
D4 and T7 for one table. That is a judgement about risk, and it is yours.

**Decided 2026-10-16: option 2.** As implemented:

- `brighttime_finder` is `NOLOGIN BYPASSRLS`, with `SELECT` on
  `opportunity.listing_search` and no privilege on any other table.
- The application enters it with `SET LOCAL ROLE` inside the finder's transaction.
  The bypass reverts at commit, so a pooled connection never carries it. The
  application's login role must be granted membership of `brighttime_finder`.
- `SqlListingFinder` refuses an empty tenant before it opens a connection.
- Three guards, each tested:
  1. `99-rls-and-grants.sql` fails the migration if the finder role gains any
     other table privilege, or if any other BrightTime role can bypass RLS.
     Tested by widening the role in a transaction: the migration names the leak.
  2. Integration tests: the role cannot read identity, enrolment, attendance,
     community, audit or listing tables; it cannot write the search table; and two
     tenants with identical listings never see each other's.
  3. A unit test that the tenant predicate survives every filter combination.
- Measured result: every finder scenario now uses an index. Figures are in
  `03-indexing-and-query-plans.md`.

### D14 — Development sign-in until Identity sign-in exists (**accepted 2026-10-17**)

The provider console and the review queue need a signed-in person with permissions and
organisation memberships; nothing signs anyone in yet. Building ASP.NET Core Identity
(passwords, passkeys, OIDC, reset) is its own M1 slice, so the consoles were built against
a seam instead:

- `BrightTimePrincipalFactory` builds the principal from the database: tenant, account
  kind, permission claims from `app_user_role` → `app_role_claim`, and one membership claim
  per active `org_membership`. Identity rows are read as `brighttime_auth`; memberships as
  the application, with the user as the actor.
- The cookie scheme, authorization handlers and pages are the production ones.
- The only stub is how someone proves who they are: `/dev/sign-in` lists seeded accounts
  and signs in as one. It is mapped only when the environment is Development **and**
  `Auth:DevelopmentSignIn` is true; otherwise the route does not exist (404), which a test
  checks. Production therefore has no way to sign in until Identity lands, which is the
  safe failure.

Identity sign-in replaces `/dev/sign-in` and calls the same factory.

*Update (password sign-in implemented).* `/sign-in` now takes an email and password, and
`/account/forgot` and `/account/reset` set or reset one by emailed link; both call the same
factory. See T21 in the threat model for the controls. `/dev/sign-in` stays as a development
convenience under the same two conditions. Seeded accounts have no password until they use
the reset flow.

*Update (passkeys implemented).* A signed-in person adds passkeys at `/account/security` and
signs in with one from `/sign-in`, through the same factory. External OIDC followed; see D17.

### D15 — Edits to a published listing go live and are reviewed after (**accepted 2026-10-17**)

`ListingLifecycle` gates first publication (a provider submits, an organiser publishes) but
said nothing about editing a listing that is already live. Re-review before the change shows
would pull a running program off the site, or freeze a corrected time, for as long as the
queue takes. So:

- An approved organisation's edit to a published listing is saved and projected at once.
- The listing then appears in the review queue's **Changed since review** lane
  (`updated_at > reviewed_at`). The organiser marks it reviewed, or unpublishes it with a
  reason the provider sees.
- A listing is never published without an organiser having seen it once.

The side-by-side diff in the screen inventory needs stored revisions; until those exist,
the lane shows the current version and when it changed.

*Accepted as is for now.* The long-term direction is stored revisions: the published version
stays live while an edited revision is reviewed, which is what the review queue's
side-by-side diff needs. That is a later slice: a revision table, editor changes and the diff
view.

*Revisions and the diff, 2026-10-17:*
- `opportunity.listing_revision` stores the editor's form as JSON at each submission and
  each organiser review: publish, request changes, reject, or "changes are fine".
- The review page shows field by field what changed since the last review, for a
  resubmission and for the "changed since review" lane.
- Edits still go live (D15 as accepted). Keeping the published version live while an edit
  waits for review is the remaining step. It now needs only the editor to write an edited
  revision instead of the listing.

### D16 — Consoles are server-rendered forms, not interactive components (**accepted 2026-10-17**)

The screen inventory marks the provider and organiser consoles "Interactive". They are
built as static server-rendered forms (GET pages, POST with antiforgery), like the finder.
They work without JavaScript, every action is a plain HTTP round trip that the tests drive
directly, and there is no circuit to hold per editor. The schedule preview is a
server round trip ("Preview sessions") rather than live. Interactivity can be layered on
where it earns its keep.

### D17 — External sign-in signs in; it never creates or matches accounts (**accepted 2026-10-17**)

Any number of OpenID Connect providers can be configured under `Auth:External:<name>`
(Google, Microsoft Entra for a district, and so on). Each is an authorization-code flow with
PKCE. ASP.NET Core's handler checks state, nonce, signature, issuer, audience and lifetime.
The rules around it:

- **An external account signs in only to the BrightTime account it was connected to.** It
  is identified by the validated issuer plus subject, the pair the provider promises never
  to reuse.
- **Email is never used.** Not to find an account and not to connect one. A provider's email
  claim can be unverified, or recycled when a domain changes hands. Matching on it would let
  whoever holds that address at the provider take the account, which is the usual takeover
  path in external sign-in. A test signs in externally with an existing person's email
  and shows that nothing connects.
- **Connecting needs the person signed in already**, from Sign-in and security. They prove
  both accounts, and the flow is finished only for the session that started it.
- **No sign-up through a provider.** An unconnected external account creates nothing and is
  told to sign in another way and connect. Self-registration is its own design (guardian and
  youth registration with consent is M2).
- **One BrightTime account per external account per deployment.** That is
  `pk_app_user_login`, global across tenants, consistent with the one-identity-per-tenant
  default (Q-level note above). A person who is staff in two tenants connects their Google
  account in one of them.
- **Query response mode**, not form_post: the return from the provider is then a top-level
  GET, so the correlation and nonce cookies stay SameSite=Lax.

Providers are deployment-wide configuration for now. Per-tenant providers, such as each
district's own Entra, arrive with host-based tenant resolution.

### D18 — A request's tenant comes from its host (**accepted 2026-10-17**)

`Tenancy:Mode=Host` (the default with a database) resolves each request's tenant from
`platform.tenant_domain`, before anything reads it.

- **Normalised hosts:** lower case, no port, no trailing dot, internationalised names as
  punycode. An IP-address host never resolves.
- **Unknown host:** 404 with a generic body, so no tenant is named and no tenant page runs.
- **Inactive tenant** (provisioning, suspended, archived): 503.
- **Aliases:** a tenant's other domains redirect permanently (308, which keeps the method) to
  its primary domain. Each tenant's real traffic then runs on one host, so a passkey's
  relying party and a session cookie mean one thing.
- **The tenant's address** is `{Tenancy:PublicScheme}://{primary domain}`, built from the
  database and never from the Host header. It is used for reset links, the passkey relying
  party (now per tenant, built per request) and calendar files. This replaces the single
  `Auth:PublicBaseUrl`, which remains only for `Tenancy:Mode=Fixed`.
- **Cache:** lookups are cached, 5 minutes when found and 30 seconds when not, with a bound
  of 10,000 entries. A suspension takes up to five minutes to reach every instance, and a
  flood of made-up hosts costs at most one query per host per half-minute.

`Tenancy:Mode=Fixed` with `Tenancy:TenantId` serves one tenant regardless of host, for a
single-tenant install. Sample data always serves the fictional tenant. `Finder:TenantId` is
gone. External OIDC providers are still deployment-wide; per-tenant providers can now key on
the resolved tenant.

### D19 — Audit rows are written by the operation, not by an EF interceptor (**accepted 2026-10-17**)

The architecture sketch had an `AuditInterceptor`. An interceptor sees rows and commands, not
operations: a roster read is one query, a listing save is a dozen statements, and neither knows
why it ran. D11 asks for one row per operation, with its subjects and a reason, so the operation
writes it.

- **Changes:** `IAuditWriter.RecordInTransactionAsync` writes the row through the change's own
  transaction. The change and its trace commit or roll back together. Organisation decisions
  and listing saves, moves and reviews do this.
- **Request events** (sign-in, sign-out, step-up, account security, explorer searches): these
  are buffered per request. They are flushed in one transaction before the response starts,
  so an audited read is on record before its data leaves. A failed flush fails the request.
- **Background work** (the password-reset dispatcher) records and flushes in its own scope.
- **The safety net stays:**
  - The boot guard refuses to start when an endpoint can return a `[YouthData]` type without
    `[YouthRecord]`.
  - An attributed endpoint that records nothing writes an `error` row naming itself.
- **Client details:** the address and user agent are stored only as HMAC-SHA256 under
  `Audit:ClientHashKey`, a deployment secret that is required outside Development. A plain
  hash of an IPv4 address is reversible by enumeration.
- **Not recorded:** an address typed into sign-in or "forgot password" that has no account.
  The audit log must not become a list of who tried which emails.

Reversible: an interceptor could be added later for a "nothing touched a youth table
unaudited" cross-check. It would add to these rows, not replace them.

### D20 — Interface strings are JSON catalogues with ICU plurals, not .resx (**accepted 2026-10-17**)

The plan asked for "the `en` resource file" without saying which format. These are flat JSON
files, one per locale, using a subset of ICU MessageFormat.

- **Why not .resx:**
  - It has no plural forms, and Arabic needs six. `IStringLocalizer` would leave plurals to
    `if (n == 1)` in every page, which is wrong in five of the six languages.
  - Translation tools (Crowdin, Weblate, Lokalise) read flat JSON with ICU directly.
  - A tenant override has to be checked against the shipped string's arguments, which needs
    the template parsed. Here it is.
- **What it costs:** about 500 lines of parser, catalogue and service (`BrightTime.Localisation`
  since D34),
  tested on their own. Apostrophes are ordinary characters, unlike ICU, which quotes with them
  and turns "don't" into a silent bug.
- **Rules CI enforces:**
  - every locale has exactly English's keys;
  - the same arguments, and the plural forms its language needs;
  - every key the code uses exists, and every key exists because the code uses it;
  - every module error has an entry;
  - no arrows that flip in Arabic;
  - reading level for youth-facing text;
  - logical-only CSS.

Reversible: the catalogue sits behind `Text`, so a move to `IStringLocalizer` with a
MessageFormat library would change one service, not the pages.

### D21 — The M1 map is a drawing of the tenant's neighbourhoods, not a tile map (**accepted 2026-10-17**)

The plan said "MapLibre + OSM, clustered pins". What shipped is a server-rendered SVG of the
tenant's own neighbourhood boundaries (`platform.neighbourhood`), shaded and labelled with how
many listings each has under the search's filters. Each area is a link that narrows the search.

- **Fictional geography.** Kettle County's coordinates sit over a real county. Street tiles would
  show real roads and places under made-up names.
- **Privacy:**
  - A count per area is all the map shows, so a site whose address is private cannot be read
    off it (T6). Pins would need to be coarsened for those sites anyway.
  - No tile server learns a young person's IP address or where they are looking.
- **Low bandwidth and no JavaScript.** The map is a few kilobytes of HTML and works with
  scripts off, as the finder must. The plan's `?lowbw=1` list-only mode is unnecessary.
- **What it is not.** It has no street-level detail, no "near me" and no clustering of
  individual sites. A real tenant will want street context.

**MapLibre + OSM stays the direction** for a real tenant, as an island over this drawing. It
needs two things from you: approval to download MapLibre GL JS (a pinned version from npm,
vendored, with no CDN at run time), and an answer to Q7 on which tile source to use. Ours, or a
paid provider's; OSM's public tile servers forbid this use. Until then the drawing is the map.

### D22 — Saved listings live on the device until accounts exist; reminders come through the calendar (**accepted 2026-10-17**)

M1 has no registration, so there is no account to save to. `saved_item` and
`listing_reminder` both need one.

- **Saved:** a cookie holding listing ids, and nothing about the person. It is HttpOnly,
  SameSite=Lax, kept for 90 days, at most 50 ids. Plain forms and redirects, so it works
  without JavaScript. Ids that are no longer published are dropped when shown.
- **Reminders:** `/saved/calendar.ics` gives every saved listing's upcoming sessions and
  deadlines, with an alarm an hour before each session and a day before each deadline. The
  person's own calendar reminds them, and BrightTime holds no email or phone number for a
  minor it has no consent for.
- **Cost:** saving is per browser, and anyone sharing that browser sees the list (T14). The
  saved page says so beside a button that clears it.

When accounts arrive (M2), signing in moves the cookie's ids into `saved_item`, and server
reminders use `listing_reminder` under the consent rules. The cookie path stays for people who
never sign in.

### D23 — Provider sign-up and invitations make adult accounts without saying who already has one (**accepted 2026-10-17**)

M1 has no registration, but the acceptance test starts with a provider applying, and an owner
has to invite staff. Both start from an email address someone typed, which may not be theirs.

- **Apply** (`/providers/apply`, public, rate limited with the sign-in routes):
  - **New address:** an adult account with no password is made. The applicant owns the
    organisation at once, because only the address's owner can set the password. The email
    carries the reset link, which also confirms the address.
  - **Existing address:** the owner membership is an invitation, accepted after signing in.
    The email carries a sign-in link.
  - **Youth, child or erased address:** nothing is made and no email is sent.
  - The answer is the same page in all three cases.
  - **Signed-in adult:** owns the new organisation at once, since the session proves the
    address.
- **Invite** (owners and admins): always an invitation, accepted after signing in. A new
  address gets the set-password link first. The answer is the same, and a youth address is
  quietly not invited, so an organisation's admin learns nothing about who uses the platform.
- **Roles:** the global role is granted when a membership becomes active, never when an
  address is typed. Owners and admins get `provider_admin`; staff and viewers get
  `program_staff`. The role stays after the membership ends: organisation-scoped
  permissions also need a live membership, so it reaches nothing. Accepting refreshes the
  session, so the organisation opens on the next page.
- **Email:** sent after the response by `AccountMailDispatcher`, which grew out of the
  reset dispatcher and keeps its rules (T21).
  - The set-password link is the 60-minute reset token. When it runs out, "Forgot password"
    sends another.
  - Making or finding the account happens in the request. An insert and a no-op differ by
    about a millisecond, which is accepted.
- **Cost:** anyone can file an application that names any address. That puts an `applied`
  organisation in the organiser's queue and sends one email, which says to ignore it. It
  cannot get anyone into the account. The answer is rate limiting per client address plus
  vetting. If spam becomes real, the next step is a CAPTCHA or confirming the email first,
  either of which needs your call.

Rejected: requiring sign-up first. There is no registration in M1, and a sign-up form has the
same enumeration problem.

### D24 — Cross-module changes travel as jobs until the outbox has a dispatcher (**accepted 2026-10-17; superseded by D27**)

The plan sends module events through `platform.outbox_message`, but nothing dispatches it
yet. `IOutboxWriter` has no implementation. Provider management needs one such event:
editing or closing a site must re-project the listings held there, because search copies
the site's place.

What shipped: `SiteEditor` queues a `directory.site_changed` job (`SiteChangedJob` in
Directory.Contracts) in the same transaction as the change. Opportunities handles it by
queueing a projection for each listing at that site. This gives the guarantee the outbox
exists for: no change without its event, no event without its change. Jobs debounce on
the site id.

Moving to the outbox later means publishing `SiteLocationChanged` instead and subscribing
the same handler. One call site changes.

### D25 — Vetting is a checklist the organiser saves; approval needs the contact verified (**accepted 2026-10-17**)

The screen inventory's "checklist, documents, decision with a reason".

- **The checklist:** contact verified, website and public details checked, safeguarding
  policy seen, and the expiry on the insurance certificate seen.
  - It is saved as a vetting event that leaves the status unchanged, so vetting can take
    days and the history shows what was checked and when.
  - Approval records the checklist it was judged on.
- **Approval needs, beyond an open site and an active owner:**
  - the contact verified. An applicant's address proves nothing about the organisation it
    names (D23), so someone must reach the owner through the organisation's public record.
  - a current insurance certificate, where the tenant sets the flag
    `vetting.insurance_required`. It is off by default; Kettle doesn't set it.
- **Clearances:** the checklist shows how many staff and mentors have no current background
  check, but doesn't block approval. Clearances can't yet be recorded in the console, and
  which kinds a tenant requires is a tenant setting that doesn't exist yet. Both come with
  roster access (M3), which is where clearances gate a person, not an organisation.
- **Documents:** not uploaded yet. `organisation_document` needs media storage with virus
  scanning, which isn't built. The organiser records what they saw instead.
- **Suspension takes listings out of search:** a listing is live only while its
  organisation is approved. Any status change queues `directory.organisation_changed` (D24),
  which re-projects the organisation's listings, and the public listing page checks the
  status directly.

The contact check is my call for M1's acceptance test. Name the tenant's actual vetting
policy and the checklist follows it.

### D26 — Taxonomy codes are permanent; terms are retired, not deleted (**accepted 2026-10-17**)

- **Codes:** a term's code is in facet values (`cat:stem`), links and saved searches, so it
  never changes once made.
- **Retiring:** a retired term leaves the editors' pickers and the finder's filters. Listings
  already tagged keep its facet until they are re-tagged, and history still names it. A
  category with active interests can't be retired, and an interest can't be restored under
  a retired category.
- **Names:** every term needs an English name, because every page falls back to English
  when a translation is missing. The other languages are optional, and the list shows how
  many each term has.
- **Search:** renaming, retiring or restoring queues `platform.term_changed` (D24), which
  re-projects the listings tagged with the term, so search matches its current name.

### D27 — The outbox: publish in the caller's transaction, deliver as one job per handler (**accepted 2026-10-18**)

The outbox now has a dispatcher, and D24's three stopgap jobs are module messages:
- `SiteChanged`, which replaces the unused `SiteLocationChanged`;
- `OrganisationStatusChanged`, with `OrganisationApproved` and `OrganisationSuspended`
  published as well where they apply;
- `TaxonomyTermChanged`.

- **Publishing:** `db.PublishAsync(tenant, message)` writes to `platform.outbox_message`
  through the module context's own connection and transaction, as `EnqueueJobAsync` does
  for jobs. That gives no change without its message and no message without its change.
  - The plan's `IOutboxWriter`, which staged messages until "the unit of work commits",
    was never implemented, and is removed. Our module contexts commit inside
    `InTransactionAsync`, so an explicit write in that transaction is simpler and visibly
    atomic.
  - The tenant policy stops the application publishing for any tenant but its own.
- **Dispatching:** in the jobs host, `OutboxDispatcher` claims unprocessed messages with
  `FOR UPDATE SKIP LOCKED`. In the same transaction that marks a message processed, it
  writes one `outbox.deliver` job per subscribed handler.
  - Each handler then has the job runner's tenant scoping, retries, backoff and dead
    letter, so one failing handler neither blocks the others nor makes them run twice.
  - A delivery job carries the message id and the handler name, never the message's
    content, keeping to the job table's ids-only rule.
  - The jobs role reads every tenant's messages through a named policy,
    `outbox_dispatch`, the same shape as `job_worker_claims`. The tenant matrix allows
    exactly that.
- **Guarantees:** delivery is at least once, so handlers are idempotent. Messages are
  dispatched in the order they were written, but deliveries run in any order, so handlers
  read current state and don't rely on order. The three reprojection handlers do both:
  queueing a projection twice collapses into one.
- **Handler names:** a handler is named by its type's full name. Renaming one strands
  deliveries already queued for it, which fail until dead.
- **Not done yet:**
  - Processed messages are kept. Deleting them is a retention-policy number (Q4); I
    propose 30 days after processing.
  - Outbox lag on `/health/ready` comes with the health endpoints.
  - Dispatch polls once a second when idle. LISTEN/NOTIFY would cut that, as for jobs,
    and is a follow-up.

### D28 — API tokens: short access tokens checked against the database, rotating refresh tokens (**accepted 2026-10-18**)

The mobile app signs in through the API host, which until now was a stub.

- **Access tokens:** JWT, HS256, 10 minutes. They carry the subject, the tenant, the
  account's security stamp, and when and how the person signed in.
  - Not roles, permissions or memberships: each request rebuilds the principal from the
    database, as the web's cookie validator does. The token is refused if the account
    isn't active or the stamp has changed.
  - The cost is one account read and one membership read per request. The benefit is
    that a suspension, a removed membership or "sign out everywhere" applies at once,
    not at token expiry. A short-lived cache keyed on the token id is the next step if
    that load matters.
  - Symmetric signing, because the API issues and verifies and nothing else needs to.
    Keys live in `Auth:Tokens:SigningKeys`; the first signs, all verify, so keys rotate
    without breaking live tokens.
  - The issuer is the tenant's own address, so a token from one tenant is refused by
    another before its tenant claim is read.
- **Refresh tokens:** opaque, 256 random bits, stored as SHA-256 hashes in
  `identity.refresh_token`, rotating and family-chained (T14).
  - Presenting a spent token revokes the family: the real holder signs in again, and a
    copy is dead.
  - Two refreshes racing with one token are the same case, so clients must not refresh
    concurrently.
  - Lifetimes: a youth's session ends after 30 minutes unused (T14), an adult's after
    30 days, and none outlives 90 days.
  - Each token row carries how the family's sign-in was proved, so a refreshed session
    keeps its original sign-in time and never counts as a fresh one for step-up.
  - A password reset revokes every family. Signing out everywhere revokes them and
    changes the stamp, which also ends web sessions.
- **Shared hosting code:** tenancy by host, the principal factory, the actor and
  client-address middleware, the audit wiring and health checks moved from the web host
  into `BrightTime.Hosting`, so the API and web hosts can't drift apart.
- **What waits:** sign-in by passkey or external provider through the API, the other
  `/me` endpoints, registration and consent (M2), and a list of a person's sessions by
  device.
- **Question for you:** 30 minutes idle is right for a shared device. For a youth's own
  phone it means signing in most times the app opens. Passkeys or device binding in M2
  are the better fix than a longer idle window.
  *Answered 2026-10-19: keep 30 minutes for now, and revisit with passkeys or device binding on
  the app.*

### D29 — The local stack: built from db/schema on first boot, seeded on every up (**accepted 2026-10-18**)

`docker compose -f deploy/compose.yml up --build` now goes from an empty volume to the
seeded Kettle County site at `http://kettle.localhost:8000` in about 30 seconds (Q11).

- **Database:** PostGIS 18-3.6, the same 18.6 / 3.6.4 the suite is verified on. Compose,
  CI and the Testcontainers fixture all moved from 17-3.4.
- **Schema and logins:** on first boot, `deploy/postgres/init-brighttime.sh` applies
  `db/schema` in order as the superuser.
  - It then creates `bt_web_login` (app + finder), `bt_auth_login` and `bt_jobs_login`
    with a local-only password. The DDL still creates only NOLOGIN group roles.
  - A schema change needs `down -v`. EF migrations are not in the picture (Q12).
- **Seed:** a one-shot `seed` service runs `BrightTime.Seed` as the superuser on every
  `up`. It is idempotent and takes about 12 seconds. The hosts wait for it to finish.
- **Exposure:** only nginx (8000), mailpit's UI (8025) and Postgres (5432) are published,
  all on 127.0.0.1. Tenants' addresses carry port 8000, so nginx is the way in.
- **Removed:** minio. Nothing reads `Storage:*`, and `minio/minio` is no longer published
  on Docker Hub. Object storage comes back with the media-storage decision.
- **Images:** jobs and seed run on the aspnet runtime, because the modules reference the
  ASP.NET Core shared framework. Connection strings turn off GSS encryption, because the
  images have no Kerberos library and Npgsql tries GSS by default.
- **Verified through compose, without JavaScript:**
  - All 396 published listings page through `/search`.
  - Every filter, sort and the text search narrow the results.
  - All six locales render, and Arabic is `dir="rtl"`.
  - Listing pages and `.ics` downloads work.
  - Dev sign-in reaches the organiser console.
  - Approving a listing there projects it through the jobs container into search.

### D30 — The app's public read API (**accepted 2026-10-19**)

`/api/v1/public/**` serves the finder to the app with no account. It covers listings
(search and detail), collections, neighbourhoods, organisations, taxonomy and the tenant.
It uses the web's services and visibility rules, so the two can't disagree about what is
published.
- **Filters:** `category[]` matches any of its values, and `accommodation[]` requires all of
  them. `SearchQuery` has one any-of list, so `interest`, `language`, `dayOfWeek` and
  `modality` wait. `nearAddress` and `nearSchool` wait on Q7. `maxCost` is whole dollars,
  as on the web.
- **Errors:** RFC 9457 problems whose `type` is a URN per error class
  (`urn:brighttime:problem:validation`, `…:consent.method_insufficient`). Validation errors
  are keyed by parameter.
- **Language:** `?locale=`, then the account's own language, then `Accept-Language`, then
  the tenant's default. The choice is sent back in `Content-Language`, with
  `Vary: Accept-Language`.
- **Caching and limits:** responses may be cached publicly for 60 seconds. Each client
  address gets 300 requests a minute (`Api:PublicRequestsPerMinute`).
- **"Why it matched":** catalogue keys with arguments. The app renders them in its own
  language, with catalogues it shares with the web.
- **Privacy:** neighbourhood boundaries are public geography, the areas the web's map draws
  (D21), and no site's position is exposed. A private site's address is now stripped
  inside `SqlListingDetails` on the public path, as `OrganisationProfiles` already did, so
  no page, feed or API has to remember to hide it.
- **Not yet:** a separate OpenAPI document and CORS policy for third parties.

### D31 — Child profiles and scoped parental consent (**accepted 2026-10-19**)

M2's consent core, on the schema the plan laid down. Q3 was answered as recommended.
- **Child profiles:** a guardian is any adult account with a confirmed email.
  - A child profile is an under-13 account with no password. It holds a first name (an
    Identity `given_name` claim) and a birth month, nothing more.
  - It stays `pending_consent` until the guardian's link is verified. The schema's
    trigger refuses an active child account without an `account` consent.
  - Young people aged 13 and over make their own accounts.
- **Method by scope (for counsel to confirm):** COPPA permits email plus only where a
  child's information is not disclosed (16 CFR 312.5(b)(2)(vi)).
  - Email plus can stand behind `account`, `enrollment`, `attendance_photo`,
    `media_capture` and `location_use`.
  - A signed form reviewed in person is needed for `messaging`, `district_data_share`
    and `research_use`.
  - `community_participation` and `showcase_publication` aren't offered for under-13s at
    all, since the capability floor makes them moot.
- **Disclosures:** versioned, and pinned by `consent_text_hash`. A grant must name the
  hash of the text shown, and a stale hash is refused (409). **The texts are drafts:**
  legal review and translation are needed before launch.
- **Signed-form fallback:** `POST /organiser/guardian-links/{id}/attest` verifies the link
  and records the `account` consent. It needs the new permission
  `consent.attest_signed_form`, granted to network organisers only. Provider staff get it
  with enrolment (M3), when it has a scope. Nobody attests their own link.
- **One transaction per change:** each grant, revoke or link change commits with its audit
  row and a recompute of `youth_capability`.
  - A revoke takes effect on the child's next request, because principals are rebuilt
    every time (D28). This meets M2's acceptance that revoking removes the ability at once.
  - Revoking a link ends every consent given through it.
  - `ck_guardian_verified` makes a revoked link drop `verified_at`, and the audit log keeps
    when and how it was verified.
- **Email plus** (the consent email and its confirmation) came next, in D34, once account
  mail and localisation had moved out of the web host.

### D32 — Field-encryption keys: a vault reference per version, a derived key per tenant (**accepted 2026-10-20**)

The schema encrypts fields such as `push_device.token_enc` with AES-256-GCM in the
application (07-threat-model). Until now, no host had a key provider.
- **Vault:** `IKeyVault` resolves a key by reference.
  - `ConfiguredKeyVault` reads `Security:Keys:<ref>` (base64, 32 bytes or more), for local
    use and single-server installs.
  - Production binds a managed vault behind the same interface. References never change.
- **Tenant keys:** each tenant's data key is HKDF-SHA256 of the version's master key, with
  the tenant id as salt and the version in the info. None is stored, and nothing is
  provisioned when a tenant is created.
- **Rotation:** `Security:DataKeys` lists the versions held. The newest encrypts, and every
  listed version decrypts.
- **Development:** without configuration, Development makes up version 1 for the life of
  the process, as the audit hash key and token signing keys do. Anywhere else, a missing
  key stops the first encryption.
- **Tokens:** opaque tokens (push tokens) are found by an exact, purpose-separated HMAC
  under the tenant key (`HmacTokenHasher`). The blind indexer folds case, which would merge
  two case-sensitive tokens.
- **Limit:** destroying one tenant's data by destroying its key needs a wrapped key per
  tenant, not derivation. That provider can replace this one if offboarding requires it.

### D33 — The app's personal data, reminders and notifications (**accepted 2026-10-20**)

- **Personal data on the server:** saved listings, interests, a search anchor and a feed,
  under `/me/*`.
  - Up to 200 saved listings, each with an optional note, follow a signed-in person between
    devices. The web keeps its device cookie (D22).
  - Interests are codes from the region's vocabulary, weighted 1 to 5.
  - The anchor is a neighbourhood, never an address (D8). The feed searches from its centre.
  - The feed is the finder, with the person's interests as ranking tokens, which is where
    each item's "why" comes from. With no interests, it shows what starts soonest nearby.
- **Age recompute:** every capability recompute books the next one for the day the age band
  changes, keyed so a repeat books nothing twice. Nothing scans every account nightly.
  - A child profile keeps the under-13 floor after 13: the calculator reads its kind, and
    protections hold until the young person has a sign-in of their own.
  - The day is audited (`consent.turned_13`).
- **Reminders:** up to 50 waiting, before a listing's next deadline or next session. Each
  books a job for its moment. The job publishes `NotificationRequested`, which lives in
  Platform's contracts so any module can ask without referencing Notifications.
- **Notifications module** (planned as its own module, D1; depends on Platform and Identity):
  - Push devices store the token encrypted (D32). A phone that changes hands moves to the
    account now signed in on it.
  - Preferences cover each kind and channel, with one quiet window for pushes.
  - Delivery always writes the inbox (unless that kind is off there), and pushes outside
    quiet hours to accounts that may be messaged.
  - It's idempotent: the notification id is derived from the tenant and the request's dedupe
    key, so a retry inserts nothing and pushes nothing.
- **Text:** notification titles are catalogue keys, which the app renders as it does search
  results' "why". The `notify.*` strings join the shared catalogues with the app's own.
  *Amended by D35: the server words pushes and the inbox.*
- **Not yet:**
  - A real push provider: Firebase and APNs need your accounts. *The server side is built in
    D40.* Until then
    `LoggingPushSender` logs the kind and platform, never the token or content.
  - Email delivery, which waits on the shared mail library.
  - SMS, which waits on Q9.

### D34 — Shared words and mail, email plus, and registration from the app (**accepted 2026-10-20**)

M2's third slice: what the API host and the app need from the web host, and the email-plus
round trip D31 left for it.
- **The words move to `BrightTime.Localisation`,** a new plain library. It holds the six
  catalogues, the ICU subset, `Catalogue`, `Text` and the wording-set types. It depends on the
  shared kernel and logging abstractions only.
  - **Why not `BrightTime.Hosting`:** Hosting is an ASP.NET Core library over Infrastructure
    (EF, Npgsql), and the MAUI app can reference neither. The app needs the same catalogues,
    for D30's "why it matched" keys and for its own strings.
  - **The server half stays in Hosting.** `Text` reads its tenant, locale and time zone through
    `ITextContext`. Hosting adapts the tenant context to it, reads overrides from
    `platform.resource_override` (`SqlWordingOverrides`) and registers it all with
    `AddBrightTimeText`. The app will bring its own context, and no overrides for now.
  - The web keeps its locale middleware, cookie and pages. The catalogue, template and
    reading-level tests move to `tests/BrightTime.Localisation.Tests`.
- **Account mail moves to `BrightTime.Hosting/Mail`:** `AccountMailDispatcher`, plus
  `AddBrightTimeEmail` (`Email:*`, `SmtpEmailSender`) and `AddAccountMail`. The web's mail is
  unchanged. The API host registers both, and compose points it at mailpit.
- **Email plus:**
  - **The token.** Making a child profile issues a 256-bit token. Only its SHA-256 is stored,
    on `guardian_link` (`email_token_hash`, `email_token_issued_at`, `email_token_expires_at`).
    `ck_guardian_email_token` allows one on a pending link only, and verifying by either
    method, or revoking, clears it. It lasts 3 days.
  - **The consent email** carries the `account` disclosure word for word, since its hash is
    what the consent row records. Its link is `https://{tenant}/app/guardian-links/{id}/verify#token=…`.
    The token is in the fragment, which no server or proxy log sees.
  - **The answer.** `POST /api/v1/guardian-links/{id}/verify {token}` must come from that
    link's guardian, signed in, so a forwarded email is no use to anyone else.
    - Wrong, replaced and spent tokens are all refused the same way (403 `consent.token_invalid`).
    - Only the right token can learn it has expired (412 `consent.token_expired`).
    - A verified link answers 409, and the row is locked for the check, so a double tap
      verifies once.
  - **The plus.** On success `VerifyCoreAsync` records the `account` consent with method
    `email_plus`. A confirmatory email then says consent was given, repeats the disclosure and
    says how to withdraw it.
  - Both emails are audited, as `consent.email_sent` and `consent.confirmation_sent`, with
    whether the mail server took them.
  - `POST /guardian-links/{id}/consent-email` replaces a lost or expired email, at most once a
    minute (429).
- **Registration from the app:** `POST /api/v1/auth/register {email}` answers 202 for every
  well-formed address, as D23 does.
  - The account is found or made (`FindOrCreateAdultAsync`), and the email composed, after
    the response, so neither the answer nor its timing says whether the address had an account.
  - A new account gets a set-password link. An existing one gets a reset link and a line
    saying it already exists, in the email only its owner reads. A youth or child address
    gets nothing.
  - Audited as `auth.registration.requested`, with whether the account was made.
- **Drafts (Q13):** the new email and error strings are machine-drafted in five languages and
  listed in each catalogue's `@@drafts`, which a test keeps the same everywhere. The disclosure
  itself stays English: translating it means a disclosure version per language, each with its
  own hash, which waits on counsel and Q13. A non-English email says so and marks that
  paragraph `lang="en"`.
- **Not yet:**
  - The app's page for `/app/guardian-links/{id}/verify`, and the app-link files
    (`apple-app-site-association`, `assetlinks.json`). Until then the link is a 404 on the web.
    *Done in D35.*
  - A forgot-password route in the API. The set-password link opens the web's
    `/account/reset`, as D23's does.
  - Mail is queued in process, as on the web, so a restart loses a queued email and the
    person asks again.

### D35 — Youth accounts, the child-to-youth claim, and the pages behind the app's links (**accepted 2026-10-20**)

The rest of M2's server work: young people's own accounts, and what a consent email's link
opens when no app claims it.
- **Registration by age:** `POST /api/v1/auth/register {email, birthMonth}`. The app asks the
  month and year of birth first, as a neutral age question, and the server takes the younger
  reading of the month, as everywhere.
  - **Under 13:** 403 `register.under_13`, with nothing kept or sent. A child's account is
    their guardian's to make (D31). The server cannot stop someone changing the month and asking
    again, so the app should remember the answer on the device.
  - **13 to 17:** a youth account. It keeps the birth month only, as a child profile does, with
    no date of birth. Nobody is signed in to recompute its capabilities, so the worker does,
    booked at once.
  - **18 or older, or no month:** an adult account, as D34 makes it. No birth month is kept.
  - The answer is 202 for every address, as in D34, and depends on the month alone. An existing
    account is left as it was, whatever kind it is.
- **T2 in the calculator:** a youth with no guardian link may upload, and organisations may
  message them, only once an organisation has vouched for them. That was already the rule for
  posting.
  - Nothing sets "vouched for" until enrolment (M3). Until then a self-registered youth can
    browse, save and enrol (with the terms accepted), and no more.
  - A youth with a guardian link follows the guardian's consents, as before.
- **The claim (child to youth):**
  - **The request.** From the day the child turns 13, by the younger reading, their guardian asks
    `POST /api/v1/children/{id}/claim {email}`, naming the young person's own address. The answer
    is 202, and the endpoint is rate limited.
  - **The email.** After the response, a link goes to that address. It works once, for 3 days,
    and is kept like a reset token: only its hash, with the address it is for. If the address
    already has an account, its owner is told instead, and nothing changes.
  - **The hand-over.** The young person chooses a password, at `/account/claim?token=…` on the
    web or with `POST /api/v1/auth/claim {token, password}` from the app. Only then does the
    account change hands: in one statement it becomes a youth account, with the address
    confirmed and the password set. Until then it is a child profile, with the under-13 floor.
  - **The guardian stays.** The link and its consents carry on. The guardian still sees the
    account (`accountKind: youth`) and still consents for it, as the calculator says for a
    managed teen. The scopes closed to under-13s (`community_participation`,
    `showcase_publication`) can now be granted, if the link's verification method allows them.
    For the young person to consent for themselves, the guardian ends the link. There is no
    route yet for the young person to ask for that.
  - **The record.** The worker recomputes the capabilities, booked at once. The steps are audited
    as `consent.claim_requested`, `consent.claim_email_sent` and `consent.account_claimed`.
- **The consent link on the web:** `/app/guardian-links/{id}/verify`, the page a consent email's
  link opens when no app claims it.
  - The guardian, signed in, sees the child's name and the account disclosure, and gives consent
    as the API does. The confirmatory email follows.
  - The token stays in the fragment. `consent-link.js` keeps it in the tab's session storage
    while the guardian signs in, puts it in the form, and forgets it once the link is verified.
  - Without JavaScript the page can't read the token, and says to turn it on or use the app.
  - The consent email now says the link works in the app or on the website.
- **App links:** `/.well-known/apple-app-site-association` and `/.well-known/assetlinks.json`,
  built from `AppLinks:*`. They answer 404 until an app is configured.
  - They claim `/app/*` only. Claim and reset links stay on the web.
  - They also share passwords and passkeys between the site and the app (`webcredentials`,
    `get_login_creds`).
- **Notification text (amends D33's "Text"):** the server words pushes and the inbox.
  - A request names `notify.<what>`, whose `.title` and `.body` are in the shared catalogues.
  - A push is worded in the language its phone was registered in. The inbox is worded in the
    request's language, so the two always match.
  - Instants are written as a day and time in the tenant's zone, and names are isolated.
  - A push needs words, so it can't be left to the app.
  - The jobs host now has the shared catalogue and the tenant's wording overrides.
- **Not yet:**
  - "Vouched for by an organisation" has no source until enrolment (M3).
  - A young person has no route to accept the terms, which self-enrolment needs.
  - The API checks no passwords against breaches (the web's PwnedPasswords check), so a claim
    from the app skips that check. The web page makes it. *Done in D39.*
  - The real app identifiers for `AppLinks:*` wait on the developer accounts.
  - A young person's own export and delete requests wait on Q5.

### D36 — The app: MAUI Blazor Hybrid over a shared, tested library (**accepted 2026-10-20**)

M2's app, as docs/plan/01 lays it out.
- **Three projects:**
  - **`BrightTime.Ui.Shared`** holds the screens, the API client, the session and the region's
    words. It is plain `net10.0`, so it builds and is tested on any machine.
  - **`BrightTime.Ui.Design`** holds the stylesheet. Every margin, padding and alignment is
    logical, so Arabic mirrors with no second stylesheet. Tap targets are at least 44px, focus
    is always visible, and dark mode is included.
  - **`BrightTime.Mobile`** is the MAUI host: a BlazorWebView and the device services. It builds
    for Android anywhere the workload is; iOS needs the Mac.
  - The host is not in `BrightTime.slnx`, so the main build and CI need no mobile workloads. CI
    builds it in its own job (`mobile`).
- **One build per region:** each region is its own white-label app.
  - The region's addresses are build properties (`BrightTimeApiBase`, `BrightTimeSiteBase`,
    `BrightTimeLinkHost`), written into `BuildConfig`. A release build without them fails.
  - Debug points at the local stack from the Android emulator (`10.0.2.2:8000`). It sends
    `Host: kettle.localhost`, because the stack tells regions apart by host. Only `10.0.2.2`
    may use plain HTTP.
- **Sign-in (D28's tokens):**
  - The access token is kept in memory only, and refresh tokens rotate.
  - An adult's refresh token is kept in the iOS keychain or Android Keystore, so they stay
    signed in.
  - A young person's never is (docs/plan/06: no "remember me", since devices are often shared),
    so closing the app signs them out.
  - Refreshes run one at a time, because a refresh token used twice ends the whole family.
  - Offline doesn't sign anyone out.
- **Words:**
  - The shared catalogues, with 124 new `app.*` strings drafted in six languages and listed in
    `@@drafts`.
  - The reading-level gate now covers `app.*`.
  - Times are written in the region's zone, not the phone's.
  - Problems are worded from the error code, never from the server's English title.
- **The API's shapes** are written out in the app, not shared with the server.
  `AppClientContractTests` runs the app's client against the real API, which keeps the two in
  step.
- **Screens:**
  - home: the feed with "why", or what starts soonest;
  - search with a filter sheet;
  - listing detail, with save, remind, share and calendar;
  - saved items, readable offline;
  - sign-in, and registration with a neutral age question;
  - interests and home area;
  - the inbox and notification settings;
  - language and account;
  - family: children, adding one, the consent centre with each scope's exact disclosure, and the
    claim at 13;
  - the consent link.
- **Capabilities:** what a young person's account may do is read from `/me/capabilities` and
  shown as the server says. The app works out nothing about consent itself.
- **The device:**
  - "Near me" asks for a coarse, foreground position only when tapped. The nearest
    neighbourhood is chosen on the phone, and only its code is sent (D8).
  - Saved items are kept as JSON in the app's private folder, so the list opens offline.
  - Share, a provider's website and the calendar file go to the system.
  - "Forgot your password?" opens the web page, as the emailed links do.
- **Links (D35):**
  - Android claims `https://{region}/app/*` (auto-verified), and iOS through associated
    domains. Each link is passed to the UI.
  - A consent link's token stays in the fragment, and in memory while the guardian signs in.
- **The age question:** an under-13 answer sends nothing and is remembered on the device for a
  day.
- **Checked on the emulator** (Pixel 7, Android 16) against the compose stack: the feed, a
  listing, Arabic laid out right to left, and a consent link opening the app from cold.
- **Not yet:**
  - **iOS:** the build and its universal-link handling are written but not compiled. They need
    the Mac.
  - **Push:** `IPushRegistrar` returns nothing until Firebase and APNs are set up, so the inbox
    works and the phone gets no pushes.
  - **Map:** waits on the tile proxy (Q7).
  - **My schedule and local notifications:** wait on enrolments (M3).
  - **Passkeys** in the app.
  - **The release:** real app identifiers and signing (the entitlements name `kettle.localhost`)
    and store compliance (M8).
  - **Physical devices:** M2's acceptance asks for physical iOS and Android devices. Only the
    emulator has run it so far.

### D37 — The terms of use, agreed to by version (**accepted 2026-10-20**)

Self-enrolment for a young person needed the terms accepted (the calculator's `TermsAccepted`),
and nothing could accept them.
- **One current version**, kept in code (`AccountTerms.Current`) and pinned by the SHA-256 of
  its exact words, as a parental consent's disclosure is (D31).
- **The record:** agreeing writes a `consent_record` (`terms`, the version, the hash, the client
  address's hash), the table the schema already had for a person's own agreements. It is now
  mapped. `app_user.terms_accepted_at` is set too.
- **Capabilities read agreement to the current hash, not to any version.** A new version is
  agreed to afresh before self-enrolment works again.
  - Accounts recompute when they agree.
  - Stored rows from before a new version change only at the next recompute. A deployment that
    changes the terms should book one for every youth account. *Built in D39: the worker's terms
    sweep.*
- **The API:**
  - `GET /api/v1/terms` (anyone) gives the words, version and hash.
  - `GET /api/v1/me/terms` says where the person stands.
  - `POST /api/v1/me/terms {hash}` records agreement. A stale hash gets 409 `terms.stale`.
    Agreeing twice changes nothing.
  - A child profile can't agree, since it has no sign-in; its guardian consents for it.
  - The recompute runs in the request, as the person is signed in.
- **The app:** a terms page, linked from Account and from the "can't sign up yourself yet" line.
- **The text is a draft.** It is plain-language, written for a 13-year-old, and English only
  until counsel reviews it and it is translated (Q13). The web shows no terms page yet.
  *It does since D40.*
- Audited as `account.terms_accepted`, with the version.

### D38 — Passkeys from the app (**accepted 2026-10-20**)

docs/plan/05's `/auth/passkeys` routes, which T21 prefers for young people.
- **The same passkeys as the web.** The same store (`user_credential`) and the same relying party:
  the region's site, by host. A passkey made on the web signs in to the app, and the other way
  round.
- **The app's origins:** iOS ceremonies come from the site's origin, through the associated
  domain (D35's `webcredentials`). Android's come from `android:apk-key-hash:<signing cert hash>`,
  which `Passkeys:AppOrigins` lists. D35's `assetlinks.json` (`get_login_creds`) lets Android
  use them.
- **The API:**
  - `POST /auth/passkeys/options` and `POST /auth/passkeys` add a passkey.
  - `POST /auth/passkeys/assert/options` and `POST /auth/passkeys/assert` sign in for tokens, as
    the password grant does.
  - `GET /me/passkeys` lists them, and `DELETE /me/passkeys/{id}` revokes one.
  - With no cookies, a ceremony's options travel back as `state`: sealed by data protection,
    bound to their purpose (and for a registration, to the account), and good for 5 minutes.
  - **The API host's data protection keys are its default, per instance.** That works for one
    instance. Several need shared keys, or a ceremony started on one fails on another. *Shared
    since D39.*
  - Adding and removing a passkey need a sign-in in the last 15 minutes (T21's step-up), read
    from the token's `auth_time`, which a refresh keeps. An older session gets 403, and the app
    asks the person to sign in again.
  - Every failed sign-in gets the password grant's single refusal (`invalid-grant`) and is
    audited as on the web.
- **The app:**
  - "Sign in with a passkey" comes first on sign-in where the phone has passkeys.
  - The passkeys page lists, adds and removes them.
  - `IPasskeyAuthenticator` is the platform's part. On Android it is Credential Manager
    (`Xamarin.AndroidX.Credentials` 1.5.0.3, the newest that agrees with MAUI 10.0.110's AndroidX
    versions), which takes and returns WebAuthn JSON untouched.
- **Tested** with a software authenticator against the real API: adding, signing in, a tampered
  signature, a foreign origin, replayed state, revoking, and the step-up refusal. One test runs
  the app's own client end to end.
- **Not yet:**
  - **Android on a device:** Credential Manager needs a Google account on the phone, and a
    region whose `assetlinks.json` Android can fetch over HTTPS. Neither is possible on this
    emulator against `kettle.localhost`, so the Android path is compiled but not run.
  - **iOS:** passkeys need AuthenticationServices code, which can't be compiled here, so the
    buttons stay hidden on iOS.
  - **Several API instances** need shared data protection keys (above). *Done in D39.*

### D39 — A shared, encrypted key ring, the terms sweep, and passwords from the app (**accepted 2026-10-20**)

Three loose ends from D37 and D38.
- **The key ring** (ASP.NET Core data protection, which seals cookies, antiforgery tokens and
  passkey ceremonies) lives in `platform.data_protection_key`, read by every instance of the web
  and the API host.
  - Each key is encrypted before it is written: AES-256-GCM under a vault key
    (`Security:Keys:data-protection`, D32; `DataProtection:KeyRef` names another). The key
    reference is bound in as associated data, so the table holds nothing usable without the vault.
  - The table is append-only by grant (no update, delete or truncate) and holds no tenant data,
    so the isolation matrix lists it as global, as it does `platform.blind_index_key`.
  - Each host keeps its own application name, so one host can't open what the other sealed.
  - **Production needs `Security:Keys:data-protection`** in both hosts, or the first cookie fails.
    Development makes one up per process, as for the other keys, so restarting signs everyone
    out.
  - The web's sample mode, which has no database, keeps the framework's per-machine keys.
- **The terms sweep** (D37's missing step):
  - On the worker's start, each tenant's stale capability rows get a recompute. Stale means a row
    that allows self-enrolment with no agreement to the current terms' hash.
  - Each booking is keyed by the account and the terms' hash, so a restart books nothing twice.
  - After a deployment that changes nothing, the set is empty.
  - Tenants are listed from `platform.tenant_domain`, the one table the jobs role reads without
    a tenant.
- **Passwords from the app:**
  - `POST /api/v1/auth/password/forgot {email}` answers 202 for every well-formed address and
    sends the web's reset link after the response (T21).
  - `POST /api/v1/auth/password/reset {token, password}` sets the new password and ends every
    session, as the web's page does.
  - The app asks for the link itself and no longer opens the web's form.
  - The API now checks new passwords, from a reset or a claim, against Have I Been Pwned by hash
    prefix when `Auth:CheckBreachedPasswords` is on, as the web does (D34 and D38 noted the gap).

### D40 — Push through FCM, and the terms on the web (**accepted 2026-10-20**)

- **FCM, ready for the account:** `FcmPushSender` sends through Firebase Cloud Messaging's HTTP
  v1 API. It handles Android directly, and iOS through APNs once the app registers with Firebase
  there.
  - It is configured by `Push:Fcm:ProjectId` and `Push:Fcm:ServiceAccountJson` (the service
    account's key file, a secret from the deployment's store). While unset, pushes are only
    logged, as before.
  - The OAuth access token is minted from the service account (an RS256-signed JWT exchanged at
    Google's token endpoint, RFC 7523) and reused until 5 minutes before it runs out.
  - A message carries the words, already in the phone's language (D35), the kind and an in-app
    path, and nothing personal beyond that.
- **Forgotten tokens:** `IPushSender` now says what became of a push (sent, failed, or
  unregistered). A token FCM no longer knows (`UNREGISTERED`: the app was removed, or the token
  was replaced) revokes that device, so nothing is pushed to it again.
- **The app's side waits for the account:** registering for a token needs the Firebase project's
  `google-services.json` (and `GoogleService-Info.plist` on iOS). Until then the app's
  `IPushRegistrar` stays `NoPush`, so the inbox works and the phone gets no pushes.
- **The terms on the web:** `/terms` shows the current terms to anyone. A signed-in person agrees
  there as in the app, through the same `AccountTerms`, by the hash of the words shown. Every
  page's footer links to it, and the axe gate covers it.

### D41 — External sign-in from the app, through the web, by one-time code (**accepted 2026-10-20**)

docs/plan/05's `/auth/external` for the app, done the way RFC 8252 has native apps do it: the
provider's flow runs in the system browser, never in the app.
- **The flow:**
  - The app makes a PKCE verifier and its S256 challenge (RFC 7636), and a state.
  - It opens the system browser at the web's `GET /sign-in/external/app/{provider}?challenge&state`.
  - The web runs the provider's flow exactly as for itself (D17), then hands
    `org.brighttime.app://external#code&state` to the app's own scheme.
  - The app checks the state, then trades the code and verifier for tokens at
    `POST /api/v1/auth/external/token`.
- **The code** is 256 random bits, kept only as a hash in `app_user_token` with the challenge. It
  lasts two minutes and is spent on the first attempt, right or wrong.
  - Without the verifier it buys nothing, so another app that claims the scheme and catches the
    redirect gains nothing.
  - Starting a flow for someone else (a GET, so with no antiforgery token) gains nothing either:
    the code goes to the device that finishes the flow, bound to a verifier only the starter holds.
- **D17 holds:** only an account already connected to the provider gets a code. An unconnected
  one hands back `error=unconnected`, and the app says to connect it on the web. Nothing is
  created or matched by email.
- **Providers:**
  - `GET /api/v1/auth/external/providers` lists the web's `Auth:External`, so a deployment gives
    both hosts the same section.
  - `Auth:AppCallback` names the app's scheme, `org.brighttime.app://external` by default.
- **The app:**
  - Sign-in shows "Sign in with {provider}" for each provider.
  - MAUI's WebAuthenticator runs it: a Custom Tab on Android, an ephemeral authentication session
    on iOS, so a shared device doesn't stay signed in to the provider.
  - Android's `ExternalSignInCallbackActivity` claims the scheme, and iOS's `Info.plist` declares
    it.
- **Tested** with the web's fake OpenID provider through to the hand-back (connected,
  unconnected, malformed starts), the redemption (wrong verifier, replay), the API's exchange,
  and the app's state check and S256.
- **Not yet:** a run on a device against a real provider. That needs a configured provider and
  the region's real host.

### D42 — M3 is split in two, and built in slices (**accepted 2026-10-20**)

docs/plan/09's sequencing note, taken: M3 is the riskiest milestone, so it ships as two.
- **M3a, registration:** the family profile, forms, eligibility, applications, waivers,
  decisions and waitlists. No money, no offline.
- **M3b, operations:** attendance with offline sync, messaging, payments and reports.
- **M3a's slices, in order:**
  1. the family profile (D43);
  2. `profile_share`, per-organisation and revocable;
  3. form definitions and the listing's form;
  4. applications (draft, idempotent submit, withdraw) with eligibility;
  5. waivers with e-signature;
  6. decisions, seat claims under `FOR UPDATE`, waitlists and offers.
- **Why:** a usable registration flow reaches providers a month earlier, which is where the
  requirements feedback is. Money and offline sync each carry their own risk, and neither
  should hold up the other.
- **The new module** is `BrightTime.Modules.Enrollment`, with its own `.Contracts`. It may
  reference the Platform, Identity, Directory and Opportunities contracts, and the
  architecture tests hold it to that.

### D43 — The family profile: owned, encrypted, audited (**accepted 2026-10-20**)

M3a's first slice: a household entered once and reused for every application.
- **Ownership:** one profile per account, made by its owner and read or changed by that owner
  only. The check is in `FamilyProfiles`, row by row (D4), on top of RLS's tenant isolation
  and actor guard.
  - A child account can't hold one (`family.child_account`). A guardian's profile lists the
    child instead.
  - Organisations get nothing here. They see a profile only through `profile_share`, which is
    the next slice.
- **What it holds:**
  - The household: size, primary language, and whether it needs an interpreter.
  - Up to 15 members: role, names, date of birth, grade, pronouns, and relationship. A youth
    or child needs a date of birth (the table's own check).
  - Each member's support needs, up to 10. Each says whether programs may see it.
  - Up to 10 contacts, each marked as an emergency contact and/or allowed to pick up.
- **Encryption (D32):**
  - Encrypted under the tenant's data key: names, dates of birth, support details, and
    contacts' names, phones and emails.
  - In the clear: the birth month (for age checks without decrypting, as for accounts),
    preferred name, pronouns and relationship.
  - A contact's phone also gets a blind index. A provider can find a contact by number
    without decrypting every row.
- **Audit:** every read is one `enrollment.family.read` row, and every change is one
  `enrollment.family.changed` row naming the change. Both are written in the same transaction
  as the change.
  - Every endpoint declares `[YouthRecord]`, and the response types are `[YouthData]`, so the
    boot guard holds them to it.
  - A refusal is audited too, as a `deny` row whose reason is the error code.
- **API changes from docs/plan/05:**
  - A member is changed with `PUT`, not `PATCH`. The encrypted fields are rewritten whole, so
    a partial update would only hide that.
  - The members come back inside `GET /family-profile`, with no separate list.
  - Contacts get their own routes: `/family-profile/contacts`.
- **Not yet:**
  - linking a member to their own account (`family_member.user_id`), which comes with
    applications;
  - school and neighbourhood on a member;
  - the app's screens;
  - support needs being seen by an enrolling program.

### D44 — Profile shares name their members, and organisations read them only while live (**accepted 2026-10-20**)

M3a's second slice: "one application serves many programs" without "every provider sees
everything".
- **A schema change:** `profile_share.member_ids uuid[] not null`.
  - The table named the fields shared but not whose. So applying for one child would have
    shared that child's siblings too.
  - A member removed from the profile drops out of every share when it is read; the array
    is not rewritten.
  - Also new:
    - checks on the field vocabulary, on a non-empty member list, and that the expiry is
      after the grant;
    - `ix_profile_share_org_live` for an organisation's inbox.
- **Fields:**
  - `household`;
  - `member_names` (with the role);
  - `member_birth_dates`;
  - `member_grade`;
  - `member_pronouns`;
  - `contacts`;
  - `support_needs`.
- **The family's side** is `GET`, `POST /family-profile/shares` and
  `DELETE /family-profile/shares/{id}`.
  - A grant names an approved organisation, optionally one of its listings, one or more
    members, the fields, and optionally an end date.
  - A second grant to the same organisation and listing replaces the first. The old one is
    revoked, and the history keeps both.
  - At most 50 live shares.
  - The list shows ended shares too, so a family sees who had what.
- **The organisation's side** is `GET /org/{orgId}/family-shares` and
  `GET /org/{orgId}/family-shares/{id}`.
  - **Who may read:** a member of that organisation at staff or above, holding
    `youth_record.read`. `OrganisationScopeHandler` now runs in the API too.
  - **What they see:** only the shared members, and only the shared fields. Everything else
    is null.
  - **When:** a revoked or expired share is `family.share_ended` (403). Another
    organisation's share is `family.share_not_found`.
  - **Audit:** every read is one `enrollment.family_share.read` row, with the organisation as
    `actor_org_id` and the share and fields in the detail. Every refusal is a `deny` row.
- **Support needs are never shown through a share yet.** The table's rule is that only an
  organisation with an active enrolment reads them, and enrolments come in slice 6 (D42).
  - Until then, `support_needs` in a share only records the family's consent.
  - The enrolment will show the needs that are both shared and marked `share_with_providers`.
- **Not yet:**
  - shares made from an application, which come with applications;
  - the provider console's screen;
  - the app's screens.

### D45 — Registration forms: typed questions, fixed once published (**accepted 2026-10-20**)

M3a's third slice: an organisation writes the questions its registration asks, beyond what
the family profile already holds.
- **The schema** (`form_definition.schema_json`) is a typed model, `FormSchema`, checked whole
  before it is stored. Nothing a provider writes is ever run.
  - **Kinds:** `short_text`, `long_text`, `number`, `date`, `yes_no`, `single_choice`,
    `multi_choice`, and `consent` (a statement the family must tick).
  - **Each question:**
    - a key (`^[a-z][a-z0-9_]{0,39}$`, unique on the form), which is what
      `application_answer.question_key` will hold;
    - a label, in English plus any of the other five languages;
    - optional help text;
    - `required`, and `sensitive`.
  - **Limits:** 1 to 60 questions, 2 to 30 choices on a choice question, a length limit only
    on text, a range only on numbers. A consent question is always required.
  - **`sensitive`** marks answers kept out of every report, export and aggregate
    (`application_answer.is_sensitive`). The family is shown the flag too.
  - **Stored canonically** (trimmed, dictionaries in key order), with its SHA-256 in
    `schema_hash`, so equal forms hash equal.
- **Lifecycle:**
  - A form starts as a draft (version 1), which can be edited.
  - Publishing fixes it. An application will record the version it answered, so "what were
    we asked?" has one answer.
  - `revise` makes the next version as a draft holding the same questions, one draft at a
    time.
  - `retire` stops a published version being attached anywhere new, and is refused while a
    listing still uses it.
  - The name is set when the form is made, and stays with it through its versions.
- **Listings:** `PUT` and `DELETE /org/{orgId}/listings/{listingId}/form` attach a published
  form to one of the organisation's own listings, or take it off.
- **Who:** only an organisation's owners and admins (`organisations.edit` at admin role or
  above) manage its forms. Staff run programs; they don't decide what families are asked.
- **Families:** `GET /api/v1/public/listings/{slug}/form` gives the published form in the
  request's language, falling back to English per question, with a 60-second public cache.
  - It sits under `/public/listings/{slug}` (D30), not docs/plan/05's `/listings/{id}/form`,
    so the app reaches a listing and its form the same way.
- **Not audited:** a form definition holds no one's data. Answers will be audited, with
  applications.
- **Not yet:**
  - the console's form builder;
  - forms shared across organisations (`org_id` null, a tenant-wide intake form);
  - pre-filling answers from the family profile.

### D46 — Applications, judged by declarative rules, submitted once (**accepted 2026-10-20**)

M3a's fourth slice: a family applies for one young person to one listing.
- **Eligibility rules** (`eligibility_rule`): a kind with a typed config, hard or advisory.
  They are evaluated here, never by provider code.
  - **This build evaluates three kinds:**
    - `age_at_start` (`min`/`max`, on an optional date, today otherwise);
    - `grade_range`;
    - `neighbourhood_in`.
  - **The other kinds wait for their data:** `school_in` and `district_resident` need a school
    lookup, `prior_enrollment` needs enrolments, and the badge and playlist kinds need M4.
  - **Each outcome** is met, not met, or not known. The verdict is false when a hard rule is
    not met, null when none fails but one can't be decided, and true otherwise. Advisory
    rules never change it.
  - **Organisation side** (owners and admins, as for forms):
    - `GET` and `PUT /org/{orgId}/listings/{id}/eligibility`;
    - `POST …/eligibility/preview`, a dry run against a made-up young person, with the saved
      rules or with rules not yet saved.
  - **Family side:** `GET /public/listings/{slug}/eligibility` shows the rules before
    applying.
  - Family members gain an optional neighbourhood, which must be one of the tenant's. It is
    coarse, never an address.
- **Applications:**
  - **Create:** `POST /applications {listingId, memberId}` needs an `Idempotency-Key`, and
    the same key returns the same draft.
    - The listing must be published and take registrations here (`builtin` or
      `waitlist_only`), and the member must be a young person in the household.
    - One live application per member and listing (the table's own index).
  - **Draft:** pinned to the listing's published form at the time. `PATCH` saves some
    answers at a time, each checked against its question; null clears one.
  - **Submit:**
    - checks every required question is answered and every required consent is ticked;
    - evaluates the rules. A failed hard rule stops it (`application.ineligible`, naming the
      kinds); an undecided one doesn't, and the provider decides;
    - shares the applicant with the organisation for that listing, with the fields the family
      chose (D44). These must include the name, because an organisation can't consider an
      application from no one;
    - stores the verdict and writes the reporting rows. Submitting again returns the same
      application.
  - **Withdraw:** withdraws a submitted application. A draft, never sent, is thrown away.
    The share stays until the family revokes it.
  - **Audit:** every read, change and refusal is audited (`enrollment.application.*`).
- **Sensitive answers are sealed.** Under the tenant's data key, they are stored in the
  snapshot as `{"sealed": …}`.
  - Their `application_answer` rows say only that the question was answered.
  - The original schema stored them in the clear. Reports exclude them anyway, so sealing
    costs reporting nothing.
- **A schema fix:** `waiver_signature.application_id` and `enrollment_id` restrict deletes
  instead of setting null.
  - The table is append-only, and its statement-level trigger refused the cascaded update
    even when no row matched. So no application or enrolment could ever be deleted.
  - Now a signed waiver keeps pointing at what was signed, and an unsigned draft can go.
- **Found, not fixed:** `organisation_vetting_event`, `listing_revision` and
  `moderation_action` are append-only too. Their `on delete cascade` / `set null` references
  make deleting an organisation, a listing or a community report fail the same way. Nothing
  deletes those today; it needs deciding before anything does.
- **Times** the module returns are rounded to the microsecond PostgreSQL keeps, so a value
  returned on write equals the value read back.
- **Not yet:**
  - the organisation's queue and decisions, and seats and waitlists (slice 6);
  - waivers (slice 5);
  - a guardian applying for a child who has their own account;
  - expiring stale drafts.

### D47 — Waivers: signed for the exact text, by age (**accepted 2026-10-21**)

M3a's fifth slice: e-signature for the releases a program needs before a young person joins.
- **The organisation's side** works as forms do (D45). Owners and admins create, edit a
  draft, publish (the text is then fixed), revise into a new version, and retire (refused
  while a listing uses it).
  - The text is Markdown, up to 20,000 characters, stored with `\n` line endings and no
    surrounding space. Its SHA-256 is `body_hash`.
  - A listing can ask for several waivers, each required or optional:
    `PUT` and `DELETE /org/{orgId}/listings/{id}/waivers/{waiverId}`.
- **The family's side:**
  - `GET /applications/{id}/waivers` gives each waiver the listing asks for: its text, its
    hash, and whether this young person has signed that text.
  - `POST /applications/{id}/waivers/{waiverId}/sign {typedName, hash}` signs it.
- **A signature is evidence.** It records:
  - the signer, and their role;
  - the young person;
  - the typed name;
  - the SHA-256 of the text, which the signer sends back so they can sign only the words that
    were published (`waiver.changed` otherwise);
  - when, and hashes of the address and browser it came from (the audit trail's keyed hashes).

  The table is append-only for every role, superuser included. Signing the same text twice
  returns the first signature, and a new version needs a new one.
- **Who signs is decided by age.**
  - For someone under 18, an adult account holding them on its family profile signs, as
    guardian. A youth account can't sign (`waiver.guardian_signs`).
  - Someone 18 or over signs for themselves (`youth_18_plus`). That waits on linking a member
    to their own account, so for now it is refused (`waiver.adult_signs`).
  - `requires_guardian` is kept and shown, and doesn't override age.
  - The guardianship is the family's own statement, as the profile is. It isn't a verified
    guardian link (D31); that comes if the county asks for it.
- **Submitting needs every required waiver signed** in the text it has at that moment
  (`application.waivers_unsigned`, naming them). Optional waivers never block.
- **Not yet:**
  - a drawn signature (`signature_media_id`; media storage is still open);
  - waivers in more than one language: the family signs the text the provider wrote;
  - the organisation's view of who signed (with decisions);
  - re-signing on enrolment.

### D48 — Decisions, seats and waitlists, under a row lock (**accepted 2026-10-21**)

M3a's last slice: an organisation decides its applications, and seats are counted right.
- **Seats:** one `seat_pool` per listing, `default`.
  - Only owners and admins set it: `PUT /org/{orgId}/listings/{id}/seats {capacity,
    waitlistCapacity}`. It can never go below what is already taken (`seats.below_taken`).
  - **Every change takes the pool's row `FOR UPDATE` first,** then moves the enrolment and
    the counters together. Two staff accepting different applicants for the last seat at
    once give it to exactly one; the other gets `seats.full`. The test races them.
  - `ck_seat_pool_taken` turns anything that slips past into a violation, never an oversell.
- **Decisions:** `POST /org/{orgId}/applications/{id}/decide {decision, note}`, and
  `bulk-decide` for up to 100, each reported on its own.
  - `review` moves a submitted application to under review.
  - `accept` takes a seat and makes an active enrolment.
  - `waitlist` takes the next position.
  - `decline` gives back a waitlist place if one is held.

  A decision that doesn't fit the application's state is `decision.not_allowed`.
- **Offers:** `POST /org/{orgId}/listings/{id}/waitlist/offer {count}` offers free seats to
  the front of the waitlist, holding a seat for each.
  - The family answers with `POST /applications/{id}/offer/accept`, which enrols, or
    `/offer/decline`, which gives the seat back.
  - Offers don't expire yet; that needs a job and a deadline the schema doesn't have.
- **Releasing:** a family withdrawing an accepted, offered or waitlisted application gives back
  what it held. Its enrolment row ends with the reason.
- **Statuses:** `declined` is the organisation's no. A family turning down an offer is
  `withdrawn`. `rejected` is left unused.
- **Who:** two permissions are new in code, `applications.view` and `applications.decide`.
  Program staff and provider admins hold them at their own organisation, as docs/plan/04
  has it.
- **What the organisation sees:**
  - submitted applications only, never drafts, oldest first;
  - the eligibility verdict;
  - the answers, sensitive ones unsealed, since the family gave them to this organisation;
  - the applicant's name and birth date only through the family's live share (D44). Revoke
    the share, and the application stays but who it's from goes.

  Every read and decision is audited with the organisation as `actor_org_id`, and refusals
  too, bulk items included.
- **A fix to D46:** a second child's submission to the same listing used to replace the
  family's share, cutting the organisation off from the first child. It now joins the share:
  the members and the fields are the union, which the family can see and narrow.
- **Not yet:**
  - a youth access grant on enrolment, which needs the member linked to an account;
  - offer deadlines;
  - several seat pools per listing (cohorts);
  - notifications to the family on a decision;
  - the console's queue screens.

### D49 — Attendance: rosters, records, offline sync and supersede (**accepted 2026-10-21**)

M3b starts with attendance, the platform's highest-risk dataset (docs/plan/07, T4). It comes
in slices:
1. rosters, records, offline batch sync and corrections, and a family's view (this decision);
2. rotating QR check-in and kiosk mode (`checkin_token`);
3. kiosk devices: enrolment, revocation and remote wipe (`kiosk_device`);
4. the app's offline roster (an encrypted store with a sync queue), and the 72-hour rule.

- **A new module,** `BrightTime.Modules.Attendance`, with its own contracts. It may reference
  the Platform, Identity, Directory, Opportunities and Enrollment contracts.
  - **Two new contracts feed it:**
    - `IListingOccurrences` (Opportunities): the meetings that exist and aren't cancelled;
    - `IEnrolmentQueries` (Enrollment): enrolments, rosters and a family's enrolments.
  - Attendance never reads another module's tables.
- **The roster:** `GET /org/{orgId}/listings/{id}/roster?occurrence=` lists every enrolled
  young person, with that meeting's record if there is one.
  - Each line shows only a display name (first name and last initial), and only where the
    family's live share names the young person.
- **Records:** `POST /org/{orgId}/attendance`. A record is taken for:
  - an active, seated enrolment at the organisation;
  - a meeting that exists and isn't cancelled, starts at most an hour from now, and started at
    most 30 days ago.

  A second record for the same meeting is `attendance.exists`; correct the first instead. The
  staff note is encrypted (`note_enc`).
- **Offline sync:** `POST /org/{orgId}/attendance/batch {deviceId, items}`, up to 500 items.
  Each item gets its own outcome:
  - `applied`;
  - `duplicate`, when `(device_id, client_record_id)` has been seen before. A replay is a
    success and writes nothing;
  - `conflict`, when someone recorded that meeting first. Theirs stands, and the server id is
    returned;
  - `rejected`, with the error code.

  Each batch leaves an `attendance_sync_batch` row with its counts.
- **Concurrency:** an organisation's attendance writes are serialised by a transaction
  advisory lock. Two phones syncing the same meeting can't race past `ux_attendance_live`
  into a 500.
- **Corrections supersede:** `PATCH /org/{orgId}/attendance/{id}` with a reason makes a new
  record, and the old one points at it.
  - The old record steps out of the live index (pointing at itself) before the new one goes
    in, so the immediate foreign key holds.
  - A superseded record can't be corrected again.
  - The reason is in the audit row, since the schema has no column for it.
- **Reading:** `GET /org/{orgId}/attendance?from&to&listingId&history` covers a window of up
  to 31 days, current records only unless history is asked for.
  - Records of an enrolment that ended more than 180 days ago are left out. That is the
    youth-access grace (T4); the grant itself waits on members having accounts.
- **Families:** `GET /me/attendance` shows the current records of everyone on the family's
  profile, never the staff's notes.
- **Who:** three new permissions, held by program staff and provider admins at their own
  organisation: `attendance.record`, `attendance.view_roster` and `attendance.offline_sync`.
  Mentors (assigned) come with M5.
- **Audit:** every read and write is audited with the organisation as `actor_org_id`:
  - `attendance.roster.read`;
  - `attendance.read`;
  - `attendance.recorded`;
  - `attendance.corrected`;
  - `attendance.synced`;
  - `attendance.family_read`.

  Refusals are audited too.
- `StoredNow()` moved to SharedKernel, since both modules round times to PostgreSQL's
  microsecond.
- **Tests:** the seeded staff accounts ran out. `ApiFixture.NewAdultAsync` registers a fresh
  adult through the API, so new tests stop competing for seeded accounts.
- **Not yet:**
  - retention (400 days, then aggregate-and-delete);
  - minutes derived from check-in and check-out;
  - the console's attendance screens.

### D50 — Check-in by QR: badges and kiosks, codes that name nobody (**accepted 2026-10-21**)

M3b's second attendance slice: checking in without anyone typing.
- **A code is 256 random bits,** shown as a QR and stored only as its SHA-256
  (`checkin_token`). It carries no identifier (docs/plan/07): a photographed code names
  nobody, and is spent or stale within minutes.
- **A badge** goes from the family to staff.
  - `POST /me/attendance/badges {enrollmentId}` gives the family's app a code for one of its
    own current enrolments. It lasts two minutes and works once (`youth_badge`).
  - Staff scan it with `POST /org/{orgId}/attendance/scan {token, occurrenceStartsAt}`, which
    records the young person present (method `qr`, checked in now).
  - The code is spent as it is scanned, even if the record is then refused, so a code is
    never tried twice.
- **A kiosk** goes from staff to the family.
  - `POST /org/{orgId}/checkin-tokens {listingId}` gives a staff screen a code for the
    listing. It lasts a minute and serves everyone who scans it in that time
    (`session_kiosk`); the screen asks for a fresh one each minute.
  - A family scans it with `POST /attendance/checkin {token, enrollmentId}`, which checks in
    one of their own young people (method `kiosk`).
  - The meeting is the one under way now, or starting within the hour (`checkin.no_meeting`
    otherwise).
- **Both paths write through D49's checks:** an active, seated enrolment at the organisation, a
  real meeting inside the window, and one live record per meeting. They are audited the same
  way, refusals included.
- **Who:**
  - issuing a kiosk code and scanning a badge need `attendance.record` at the organisation;
  - a family asks for badges and checks in only for its own enrolments.
- **Not yet:**
  - sweeping expired codes (`ix_checkin_token_sweep` is ready for a job);
  - binding kiosk codes to an enrolled kiosk device (`staff_scanner`), which is the next
    slice;
  - check-out by code.

### D51 — Devices: registered to sync, revoked to wipe (**accepted 2026-10-21**)

M3b's third attendance slice: the server side of docs/plan/07's T12, for offline data on staff
phones and kiosks.
- **Registering:** `POST /org/{orgId}/kiosk-devices {deviceId, label, siteId, appVersion}`, by
  anyone holding `attendance.offline_sync` at the organisation.
  - The device id is the app's own random id. Registering again refreshes the label and
    version.
  - An id belongs to one organisation (`device.taken`). A revoked id is never registered
    again (`device.revoked`): a wiped device makes a fresh one.
- **Offline sync needs a registered, unrevoked device.** D49's batch now refuses others with
  `device.unknown` or `device.revoked`, which refuses the whole batch. Each accepted batch
  refreshes the device's `last_seen_at`.
  - So a lost phone, once revoked, can't add records. Idempotent replay already meant it
    couldn't change any.
- **Revoking:** `POST /org/{orgId}/kiosk-devices/{id}/revoke {reason}` sets `revoked_at` and
  `wipe_requested_at`.
  - An admin revokes any of the organisation's devices. A staff member revokes those they
    registered, since a lost phone is reported by whoever lost it.
  - It is audited (`attendance.device_registered` and `attendance.device_revoked`).
- **Contact:** `POST /attendance/devices/contact {deviceId}` says whether to wipe now, and the
  most hours the app may stay offline before refusing to open (72).
  - It needs no sign-in, because a phone whose user has left must still learn to purge. It
    reveals only whether that id is registered and whether to wipe. It is rate-limited like
    the public API.
  - A signed-in member of the device's organisation also refreshes its last-seen time.
- **Listing:** `GET /org/{orgId}/kiosk-devices` shows the organisation's devices to its owners
  and admins.
- **Not yet:**
  - a device signing in as itself, without a staff account (kiosk mode unattended). It needs
    an authentication scheme and a credential the table doesn't hold. Until then a kiosk
    runs under a signed-in staff member;
  - the app's purge and its 72-hour refusal, which come with the offline roster (slice 4).

### D52 — The app's offline roster: sealed on the phone, synced once, wiped on word (**accepted 2026-10-21**)

M3b's fourth attendance slice, and the last: the staff roster in the app, working without a
signal.
- **The screen** (`/attendance`, linked from Account for anyone with `attendance.view_roster`):
  - lists today's meetings at every organisation the person works for. They come from a new
    `GET /org/{orgId}/attendance/meetings`: published listings' meetings, by default from six
    hours ago to eighteen hours ahead;
  - "take roster" brings a meeting onto the phone;
  - marks (present, late, absent, excused) are kept on the phone, with or without a signal;
  - "send now" syncs them.
- **What the phone holds** is docs/plan/06's minimum: the enrolment, the display name (only if
  the family shared it), and the meeting. It never holds a date of birth, support needs or
  contacts.
- **Sealed at rest:** packs, the queue of marks and the problems are AES-256-GCM documents.
  - The key is in the keychain or Keystore (`ISecretStore`); only ciphertext is in the app's
    storage (`IBlobStore`).
  - Each document's name is its associated data. A document that won't open counts as absent.
  - The sealed folder is out of backups: Android's backups are off, and iOS marks the folder
    excluded.
  - **A deviation from docs/plan/06, which named SQLCipher.** A pack and a queue are a few
    kilobytes read and written whole. Sealed documents protect them the same way without a
    native database, and the encryption is tested in the shared library rather than trusted
    to a binding.
- **Sync** is D49's batch under a device id of the roster's own, not the sign-in one, registered
  with each organisation (D51).
  - Every mark has a fresh client id. A batch whose answer was lost is resent safely, since the
    server says `duplicate`. A mark changed before sending is replaced under a new id, so an id
    once sent never carries different words.
  - Applied and duplicate marks leave the queue. Conflicts and refusals become problems, shown
    by name until dismissed.
  - Anything unsent stays. An unregistered organisation prompts the person to set up the phone
    with a label, then sends.
- **Wipe and lock (T12):**
  - **Wipe:** the server can say wipe, on contact or by refusing a batch as revoked. Then the
    key goes first, then every document, and the device takes a new id.
  - **Lock:** without contact for longer than the server's limit (72 hours, as it reports),
    the roster refuses to open. The marks wait; they aren't wiped.
  - **Expiry:** a pack is let go a day after its meeting, unless marks for it still wait.
- **Not yet:**
  - a run on a phone, in airplane mode, against the compose stack. The Android build compiles;
    the iOS build needs the Mac;
  - correcting a conflict from the phone (it's shown; staff correct it on the web or online);
  - the kiosk screens (badge and kiosk codes, D50), which come with the app's QR scanning.

### D53 — Messaging: organisations and families, never a pair with a minor (**accepted 2026-10-21**)

M3b's messaging, first slice: conversations between an organisation and a family, with
docs/plan/07's T1 enforced at every layer. Broadcasts come next (D54); SMS waits on Q9.
- **A new module,** `BrightTime.Modules.Community`, for the `community` schema, with its own
  contracts. It may reference the Platform, Identity, Directory, Opportunities and Enrollment
  contracts.
  - **New contract methods:**
    - `IDirectoryQueries.GetOrganisationMembersAsync`, to find a second adult;
    - `IEnrolmentQueries.FamilyOfAsync`, for the account behind an enrolment or submitted
      application;
    - `IEnrolmentQueries.FamilyOwnersAsync`, for D54.
- **Starting a conversation:** `POST /org/{orgId}/conversations {enrollmentId | applicationId,
  subject, body}`, by holders of the new `messages.send_to_family` (program staff and provider
  admins at their own organisation).
  - It opens a conversation with the account that owns that family's profile.
  - There is still no permission to message a young person alone; docs/plan/04's
    `messages.send_to_youth_1to1` stays a permanent never, with no row to grant.
- **When the family's account is a young person's:**
  - their consent must allow messages from organisations (`messaging.not_messageable`);
  - no contact restriction may keep the staff member away (`messaging.restricted`);
  - an admin of the organisation joins as `org_admin_observer`. With no second adult to add,
    it is refused: `messaging.unsafe_shape` (409; docs/plan/05 said 422, which `ApiProblems`
    has no kind for). The remedy is to ask the programme coordinator to join.
  - The thread tells everyone in it that the organisation reads it (`readByOrganisation`).
- **The database is the guarantee.** `enforce_safe_conversation()` checks every participant
  change at commit. New in this slice, `set_participant_minor()` makes `is_minor` the database's
  answer from the account (youth or child), whatever the write says. Before it, a lying update
  could pass a minor off as an adult and make a pair look like a group.
  - **Integration tests try every route to a pair, and each fails at the database:**
    - the direct insert;
    - removing adults one by one;
    - deleting participants;
    - the guardian and the observer leaving together;
    - declaring the minor an adult;
    - adding a second minor as staff.
  - The fourth route T1 names, a mentor added to a two-person space, waits for spaces.
- **Screening:** an adult's message into a conversation with a minor is checked for an attempt
  to move it off the platform. That means a phone number, an email address, a link, a social
  handle, another messaging app's name, or a request to be contacted privately.
  - A flagged message is held (`state = held`, with the reasons in `screen_result`), never
    silently dropped. Only its sender sees it, marked held, until a moderator decides; the
    moderation queue is a later slice. It errs towards holding.
- **Reading and writing:** `GET /conversations` (with unread counts), `GET /conversations/{id}`
  (which marks it read), and `POST /conversations/{id}/messages`, for participants only.
  - An announcement (`org_broadcast`, D54) takes no replies from families.
  - Every read, send and refusal is audited.
- **Notifications:** a sent message requests a `new_message` notification for every other
  participant except observers, and families can switch these off.
  - The push says only who wrote: "{org} sent you a message", with a link into the app. Never
    what the message says (T13).
- **Not yet:**
  - broadcasts (D54);
  - the moderation queue and releasing held messages;
  - closing a conversation;
  - the app's and the console's message screens;
  - SMS (Q9).

### D54 — Broadcasts: one announcement, no replies, no family named to another (**accepted 2026-10-21**)

M3b's messaging, second slice: telling every family on a listing at once.
- **Sending:** `POST /org/{orgId}/broadcasts {listingId, subject, body}`, by holders of
  `messages.send_to_family` at the listing's organisation.
  - It reaches the owners of the family profiles of the listing's active, enrolled young
    people (`IEnrolmentQueries.FamilyOwnersAsync`). Waitlisted families and applicants aren't
    included.
  - It is one `org_broadcast` conversation. Families read it and can't answer it
    (`messaging.broadcast`).
  - No family sees another: a participant sees the roles in it, never who.
- **Young people are treated as in D53:**
  - one whose consent doesn't allow organisation messages is left out;
  - so is anyone a contact restriction keeps from the sender;
  - with any young person among the recipients, an admin of the organisation observes it.
    Without one, the young people are left out rather than sent to without a second adult of
    the organisation's.

  The answer says how many families it reached and how many young people it left out. With
  nobody left to tell, it is `messaging.no_recipients` (409).
- **It is screened and notified as any message.** With a young person in it, the text is
  screened for contact exchange and held if flagged; then no one but the sender sees it. Each
  recipient gets the same push as for a message, "{org} sent you a message".
- **Not yet:**
  - broadcasts to waitlisted families or applicants;
  - scheduling a broadcast;
  - email as well as push for announcements;
  - SMS (Q9).

### D55 — Fees, sliding scales and payments, with no card and no income kept (**accepted 2026-10-21**)

M3b's payments, first slice: everything that doesn't depend on which provider Q6 picks. The
provider gateway and its webhooks come in D56.
- **Fees** (`fee_schedule`): `GET` and `PUT /org/{orgId}/listings/{id}/fees`, by the
  organisation's owners and admins.
  - Up to 10 fees per listing, exactly one the default. Each has an amount up to $100,000 and a
    cadence (`one_time`, `weekly`, `monthly`, `per_session`, `seasonal`).
  - A fee has up to 10 sliding-scale tiers, none dearer than the fee, each by household size
    (1 to 30) and an income band label.
  - A fee that a payment refers to can be changed in place (by its id) but not dropped
    (`fees.in_use`). The payment keeps its own amount.
- **No income is kept.** A tier names a band; the family picks the tier it fits when it pays,
  and only the amount is recorded. Household size, which the profile already holds, decides
  which tiers are offered: `GET /applications/{id}/fees` gives the listing's fees with only the
  tiers that household fits.
- **Paying:** `POST /payments/intents {applicationId | enrollmentId, feeScheduleId, tierId}`,
  with an `Idempotency-Key`.
  - Only the adult who owns the family profile pays (docs/plan/04: `payments.initiate` is
    18+), for an accepted or offered application or an active enrolment.
  - The tier must fit the household. While a payment for the same place is open, asking again
    returns it rather than a second one.
  - It goes through `IPaymentGateway`. The provider's client secret or hosted page comes back
    once, for the provider's own element, and isn't kept. No card data is ever held (T20).
- **The manual gateway** is the default, and the only one until Q6: cash, a cheque, a transfer
  the organisation takes itself. The intent waits as `requires_action` until an admin records
  it received (`POST /org/{orgId}/payments/{id}/received`). That works only for manual
  payments; a provider reports its own.
- **Waiving:** `POST /org/{orgId}/payments/{id}/waive {reason}`, with the reason required (the
  schema's own check). Recording and waiving are audited.
  - Both need the new `payments.waive`, which provider admins hold at their own organisation.
    The routes are under `/org/{orgId}`, not docs/plan/05's `/payments/intents/{id}/waive`, so
    the organisation is in the route.
- `GET /org/{orgId}/listings/{id}/payments` lists a listing's payments for its admins.
- **Not yet:**
  - the provider gateway (Stripe, if Q6 confirms) and signature-verified, idempotent webhooks
    (D56);
  - refunds;
  - fees by cadence over time (each payment is one charge);
  - the app's and the console's payment screens.

### D56 — Stripe, believed only by signature and applied by the worker (**accepted 2026-10-21**)

Q6 is answered: Stripe. This is the provider gateway D55 left out.
- **The gateway** (`StripePaymentGateway`) calls Stripe's HTTP API directly, with no SDK.
  - It makes a payment intent in cents with `automatic_payment_methods`, and pins the API
    version (`2024-06-20`), so Stripe changing its default changes nothing here.
  - The family's `Idempotency-Key` goes to Stripe, scoped to the tenant and the adult, because
    Stripe's keys span the whole account. A retried request makes no second charge.
  - The intent's client secret comes back for Stripe's own card element, so the card never
    reaches us (T20, SAQ-A). It isn't kept. When the family asks again while the payment is
    open, the secret is fetched from Stripe again.
  - Stripe's statuses map to ours. `requires_payment_method`, `requires_confirmation` and
    `requires_action` become `requires_action`; `processing` and `requires_capture` become
    `processing`; `succeeded` and `canceled` map to themselves.
- **Configuration:** `Payments:Stripe:SecretKey` and `Payments:Stripe:WebhookSecret`, both from
  the secret store. Stripe is the gateway only with both; otherwise it stays manual, because
  without the webhook secret nothing could confirm a payment.
- **Failures:** a refusal, or no answer within 30 seconds, is `payments.provider` (503), and
  no payment is kept.
  - Logs carry only Stripe's error code (for example `card_declined`), never its message.
  - Payments made earlier through another provider keep that provider. Only the manual gateway
    can mark one received.
- **Webhooks:** `POST /api/v1/webhooks/payments/stripe`, anonymous, on the tenant's own host.
  - It is believed only with a valid `Stripe-Signature`: an HMAC-SHA256 of
    `{timestamp}.{body}`, compared in fixed time, at most 5 minutes old so a captured callback
    can't be replayed.
  - A second `v1` signature is accepted while a secret is rolled.
  - Another provider's path is a 404 (`payments.webhook_provider`). A bad, missing or stale
    signature is a 400 (`payments.webhook_signature`). Bodies over 256 KB are refused.
  - A genuine event about nothing a payment changes gets a 200 and is ignored.
  - The route isn't rate-limited by address. Every callback comes from Stripe's few addresses,
    and a forged one costs only an HMAC.
- **Applied by the worker, not the request.** The actor guard on `payment_intent` refuses a
  write with no one signed in, and a callback has no one. So the verified event is queued as an
  `enrollment.payment_event` job carrying only the provider's reference and the status. The
  jobs host applies it under the tenant policy.
  - The job's key is the event id, so a redelivery that arrives while one is waiting
    collapses into it.
  - The status only moves forward, so redelivery and Stripe's arrival order don't matter:
    - `succeeded` applies from any open status, and from `failed`;
    - `processing` applies from `created` or `requires_action`;
    - `requires_action` applies from `created` or `processing` (a debit that bounced asks
      for another way to pay);
    - `failed` and `cancelled` apply from any open status;
    - `refunded` applies only from `succeeded`.
  - A late event never undoes a payment. A waived or refunded payment is left as it is.
  - A reference no payment in the tenant has is ignored.
- **Events read:**
  - `payment_intent.succeeded`, `.processing`, `.requires_action` and `.canceled`;
  - `.payment_failed`, which stays `requires_action`, because Stripe keeps the intent for
    another card;
  - `charge.refunded` when the charge is fully refunded.
- **For you to look at:**
  - A payment that succeeds after an admin waived it is logged as a warning (event 5602), not
    changed: money was taken, and someone has to refund it or correct the record.
  - Stripe Connect is not used. One Stripe account takes every organisation's fees. If
    organisations must be paid out directly, that is Connect, and a decision of its own.
- **Not yet:**
  - refunds started here (the gateway can, but no route asks it to; a refund made in Stripe's
    dashboard is reported back);
  - partial refunds;
  - the app's and the console's card screens (Stripe's element with the client secret).

### D57 — Provider reports, made on request and never stored (**accepted 2026-10-21**)

M3b's reports, first slice. docs/plan/09 names the provider reports (roster, attendance,
participation), so they are built now. The funder packet waits on Q10.
- **Three reports**, as PDF (QuestPDF), Excel (ClosedXML) or CSV:
  - **Roster**, for one listing: each young person enrolled now, the date they enrolled, and how
    many meetings they attended out of those recorded.
  - **Attendance**, for one listing over up to 31 days: each young person against each meeting
    (P, A, L, E, LE, ?), with a legend. It includes anyone whose enrolment ended within the
    180-day grace period (D49).
  - **Participation**, for the organisation over up to a year. It shows meetings, young people,
    attendances, records and the attendance rate by activity, plus young people by age band.
    It only counts.
- **Names follow the share.** A name appears only where the family's live share gives it;
  otherwise the row says "Name not shared". Staff notes are never included.
- **Small counts are hidden in participation**, the report an organisation hands to a funder.
  - `SmallCells` applies `analytics.suppress()`'s rule: zero is shown, and 1 to k−1 is hidden.
  - k is the larger of the tenant's participation and attendance thresholds
    (`analytics.min_cell`, at least 5, default 10).
  - Complementary suppression is on: a line with a shown total and one hidden figure loses its
    smallest other figure too.
  - Hidden figures show as "<10", and a note says how many were hidden and why (docs/plan/05's
    envelope).
  - A young person in two activities counts once in the totals. Age bands come from the month of
    birth, through a new `IEnrolmentQueries.FactsAsync` that carries no name or date of birth.
- **Made on request, never stored.** There is no object storage yet, and a stored roster is one
  more copy of children's names.
  - `POST /api/v1/org/{orgId}/reports/runs {reportKey, listingId?, from?, to?, format}` renders
    the report in the request and returns the file with `Cache-Control: no-store`.
  - Every run is a row in `analytics.report_run` (state `available`, no `media_id`, its row count
    and hidden-figure count).
  - Every run is audited (`reports.provider.*`) with the enrolments a named report covered.
    Refusals are audited too.
  - `GET /org/{orgId}/reports/catalogue` lists what the caller may run.
  - docs/plan/05's asynchronous `GET /reports/runs/{id}` waits for object storage and the funder
    packet.
- **Who:** a new `reports.run_provider` permission, which program staff and provider admins hold
  at their own organisation (docs/plan/04). Roster and attendance also need
  `attendance.view_roster`, so a role given only reports would see only counts.
- **Rendering** lives in a new `BrightTime.Reporting` project. It works in memory and writes
  nothing to disk.
  - Wording comes from the catalogues in the reader's language, and Arabic reads right to left.
  - CSV is UTF-8 with a byte-order mark, and a cell that would run as a formula is defused with
    an apostrophe.
  - An Excel cell is a number only in a numeric column; anything else is text.
- **For you to look at:**
  - QuestPDF's Community licence is free only under US$1M annual revenue. `Reports:QuestPdfLicence`
    sets the deployment's licence (`Community` by default). A county deployment may need
    Professional, or a different renderer.
  - Fonts: names in Bengali, Chinese or Arabic need fonts in the container image. Until fonts
    are bundled, a missing glyph falls back rather than failing the report.
- **Not yet:**
  - the funder packet (Q10);
  - demographics beyond age band;
  - the console's report screen;
  - scheduled or emailed reports.

### D58 — The provider console runs M3 on the same modules, starting with Reports (**accepted 2026-10-22**)

Every M3 provider feature so far is API-only: applications, rosters, attendance, fees, payments
and reports. docs/plan/06 puts them in the provider console. This starts that work.
- **The console runs the same modules the API does.** The web host now registers Enrollment and
  Attendance (`AddBrightTimeRegistration`).
  - It uses the same login (`brighttime_app`), the tenant's RLS, the actor guard, and family data
    decrypted under the tenant's data key (D32).
  - A console page is one more caller of the same services, not a second implementation. Only
    with a database, as every console page is.
- **One report runner for both hosts.** The checks, wording and rendering moved out of the API
  into `ProviderReportRunner` (`BrightTime.Reporting`), which the API and the console both call.
  - Each host still does its own authorisation and audits its own refusals.
  - Periods are normalised to UTC in the module. A period sent with a local offset (the console's
    days, or an API client's) used to fail in Npgsql.
- **The Reports page:** `/provider/orgs/{orgId}/reports`, a new Reports tab for those holding
  `reports.run_provider`.
  - Each report the caller may run is a form: an activity where one is needed, a period as days
    in the tenant's zone, and a file type.
  - It posts to `POST /provider/orgs/{orgId}/reports/download`, an endpoint, because a page can't
    answer with a file.
  - The endpoint checks antiforgery and the same permissions as the API, then returns the file
    with `Cache-Control: no-store`. A bad request goes back to the page with the reason.
  - The page reminds staff that the file isn't kept here, and that some reports name young
    people.
- **Next, in this order:**
  1. applications (queue, decide, bulk decide, waitlist offers);
  2. rosters and attendance with correction history;
  3. fees and payments (waive, mark received);
  4. registration forms, eligibility and waivers;
  5. messaging.

### D59 — Applications in the provider console (**accepted 2026-10-22**)

The first screen on D58's list. It calls D48's `Decisions` service unchanged.
- **Three pages** under a new Applications tab, shown to holders of `applications.view`:
  - `/provider/orgs/{orgId}/applications` lists the organisation's activities, drafts left out.
    It reads no application, so it audits nothing.
  - `/provider/orgs/{orgId}/listings/{listingId}/applications` shows an activity's queue,
    filterable by status.
    - Each row shows the applicant's name only where the family's live share gives it (otherwise
      "Name not shared"), the status, the date applied, the eligibility verdict and the waitlist
      place.
    - Places taken and the waitlist sit above the queue. Owners and admins set places there
      (`orgs.edit`, as the API's seats route).
    - With `applications.decide`, staff tick applications and decide them together, and offer
      freed places to the waitlist.
  - `/provider/orgs/{orgId}/applications/{id}` shows one application:
    - what the share allows (name, and birth date only if shared);
    - each eligibility rule (must be met or advice only) and whether it is met;
    - the answers, worded with the form's own labels and choices, sensitive ones marked;
    - the decision and its note.
    - With `applications.decide`, the page has a form to accept, waitlist, decline or mark as being
      reviewed, with a note.
- **Bulk decisions say what they left.** Each application is decided on its own merits (D48). The
  page reports how many were decided, and names each one left as it was with the reason (no
  place left, already decided). Each refusal is audited, as the API's bulk route does.
- **Audit:** the queue and application pages declare `[YouthRecord]`. The `Decisions` service
  writes the reads and decisions; the pages audit their own refusals (another organisation's staff,
  a decision refused).
- **Found while building:** a waitlisting keeps its note but sets no decided date, so the note
  has its own row rather than hanging off the date.
- **Not yet:** paging past 500 applications, sorting, and the family's side in the console.
  Families use the app.

### D60 — Rosters and attendance in the provider console (**accepted 2026-10-22**)

The second screen on D58's list. It calls D49's `AttendanceRecords` service unchanged.
- **The week:** `/provider/orgs/{orgId}/attendance` sits under a new Attendance tab for holders of
  `attendance.view_roster`.
  - It shows a week of the organisation's meetings by day, in the tenant's zone, with previous and
    next links. Each meeting links to its roster.
  - It names nobody, so it audits nothing.
- **The roster:** `/provider/orgs/{orgId}/listings/{listingId}/roster?at=…` shows each enrolled
  young person: a name only where the family's share gives it, the current mark and the check-in
  time.
  - With no meeting named, it opens the latest one that has started (or starts within the hour)
    this past week, else the next, else says there's none.
- **Marking and correcting,** with `attendance.record`, in one form for the whole roster:
  - an unmarked young person given a mark gets a new record;
  - a changed mark becomes a correction, which supersedes the old record and keeps it (D49);
  - a correction needs a reason, kept in the audit row, and carries over the old record's times,
    minutes and note, so only the mark changes;
  - rows stand on their own: a refused row (a meeting not started, more than 30 days old, an
    enrolment ended) says why next to the name, and the rest are saved.
- **Correction history:** the roster lists each corrected mark with what it was and when.
- **Audit:** the service audits reads, marks and corrections; the roster page declares
  `[YouthRecord]` and audits its own refusals.
- **Not yet:**
  - the QR scanner and kiosk mode in the console (D50's codes work from the app);
  - minutes, check-out and notes in the form;
  - showing a correction's reason, which only the audit trail holds.

### D61 — Fees and payments in the provider console (**accepted 2026-10-22**)

The third screen on D58's list. It calls D55's `FeeSchedules` and `PaymentIntents`.
- **Who:** a new Fees and payments tab for owners and admins, the same people the API lets manage
  them.
  - `/provider/orgs/{orgId}/payments` lists the activities, each with links to its fees and its
    payments.
  - The fees page needs `orgs.edit` as admin, as the API's fees route does. The payments page
    needs `payments.waive` as admin.
- **Fees:** `/provider/orgs/{orgId}/listings/{listingId}/fees`.
  - Each change is its own small form: add a fee, edit one, remove one, add a price level, remove
    one. The page sends the whole set to `FeeSchedules`, which checks it as before.
  - Amounts are typed as dollars ("1,200", "$12.50"). Exactly one fee stays the default: the one
    just made default, else the one already default, else the first.
  - Price levels name a household size and an optional income band. The page says plainly that
    families choose their own level and nobody is asked their income.
  - A fee a payment uses can't be removed (`fees.in_use`); the page says so.
- **Payments:** `/provider/orgs/{orgId}/listings/{listingId}/payments` shows who each payment is
  for, the amount, the status, in person or online, and when it started.
  - Names come from the applications queue, so only as the family shares them, and audited as
    that read is.
  - An open payment can be waived with a reason. One made in person can be marked received.
  - The page reminds admins that card details never pass through the platform.
- **A bug found here, fixed in the module:** changing a fee in place dropped its default flag.
  - `SetAsync` cleared every kept fee's default flag in bulk, but the fees were loaded with change
    tracking. EF then saw no change when the default was set back, and wrote nothing.
  - They now load untracked. The API test checks the flag survives.
- **Not yet:** refunds (D56), payments by date or status across activities, and editing a price
  level in place (remove it and add it again).

### D62 — The form builder in the provider console (**accepted 2026-10-22**)

The first part of D58's fourth item; eligibility rules and waivers follow. It calls D45's
`FormDefinitions` unchanged.
- **Who:** owners and admins (`orgs.edit` as admin, as the API's form routes), under a new Forms
  tab.
- **The forms page:** `/provider/orgs/{orgId}/forms` lists every version with its status and
  question count. A new form starts with a name and its first question, since a form needs one.
- **The editor:** `/provider/orgs/{orgId}/forms/{id}` shows one version.
  - A draft's questions change one small form at a time: add, edit, move up, remove.
  - A published version shows its questions read-only. It can be revised (the next version opens
    as a draft holding the same questions) or retired, which is refused while a listing uses it.
  - It can be attached to any of the organisation's listings, replacing whatever form that listing
    asked for, and detached again.
- **Providers write words, not keys.**
  - A question's key comes from its English label: lower-case ASCII, accents dropped, at most 40,
    numbered if taken. A label with no Latin letters, or one starting with a digit, gets `q`.
  - Choices are one per line, and each gets a key the same way.
  - Editing keeps the question's key, and the key of every choice whose English is unchanged, so
    answers already given keep their meaning. FormSchema still checks everything.
- **Other languages:** a question's label can be written in each of the other five languages,
  under a collapsed section. Help and choices are English only here; the API takes them all.
- **Sensitive questions** are marked in the builder. The page says what that means: never in
  reports, exports or ranking (T16).
- **Not yet:**
  - translating help text and choices in the console;
  - a preview of the form as a family sees it;
  - eligibility rules and waivers (next).

### D63 — Eligibility rules and waivers in the provider console (**accepted 2026-10-22**)

The rest of D58's fourth item. It calls D46's `EligibilityRules` and D47's `Waivers` unchanged.
Both are under the Forms tab, for owners and admins (`orgs.edit` as admin, as the API's routes).
- **Eligibility:** `/provider/orgs/{orgId}/listings/{listingId}/eligibility`, linked for each
  activity from the forms page.
  - The rules are written out in words: "Age at the start: 9 to 12 (on 1 Jul 2027)", or a list of
    neighbourhoods. Each is marked must be met or advice only, and can be removed.
  - A rule is added by kind: an age or grade range (with the date an age is taken on), or
    neighbourhoods chosen from the tenant's own. The service checks it; a backwards range comes
    back as "Rule 3: …".
  - **Try the rules:** a made-up date of birth, grade and neighbourhood run through the current
    rules (`PreviewAsync`). It shows the verdict and each rule's outcome, saves nothing and reads
    nobody real.
- **Waivers:** `/provider/orgs/{orgId}/waivers` lists every version, and a new waiver is written
  as a draft: a title, Markdown text, and whether a parent or guardian signs for anyone under 18.
  - `/provider/orgs/{orgId}/waivers/{id}` edits a draft's text.
  - A published version shows its text rendered and locked, because families sign that exact
    text and its hash.
  - A version can be published, revised into a new draft, or retired (refused while a listing asks
    for it). It can be asked for on any of the organisation's listings, as required or optional,
    and taken off again.
  - The page counts signatures but shows nobody's.
- **Not yet:** editing a rule in place (remove it and add it again), and who signed a waiver,
  which the organisation sees on each application.

### D64 — Messaging in the provider console (**accepted 2026-10-22**)

The last of D58's list. It calls D53 and D54's `Conversations` unchanged, so every safety rule is
the module's:
- no adult alone with a minor, so an admin observer is added or the conversation is refused;
- consent and contact restrictions are respected;
- an adult's message to a minor is screened, and held if it tries to move the conversation
  elsewhere.

The console host now registers the Community module and `IIdentityQueries`, which decides who may
be messaged.
- **Who:** holders of `messages.send_to_family` (program staff and provider admins), under a new
  Messages tab.
- **Messages:** `/provider/orgs/{orgId}/messages` lists the conversations this person is in for the
  organisation, newest first.
  - Each shows its subject, whether it is a conversation or an announcement, unread messages, and
    whether a young person is in it.
  - A conversation is opened from an application's page or a roster row ("Message the family"),
    which bring the application or enrolment along.
  - An announcement goes to every family with a child enrolled in a published activity. The page
    then says how many families it reached and how many young people it left out, and why (no
    consent, or no admin to read along).
- **A conversation:** `/provider/orgs/{orgId}/messages/{id}`, only for someone in it and only the
  organisation's own.
  - It names who is in it. When a young person is, it says an admin of the organisation reads it
    too, as everyone in it is told.
  - A message held for a moderator shows only to its sender, marked so.
  - Staff reply, announcements included.
- **Audit:** the module audits every read and send; the pages declare `[YouthRecord]`.
- **Not yet:** an organisation-wide inbox for admins of conversations they aren't in, closing a
  conversation, and SMS (Q9).

### D65 — M4 begins: participation from attendance, and the young person's portfolio (**accepted 2026-10-22**)

M4's first slice, in a new Learning module. It may reference the contracts of Platform,
Identity, Directory, Opportunities, Enrollment and Attendance (docs/plan/01's DAG), and the
architecture test now holds it to that.
- **A family member linked to their own account.** Until now nothing set
  `family_member.user_id`, so no enrolment ever belonged to a young person's account, and a
  portfolio would have stayed empty.
  - `PUT /family-profile/members/{id}/account {userId}` links a youth or child member to the
    owner's own account or to one they are a verified guardian of. `null` unlinks.
  - Nobody else's child can be claimed (`family.link_forbidden`), and an adult member can't be
    linked (`family.link_role`).
  - The member's enrolments follow (`enrollment.youth_user_id`), and each publishes
    `EnrolmentAccountChanged`.
- **Participation from attendance.**
  - Attendance publishes `AttendanceChanged(enrollmentId)` on every write: a record, a check-in,
    a sync and a correction.
  - The worker's `ParticipationProjector` rebuilds that enrolment's `participation_record` whole
    from its current records, so a correction, a replayed sync or a redelivered message leaves the
    same row.
  - Sessions are those marked present, late or left early. Hours are the minutes recorded where
    staff recorded them, and the meeting's length otherwise.
  - The title and organisation are copied, so the record outlives the listing.
  - No linked account, or no session attended, means no record, and unlinking takes it away.
  - New contracts: `IAttendanceQueries` (current records only: no notes, devices or recorders);
    `EnrolmentFacts.YouthUserId`; `IEnrolmentQueries.SurnamesOfAsync`.
- **The portfolio is the young person's.**
  - `GET` and `PUT /me/portfolio`; `POST /me/portfolio/participation/{id}/hide` and `/show`.
  - Private until they choose otherwise. They can hide any record, which leaves their total and
    any shown page. Nothing is deleted.
  - Only `private`, `link_only` and `public` are offered. The schema's `mentors` and `org` wait
    for M5 and M6 to give them meaning.
- **Showing it.** `link_only` or `public` needs:
  - the showcase capability, so nobody under 13 (`portfolio.showcase`);
  - a link name of 3 to 40 lower-case letters, digits and hyphens, unique in the tenant;
  - a display name without the young person's family name, read from the household members
    linked to the account. The schema leaves this rule to the application.
- **The page anyone with the link sees.** `GET /public/portfolios/{slug}` is anonymous and
  rate-limited, and returns the display name, headline, bio, hours and unhidden records. It never
  returns an account, an enrolment or a legal name.
  - A `link_only` page is sent with `X-Robots-Tag: noindex`.
  - Participation sits behind the actor guard, so the page reads it through
    `learning.public_participation(tenant, slug)`. That is a security-definer function owned by
    the migrator, which itself checks the tenant, the link name and that the owner chose to show
    the portfolio, and returns only unhidden records. The app role has execute on it and nothing
    more.
  - Every read and change is audited, public reads included.
- **Not yet:**
  - goals, skills and projects with staff approval;
  - self-reported and provider-entered hours;
  - a guardian's view of a child's portfolio;
  - the portfolio in the app;
  - exports, which come with badges.

## Open questions

**Q1 — Which New York county?** You chose "a New York county" without naming
it. Nothing is blocked: the prompt asks for a fictional seed region, and I
will seed **Kettle County, NY** (fictional — 11 municipalities, 4 school
districts, 18 neighbourhoods, 1 transit agency). The real county only
affects production tenant configuration: boundary import source, GTFS feed,
ACS vintage and the neighbourhood vocabulary. Name it when convenient.

**Q2 — Is `ny-ed-law-2d` the right privacy pack, and is a district
integration actually in scope for launch?** The pack is built and selectable.
But Part 121 obligations (parents' bill of rights, 7-day breach notification,
annual training records, DPA before connection) attach meaningfully once
district data flows. If no district integration is planned for year one, I
would keep the pack active for the encryption and notification requirements
and leave `district_connection` disabled — which is already the default.

**Q3 — Verifiable parental consent method.** COPPA permits several. The
schema supports email-plus, payment-card verification, government-ID match,
signed-form review and video-call review. Email-plus is cheapest and weakest;
card verification costs a small charge and excludes unbanked families;
ID match costs a vendor and excludes undocumented families. My recommendation
is **email-plus as default, with organiser-attested signed-form review as the
in-person fallback at enrollment** — which also fits how families actually
reach these programs. Confirm, or name a vendor.
*Answered 2026-10-19: as recommended. Email-plus is the default, and an organiser-attested signed
form reviewed in person at enrollment is the fallback.*

**Q4 — Retention defaults.** I need numbers, or I will seed these and you can
change them per tenant: attendance 400 days; applications 3 years (funder
audit); community posts 2 years; messages 2 years; media 2 years after the
owning record; search logs 90 days; audit 7 years; learner portfolio
indefinite (owned by the young person, deleted on request); field
observations indefinite (they are public-asset data); survey responses 5
years.

**Q5 — Deletion versus audit.** A youth exercises deletion. I propose: erase
the record, pseudonymise `subject_ids` in the audit log, keep the audit entry.
The alternative (erase the audit entries too) destroys the thing that
protects them. Your counsel's call, and it changes the deletion job.

**Q6 — Payment provider.** `IPaymentGateway` abstracts it, and the schema
holds no card data either way. Stripe is the default assumption. Confirm, or
name one, because sliding-scale behaviour and refund semantics differ enough
to matter in M3.

*Until then (2026-10-21):* D55 built everything that doesn't depend on the
provider. The manual gateway records payments an organisation takes itself.
The provider gateway and webhooks (D56) wait on this answer.
*Answered 2026-10-21: Stripe. D56 builds its gateway and webhooks; the manual gateway stays for
deployments without Stripe's secrets.*

**Q7 — Geocoding and map tiles.** Default is MapLibre + OpenStreetMap as the
prompt specifies. For *geocoding* I need to pick: self-hosted Nominatim or
Pelias (no third party sees a family's address, but it is infrastructure you
operate), or a hosted geocoder (easier, but a family's typed address reaches
a vendor). Given D8 I lean self-hosted Nominatim in the compose stack.
Confirm, since it changes the deployment footprint.

*Until then (2026-10-17):* a site made in the provider console is placed at its
neighbourhood's centre with `geocode_accuracy = 'locality'`. It appears in neighbourhood and
map results, but distance search skips it: locality accuracy is not used for distance, and
the finder now enforces that. Seeded sites keep their exact places.

**Q8 — Content screening provider.** "Automated screening for personal
information and abuse before posting" needs an implementation.
`IScreeningService` is the seam. Options: a hosted moderation API (quality,
but youth-authored text leaves the perimeter), or a local model plus
regex/NER for PII (nothing leaves, weaker on abuse detection). I lean local
PII detection always, plus a hosted abuse classifier behind a tenant feature
flag that is **off** until a DPA exists. Your call.

**Q9 — SMS provider and A2P registration.** US A2P 10DLC registration takes
weeks and requires a brand and campaign. If SMS is in scope for M3 this needs
starting now. Confirm whether SMS ships in M3 or slips to M8.

**Q10 — Who are the three concrete reporting consumers?** "Reports for the
provider and for its funders" is the vaguest requirement in the prompt and
reporting is where scope goes to die. Name three real reports — ideally actual
funder templates — and I will build those and a generic builder, rather than
guessing at a universal one.

*Until then (2026-10-21):* D57 built the provider reports docs/plan/09 names (roster,
attendance, participation) with small counts hidden. The funder packet waits on this answer.

**Q11 — Docker.** Not installed on this machine, and neither is `psql`. That
blocks Testcontainers, the local PostGIS stack, and the real `EXPLAIN` output
you asked for. Docker Desktop, OrbStack or Colima — your preference. This is
the only true blocker to starting M1.
*Answered 2026-10-18: Docker Desktop, on the Windows machine. The compose gate runs there (D29).*

**Q12 — Do you want EF migrations or SQL-first?** You asked for "migrations
reviewed as SQL". Two ways: (a) EF Core migrations as the source of truth,
with `dotnet ef migrations script` committed alongside and CI asserting the
generated SQL matches, or (b) hand-written SQL as the source of truth with
`dotnet ef dbcontext scaffold`-verified models and a CI drift check. The DDL
in `db/schema/` is written for (b) and uses several things EF cannot express
cleanly — partial indexes with `coalesce`, `UNIQUE NULLS NOT DISTINCT`,
generated columns, constraint triggers, RLS policies, partitioning. I lean
**(b)**: SQL-first, EF models mapped to it, CI failing on drift in either
direction. As a DBA you may have a strong view.

*Implemented (approved with the plan).* `SchemaDriftTests` compares every table a
module context maps against the live schema: a mapped column the database lacks, a
database column the model leaves unmapped, a type difference, or a nullability
difference. Its first run found 75 differences across three contexts that had never
touched a database. 72 came from one convention (strings mapped to `varchar(4000)`;
the schema is `text`), one was a real runtime bug (`youth_capability.is_under_13`,
which the naming convention wrote as `is_under13`), and two were spelling. All four
module contexts now match exactly. Tables no context maps yet are not checked;
a table that is mapped must be mapped completely.

**Q13 — Who reviews the translations?** All five non-English catalogues (about 560 strings
each) were drafted by machine on 2026-10-17. Their files say so (`@@review`), and the pages
are fully wired. Machine drafts are not fit to launch for a youth audience:
- Haitian Creole and Bengali are the weakest.
- The legal and safety copy that comes in M2 must not ship unreviewed in any language.

I propose that native-speaker review of each locale is a launch gate, tracked by the
`@@review` line. Does the county have translators, or should we budget for a vendor? The
listing content (D6) is a separate question: providers write it, and machine translations are
already marked as such on the page.

