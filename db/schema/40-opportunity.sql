-- =====================================================================
-- BrightTime :: 40 :: opportunity
-- Listings (programs, events, jobs, resources), schedules, occurrences,
-- collections, neighbourhood pages, saved items, search read model,
-- zero-result logging for unmet-demand analysis.
-- =====================================================================
set search_path = opportunity, public;

-- ---------------------------------------------------------------------
-- Listing: the one aggregate four public things share. `kind` discriminates;
-- CHECK constraints keep each kind's required fields honest.
-- ---------------------------------------------------------------------
create table listing (
  id                 uuid primary key,
  tenant_id          uuid not null references platform.tenant(id) on delete restrict,
  org_id             uuid not null references directory.organisation(id) on delete cascade,
  kind               text not null,
  slug               citext not null,
  title              text not null,
  summary            text not null,
  description        text,
  primary_language   text not null default 'en',
  status             text not null default 'draft',

  -- audience
  age_min            smallint,
  age_max            smallint,
  age_range          int4range generated always as
                       (int4range(age_min::int, age_max::int, '[]')) stored,
  grade_min          smallint,
  grade_max          smallint,
  grade_range        int4range generated always as
                       (int4range(grade_min::int, grade_max::int, '[]')) stored,
  audience_note      text,

  -- delivery
  modality           text not null default 'in_person',
  virtual_url        text,
  is_drop_in         boolean not null default false,
  meals_provided     boolean not null default false,
  transport_provided boolean not null default false,
  transport_note     text,

  -- cost
  cost_model         text not null default 'free',
  cost_cents_min     int,
  cost_cents_max     int,
  is_free            boolean generated always as (cost_model = 'free') stored,
  sliding_scale_note text,

  -- registration
  registration_mode  text not null default 'external',
  external_url       text,
  external_url_state text not null default 'unchecked',
  external_url_checked_at timestamptz,
  capacity_total     int,
  waitlist_enabled   boolean not null default false,

  -- publication workflow
  submitted_at       timestamptz,
  submitted_by       uuid references identity.app_user(id),
  reviewed_at        timestamptz,
  reviewed_by        uuid references identity.app_user(id),
  review_note        text,
  published_at       timestamptz,
  unpublished_at     timestamptz,
  expires_at         timestamptz,

  -- provenance
  source             text not null default 'manual',
  source_record_id   text,
  cloned_from_id     uuid references listing(id),
  season_label       text,

  hero_media_id      uuid references platform.media_object(id),
  contact_email      citext,
  contact_phone      text,
  accessibility_note text,

  created_at         timestamptz not null default now(),
  updated_at         timestamptz not null default now(),
  created_by         uuid references identity.app_user(id),

  constraint ux_listing_slug unique (tenant_id, slug),
  constraint ck_listing_kind check (kind in ('program','event','job','resource')),
  constraint ck_listing_status check (status in
    ('draft','pending_review','changes_requested','published','unpublished','rejected','archived')),
  constraint ck_listing_modality check (modality in ('in_person','virtual','hybrid')),
  constraint ck_listing_cost_model check (cost_model in ('free','fixed','sliding_scale','varies','stipend_paid')),
  constraint ck_listing_registration_mode check (registration_mode in ('none','external','builtin','waitlist_only')),
  constraint ck_listing_url_state check (external_url_state in ('unchecked','ok','redirect','broken','blocked')),
  constraint ck_listing_source check (source in ('manual','csv_import','hsds_import','api','district_feed')),

  constraint ck_listing_ages check (age_min is null or age_max is null or age_min <= age_max),
  constraint ck_listing_age_bounds check ((age_min is null or age_min between 0 and 30)
                                      and (age_max is null or age_max between 0 and 30)),
  constraint ck_listing_grades check (grade_min is null or grade_max is null or grade_min <= grade_max),
  constraint ck_listing_grade_bounds check ((grade_min is null or grade_min between -1 and 12)
                                        and (grade_max is null or grade_max between -1 and 12)),
  constraint ck_listing_cost check (
    case cost_model
      when 'free' then cost_cents_min is null and cost_cents_max is null
      when 'fixed' then cost_cents_min is not null and cost_cents_max is null
      when 'sliding_scale' then cost_cents_min is not null and cost_cents_max is not null
                               and cost_cents_max >= cost_cents_min
      else true
    end),
  constraint ck_listing_external_url check (
    registration_mode <> 'external' or (external_url is not null and external_url ~* '^https?://')),
  constraint ck_listing_virtual_url check (
    modality = 'in_person' or virtual_url is null or virtual_url ~* '^https?://'),
  constraint ck_listing_capacity check (capacity_total is null or capacity_total > 0),
  constraint ck_listing_builtin_capacity check (
    registration_mode <> 'builtin' or capacity_total is not null),
  constraint ck_listing_published check ((status = 'published') = (published_at is not null)),
  constraint ck_listing_job_is_paid check (kind <> 'job' or cost_model in ('stipend_paid','free')),
  constraint ck_listing_resource_dropin check (kind <> 'resource' or registration_mode = 'none')
);
create index ix_listing_org_status on listing (org_id, status);
create index ix_listing_review_queue on listing (tenant_id, submitted_at)
  where status in ('pending_review','changes_requested');
create index ix_listing_published on listing (tenant_id, published_at desc) where status = 'published';
create index ix_listing_expiring on listing (expires_at) where status = 'published' and expires_at is not null;
create index ix_listing_url_recheck on listing (external_url_checked_at)
  where registration_mode = 'external' and status = 'published';
create trigger listing_touch before update on listing
  for each row execute function platform.touch_updated_at();

create table listing_translation (
  listing_id   uuid not null references listing(id) on delete cascade,
  locale       text not null,
  title        text not null,
  summary      text not null,
  description  text,
  translation_source text not null default 'human',
  translated_at timestamptz not null default now(),
  translated_by uuid references identity.app_user(id),
  reviewed_at  timestamptz,
  constraint pk_listing_translation primary key (listing_id, locale),
  constraint ck_listing_translation_source check (translation_source in ('human','machine','machine_reviewed'))
);

create table listing_term (
  listing_id uuid not null references listing(id) on delete cascade,
  term_id    uuid not null references platform.taxonomy_term(id) on delete restrict,
  is_primary boolean not null default false,
  constraint pk_listing_term primary key (listing_id, term_id)
);
create index ix_listing_term_term on listing_term (term_id);
create unique index ux_listing_term_primary on listing_term (listing_id) where is_primary;

create table listing_site (
  listing_id uuid not null references listing(id) on delete cascade,
  site_id    uuid not null references directory.site(id) on delete restrict,
  constraint pk_listing_site primary key (listing_id, site_id)
);
create index ix_listing_site_site on listing_site (site_id);

create table listing_language (
  listing_id    uuid not null references listing(id) on delete cascade,
  language_code text not null,
  is_instruction boolean not null default true,
  constraint pk_listing_language primary key (listing_id, language_code)
);

create table listing_accommodation (
  listing_id uuid not null references listing(id) on delete cascade,
  term_id    uuid not null references platform.taxonomy_term(id) on delete restrict,
  note       text,
  constraint pk_listing_accommodation primary key (listing_id, term_id)
);

create table listing_media (
  id         uuid primary key,
  listing_id uuid not null references listing(id) on delete cascade,
  media_id   uuid not null references platform.media_object(id) on delete restrict,
  alt_text   text not null,
  sort_order smallint not null default 0,
  constraint ux_listing_media unique (listing_id, media_id),
  constraint ck_listing_media_alt check (length(btrim(alt_text)) > 0)
);

comment on column listing_media.alt_text is
  'NOT NULL and non-empty: WCAG 2.2 AA 1.1.1 is enforced by the schema, not by reviewer diligence.';

-- ---------------------------------------------------------------------
-- Schedules. A session is a rule (RFC 5545 RRULE); occurrences are
-- materialised ahead by a job so that date filtering is a simple
-- indexed range predicate rather than recurrence expansion at query time.
-- ---------------------------------------------------------------------
create table listing_session (
  id              uuid primary key,
  tenant_id       uuid not null references platform.tenant(id) on delete cascade,
  listing_id      uuid not null references listing(id) on delete cascade,
  site_id         uuid references directory.site(id) on delete restrict,
  label           text,
  timezone        text not null,
  starts_on       date not null,
  ends_on         date,
  start_time      time,
  end_time        time,
  rrule           text,
  exdates         date[] not null default '{}',
  is_all_day      boolean not null default false,
  is_cancelled    boolean not null default false,
  expanded_through date,
  constraint ck_listing_session_dates check (ends_on is null or ends_on >= starts_on),
  constraint ck_listing_session_times
    check (is_all_day or (start_time is not null and end_time is not null and end_time > start_time)),
  constraint ck_listing_session_rrule check (rrule is null or rrule ~ '^(RRULE:)?FREQ=')
);
create index ix_listing_session_listing on listing_session (listing_id) where not is_cancelled;
-- No time-dependent predicate: now() is STABLE, which an index predicate forbids,
-- and the date comparison would depend on the session's TimeZone. The expansion
-- job filters on ends_on at run time; the index only has to find work quickly.
create index ix_listing_session_expand on listing_session (expanded_through, ends_on)
  where not is_cancelled;

create table listing_occurrence (
  id          bigint generated always as identity primary key,
  tenant_id   uuid not null,
  listing_id  uuid not null references listing(id) on delete cascade,
  session_id  uuid not null references listing_session(id) on delete cascade,
  site_id     uuid,
  starts_at   timestamptz not null,
  ends_at     timestamptz not null,
  day_of_week smallint not null,
  is_cancelled boolean not null default false,
  constraint ux_listing_occurrence unique (session_id, starts_at),
  constraint ck_listing_occurrence_order check (ends_at > starts_at)
);
create index ix_listing_occurrence_window
  on listing_occurrence (tenant_id, starts_at) where not is_cancelled;
create index ix_listing_occurrence_listing
  on listing_occurrence (listing_id, starts_at) where not is_cancelled;

comment on column listing_occurrence.day_of_week is
  '0=Sunday. Written by the expansion job using the session timezone, not derived from starts_at: extract(isodow from timestamptz) is STABLE (TimeZone-dependent) and cannot back a generated column, and a UTC-derived value would be wrong for late-evening sessions.';

create table listing_deadline (
  id          uuid primary key,
  listing_id  uuid not null references listing(id) on delete cascade,
  deadline_kind text not null,
  due_at      timestamptz not null,
  label       text,
  constraint ck_listing_deadline_kind check (deadline_kind in
    ('application','registration','audition','early_bird','scholarship','document_submission'))
);
create index ix_listing_deadline_due on listing_deadline (due_at);

-- ---------------------------------------------------------------------
-- Curation
-- ---------------------------------------------------------------------
create table collection (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  slug          citext not null,
  title         text not null,
  description   text,
  hero_media_id uuid references platform.media_object(id),
  curator_user_id uuid references identity.app_user(id),
  status        text not null default 'draft',
  starts_on     date,
  ends_on       date,
  is_featured   boolean not null default false,
  sort_order    smallint not null default 0,
  created_at    timestamptz not null default now(),
  updated_at    timestamptz not null default now(),
  constraint ux_collection_slug unique (tenant_id, slug),
  constraint ck_collection_status check (status in ('draft','published','archived')),
  constraint ck_collection_dates check (ends_on is null or starts_on is null or ends_on >= starts_on)
);
create index ix_collection_featured on collection (tenant_id, sort_order)
  where status = 'published' and is_featured;

create table collection_item (
  collection_id uuid not null references collection(id) on delete cascade,
  listing_id    uuid not null references listing(id) on delete cascade,
  sort_order    smallint not null default 0,
  curator_note  text,
  constraint pk_collection_item primary key (collection_id, listing_id)
);

create table collection_translation (
  collection_id uuid not null references collection(id) on delete cascade,
  locale        text not null,
  title         text not null,
  description   text,
  constraint pk_collection_translation primary key (collection_id, locale)
);

create table neighbourhood_page (
  id               uuid primary key,
  tenant_id        uuid not null references platform.tenant(id) on delete cascade,
  neighbourhood_id uuid not null references platform.neighbourhood(id) on delete cascade,
  status           text not null default 'draft',
  hero_media_id    uuid references platform.media_object(id),
  organiser_user_id uuid references identity.app_user(id),
  updated_at       timestamptz not null default now(),
  constraint ux_neighbourhood_page unique (neighbourhood_id),
  constraint ck_neighbourhood_page_status check (status in ('draft','published','archived'))
);

create table neighbourhood_page_block (
  id       uuid primary key,
  page_id  uuid not null references neighbourhood_page(id) on delete cascade,
  locale   text not null,
  block_kind text not null,
  sort_order smallint not null default 0,
  heading  text,
  body_md  text,
  payload  jsonb not null default '{}'::jsonb,
  constraint ck_nhp_block_kind check (block_kind in
    ('rich_text','collection','listing_list','contact_list','map','faq','transit'))
);
create index ix_nhp_block_page on neighbourhood_page_block (page_id, locale, sort_order);

-- ---------------------------------------------------------------------
-- Youth-owned signals
-- ---------------------------------------------------------------------
create table saved_item (
  id          uuid primary key,
  tenant_id   uuid not null references platform.tenant(id) on delete cascade,
  user_id     uuid not null references identity.app_user(id) on delete cascade,
  listing_id  uuid not null references listing(id) on delete cascade,
  note        text,
  saved_at    timestamptz not null default now(),
  constraint ux_saved_item unique (user_id, listing_id)
);
create index ix_saved_item_listing on saved_item (listing_id);

create table listing_reminder (
  id          uuid primary key,
  tenant_id   uuid not null references platform.tenant(id) on delete cascade,
  user_id     uuid not null references identity.app_user(id) on delete cascade,
  listing_id  uuid not null references listing(id) on delete cascade,
  anchor      text not null,
  lead_minutes int not null default 1440,
  remind_at   timestamptz not null,
  sent_at     timestamptz,
  constraint ck_listing_reminder_anchor check (anchor in ('deadline','first_session','next_session','custom')),
  constraint ck_listing_reminder_lead check (lead_minutes between 0 and 43200)
);
create index ix_listing_reminder_due on listing_reminder (remind_at) where sent_at is null;

create table user_interest (
  user_id    uuid not null references identity.app_user(id) on delete cascade,
  term_id    uuid not null references platform.taxonomy_term(id) on delete cascade,
  weight     smallint not null default 1,
  declared_at timestamptz not null default now(),
  constraint pk_user_interest primary key (user_id, term_id),
  constraint ck_user_interest_weight check (weight between 1 and 5)
);

create table user_search_preference (
  user_id          uuid primary key references identity.app_user(id) on delete cascade,
  tenant_id        uuid not null references platform.tenant(id) on delete cascade,
  anchor_kind      text not null default 'none',
  anchor_neighbourhood_id uuid references platform.neighbourhood(id),
  anchor_school_id uuid references platform.school(id),
  max_distance_m   int,
  preferred_languages text[] not null default '{}',
  needs_accommodation_terms uuid[] not null default '{}',
  low_bandwidth    boolean not null default false,
  updated_at       timestamptz not null default now(),
  constraint ck_usp_anchor check (anchor_kind in ('none','neighbourhood','school')),
  constraint ck_usp_anchor_ref check (
    case anchor_kind
      when 'neighbourhood' then anchor_neighbourhood_id is not null
      when 'school'        then anchor_school_id is not null
      else true
    end),
  constraint ck_usp_distance check (max_distance_m is null or max_distance_m between 100 and 80000)
);

comment on table user_search_preference is
  'The search anchor is a neighbourhood or a school, never a home address. A typed address is geocoded in-request and never persisted.';

-- ---------------------------------------------------------------------
-- Search telemetry. No user id, no raw query text: a normalised query
-- hash plus the filter set. Feeds unmet-demand analysis in G.
-- ---------------------------------------------------------------------
create table search_event (
  id            bigint generated always as identity,
  tenant_id     uuid not null,
  occurred_at   timestamptz not null default now(),
  query_hash    bytea,
  query_token_count smallint,
  filters       jsonb not null default '{}'::jsonb,
  neighbourhood_id uuid,
  result_count  int not null,
  had_zero_results boolean generated always as (result_count = 0) stored,
  surface       text not null default 'web',
  session_hash  bytea,
  locale        text,
  constraint pk_search_event primary key (id, occurred_at),
  constraint ck_search_event_surface check (surface in ('web','mobile','widget','api'))
) partition by range (occurred_at);
create table search_event_default partition of search_event default;

comment on table search_event is
  'Deliberately pseudonymous: session_hash is HMAC(session id, daily-rotating key) so same-session queries can be grouped for a day and never beyond.';

-- ---------------------------------------------------------------------
-- SEARCH READ MODEL
--
-- One row per (listing, site). Virtual-only listings get a single row
-- with site_id IS NULL, which is why the uniqueness is NULLS NOT DISTINCT.
--
-- `facets` folds every set/equality predicate into one text[] so that a
-- single GIN index serves category, interest, age, grade, language,
-- accommodation, day-of-week, modality, neighbourhood and the boolean
-- amenities. See docs/plan/03-indexing-and-query-plans.md.
--
-- Maintained by the projection job (outbox-driven), never by hand.
-- ---------------------------------------------------------------------
create table listing_search (
  id              uuid primary key,
  tenant_id       uuid not null,
  listing_id      uuid not null references listing(id) on delete cascade,
  site_id         uuid,
  org_id          uuid not null,
  -- Copied from listing.slug so a result card links to its page without a lookup.
  slug            citext not null,
  kind            text not null,
  is_published    boolean not null,

  facets          text[] not null default '{}',

  age_range       int4range,
  grade_range     int4range,
  cost_cents      int not null default 0,
  is_free         boolean not null default true,
  modality        text not null,
  neighbourhood_id uuid,
  location        geography(Point, 4326),

  session_window  tstzrange,
  next_start_at   timestamptz,
  next_deadline_at timestamptz,
  is_drop_in      boolean not null default false,

  search_vector   tsvector not null default ''::tsvector,
  fuzzy_text      text not null default '',
  title           text not null,
  summary         text not null,

  popularity      real not null default 0,
  published_at    timestamptz,
  projected_at    timestamptz not null default now(),

  constraint ux_listing_search_site unique nulls not distinct (listing_id, site_id),
  constraint ck_listing_search_cost check (cost_cents >= 0)
) with (fillfactor = 80,
        autovacuum_vacuum_scale_factor = 0.02,
        autovacuum_analyze_scale_factor = 0.01);

-- ---- the six indexes that carry the finder -------------------------
-- 1. every equality/set predicate, with tenant as a leading GIN key
create index ix_ls_facets on listing_search using gin (tenant_id, facets)
  where is_published;
-- 2. full-text
create index ix_ls_fts on listing_search using gin (tenant_id, search_vector)
  where is_published;
-- 3. typo tolerance over title + org name + category names
create index ix_ls_fuzzy on listing_search using gin (fuzzy_text gin_trgm_ops)
  where is_published;
-- 4. distance
create index ix_ls_geo on listing_search using gist (location)
  where is_published and location is not null;
-- 5. date-range overlap
create index ix_ls_window on listing_search using gist (tenant_id, session_window)
  where is_published;
-- 6. default browse order and keyset pagination
create index ix_ls_next_start on listing_search (tenant_id, next_start_at, listing_id)
  where is_published;
-- maintenance helper: rows whose next_start_at has gone stale
create index ix_ls_stale on listing_search (next_start_at)
  where is_published and next_start_at is not null;
create index ix_ls_cost on listing_search (tenant_id, cost_cents)
  where is_published and not is_free;

-- Correlated columns the planner otherwise mis-estimates.
create statistics st_ls_kind_hood (dependencies, ndistinct)
  on tenant_id, kind, neighbourhood_id from listing_search;
alter table listing_search alter column facets set statistics 2000;

comment on column listing_search.facets is
  $$Token namespaces: kind:, cat:, int:, age:, grade:, lang:, accom:, dow:, mode:, hood:, org:, plus bare flags free, meals, transport, dropin, virtual_ok. Age and grade are enumerated per year (age:5 .. age:24) so containment becomes a cheap GIN lookup.$$;
