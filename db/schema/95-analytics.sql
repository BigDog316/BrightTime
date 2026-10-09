-- =====================================================================
-- BrightTime :: 95 :: analytics
-- Nightly rollups, dashboard facts, small-cell suppression, and the
-- de-identified research export pipeline.
--
-- Rule: no table in this schema holds a direct identifier. Youth appear
-- only as a per-tenant pseudonym (youth_key) that is a keyed HMAC of the
-- user id, rotated per export. Dashboards read the suppressed views in
-- analytics.v_*, never the fact tables.
-- =====================================================================
set search_path = analytics, public;

create table youth_pseudonym (
  tenant_id   uuid not null references platform.tenant(id) on delete cascade,
  user_id     uuid not null references identity.app_user(id) on delete cascade,
  youth_key   bytea not null,
  key_version smallint not null default 1,
  created_at  timestamptz not null default now(),
  constraint pk_youth_pseudonym primary key (tenant_id, user_id),
  constraint ux_youth_pseudonym_key unique (tenant_id, youth_key, key_version)
);

create table participation_daily (
  tenant_id        uuid not null references platform.tenant(id) on delete cascade,
  activity_date    date not null,
  org_id           uuid,
  listing_id       uuid,
  neighbourhood_id uuid,
  category_term_id uuid,
  age_band         text not null,
  modality         text,
  youth_count      int not null default 0,
  session_count    int not null default 0,
  attendance_minutes bigint not null default 0,
  present_count    int not null default 0,
  absent_count     int not null default 0,
  computed_at      timestamptz not null default now(),
  constraint ck_participation_daily_counts check (youth_count >= 0 and present_count >= 0)
) partition by range (activity_date);
create table participation_daily_default partition of participation_daily default;
create index ix_participation_daily_tenant
  on participation_daily_default (tenant_id, activity_date desc);

-- Distinct-youth counting across arbitrary slices needs a sketch, not a
-- sum of per-slice counts. HLL would be ideal; without the extension we
-- keep an exact roster of pseudonyms per slice-day and count distinct.
create table participation_youth_day (
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  activity_date date not null,
  youth_key     bytea not null,
  org_id        uuid,
  listing_id    uuid,
  neighbourhood_id uuid,
  category_term_id uuid,
  age_band      text not null,
  constraint pk_participation_youth_day
    primary key (tenant_id, activity_date, youth_key, listing_id)
) partition by range (activity_date);
create table participation_youth_day_default partition of participation_youth_day default;

create table seat_utilisation_daily (
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  as_of_date    date not null,
  listing_id    uuid not null,
  org_id        uuid not null,
  neighbourhood_id uuid,
  capacity      int not null,
  seats_filled  int not null,
  waitlist_depth int not null default 0,
  fill_rate     numeric(5,4) generated always as
                  (case when capacity > 0 then seats_filled::numeric / capacity else null end) stored,
  constraint pk_seat_utilisation primary key (tenant_id, as_of_date, listing_id)
);
create index ix_seat_utilisation_date on seat_utilisation_daily (tenant_id, as_of_date desc);

-- Unmet demand: zero-result searches plus waitlist pressure, by slice.
create table demand_gap_daily (
  id               bigint generated always as identity primary key,
  tenant_id        uuid not null references platform.tenant(id) on delete cascade,
  as_of_date       date not null,
  neighbourhood_id uuid,
  category_term_id uuid,
  age_band         text,
  zero_result_searches int not null default 0,
  total_searches   int not null default 0,
  waitlisted_youth int not null default 0,
  unfilled_seats   int not null default 0,
  top_missing_terms text[] not null default '{}'
);
create unique index ux_demand_gap
  on demand_gap_daily (tenant_id, as_of_date,
                       coalesce(neighbourhood_id, '00000000-0000-0000-0000-000000000000'::uuid),
                       coalesce(category_term_id, '00000000-0000-0000-0000-000000000000'::uuid),
                       coalesce(age_band, '*'));
create index ix_demand_gap_date on demand_gap_daily (tenant_id, as_of_date desc);

create table retention_cohort (
  id            bigint generated always as identity primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  cohort_season text not null,
  age_band      text not null,
  neighbourhood_id uuid,
  cohort_size   int not null,
  returned_next_season int not null default 0,
  returned_same_org int not null default 0,
  returned_within_year int not null default 0,
  computed_at   timestamptz not null default now(),
  constraint ck_retention_sizes check (returned_next_season <= cohort_size)
);
create unique index ux_retention_cohort
  on retention_cohort (tenant_id, cohort_season, age_band,
                       coalesce(neighbourhood_id, '00000000-0000-0000-0000-000000000000'::uuid));

create table badge_metric_daily (
  tenant_id      uuid not null references platform.tenant(id) on delete cascade,
  as_of_date     date not null,
  badge_class_id uuid not null,
  issued_count   int not null default 0,
  revoked_count  int not null default 0,
  distinct_recipients int not null default 0,
  constraint pk_badge_metric primary key (tenant_id, as_of_date, badge_class_id)
);

-- ---------------------------------------------------------------------
-- Suppression.
--
-- Every aggregate the platform shows passes through analytics.suppress(),
-- which returns NULL below the tenant's threshold. Complementary
-- suppression (hiding one extra cell in a row whose total would otherwise
-- let a reader subtract back to a suppressed cell) is applied by the
-- reporting layer using analytics.needs_complementary().
-- ---------------------------------------------------------------------
create table suppression_policy (
  tenant_id       uuid not null references platform.tenant(id) on delete cascade,
  metric_family   text not null,
  min_cell_size   smallint not null,
  apply_complementary boolean not null default true,
  updated_at      timestamptz not null default now(),
  constraint pk_suppression_policy primary key (tenant_id, metric_family),
  constraint ck_suppression_min check (min_cell_size >= 5),
  constraint ck_suppression_family check (metric_family in
    ('participation','attendance','demand','retention','badges','survey','workforce'))
);

create or replace function analytics.min_cell(p_tenant uuid, p_family text)
  returns smallint language sql stable parallel safe as $fn$
  select coalesce(
    (select min_cell_size from analytics.suppression_policy
      where tenant_id = p_tenant and metric_family = p_family),
    (select min_cell_size from platform.tenant where id = p_tenant),
    10)::smallint
$fn$;

create or replace function analytics.suppress(p_value bigint, p_threshold smallint)
  returns bigint language sql immutable parallel safe as $fn$
  select case when p_value is null then null
              when p_value = 0 then 0
              when p_value < p_threshold then null
              else p_value end
$fn$;

comment on function analytics.suppress(bigint, smallint) is
  'Zero is published (a true absence of service is the point of the dashboard); 1..threshold-1 is suppressed.';

-- Dashboard-facing view. Civic and funder roles are granted this, never
-- the underlying fact tables.
create or replace view analytics.v_participation_by_neighbourhood as
select pd.tenant_id,
       pd.activity_date,
       pd.neighbourhood_id,
       pd.age_band,
       pd.category_term_id,
       analytics.suppress(sum(pd.youth_count)::bigint,
                          analytics.min_cell(pd.tenant_id, 'participation')) as youth_count,
       analytics.suppress(sum(pd.present_count)::bigint,
                          analytics.min_cell(pd.tenant_id, 'attendance'))    as present_count,
       sum(pd.session_count)                                                 as session_count
from analytics.participation_daily pd
group by pd.tenant_id, pd.activity_date, pd.neighbourhood_id, pd.age_band, pd.category_term_id;

create or replace view analytics.v_demand_gap as
select dg.tenant_id,
       dg.as_of_date,
       dg.neighbourhood_id,
       dg.category_term_id,
       dg.age_band,
       dg.zero_result_searches,
       dg.total_searches,
       analytics.suppress(dg.waitlisted_youth::bigint,
                          analytics.min_cell(dg.tenant_id, 'demand')) as waitlisted_youth,
       dg.unfilled_seats
from analytics.demand_gap_daily dg;

-- ---------------------------------------------------------------------
-- Research export. Approval is a workflow, not a flag: a request needs a
-- named approver, a stated purpose and a k-anonymity parameter, and the
-- artifact expires.
-- ---------------------------------------------------------------------
create table research_export (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  requested_by  uuid not null references identity.app_user(id) on delete restrict,
  purpose       text not null,
  spec          jsonb not null,
  k_anonymity   smallint not null default 10,
  generalisation jsonb not null default '{}'::jsonb,
  state         text not null default 'requested',
  approved_by   uuid references identity.app_user(id),
  approved_at   timestamptz,
  dua_reference text,
  media_id      uuid references platform.media_object(id),
  row_count     int,
  suppressed_cell_count int,
  created_at    timestamptz not null default now(),
  expires_at    timestamptz,
  revoked_at    timestamptz,
  constraint ck_research_state check (state in
    ('requested','approved','generating','available','expired','revoked','rejected')),
  constraint ck_research_k check (k_anonymity >= 5),
  constraint ck_research_approval check (state not in ('approved','generating','available')
                                         or (approved_by is not null and dua_reference is not null)),
  constraint ck_research_purpose check (length(btrim(purpose)) >= 40)
);
create index ix_research_export_state on research_export (tenant_id, state);

comment on constraint ck_research_purpose on research_export is
  'A one-word purpose is not a purpose. Forty characters forces a sentence that an IRB or a parent could read.';

create table report_run (
  id          uuid primary key,
  tenant_id   uuid not null references platform.tenant(id) on delete cascade,
  report_key  text not null,
  requested_by uuid references identity.app_user(id),
  scope       jsonb not null default '{}'::jsonb,
  format      text not null,
  state       text not null default 'queued',
  media_id    uuid references platform.media_object(id),
  row_count   int,
  suppressed_cell_count int,
  started_at  timestamptz,
  completed_at timestamptz,
  expires_at  timestamptz,
  error       text,
  constraint ck_report_run_format check (format in ('pdf','xlsx','csv','json')),
  constraint ck_report_run_state check (state in ('queued','running','available','failed','expired'))
);
create index ix_report_run_tenant on report_run (tenant_id, report_key, completed_at desc);
