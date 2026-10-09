-- Default landing page: no query, no filters, "starting soonest".
-- Expected: index-only-ish scan of ix_ls_next_start, no sort node.
explain (analyze, buffers, verbose, settings)
select ls.listing_id, ls.title, ls.next_start_at
from opportunity.listing_search ls
where ls.tenant_id = current_setting('brighttime.tenant_id')::uuid
  and ls.is_published
  and ls.next_start_at >= now()
order by ls.next_start_at, ls.listing_id
limit 24;
