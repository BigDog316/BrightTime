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
