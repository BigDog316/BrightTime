-- =====================================================================
-- Synthetic volume for measuring the finder's query plans.
--
-- Loads 50,000 published listings for one synthetic tenant, about 56,000
-- search rows: a tenth of listings run at two sites and one in twenty is
-- online only, as the real catalogue does. Plans on a handful of rows say
-- nothing about production; the sample catalogue is far too small for that.
--
-- Values are drawn from a seeded random sequence, independently of each
-- other. An earlier version derived category, price and age from the row
-- number, which made "stem" always free and taught the planner nothing about
-- real correlations except how to underestimate them.
--
-- Re-runnable: removes this tenant's rows first. Requires the schema applied.
-- Data is random and fictional. The tenant id is fixed so the scenarios and
-- FinderPlanTests can reference it; the tenant's display name carries the
-- version, so a test can tell whether this exact load is in place.
-- =====================================================================

delete from opportunity.listing where tenant_id = '0196c3e8-0000-7000-8000-00000000e001';
delete from platform.neighbourhood where tenant_id = '0196c3e8-0000-7000-8000-00000000e001';

insert into platform.tenant (id, slug, display_name, region_name, region_kind, default_locale, timezone)
values ('0196c3e8-0000-7000-8000-00000000e001', 'synthetic-plans', 'Synthetic plans v3',
        'Synthetic County', 'demo', 'en', 'America/New_York')
on conflict (id) do update set display_name = excluded.display_name;

insert into directory.organisation (id, tenant_id, legal_name, display_name, slug, org_kind, status, approved_at)
select ('0196c3e8-0000-7000-8000-0000000' || lpad(to_hex(g), 5, '0'))::uuid,
       '0196c3e8-0000-7000-8000-00000000e001', 'Org ' || g, 'Org ' || g, 'syn-org-' || g, 'nonprofit', 'approved', now()
from generate_series(1, 400) g
on conflict (id) do nothing;

insert into platform.neighbourhood (id, tenant_id, code, kind)
select ('0196c3e8-0000-7000-8000-00000001' || lpad(to_hex(g), 4, '0'))::uuid,
       '0196c3e8-0000-7000-8000-00000000e001', 'syn-hood-' || g, 'neighbourhood'
from generate_series(1, 18) g;

select setseed(0.4242);

create temporary table syn as
select g,
       gen_random_uuid()                                             as listing_id,
       ('0196c3e8-0000-7000-8000-0000000' || lpad(to_hex(1 + floor(random() * 400)::int), 5, '0'))::uuid as org_id,
       case when random() < 0.25 then 'event' else 'program' end     as kind,
       (array['robotics','coding','art','music','science','basketball',
              'reading','chess','theatre','swimming'])[1 + floor(random() * 10)::int] as word,
       (array['stem','arts','sports','academic','outdoors','health',
              'civic','careers','life-skills','college-prep'])[1 + floor(random() * 10)::int] as cat,
       5 + floor(random() * 11)::int                                 as age_min,
       2 + floor(random() * 5)::int                                  as age_span,
       case when random() < 0.55 then 0 else (500 + floor(random() * 20) * 250)::int end as cost_cents,
       now() + (random() * 60) * interval '1 day'                    as session_start,
       now() - (random() * 90) * interval '1 day'                    as published_at,
       random() * 100                                                as popularity,
       floor(random() * 7)::int                                      as dow,
       case when random() < 0.05 then 0 when random() < 0.11 then 2 else 1 end as sites,
       ('0196c3e8-0000-7000-8000-00000001' || lpad(to_hex(1 + floor(random() * 18)::int), 4, '0'))::uuid as hood
from generate_series(1, 50000) g;

insert into opportunity.listing
  (id, tenant_id, org_id, kind, slug, title, summary, status, published_at,
   registration_mode, external_url, cost_model, cost_cents_min, modality, age_min, age_max)
select listing_id, '0196c3e8-0000-7000-8000-00000000e001', org_id, kind,
       'syn-' || g, initcap(word) || ' ' || g, 'A synthetic ' || word || ' session.', 'published', published_at,
       'external', 'https://example.org/syn/' || g,
       case when cost_cents = 0 then 'free' else 'fixed' end,
       case when cost_cents = 0 then null else cost_cents end,
       case when sites = 0 then 'virtual' else 'in_person' end, age_min, age_min + age_span
from syn;

-- One row per site; an online-only listing has one row with no site and no place.
insert into opportunity.listing_search
  (id, tenant_id, listing_id, site_id, org_id, slug, kind, is_published, is_primary, facets,
   age_range, cost_cents, is_free, modality, neighbourhood_id, location, session_window,
   next_start_at, popularity, published_at, is_drop_in, title, summary, fuzzy_text, search_vector)
select gen_random_uuid(), '0196c3e8-0000-7000-8000-00000000e001', s.listing_id,
       case when s.sites = 0 then null else gen_random_uuid() end,
       s.org_id, 'syn-' || s.g, s.kind, true, site_number = 1,
       array_cat(
         array['kind:' || s.kind, 'cat:' || s.cat, 'dow:' || s.dow,
               'mode:' || case when s.sites = 0 then 'virtual' else 'in_person' end, 'org:' || s.org_id::text]
           || case when s.cost_cents = 0 then array['free'] else array[]::text[] end
           || case when s.sites = 0 then array[]::text[] else array['hood:' || s.hood::text] end,
         array(select 'age:' || a from generate_series(s.age_min, s.age_min + s.age_span) a)),
       int4range(s.age_min, s.age_min + s.age_span, '[]'),
       s.cost_cents, s.cost_cents = 0,
       case when s.sites = 0 then 'virtual' else 'in_person' end,
       case when s.sites = 0 then null else s.hood end,
       case when s.sites = 0 then null
            else st_setsrid(st_makepoint(-73.9 + random() * 0.3, 41.0 + random() * 0.2), 4326)::geography end,
       tstzrange(s.session_start, s.session_start + interval '90 minutes'),
       s.session_start, s.popularity, s.published_at, false,
       initcap(s.word) || ' ' || s.g, 'A synthetic ' || s.word || ' session.',
       initcap(s.word) || ' ' || s.g,
       to_tsvector('platform.ts_en'::regconfig, initcap(s.word) || ' ' || s.g || ' ' || s.word)
from syn s
cross join lateral generate_series(1, greatest(s.sites, 1)) site_number;

drop table syn;

analyze opportunity.listing;
analyze opportunity.listing_search;
