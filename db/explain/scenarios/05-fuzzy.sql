-- Typo tolerance. Expected: Bitmap Index Scan on ix_ls_fuzzy with a
-- recheck. Strict word similarity (<<%) against whole words of the fuzzy
-- text, at the threshold the finder pins (SET LOCAL there, per transaction).
set pg_trgm.strict_word_similarity_threshold = 0.5;
explain (analyze, buffers, verbose, settings)
select ls.listing_id, strict_word_similarity('baskitball', ls.fuzzy_text) as sim
from opportunity.listing_search ls
where ls.tenant_id = current_setting('brighttime.tenant_id')::uuid
  and ls.is_published
  and 'baskitball' <<% ls.fuzzy_text
order by sim desc, ls.listing_id
limit 24;
