-- =====================================================================
-- Synthetic volume for measuring the finder's query plans.
--
-- Loads 50,000 search rows for one synthetic tenant, so the planner has enough
-- rows to choose between indexes. Plans on a handful of rows say nothing about
-- production; the sample catalogue is far too small for that.
--
-- Re-runnable: removes this tenant's rows first. Requires the schema applied.
-- Data is random and fictional. The tenant id is fixed so the scenarios can
-- reference it.
-- =====================================================================
\set ON_ERROR_STOP on

delete from opportunity.listing where tenant_id = '0196c3e8-0000-7000-8000-00000000e001';

insert into platform.tenant (id, slug, display_name, region_name, region_kind, default_locale, timezone)
values ('0196c3e8-0000-7000-8000-00000000e001', 'synthetic-plans', 'Synthetic plans',
        'Synthetic County', 'demo', 'en', 'America/New_York')
on conflict (id) do nothing;

insert into directory.organisation (id, tenant_id, legal_name, display_name, slug, org_kind)
select ('0196c3e8-0000-7000-8000-0000000' || lpad(to_hex(g), 5, '0'))::uuid,
       '0196c3e8-0000-7000-8000-00000000e001', 'Org ' || g, 'Org ' || g, 'syn-org-' || g, 'nonprofit'
from generate_series(1, 400) g
on conflict (id) do nothing;

-- 50,000 listings, one site each. Each listing is a published program or event with
-- an age range, a price, a category and a session window.
create temporary table syn as
select g,
       gen_random_uuid()                                           as listing_id,
       gen_random_uuid()                                           as site_id,
       ('0196c3e8-0000-7000-8000-0000000' || lpad(to_hex(1 + g % 400), 5, '0'))::uuid as org_id,
       case when g % 4 = 0 then 'event' else 'program' end         as kind,
       (array['robotics','coding','art','music','science','basketball',
              'reading','chess','theatre','swimming'])[1 + g % 10]   as word,
       (array['stem','arts','sports','academic'])[1 + g % 4]       as cat,
       5 + g % 11                                                  as age_min,
       5 + g % 11 + 2 + g % 5                                      as age_max,
       case when g % 2 = 0 then 0 else 2500 end                    as cost_cents,
       now() + (random() * 60) * interval '1 day'                  as session_start,
       now() - (random() * 90) * interval '1 day'                  as published_at,
       random() * 100                                              as popularity,
       -73.9 + random() * 0.3                                      as lon,
       41.0 + random() * 0.2                                       as lat
from generate_series(1, 50000) g;

insert into opportunity.listing
  (id, tenant_id, org_id, kind, slug, title, summary, status, published_at,
   registration_mode, external_url, cost_model, cost_cents_min, modality, age_min, age_max)
select listing_id, '0196c3e8-0000-7000-8000-00000000e001', org_id, kind,
       'syn-' || g, initcap(word) || ' ' || g, 'A synthetic ' || word || ' session.', 'published', published_at,
       'external', 'https://example.org/syn/' || g,
       case when cost_cents = 0 then 'free' else 'fixed' end,
       case when cost_cents = 0 then null else cost_cents end,
       'in_person', age_min, age_max
from syn;

insert into opportunity.listing_search
  (id, tenant_id, listing_id, site_id, org_id, kind, is_published, facets,
   age_range, cost_cents, is_free, modality, location, session_window,
   next_start_at, popularity, published_at, is_drop_in, title, summary, fuzzy_text, search_vector)
select gen_random_uuid(), '0196c3e8-0000-7000-8000-00000000e001', listing_id, site_id, org_id, kind, true,
       array_cat(
         array['kind:' || kind, 'cat:' || cat, 'mode:in_person', 'org:' || org_id::text]
           || case when cost_cents = 0 then array['free'] else array[]::text[] end,
         array(select 'age:' || a from generate_series(age_min, age_max) a)),
       int4range(age_min, age_max, '[]'),
       cost_cents, cost_cents = 0, 'in_person',
       st_setsrid(st_makepoint(lon, lat), 4326)::geography,
       tstzrange(session_start, session_start + interval '90 minutes'),
       session_start, popularity, published_at, false,
       initcap(word) || ' ' || g, 'A synthetic ' || word || ' session.',
       initcap(word) || ' ' || g,
       to_tsvector('platform.ts_en'::regconfig, initcap(word) || ' ' || g || ' ' || word)
from syn;

analyze opportunity.listing;
analyze opportunity.listing_search;

select count(*) as listing_search_rows from opportunity.listing_search
where tenant_id = '0196c3e8-0000-7000-8000-00000000e001';
