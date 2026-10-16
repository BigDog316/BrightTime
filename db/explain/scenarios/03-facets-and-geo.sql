-- The common real query: age + category + free + within 5 km of a point (a school, in use).
-- Expected: BitmapAnd of ix_ls_facets and ix_ls_geo. This is the plan that
-- matters most; if the planner picks only one index here, revisit the
-- facet statistics target.
explain (analyze, buffers, verbose, settings)
select ls.listing_id, st_distance(ls.location, s.location) as distance_m
from opportunity.listing_search ls
cross join (select st_setsrid(st_makepoint(-73.7629, 41.0345), 4326)::geography as location) s
where ls.tenant_id = current_setting('brighttime.tenant_id')::uuid
  and ls.is_published
  and ls.facets @> array['age:15','cat:stem','free']
  and st_dwithin(ls.location, s.location, 5000)
order by distance_m
limit 24;
