-- Worst case the UI can produce: text + 6 facets + date + cost + distance.
-- The number to watch is shared buffer reads, not the plan shape.
explain (analyze, buffers, verbose, settings)
select ls.listing_id
from opportunity.listing_search ls
where ls.tenant_id = current_setting('brighttime.tenant_id')::uuid
  and ls.is_published
  and ls.search_vector @@ websearch_to_tsquery('platform.ts_es', 'arte')
  and ls.facets @> array['age:13','cat:arts','lang:es','accom:wheelchair','meals','mode:in_person']
  and ls.session_window && tstzrange(now(), now() + interval '60 days')
  and ls.cost_cents <= 5000
  and st_dwithin(ls.location,
                 st_setsrid(st_makepoint(-73.76, 41.03), 4326)::geography,
                 8000)
limit 24;
