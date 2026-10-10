-- =====================================================================
-- BrightTime :: the main search query
--
-- This is the REFERENCE form: every predicate present, each guarded by a
-- "parameter is null" escape. It exists so that EXPLAIN baselines are
-- reproducible and so the shape is reviewable in one place.
--
-- Kept in step with SearchQueryBuilder (src/BrightTime.Modules.Opportunities/Search).
-- If the two disagree, the builder is the one under test; fix this file to match.
--
-- PRODUCTION DOES NOT SEND THIS STATEMENT.
-- The finder composes SQL from only the filters the caller actually
-- supplied (ListingSearchQueryBuilder). Reason: the "(:p is null or ...)"
-- idiom forces the planner into a generic plan that must assume every
-- branch is live, which costs a BitmapAnd over indexes that contribute
-- nothing and, worse, makes st_dwithin() non-selective when no origin was
-- given. Measured difference is in docs/plan/03-indexing-and-query-plans.md.
--
-- Parameters
--   $1  tenant_id            uuid
--   $2  q                    text        free text, null when browsing
--   $3  facets_all           text[]      tokens that must ALL be present (AND semantics)
--   $4  facets_any           text[]      tokens where ANY match is enough (OR within a facet group)
--   $5  when_range           tstzrange   null for "any time"
--   $6  max_cost_cents       int         null for "any cost"
--   $7  free_only            boolean
--   $8  origin               geography(Point,4326)  null when no location anchor
--   $9  radius_m             int
--   $10 kinds                text[]      program/event/job/resource
--   $11 ts_config            text        'platform.ts_en' | 'platform.ts_es' | 'platform.ts_simple'
--   $12 interest_terms       text[]      the viewer's declared interests, for the explainable boost
--   $13 sort                 text        'relevance' | 'soonest' | 'nearest' | 'newest'
--   $14 cursor_sort_value    numeric     keyset cursor, null on first page
--   $15 cursor_listing_id    uuid
--   $16 page_size            int
--
-- Ranking inputs are, deliberately and exhaustively: text match, distance
-- from a user-chosen anchor, how soon it starts, declared-interest overlap,
-- and a popularity figure derived from saves and views. Nothing else.
-- No sensitive attribute is an input. See docs/plan/07-threat-model.md T16.
-- =====================================================================

with matched as (
    select
        ls.listing_id,
        ls.site_id,
        ls.title,
        ls.summary,
        ls.org_id,
        ls.kind,
        ls.cost_cents,
        ls.is_free,
        ls.modality,
        ls.neighbourhood_id,
        ls.next_start_at,
        ls.next_deadline_at,
        ls.popularity,
        ls.published_at,
        ls.facets,
        case when $8::geography is null then null
             else st_distance(ls.location, $8::geography)
        end                                             as distance_m,
        case when $2::text is null or $2 = '' then 0
             else ts_rank_cd(ls.search_vector,
                             websearch_to_tsquery($11::regconfig, $2), 32)
        end                                             as text_rank,
        case when $12::text[] is null then 0
             else cardinality(array(select unnest(ls.facets)
                                    intersect
                                    select unnest($12::text[])))
        end                                             as interest_overlap
    from opportunity.listing_search ls
    where ls.tenant_id = $1::uuid
      and ls.is_published
      and ($10::text[]  is null or ls.kind = any($10::text[]))
      and ($3::text[]   is null or ls.facets @> $3::text[])
      and ($4::text[]   is null or ls.facets && $4::text[])
      and ($5::tstzrange is null or ls.session_window && $5::tstzrange)
      and ($7::boolean   is not true or ls.is_free)
      and ($6::int       is null or ls.cost_cents <= $6::int)
      and ($8::geography is null or st_dwithin(ls.location, $8::geography, $9::int))
      and ($2::text is null or $2 = ''
           or ls.search_vector @@ websearch_to_tsquery($11::regconfig, $2)
           or ls.fuzzy_text % $2::text)
),
-- One row per listing: a listing with four sites must not appear four
-- times. Keep the nearest site when a location anchor was given.
best_site as (
    select distinct on (listing_id) *
    from matched
    order by listing_id, distance_m asc nulls last
),
scored as (
    select bs.*,
           -- Explainable composite. Each term is reported to the client in
           -- the `why` payload so the UI can say "matched 'robotics',
           -- 1.2 km from Kettle High, starts in 9 days".
           round((
                0.45 * least(bs.text_rank * 4.0, 1.0)
              + 0.20 * case when bs.distance_m is null then 0.5
                            else exp(-bs.distance_m / 4000.0) end
              + 0.15 * case when bs.next_start_at is null then 0.3
                            else exp(-greatest(
                                   extract(epoch from (bs.next_start_at - now())) / 86400.0,
                                   0) / 30.0) end
              + 0.12 * least(bs.interest_overlap / 3.0, 1.0)
              + 0.08 * least(bs.popularity / 100.0, 1.0)
           )::numeric, 6) as score
    from best_site bs
)
select s.listing_id,
       s.site_id,
       s.title,
       s.summary,
       s.org_id,
       s.kind,
       s.cost_cents,
       s.is_free,
       s.modality,
       s.neighbourhood_id,
       s.next_start_at,
       s.next_deadline_at,
       round(s.distance_m)::int as distance_m,
       s.score,
       jsonb_build_object(
         'text_rank',        round(s.text_rank::numeric, 4),
         'distance_m',       round(s.distance_m),
         'days_until_start', case when s.next_start_at is null then null
                                  else greatest(
                                    floor(extract(epoch from (s.next_start_at - now())) / 86400.0), 0)
                             end,
         'interest_overlap', s.interest_overlap,
         'popularity',       s.popularity
       ) as why
from scored s
where case $13::text
        -- Keyset pagination. Deterministic because the sort key pair is
        -- unique and the score is a pure function of the parameters.
        when 'soonest'  then ($14 is null
                              or (s.next_start_at, s.listing_id)
                                   > (to_timestamp($14::double precision), $15::uuid))
        when 'nearest'  then ($14 is null
                              or (s.distance_m, s.listing_id) > ($14::double precision, $15::uuid))
        when 'newest'   then ($14 is null
                              or (s.published_at, s.listing_id)
                                   < (to_timestamp($14::double precision), $15::uuid))
        else                 ($14 is null
                              or (s.score, s.listing_id) < ($14::numeric, $15::uuid))
      end
-- The id direction follows the sort direction. A single trailing
-- "s.listing_id" would be ascending for every sort, but the keyset comparison
-- is '<' for descending sorts: tied rows would be skipped or repeated.
order by
  case $13::text when 'soonest' then s.next_start_at end asc  nulls last,
  case $13::text when 'nearest' then s.distance_m    end asc  nulls last,
  case $13::text when 'newest'  then s.published_at  end desc nulls last,
  case when $13::text not in ('soonest','nearest','newest') then s.score end desc nulls last,
  case when $13::text in ('soonest','nearest') then s.listing_id end asc  nulls last,
  case when $13::text not in ('soonest','nearest') then s.listing_id end desc nulls last
limit $16::int;
