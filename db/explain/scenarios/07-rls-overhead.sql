-- Measures the cost of the RLS predicate itself: the same scan with the
-- tenant filter written by hand (what brighttime_migrator sees) versus
-- through the policy (what brighttime_app sees). Run as both roles and
-- diff the plans. A material gap is the signal to denormalise tenant_id
-- into the layer-2 child tables.
explain (analyze, buffers, verbose, settings)
select lt.listing_id, lt.locale, lt.title
from opportunity.listing_translation lt
where lt.listing_id in (
  select listing_id from opportunity.listing_search
  where tenant_id = current_setting('brighttime.tenant_id')::uuid
    and is_published
  limit 200
);
