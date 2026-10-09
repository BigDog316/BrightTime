-- =====================================================================
-- BrightTime :: 90 :: landscape
-- Community asset inventory, youth-led field collection (offline-first),
-- resident and youth surveys, census denominators, travel-time layers,
-- supply-vs-need analysis and opportunity-desert scoring.
-- =====================================================================
set search_path = landscape, public;

create table asset (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  asset_kind    text not null,
  name          text not null,
  operator_org_id uuid references directory.organisation(id) on delete set null,
  address_line1 text,
  locality      text,
  postal_code   text,
  location      geography(Point, 4326) not null,
  neighbourhood_id uuid references platform.neighbourhood(id),
  phone         text,
  website_url   text,
  status        text not null default 'proposed',
  confidence    text not null default 'unverified',
  source        text not null,
  source_ref    text,
  first_observed_at timestamptz,
  last_observed_at  timestamptz,
  verified_by   uuid references identity.app_user(id),
  verified_at   timestamptz,
  closed_at     timestamptz,
  created_at    timestamptz not null default now(),
  updated_at    timestamptz not null default now(),
  constraint ck_asset_kind check (asset_kind in
    ('school','park','playground','library','rec_center','community_center','faith_site',
     'museum','pool','sports_field','makerspace','clinic','youth_center','transit_stop',
     'wifi_hotspot','food_site','other')),
  constraint ck_asset_status check (status in ('proposed','verified','rejected','duplicate','closed')),
  constraint ck_asset_confidence check (confidence in ('unverified','single_observation','corroborated','authoritative')),
  constraint ck_asset_source check (source in
    ('field_collection','open_data','partner_import','organiser_entry','osm','resident_report')),
  constraint ck_asset_verified check ((status = 'verified') = (verified_at is not null))
);
create index ix_asset_location on asset using gist (location) where status = 'verified';
create index ix_asset_tenant_kind on asset (tenant_id, asset_kind) where status = 'verified';
create index ix_asset_review on asset (tenant_id, created_at) where status = 'proposed';
create index ix_asset_name_trgm on asset using gin (name gin_trgm_ops);
create trigger asset_touch before update on asset
  for each row execute function platform.touch_updated_at();

create table asset_feature (
  asset_id   uuid not null references asset(id) on delete cascade,
  term_id    uuid not null references platform.taxonomy_term(id) on delete restrict,
  value_text text,
  value_bool boolean,
  value_number numeric,
  observed_at timestamptz,
  constraint pk_asset_feature primary key (asset_id, term_id)
);

create table asset_photo (
  id          uuid primary key,
  asset_id    uuid not null references asset(id) on delete cascade,
  media_id    uuid not null references platform.media_object(id) on delete restrict,
  caption     text,
  taken_at    timestamptz,
  review_state text not null default 'pending',
  reviewed_by uuid references identity.app_user(id),
  reviewed_at timestamptz,
  reject_reason text,
  constraint ck_asset_photo_review check (review_state in ('pending','approved','rejected','redacted')),
  constraint ux_asset_photo unique (asset_id, media_id)
);
create index ix_asset_photo_review on asset_photo (review_state) where review_state = 'pending';

comment on table asset_photo is
  'Field photographs are reviewed before they are visible. Review rejects any image containing identifiable people. GPS EXIF is stripped at upload (platform.media_object.exif_stripped_at); the intended coordinate is carried in field_observation, not in the file.';

create table asset_merge (
  id         uuid primary key,
  tenant_id  uuid not null references platform.tenant(id) on delete cascade,
  kept_asset_id   uuid not null references asset(id) on delete cascade,
  merged_asset_id uuid not null references asset(id) on delete cascade,
  merged_by  uuid references identity.app_user(id),
  merged_at  timestamptz not null default now(),
  constraint ck_asset_merge_distinct check (kept_asset_id <> merged_asset_id)
);

-- ---------------------------------------------------------------------
-- Field collection (MAPSCorps-style: trained youth and staff in the field)
-- ---------------------------------------------------------------------
create table field_campaign (
  id          uuid primary key,
  tenant_id   uuid not null references platform.tenant(id) on delete cascade,
  name        text not null,
  protocol_md text,
  starts_on   date not null,
  ends_on     date,
  target_asset_kinds text[] not null default '{}',
  requires_photo boolean not null default false,
  min_collector_age smallint not null default 14,
  status      text not null default 'planning',
  created_by  uuid references identity.app_user(id),
  constraint ck_field_campaign_status check (status in ('planning','active','paused','closed')),
  constraint ck_field_campaign_dates check (ends_on is null or ends_on >= starts_on)
);

create table field_assignment (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  campaign_id   uuid not null references field_campaign(id) on delete cascade,
  collector_user_id uuid not null references identity.app_user(id) on delete cascade,
  supervisor_user_id uuid references identity.app_user(id) on delete set null,
  area          geography(MultiPolygon, 4326),
  neighbourhood_id uuid references platform.neighbourhood(id),
  state         text not null default 'assigned',
  assigned_at   timestamptz not null default now(),
  completed_at  timestamptz,
  constraint ck_field_assignment_state check (state in ('assigned','in_progress','submitted','accepted','returned')),
  -- Youth never go out unsupervised in the data model either.
  constraint ck_field_assignment_supervisor check (state = 'assigned' or supervisor_user_id is not null)
);
create index ix_field_assignment_collector on field_assignment (collector_user_id, state);
create index ix_field_assignment_area on field_assignment using gist (area);

create table field_observation (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  campaign_id   uuid not null references field_campaign(id) on delete cascade,
  assignment_id uuid references field_assignment(id) on delete set null,
  collector_user_id uuid not null references identity.app_user(id) on delete restrict,
  asset_id      uuid references asset(id) on delete set null,
  observation_kind text not null,
  name_observed text,
  payload       jsonb not null default '{}'::jsonb,
  location      geography(Point, 4326) not null,
  accuracy_m    numeric(7,2),
  captured_at   timestamptz not null,
  device_id     text,
  client_record_id uuid,
  received_at   timestamptz not null default now(),
  review_state  text not null default 'pending',
  reviewed_by   uuid references identity.app_user(id),
  reviewed_at   timestamptz,
  review_note   text,
  constraint ck_field_obs_kind check (observation_kind in
    ('new_asset','confirm_asset','update_asset','asset_closed','duplicate','feature_note')),
  constraint ck_field_obs_review check (review_state in ('pending','accepted','rejected','needs_followup')),
  constraint ck_field_obs_accuracy check (accuracy_m is null or accuracy_m >= 0)
);
create unique index ux_field_obs_client_record
  on field_observation (device_id, client_record_id)
  where client_record_id is not null;
create index ix_field_obs_queue on field_observation (tenant_id, received_at) where review_state = 'pending';
create index ix_field_obs_location on field_observation using gist (location);
create index ix_field_obs_collector on field_observation (collector_user_id, captured_at desc);

comment on column field_observation.location is
  'The location of the ASSET being recorded, captured at the moment the collector taps "record here". The platform does not log a collector''s track, idle position or route.';

-- ---------------------------------------------------------------------
-- Surveys
-- ---------------------------------------------------------------------
create table survey (
  id           uuid primary key,
  tenant_id    uuid not null references platform.tenant(id) on delete cascade,
  survey_kind  text not null,
  title        text not null,
  intro_md     text,
  definition   jsonb not null,
  is_anonymous boolean not null default true,
  target_audience text not null default 'resident',
  org_id       uuid references directory.organisation(id) on delete cascade,
  listing_id   uuid references opportunity.listing(id) on delete cascade,
  opens_at     timestamptz,
  closes_at    timestamptz,
  status       text not null default 'draft',
  created_by   uuid references identity.app_user(id),
  created_at   timestamptz not null default now(),
  constraint ck_survey_kind check (survey_kind in
    ('resident_needs','youth_voice','program_feedback','program_quality_self_assessment','outcome_followup')),
  constraint ck_survey_audience check (target_audience in ('resident','youth','guardian','staff','mentor')),
  constraint ck_survey_status check (status in ('draft','open','closed','archived')),
  constraint ck_survey_definition check (jsonb_typeof(definition -> 'questions') = 'array'),
  constraint ck_survey_window check (closes_at is null or opens_at is null or closes_at > opens_at),
  -- A youth survey is anonymous unless it is an outcome follow-up tied to
  -- an enrollment, which needs consent recorded separately.
  constraint ck_survey_youth_anonymous
    check (target_audience <> 'youth' or is_anonymous or survey_kind = 'outcome_followup')
);

create table survey_response (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  survey_id     uuid not null references survey(id) on delete cascade,
  respondent_user_id uuid references identity.app_user(id) on delete set null,
  respondent_token_hash bytea,
  neighbourhood_id uuid references platform.neighbourhood(id),
  age_band      text,
  locale        text,
  answers       jsonb not null default '{}'::jsonb,
  started_at    timestamptz not null default now(),
  submitted_at  timestamptz,
  constraint ck_survey_response_identity
    check (respondent_user_id is null or respondent_token_hash is null)
);
create index ix_survey_response_survey on survey_response (survey_id, submitted_at);
create unique index ux_survey_response_once
  on survey_response (survey_id, respondent_user_id)
  where respondent_user_id is not null;

create table survey_answer (
  response_id   uuid not null references survey_response(id) on delete cascade,
  question_key  text not null,
  value_text    text,
  value_number  numeric,
  value_bool    boolean,
  value_choices text[],
  constraint pk_survey_answer primary key (response_id, question_key)
);
create index ix_survey_answer_key on survey_answer (question_key);

-- Barriers reported by residents: the demand-side half of the landscape.
create table reported_barrier (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  response_id   uuid references survey_response(id) on delete cascade,
  neighbourhood_id uuid references platform.neighbourhood(id),
  barrier_kind  text not null,
  severity      smallint,
  age_band      text,
  reported_at   timestamptz not null default now(),
  constraint ck_barrier_kind check (barrier_kind in
    ('cost','transportation','schedule','awareness','safety','language','accessibility',
     'capacity_full','documentation','childcare','trust','other')),
  constraint ck_barrier_severity check (severity is null or severity between 1 and 5)
);
create index ix_barrier_hood on reported_barrier (tenant_id, neighbourhood_id, barrier_kind);

-- ---------------------------------------------------------------------
-- Denominators and accessibility layers
-- ---------------------------------------------------------------------
create table census_denominator (
  tenant_id        uuid not null references platform.tenant(id) on delete cascade,
  neighbourhood_id uuid not null references platform.neighbourhood(id) on delete cascade,
  vintage_year     smallint not null,
  age_band         text not null,
  population       int not null,
  margin_of_error  int,
  source           text not null,
  loaded_at        timestamptz not null default now(),
  constraint pk_census_denominator primary key (tenant_id, neighbourhood_id, vintage_year, age_band),
  constraint ck_census_population check (population >= 0),
  constraint ck_census_source check (source in ('acs5','decennial','pep','nces','state_ed','local_estimate'))
);

create table transit_feed (
  id          uuid primary key,
  tenant_id   uuid not null references platform.tenant(id) on delete cascade,
  agency_name text not null,
  feed_url    text not null,
  feed_sha256 bytea,
  valid_from  date,
  valid_to    date,
  loaded_at   timestamptz,
  constraint ux_transit_feed unique (tenant_id, agency_name, feed_url)
);

create table transit_stop (
  id          uuid primary key,
  tenant_id   uuid not null references platform.tenant(id) on delete cascade,
  feed_id     uuid not null references transit_feed(id) on delete cascade,
  gtfs_stop_id text not null,
  name        text not null,
  location    geography(Point, 4326) not null,
  is_wheelchair_boarding boolean,
  constraint ux_transit_stop unique (feed_id, gtfs_stop_id)
);
create index ix_transit_stop_location on transit_stop using gist (location);

-- Precomputed accessibility: origin is a neighbourhood centroid (or a
-- school), destination is a provider site. Kept as a table rather than
-- computed live because a transit isochrone is far too slow for search.
create table travel_time (
  tenant_id    uuid not null references platform.tenant(id) on delete cascade,
  origin_kind  text not null,
  origin_id    uuid not null,
  site_id      uuid not null references directory.site(id) on delete cascade,
  mode         text not null,
  minutes      smallint not null,
  transfers    smallint,
  departure_band text not null default 'weekday_pm',
  computed_at  timestamptz not null default now(),
  constraint pk_travel_time primary key (tenant_id, origin_kind, origin_id, site_id, mode, departure_band),
  constraint ck_travel_origin_kind check (origin_kind in ('neighbourhood','school')),
  constraint ck_travel_mode check (mode in ('walk','transit','bike','drive')),
  constraint ck_travel_minutes check (minutes between 0 and 600),
  constraint ck_travel_band check (departure_band in ('weekday_am','weekday_pm','weekday_evening','saturday'))
);
create index ix_travel_time_site on travel_time (site_id, mode, minutes);

-- ---------------------------------------------------------------------
-- Analysis outputs. Recomputed nightly; every row carries the inputs so
-- a civic user can see why a neighbourhood scored as it did.
-- ---------------------------------------------------------------------
create table supply_metric (
  id               bigint generated always as identity primary key,
  tenant_id        uuid not null references platform.tenant(id) on delete cascade,
  neighbourhood_id uuid not null references platform.neighbourhood(id) on delete cascade,
  as_of_date       date not null,
  age_band         text not null,
  category_term_id uuid references platform.taxonomy_term(id) on delete cascade,
  program_count    int not null default 0,
  seat_count       int not null default 0,
  free_seat_count  int not null default 0,
  youth_population int,
  seats_per_1000   numeric(10,2),
  median_cost_cents int,
  transit_reachable_seat_count int
);
-- PRIMARY KEY cannot carry an expression, and "all categories" rows need a
-- NULL category, so natural uniqueness is an index.
create unique index ux_supply_metric
  on supply_metric (tenant_id, neighbourhood_id, as_of_date, age_band,
                    coalesce(category_term_id, '00000000-0000-0000-0000-000000000000'::uuid));
create index ix_supply_metric_latest on supply_metric (tenant_id, as_of_date desc, age_band);

create table opportunity_desert (
  tenant_id        uuid not null references platform.tenant(id) on delete cascade,
  neighbourhood_id uuid not null references platform.neighbourhood(id) on delete cascade,
  as_of_date       date not null,
  age_band         text not null,
  score            numeric(6,3) not null,
  rank_in_tenant   int,
  drivers          jsonb not null default '{}'::jsonb,
  constraint pk_opportunity_desert primary key (tenant_id, neighbourhood_id, as_of_date, age_band),
  constraint ck_desert_score check (score between 0 and 100)
);

comment on column opportunity_desert.drivers is
  'The factor contributions that produced the score (supply per 1000, transit reach, cost coverage, age-band gaps, reported barriers). Published alongside the score so the number is arguable rather than oracular.';
