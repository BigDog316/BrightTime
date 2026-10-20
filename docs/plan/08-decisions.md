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
  - A real push provider: Firebase and APNs need your accounts. Until then
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
    from the app skips that check. The web page makes it.
  - The real app identifiers for `AppLinks:*` wait on the developer accounts.
  - A young person's own export and delete requests wait on Q5.

### D36 — The app: MAUI Blazor Hybrid over a shared, tested library (**proposed 2026-10-20; implemented, reversible**)

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

