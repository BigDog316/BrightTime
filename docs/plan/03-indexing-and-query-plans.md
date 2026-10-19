# 03 — Indexing strategy and query plans

## Measured, not predicted

The plans below were measured on PostgreSQL 18.6 with PostGIS 3.6.4, against
50,000 synthetic search rows for one tenant (`db/explain/synthetic-load.sql`).
Reproduce them with `db/explain/run-explain.sh` after loading the data.

**Since 2026-10-17 the gate is `FinderPlanTests`** (section "As shipped, and gated" below).
It runs the SQL the finder actually sends, built by `SearchQueryBuilder`, and the
hand-written scenario files are history. The two differed, and the difference mattered.

Two things are measured and reported. The first is the plans **as the
application runs them**, under row-level security. The second is the same
queries with RLS bypassed, which is the best the planner can do on this table.
The gap between the two is the finding, and it is covered in the RLS section
and in decision D13.

Raw plans are in `db/explain/measured/rls-on/` and `db/explain/measured/rls-bypassed/`.

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

## Measured plans

Execution times are warm-cache, from `EXPLAIN (ANALYZE, BUFFERS)`, on 50,000 rows.

| Scenario | Before D13 (RLS on) | As shipped (finder role) | Plan as shipped |
|---|---|---|---|
| 01 browse, soonest | 0.09 ms | 0.08 ms | index scan, `ix_ls_next_start` |
| 02 keyword only | 22.1 ms | **0.28 ms** | bitmap, `ix_ls_fts` |
| 03 age, category, free, within 5 km | 43.0 ms | 26.7 ms | bitmap, `ix_ls_facets`, distance rechecked per row |
| 04 date window | 12.1 ms | **0.10 ms** | bitmap, `ix_ls_facets` |
| 05 fuzzy typo | 65.0 ms | 15.7 ms | bitmap, `ix_ls_fuzzy` |
| 06 six facets, text, date, cost, distance | 20.5 ms | **0.16 ms** | bitmap, `ix_ls_facets` |
| 07 translation join (RLS measurement, login role) | 0.04 ms | 0.03 ms | empty table, not meaningful |

"As shipped" is the finder after decision D13: the same query, run as
`brighttime_finder`. Raw plans: `db/explain/measured/finder-role/`. The earlier
columns remain in `rls-on/` and `rls-bypassed/`.

**Before D13, scenarios 02, 04, 05 and 06 sequentially scanned the whole table
under RLS.** As shipped, every finder scenario uses an index.

**Still slow, and not caused by RLS:**

- **03, facets plus distance, 27 ms.** The facet index returns about 5,700
  candidates and the distance is checked per row. The planner estimates one row
  for the combined filter, when 722 match. Next step: test the GiST index on
  `location` leading for small radii, and add extended statistics if the estimate
  stays wrong.
- **05, fuzzy matching, 16 ms.** Trigram similarity over 50,000 short strings.
  Next step: raise the similarity threshold for short queries, as section "The
  query" already proposes, and measure again. (Superseded: the synthetic strings
  were too short to show that `%` matched nothing on real listings. See "Typo
  tolerance" below.)

Where the predictions were wrong:

- **I predicted a `BitmapAnd` of the facet and geo indexes in scenario 03.** Under
  RLS the planner uses a parallel sequential scan. Without RLS it uses the facet
  index alone, then rechecks the distance per row.
- **I predicted the facet index would serve the containment check everywhere.** It
  does, but only when no security barrier sits in front of it. See below.
- **The planner estimates one row for the finder's combined filter.** It returns
  722. Under RLS the estimate is lost entirely, which is part of why the plan is
  a sequential scan. Without RLS the estimate is still low, so scenario 03 remains
  the slowest query even with the index in use.

### As shipped, and gated (2026-10-17)

`FinderPlanTests` builds each scenario with the finder's own `SearchQuery` and
`SearchQueryBuilder`. It runs `EXPLAIN (ANALYZE, BUFFERS)` as `brighttime_finder`, over the
synthetic load, which is now drawn from a seeded random sequence:
- 50,000 listings and 55,290 rows;
- a tenth of listings at two sites, and one in twenty online only.

It fails if:
- any node sequentially scans `listing_search`;
- a scan on it is estimated more than ten times off (once either side reaches 50 rows).

It doesn't judge the arms of a `BitmapOr`/`BitmapAnd` (the planner acts on their
combination, estimated at the heap scan above), or a scan that a `LIMIT` stopped early.
CI runs it, and the plans are committed in `db/explain/measured/shipped/`.

| Scenario | Execution | Plan |
|---|---|---|
| 01 browse, soonest | 0.06 ms | index scan, `ix_ls_next_start`, stops after a page |
| 02 keyword | 28 ms | bitmap OR, `ix_ls_fts` + `ix_ls_fuzzy` |
| 03 age, category, free, within 5 km | 22 ms | bitmap, `ix_ls_facets`, box then distance |
| 04 date window, soonest | 2.7 ms | index scan, `ix_ls_next_start` |
| 05 fuzzy typo | 21 ms | bitmap OR, `ix_ls_fts` + `ix_ls_fuzzy` |
| 06 three facets, kind, text, window, cost, distance | 10 ms | bitmap, `ix_ls_facets` |
| 07 map counts, category and age | 8 ms | bitmap, `ix_ls_facets` |

What the gate found in the shipped query, and what changed:
- **Every browse read the whole tenant.** The builder kept every matching row, then
  collapsed each listing's sites with `DISTINCT ON`, and only then sorted and limited. So
  "soonest" sorted 55,000 rows to show 24 (96 ms, sequential scan).
  - Now, without an origin, no filter depends on the site: facets, times and prices are
    the listing's. So the query reads only each listing's primary row
    (`listing_search.is_primary`, exactly one per listing, enforced by a unique index)
    and collapses nothing.
  - An ordered browse walks the partial index `ix_ls_next_start` and stops at the page
    size.
  - With an origin, every site is read and each listing is shown at its nearest.
- **`ST_DWithin` on geography misestimates itself.** Its support function adds an
  accurate box test, estimated 13,359 against 12,780 found. But the call itself is costed
  as selecting almost nothing, and that multiplies every estimate above it down to one
  row (1 against 203 in scenario 03).
  - The builder now writes the box itself, with `&&` against an envelope computed with
    margin in C#, which the geo index serves and the planner estimates well.
  - The exact spheroid `ST_Distance` then decides.
  - A unit test checks that the box contains the whole circle at every bearing.
- **The old synthetic load derived category, price and age from the row number**, so every
  `stem` row was free. Its "underestimate" was partly a correlation real data doesn't have.

Still to do, and not gated:
- **Text, 02 and 05, about 25 ms.** Nearly all of it is ranking the roughly 5,000 rows
  that match, and the trigram arm matches about as many as full text. Next step: rank
  only the full-text matches when there are enough of them. (The threshold idea is
  superseded; see "Typo tolerance" below.)
- **An unfiltered map**, and an unfiltered relevance browse, read every row of the tenant
  whatever the plan, because they count or score everything. On a table holding many
  tenants they use the tenant-leading indexes. On this one-tenant test table a scan is
  right, so the gate measures a filtered map instead.

### Typo tolerance (2026-10-18)

**`%` matched nothing on real listings.** The trigram arm was `fuzzy_text % @text`, and `%`
compares the query with the whole string. `fuzzy_text` holds the title, organisation and
category names, about 40 to 90 characters, so a one-word query's similarity to it is tiny.
For example, "socer" against "Soccer skills clinic Eastbank Youth Club Sports and fitness
Soccer" scores 0.09, against a threshold of 0.3. `/search?q=socer` found nothing, and
`q=soccer` found 16 listings. The synthetic load hid this: its `fuzzy_text` was only the
title ("Robotics 123"), short enough for whole-string similarity to work.

**The arm is now `@text <<% ls.fuzzy_text`, strict word similarity, at 0.5.**
- It compares the query with runs of whole words in the fuzzy text.
- `ix_ls_fuzzy` serves it: the plan shows `fuzzy_text %>> @text`, the commutator, as the
  index condition.
- The threshold is pg_trgm's default for this operator. The finder pins it with
  `SET LOCAL pg_trgm.strict_word_similarity_threshold` in the same round trip as the role
  (`SqlListingFinder.TransactionSetup`), so the meaning of the arm doesn't depend on the
  server's configuration.

Measured over the seeded catalogue (396 published listings). Each cell counts listings the
trigram arm matches:

| Query | `%` (0.3) | `<%` word (0.6) | `<<%` strict (0.5) | Wanted |
|---|---|---|---|---|
| socer | 0 | 16 | 16 | the 16 that "soccer" finds |
| robtics | 0 | 0 | 8 | the 8 that "robotics" finds |
| baskitball | 0 | 0 | 31 | the 31 that "basketball" finds |
| yogga, lifegard | 0 | 0 | 6, 5 | yoga, lifeguard |
| socer skils | 0 | 11 | 11 | Soccer skills clinic (11) |
| socer clinic | 0 | 0 | 11 | Soccer skills clinic (11) |
| ball | 0 | 31 | 0 | nothing: a fragment of "basketball" |
| car | 0 | 46 | 0 | nothing: a fragment of "careers" |
| xylophone, zebra | 0 | 0 | 0 | nothing |

Why strict, and why 0.5:
- **Recall.** Plain word similarity (`<%`) at its 0.6 default misses one-letter typos in
  mid-length words: "robtics" scores 0.55 and "baskitball" 0.57.
- **No fragment matches.** Lowering `<%` to catch those would not help, because `<%` scores
  a query against any run of trigrams. So "ball" scores 0.6 inside "basketball" and "car"
  scores 0.75 inside "careers". Strict word similarity scores whole words only: 0.45 and
  0.33.

`KettleSeedDatabaseTests` pins all of this down:
- a misspelt word finds what the correct spelling finds;
- a misspelt phrase finds its listings;
- an unrelated word, or a fragment of a longer word, finds nothing.

**Cost.** Synthetic load v4 gives `fuzzy_text` the projector's shape: title, organisation,
category and interest names. Medians of seven runs, vacuumed, as `brighttime_finder`, on the
Windows/Docker scratch database. These numbers are not comparable with the table above,
which came from the Mac.

| Query, load | `%` | `<<%` |
|---|---|---|
| 05 "robtics", title-only `fuzzy_text` (v3) | 44 ms, 4,935 matches | 48 ms |
| 05 "robtics", projector-shaped `fuzzy_text` (v4) | 25 ms, **0 matches** | 77 ms, 4,935 matches |
| 02 "robotics", either load | 34 ms | 34 ms |

The gate's single run of 05 measured 56 to 68 ms on this machine. The plan is a bitmap OR of
`ix_ls_fts` and `ix_ls_fuzzy`, as before. The fuzzy index hands the heap scan 10,987
candidates, and 5,498 survive the recheck. GIN can count only the query's trigrams that a row
contains, not whether they are adjacent within one word. That index arm is estimated at
5,615, about half what it returns. The gate doesn't judge arms of a `BitmapOr`, and the heap
scan above it is estimated within 30%.

Where the 77 ms goes:
- **Ranking, about 34 ms.** The query matches a tenth of the table, because the load has
  only ten title words. Scoring that many rows costs what it costs in 02.
- **The operator, the rest.** It runs on each of the 10,987 candidates in the heap scan's
  recheck, and again in its filter on each row that survives. The filter is not dropped because pg_trgm's operators are `STABLE`
  (they read the threshold setting), so the planner won't prove the filter is implied by
  the recheck. With the FTS arm removed, the same query takes 54 ms, so the second
  evaluation costs about 23 ms.

Next step: make the trigram arm a fallback, used only when full text finds less than a
page. Typo tolerance would then cost nothing for a correctly spelt query. A misspelt query
would scan one index, with no OR, so the operator would run once.

**Chinese is not helped, by this or by `%`.** Chinese has no spaces, so a whole title such as
课后艺术工作室 is one "word".
- A two-character query (艺术, "art") contains no trigram that occurs inside a longer run.
  So `%`, `<%`, `<<%` and full text all find nothing for it.
- Plain `<%` would find a four-character run inside a title (英语会话 inside 青少年英语会话).
  Strict word similarity does not.
- Before this change, none of these queries matched either.

This is the D6 ceiling, measured: for zh, trigram recall works only for queries that match
whole titles. It needs a segmenter (zhparser, pg_jieba) or bigram indexing (pg_bigm), not a
different trigram operator.

### Why the index is ignored under RLS

The policy `tenant_isolation` on `opportunity.listing_search` is a security barrier.
PostgreSQL evaluates a security barrier before any user-supplied qualifier, and a
qualifier can only move below the barrier if it is `LEAKPROOF`. The containment
operator `@>` and `ST_DWithin` are not leakproof.

The application's own filters therefore cannot become index conditions. Measured
attempts that did not change the plan:

1. Marking `platform.current_tenant_id()` `LEAKPROOF`. The function is inlined,
   so the flag is never consulted.
2. Adding `SET search_path` to stop inlining. The planner then folds the tenant
   check into a one-time filter and drops it from index consideration.
3. Rewriting the policy as a scalar sub-select, `(select platform.current_tenant_id())`.
   The same fold happens.

None of these is a fix inside the DDL. The three attempts were made on the scratch
database and reverted by rebuilding it from the committed schema.

## RLS cost, and the stance on it

Measured facts that shape the design:

1. **The tenant policy is not free on the search table.** It was assumed to
   become an index condition the planner evaluates once. Measured: under the
   policy, the planner loses the index for every filter the application adds,
   not just for the tenant check. Scenarios 02, 04, 05 and 06 go from 0.1 ms to
   12–65 ms. See "Why the index is ignored under RLS" above.
2. **`platform.current_tenant_id()` cannot be made `LEAKPROOF` in a way that
   helps.** Marking it leakproof does nothing while it is inlined, and stopping
   inlining does not restore the index path. Only a superuser may set LEAKPROOF,
   so any fix in this direction also needs a DBA step.
3. **The layer-2 `EXISTS`-against-parent policies are not yet measured.** They
   apply to `opportunity.listing_translation` and other child tables. Scenario 07
   is the only plan that touches one, and its table is empty, so it proves
   nothing. Measure once child tables hold data.

The decision this leads to is recorded in `08-decisions.md` as D13, awaiting
approval. It changes how the public finder reads its data, and that changes a
security design already approved, so I have not made the change.

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
not index 2. For zh this works only for whole titles, not for words within
them (measured; see "Typo tolerance").

This is adequate for launch and I would not add a search engine yet. But it is
a real ceiling, and the `ISearchProvider` seam exists so that moving to
Meilisearch or OpenSearch is a provider swap rather than a rewrite. I would
revisit when either (a) zh/ar query volume becomes material, or (b) a tenant
exceeds ~100k published listings. See decision D6.
