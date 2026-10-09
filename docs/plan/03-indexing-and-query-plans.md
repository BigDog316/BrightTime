# 03 — Indexing strategy and query plans

## Up front: the plans are not measured yet

You asked for the query plans for the main search query. I cannot produce
real ones on this machine — `docker` and `psql` are both absent, so there is
no PostGIS instance to plan against. **Everything below labelled "expected"
is a prediction, not an observation.** Fabricating `EXPLAIN ANALYZE` output
would be worse than useless to you.

What is ready to run the moment a database exists:

- `db/explain/run-explain.sh` — runs every scenario with
  `EXPLAIN (ANALYZE, BUFFERS, VERBOSE, SETTINGS)` and writes one file per
  scenario into `db/explain/out/`, which gets committed.
- `db/explain/scenarios/01..07` — browse, text-only, facets+geo, date window,
  fuzzy, worst case, and an RLS-overhead A/B.
- A CI job that fails a PR if a committed plan acquires a `Seq Scan` on
  `opportunity.listing_search`.

Prerequisite: Docker Desktop, OrbStack or Colima. First item in M1 either way.

## The read model

The finder never queries `opportunity.listing` and never joins six tables.
It queries one denormalised table, `opportunity.listing_search`, one row per
`(listing, site)`. Virtual-only listings get a single row with
`site_id IS NULL`, which is why uniqueness is
`UNIQUE NULLS NOT DISTINCT (listing_id, site_id)`.

Maintenance is outbox-driven: `ListingPublished`, `ListingUpdated`,
`SiteMoved`, `OccurrencesExpanded`, `TaxonomyRenamed` each enqueue a
`ProjectListing` job keyed on `unique_key = 'proj:' || listing_id`, so a burst
of edits collapses to one reprojection. A nightly reconcile rebuilds any row
whose `projected_at` predates its source's `updated_at`.

### The facet-array trick

Every equality-and-set predicate in the filter list — category, interest, age,
grade, language, accommodation, day of week, modality, neighbourhood,
organisation, free, meals, transport, drop-in — is folded into one
`text[]` column:

```
facets = {kind:program, cat:stem, cat:robotics, int:engineering,
          age:13, age:14, age:15, age:16, age:17,
          grade:8, grade:9, grade:10, grade:11, grade:12,
          lang:en, lang:es, accom:wheelchair, accom:asl,
          dow:2, dow:4, mode:in_person, hood:7f3a…, org:91c…,
          free, meals, transport}
```

Age and grade are **enumerated per year** rather than stored as a range in the
hot path, which turns "is a 15-year-old eligible" from a GiST range
containment into a GIN token lookup. The domain is tiny (ages 0–30, grades
−1–12), so the array stays short.

Consequences:

- One GIN index (`ix_ls_facets`) serves ~14 filter dimensions. `@>` for AND
  semantics, `&&` for OR-within-a-group.
- The planner gets a single, well-estimated access path instead of six
  marginal ones it has to decide between.
- Adding a filter dimension is a projection change, not a migration and a new
  index.

Cost: the array is denormalised, so a taxonomy rename reprojects affected
rows (handled by the `TaxonomyRenamed` message). `int4range` columns are kept
alongside for display and for the rare "overlaps this age band" query.

### The six indexes

| # | Index | Type | Serves |
|---|---|---|---|
| 1 | `ix_ls_facets (tenant_id, facets)` | GIN, partial `WHERE is_published` | every set/equality filter |
| 2 | `ix_ls_fts (tenant_id, search_vector)` | GIN, partial | keyword search |
| 3 | `ix_ls_fuzzy (fuzzy_text gin_trgm_ops)` | GIN, partial | typo tolerance, zh/bn/ht/ar recall |
| 4 | `ix_ls_geo (location)` | GiST, partial `AND location IS NOT NULL` | `ST_DWithin`, distance sort |
| 5 | `ix_ls_window (tenant_id, session_window)` | GiST, partial | date-range overlap |
| 6 | `ix_ls_next_start (tenant_id, next_start_at, listing_id)` | btree, partial | default browse order, keyset pagination |

Plus `ix_ls_cost` for the cost slider and `ix_ls_stale` for the maintenance job.

Three deliberate choices:

**`tenant_id` is inside the GIN and GiST indexes**, not only in a btree. This
needs `btree_gin` (uuid support: PostgreSQL 13+) and `btree_gist`. Without it,
a single-tenant query has to bitmap-AND a tenant btree against the GIN, and on
a 20-tenant deployment the tenant key is the single most selective predicate
there is. Putting it in the composite index makes it free.

**Every hot index is partial on `is_published`.** Drafts, pending review,
rejected and archived listings are a large and permanently growing minority
that no public query ever wants. Partial indexes keep them out of the index
entirely, which shrinks the index, keeps it in cache, and means an organiser
bulk-rejecting 400 imported listings does not bloat the finder's indexes.

**`fillfactor = 80` and aggressive autovacuum** on `listing_search`
(`autovacuum_vacuum_scale_factor = 0.02`). It is an update-heavy projection:
`next_start_at` churns as time passes and `popularity` churns constantly. The
default 20% scale factor on a 200k-row table means 40k dead tuples before a
vacuum, which is exactly the index bloat that makes a GIN index stop fitting
in cache.

### Statistics

```sql
ALTER TABLE listing_search ALTER COLUMN facets SET STATISTICS 2000;
CREATE STATISTICS st_ls_kind_hood (dependencies, ndistinct)
  ON tenant_id, kind, neighbourhood_id FROM listing_search;
```

Array MCV lists are what make the planner's selectivity estimate for
`facets @> array[...]` believable; the default target of 100 is far too low for
an array with a few hundred distinct tokens. The extended statistics object
exists because `kind` and `neighbourhood_id` are strongly correlated in
practice (libraries cluster, sports fields cluster) and the planner's
independence assumption otherwise underestimates the AND by an order of
magnitude.

## The query

`db/queries/search-listings.sql` is the reference form. **Production does not
send it.** `ListingSearchQueryBuilder` composes SQL from only the filters the
caller actually supplied.

This matters more than it sounds. The `(:p IS NULL OR predicate)` idiom —
convenient, and what most codebases ship — forces a generic plan that must
assume every branch is live. Two concrete failures:

1. `($8::geography IS NULL OR ST_DWithin(location, $8, $9))` with a null
   origin. The planner cannot know the branch is dead, so it either costs the
   GiST path as if it will filter, or it refuses the index and takes a seq
   scan. Either way it is wrong.
2. With eight optional predicates, the generic plan BitmapANDs indexes that
   contribute nothing, paying the bitmap construction cost on every request.

Composition gives each filter combination its own plan, which Postgres then
caches per shape. There are perhaps 30 shapes that real users produce; the
prepared-statement cache handles that comfortably.

Shape of the composed query:

```
filter  →  dedupe to one row per listing  →  score  →  keyset page
```

1. **filter** — all supplied predicates against `listing_search`.
2. **dedupe** — `DISTINCT ON (listing_id) ... ORDER BY listing_id, distance_m
   NULLS LAST`. A listing at four sites must appear once, at its nearest site.
3. **score** — only over the surviving rows. A weighted sum of text rank,
   distance decay, start-soon decay, declared-interest overlap and popularity.
4. **page** — keyset on `(sort_key, listing_id)`, never `OFFSET`. Offset
   pagination on a scored set re-executes and re-sorts the whole match set for
   page 40; keyset does not. It also keeps results stable while a provider
   publishes something mid-session.

### Ranking is explainable by construction

The query returns a `why` jsonb object per row with each factor's contribution,
and the UI renders it ("matched *robotics*, 1.2 km from Kettle High, starts in
9 days, 3 of your interests"). The inputs are exhaustively:

- text match against title/summary/description/org/category
- distance from a **user-chosen** anchor (neighbourhood or school)
- time until next start
- overlap with declared interests
- popularity (saves + views, decayed)

The inputs are **not**, and a unit test asserts the feature-extractor rejects
them: race, ethnicity, income or free-lunch status, disability or accommodation
need, immigration status, gender, school performance, any district-sourced
field, any `is_sensitive` application answer. Accommodation terms are a
*filter* the user applies, never a ranking input.

## Expected plans

Predictions, to be replaced by `db/explain/out/` in M1. Row counts assume one
tenant with ~8,000 published listings across ~2,400 sites, so ~11,000
`listing_search` rows, within a 20-tenant deployment of ~220,000 rows.

**Scenario 01 — browse, soonest first, 24 rows.** The whole point of index 6.

```
Limit
  └─ Index Scan using ix_ls_next_start on listing_search
       Index Cond: (tenant_id = $1 AND next_start_at >= now())
```
No sort node, no heap fetch beyond 24 rows. Target: < 2 ms, < 40 shared hits.

**Scenario 02 — keyword only.**

```
Limit
  └─ Sort  (rank DESC)
       └─ Bitmap Heap Scan on listing_search
            Recheck Cond: (search_vector @@ query)
            └─ Bitmap Index Scan on ix_ls_fts
```
The sort is unavoidable: `ts_rank_cd` is not indexable. It sorts the match set,
not the table — acceptable while matches stay in the low thousands. If a
common one-word query matches 20k+ rows, the mitigation is a `popularity`
prefilter, not a bigger sort.

**Scenario 03 — age + category + free + within 5 km. The plan that matters.**

```
Limit
  └─ Sort  (distance_m)
       └─ Bitmap Heap Scan on listing_search
            Recheck Cond: (facets @> '{age:15,cat:stem,free}' AND ST_DWithin(...))
            └─ BitmapAnd
                 ├─ Bitmap Index Scan on ix_ls_facets
                 └─ Bitmap Index Scan on ix_ls_geo
```
If the planner picks only one of the two, the facet statistics target is too
low or `ST_DWithin`'s selectivity estimate is off. Both are fixable; the
scenario file exists to catch it.

**Scenario 04 — date window + day of week.** `BitmapAnd` of `ix_ls_window`
and `ix_ls_facets`. The GiST range index is the weaker of the two; if the
window is wide (a whole season) expect the planner to lead with facets and
recheck the range, which is correct.

**Scenario 05 — fuzzy.** `Bitmap Index Scan on ix_ls_fuzzy` with a recheck.
`pg_trgm.similarity_threshold` is set per request (`SET LOCAL`), not globally:
0.3 for a short query, 0.45 for a long one. A global threshold is wrong for
one of the two.

**Scenario 06 — worst case, six facets + text + date + cost + distance.**
Expect `BitmapAnd` over three indexes at most — Postgres stops adding
bitmaps when marginal selectivity is poor, which is the right instinct. The
number to watch here is buffer reads, not plan shape. Target: < 15 ms warm.

**Scenario 07 — RLS overhead.** Same query as `brighttime_migrator`
(BYPASSRLS, hand-written tenant filter) and as `brighttime_app` (policy).

## RLS cost, and the stance on it

Three facts that shape the design:

1. **The tenant policy is nearly free.** `tenant_id =
   platform_current_tenant_id()` is an indexed equality against a `STABLE`
   function the planner inlines and evaluates once. It becomes an index
   condition, not a filter.
2. **`platform.current_tenant_id()` cannot be `LEAKPROOF`** — only a
   superuser may mark a function leakproof. The consequence is that Postgres
   will not push user-supplied quals below the RLS qual in a security barrier
   context, so some plans lose a qual-pushdown opportunity. For the finder
   this is irrelevant (the RLS qual *is* the selective one). For views over
   youth tables it is measurable, which is why reports read fact tables
   directly under an application policy rather than through stacked views.
3. **The layer-2 `EXISTS`-against-parent policies are the real cost.** On
   `opportunity.listing_translation` every row access becomes a semi-join to
   `listing`. These are all primary-key lookups in detail views, not search,
   so the prediction is that it does not matter. Scenario 07 is there to
   check. If it does matter, the fix is mechanical: add `tenant_id` to those
   child tables and move them into layer 1.

**The stance:** RLS carries tenant isolation and one coarse "is there an
authenticated actor at all" guard (`RESTRICTIVE`, so it tightens rather than
loosens — multiple `PERMISSIVE` policies are OR-ed, which is a trap). Fine-
grained authorisation — *which* youth this provider may see, whether this
guardian is verified, whether this mentor's clearance is current — lives in
the application, where it can produce a 403 with a reason and an audit entry
instead of a silently empty result set.

Pushing per-record authorisation into RLS predicates would mean five-way joins
in a policy, no useful error messages, and plans nobody can reason about. It
also fails a basic test: a youth should be *told* their guardian has not yet
verified the link, not shown an empty page.

Both layers are tested. `BrightTime.IntegrationTests` includes a cross-tenant
matrix: for every module, for every role, assert that a request carrying
tenant A's context reads exactly zero of tenant B's rows — through the API, and
again through a raw `DbContext` with a hand-forged query, which is the case
RLS exists to catch.

## Other indexing notes

- **Job queue**: one partial btree `(queue, priority, run_after) WHERE state =
  'ready'`. The claim is `SELECT … WHERE state='ready' AND run_after <= now()
  ORDER BY priority, run_after FOR UPDATE SKIP LOCKED LIMIT n`. Keeping the
  index partial means its size tracks the *backlog*, not the history.
- **Outbox**: `ix_outbox_pending (occurred_at) WHERE processed_at IS NULL`.
  Same reasoning. Processed rows are pruned by a retention job after 14 days.
- **Audit**: range-partitioned monthly; per-partition indexes on
  `(tenant_id, occurred_at DESC)`, `(actor_id, occurred_at DESC)` and GIN on
  `subject_ids`. One row per *operation* with a `subject_ids` array, not one
  per record — this is what makes "audit every read of a youth record"
  affordable. A roster read of 40 youth is one row, not 40.
- **Attendance**: the live-record unique index is partial on `superseded_by IS
  NULL`, so corrections append rather than update and the history is intact.
- **`listing_occurrence`**: the expansion job writes `day_of_week` explicitly.
  It cannot be a generated column — `extract(isodow from timestamptz)` is
  `STABLE`, not `IMMUTABLE`, because it depends on the `TimeZone` GUC. A
  UTC-derived value would also be wrong for a 9pm Friday session in New York.
- **UUID keys**: application-generated **UUIDv7** (`Guid.CreateVersion7()`),
  not `gen_random_uuid()`. Time-ordered keys keep btree inserts at the right
  edge of the index instead of scattering them, which on an insert-heavy table
  like `attendance_record` or `audit_event` is the difference between
  appending pages and dirtying random ones. The DDL has no `DEFAULT` on `id`
  deliberately: an accidental server-side v4 would be invisible.

## Multilingual search: the honest limit

Postgres ships Snowball stemmers for English and Spanish. For the other four
required languages it ships nothing:

| Locale | Approach |
|---|---|
| en | `platform.ts_en` — english stemmer + unaccent |
| es | `platform.ts_es` — spanish stemmer + unaccent |
| zh-Hans | `platform.ts_simple` + **trigram is the primary path**. Chinese has no whitespace tokenisation in core Postgres, so `to_tsvector` produces one enormous token per run of characters. Trigram n-grams actually work. |
| bn, ht, ar | `platform.ts_simple` + unaccent, trigram for recall |

`ar` additionally needs RTL layout throughout the UI, which is cheap only if
done from the first commit — CSS logical properties (`margin-inline-start`,
not `margin-left`), `dir` on `<html>`, and no directional ASCII art in strings.

`search_vector` is built with the listing's own language configuration, and
`fuzzy_text` holds the unaccented concatenation of title, org name and
category names for every locale. For zh/bn/ht/ar, recall comes from index 3,
not index 2.

This is adequate for launch and I would not add a search engine yet. But it is
a real ceiling, and the `ISearchProvider` seam exists so that moving to
Meilisearch or OpenSearch is a provider swap rather than a rewrite. I would
revisit when either (a) zh/ar query volume becomes material, or (b) a tenant
exceeds ~100k published listings. See decision D6.
