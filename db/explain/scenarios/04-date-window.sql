-- "Something for next Saturday". Range overlap plus a day-of-week facet.
explain (analyze, buffers, verbose, settings)
select ls.listing_id, ls.next_start_at
from opportunity.listing_search ls
where ls.tenant_id = current_setting('brighttime.tenant_id')::uuid
  and ls.is_published
  and ls.session_window && tstzrange(date_trunc('week', now()) + interval '12 days',
                                     date_trunc('week', now()) + interval '13 days')
  and ls.facets @> array['dow:6']
order by ls.next_start_at
limit 24;
