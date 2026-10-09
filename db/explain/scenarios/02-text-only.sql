-- Keyword search, no other filter. Expected: Bitmap Index Scan on ix_ls_fts
-- (tenant_id as the leading btree_gin key), then rank + sort.
explain (analyze, buffers, verbose, settings)
select ls.listing_id, ts_rank_cd(ls.search_vector, q.query, 32) as rank
from opportunity.listing_search ls,
     websearch_to_tsquery('platform.ts_en', 'robotics after school') as q(query)
where ls.tenant_id = current_setting('brighttime.tenant_id')::uuid
  and ls.is_published
  and ls.search_vector @@ q.query
order by rank desc, ls.listing_id
limit 24;
