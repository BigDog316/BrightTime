-- Typo tolerance. Expected: Bitmap Index Scan on ix_ls_fuzzy with a
-- recheck; similarity threshold set per request rather than globally.
explain (analyze, buffers, verbose, settings)
select ls.listing_id, similarity(ls.fuzzy_text, 'baskitball') as sim
from opportunity.listing_search ls
where ls.tenant_id = current_setting('brighttime.tenant_id')::uuid
  and ls.is_published
  and ls.fuzzy_text % 'baskitball'
order by sim desc, ls.listing_id
limit 24;
