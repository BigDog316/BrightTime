-- =====================================================================
-- BrightTime :: 10 :: platform
-- Tenancy, branding, feature flags, geography, taxonomy, localisation,
-- media, audit, outbox, jobs, retention, public API clients.
-- =====================================================================
set search_path = platform, public;

-- ---------------------------------------------------------------------
-- Tenants
-- ---------------------------------------------------------------------
create table tenant (
  id                  uuid        primary key,
  slug                citext      not null,
  display_name        text        not null,
  region_name         text        not null,          -- 'Kettle County, New York'
  region_kind         text        not null,
  default_locale      text        not null,
  timezone            text        not null,          -- IANA, e.g. 'America/New_York'
  status              text        not null default 'provisioning',
  -- Branding and flags are genuinely open-ended per white-label deployment.
  branding            jsonb       not null default '{}'::jsonb,
  student_privacy_pack text       not null default 'us-ferpa-baseline',
  min_cell_size       smallint    not null default 10,
  created_at          timestamptz not null default now(),
  updated_at          timestamptz not null default now(),
  constraint ux_tenant_slug unique (slug),
  constraint ck_tenant_status
    check (status in ('provisioning','active','suspended','archived')),
  constraint ck_tenant_region_kind
    check (region_kind in ('city','county','region','state','demo')),
  constraint ck_tenant_privacy_pack
    check (student_privacy_pack in ('us-ferpa-baseline','ny-ed-law-2d','ca-sopipa')),
  constraint ck_tenant_min_cell check (min_cell_size >= 5),
  constraint ck_tenant_locale check (default_locale ~ '^[a-z]{2}(-[A-Za-z0-9]{2,8})*$')
);
create trigger tenant_touch before update on tenant
  for each row execute function platform.touch_updated_at();

comment on column tenant.student_privacy_pack is
  'Selects the compliance policy pack: consent copy, retention defaults, breach-notification SLA, DPA requirements.';

create table tenant_domain (
  tenant_id  uuid   not null references tenant(id) on delete cascade,
  host       citext not null,
  is_primary boolean not null default false,
  constraint pk_tenant_domain primary key (host),
  constraint ck_tenant_domain_host check (host ~ '^[a-z0-9.-]+$')
);
create unique index ux_tenant_domain_primary
  on tenant_domain (tenant_id) where is_primary;

create table tenant_locale (
  tenant_id  uuid    not null references tenant(id) on delete cascade,
  locale     text    not null,
  is_default boolean not null default false,
  is_rtl     boolean not null default false,
  ts_config  text      not null default 'platform.ts_simple',
  sort_order smallint not null default 0,
  constraint pk_tenant_locale primary key (tenant_id, locale)
);
create unique index ux_tenant_locale_default
  on tenant_locale (tenant_id) where is_default;

create table feature_flag (
  tenant_id   uuid    not null references tenant(id) on delete cascade,
  key         text    not null,
  is_enabled  boolean not null default false,
  rollout     jsonb   not null default '{}'::jsonb,
  updated_at  timestamptz not null default now(),
  updated_by  uuid,
  constraint pk_feature_flag primary key (tenant_id, key),
  constraint ck_feature_flag_key check (key ~ '^[a-z][a-z0-9_.]{2,63}$')
);

-- ---------------------------------------------------------------------
-- Localisation overrides. Compiled .resx is the baseline; this table lets
-- an organiser retitle "Neighbourhood" to "Ward" without a deploy.
-- ---------------------------------------------------------------------
create table resource_override (
  tenant_id uuid not null references tenant(id) on delete cascade,
  locale    text not null,
  key       text not null,
  value     text not null,
  updated_at timestamptz not null default now(),
  updated_by uuid,
  constraint pk_resource_override primary key (tenant_id, locale, key)
);

-- ---------------------------------------------------------------------
-- Geography
-- ---------------------------------------------------------------------
create table neighbourhood (
  id               uuid not null,
  tenant_id        uuid not null references tenant(id) on delete cascade,
  parent_id        uuid,
  code             citext not null,
  kind             text not null,
  boundary         geography(MultiPolygon, 4326),
  centroid         geography(Point, 4326),
  land_area_sqkm   numeric(10,3),
  source           text,
  effective_from   date,
  effective_to     date,
  created_at       timestamptz not null default now(),
  updated_at       timestamptz not null default now(),
  constraint pk_neighbourhood primary key (id),
  constraint fk_neighbourhood_parent foreign key (parent_id) references neighbourhood(id),
  constraint ux_neighbourhood_code unique (tenant_id, code),
  constraint ck_neighbourhood_kind check (kind in
    ('county','municipality','village','school_district','census_tract','neighbourhood','ward','custom')),
  constraint ck_neighbourhood_dates check (effective_to is null or effective_to > effective_from)
);
create index ix_neighbourhood_boundary on neighbourhood using gist (boundary);
create index ix_neighbourhood_tenant_kind on neighbourhood (tenant_id, kind);

-- Display names live in a side table so every geography is translatable.
create table neighbourhood_name (
  neighbourhood_id uuid not null references neighbourhood(id) on delete cascade,
  locale           text not null,
  name             text not null,
  constraint pk_neighbourhood_name primary key (neighbourhood_id, locale)
);

create table school_district (
  id        uuid primary key,
  tenant_id uuid not null references tenant(id) on delete cascade,
  name      text not null,
  nces_lea_id text,
  boundary  geography(MultiPolygon, 4326),
  constraint ux_school_district_name unique (tenant_id, name)
);

create table school (
  id          uuid primary key,
  tenant_id   uuid not null references tenant(id) on delete cascade,
  district_id uuid references school_district(id),
  name        text not null,
  nces_school_id text,
  grade_min   smallint,
  grade_max   smallint,
  location    geography(Point, 4326),
  neighbourhood_id uuid references neighbourhood(id),
  is_active   boolean not null default true,
  constraint ck_school_grades check (grade_min is null or grade_max is null or grade_min <= grade_max),
  constraint ux_school_name unique (tenant_id, name)
);
create index ix_school_location on school using gist (location);
create index ix_school_tenant_active on school (tenant_id) where is_active;

comment on table school is
  'Reference data only: school names and locations so youth can search "near my school". Holds no student records.';

-- ---------------------------------------------------------------------
-- Taxonomy. Tenant-configurable, so lookup tables rather than enums.
-- ---------------------------------------------------------------------
create table taxonomy_term (
  id          uuid primary key,
  tenant_id   uuid not null references tenant(id) on delete cascade,
  vocabulary  text not null,
  parent_id   uuid references taxonomy_term(id),
  code        citext not null,
  icon        text,
  colour      text,
  sort_order  smallint not null default 0,
  is_interest boolean not null default false,
  is_active   boolean not null default true,
  constraint ux_taxonomy_term_code unique (tenant_id, vocabulary, code),
  constraint ck_taxonomy_vocabulary check (vocabulary in
    ('category','interest','skill','accommodation','language','audience','quality_domain','asset_feature')),
  constraint ck_taxonomy_colour check (colour is null or colour ~ '^#[0-9a-fA-F]{6}$')
);
create index ix_taxonomy_term_tenant_vocab on taxonomy_term (tenant_id, vocabulary) where is_active;

create table taxonomy_term_name (
  term_id uuid not null references taxonomy_term(id) on delete cascade,
  locale  text not null,
  name    text not null,
  description text,
  constraint pk_taxonomy_term_name primary key (term_id, locale)
);

-- ---------------------------------------------------------------------
-- Media. Bytes live in S3-compatible storage; this is the metadata and
-- the safety gate. Nothing is servable until scan_state='clean' and,
-- for anything with a camera origin, exif_stripped_at is set.
-- ---------------------------------------------------------------------
create table media_object (
  id            uuid primary key,
  tenant_id     uuid not null references tenant(id) on delete cascade,
  object_key    text not null,
  content_type  text not null,
  byte_size     bigint not null,
  sha256        bytea not null,
  width         int,
  height        int,
  duration_ms   int,
  origin        text not null default 'upload',
  scan_state    text not null default 'pending',
  scan_detail   text,
  exif_stripped_at timestamptz,
  uploaded_by   uuid,
  visibility    text not null default 'private',
  created_at    timestamptz not null default now(),
  constraint ux_media_object_key unique (tenant_id, object_key),
  constraint ck_media_size check (byte_size > 0 and byte_size <= 536870912),
  constraint ck_media_origin check (origin in ('upload','camera','import','generated')),
  constraint ck_media_scan check (scan_state in ('pending','clean','infected','failed','quarantined')),
  constraint ck_media_visibility check (visibility in ('private','org','tenant','public')),
  -- Camera-origin media must be stripped before it can ever be public.
  constraint ck_media_exif_before_public
    check (visibility <> 'public' or origin <> 'camera' or exif_stripped_at is not null)
);
create index ix_media_object_scan on media_object (scan_state) where scan_state <> 'clean';
create index ix_media_object_sha on media_object (tenant_id, sha256);

-- ---------------------------------------------------------------------
-- Audit log.
--
-- One row per *operation*, not per record: a roster read that touches 40
-- youth records writes one row carrying subject_ids. That keeps "audit
-- every read of a youth record" affordable. Range-partitioned monthly,
-- append-only by grant and by trigger.
-- ---------------------------------------------------------------------
create table audit_event (
  id            uuid        not null,
  occurred_at   timestamptz not null default now(),
  tenant_id     uuid        not null,
  actor_id      uuid,
  actor_kind    text        not null,
  actor_org_id  uuid,
  action        text        not null,          -- 'enrollment.roster.read'
  outcome       text        not null default 'allow',
  subject_kind  text,                          -- 'youth','application','post'
  subject_ids   uuid[],
  subject_count int         not null default 0,
  resource      text,                          -- 'GET /api/v1/orgs/{id}/rosters'
  reason        text,                          -- required for break-glass reads
  request_id    text,
  ip_hash       bytea,
  user_agent_hash bytea,
  detail        jsonb       not null default '{}'::jsonb,
  constraint pk_audit_event primary key (id, occurred_at),
  constraint ck_audit_actor_kind check (actor_kind in ('user','service','job','anonymous','integration')),
  constraint ck_audit_outcome check (outcome in ('allow','deny','error')),
  constraint ck_audit_subject_count check (subject_count >= 0),
  -- A deny or a break-glass read must say why.
  constraint ck_audit_reason_required
    check (outcome <> 'deny' or reason is not null)
) partition by range (occurred_at);

-- The DEFAULT partition is a safety net, not a destination. Once a row for
-- month M lands here, CREATE TABLE ... FOR VALUES for month M will fail with
-- a conflict, and the fix is a detach-move-reattach under lock. So
-- ensure_audit_partitions() runs four months ahead on a daily schedule, and
-- a non-empty default partition raises a health-check alert.
create table audit_event_default partition of audit_event default;

-- Partitions are created a quarter ahead by platform.ensure_audit_partitions().
create or replace function platform.ensure_audit_partitions(p_months int default 4)
  returns void language plpgsql as $fn$
declare
  v_start date := date_trunc('month', now())::date;
  v_i     int;
  v_from  date;
  v_to    date;
  v_name  text;
begin
  for v_i in 0 .. greatest(p_months, 1) - 1 loop
    v_from := (v_start + (v_i || ' month')::interval)::date;
    v_to   := (v_from + interval '1 month')::date;
    v_name := format('audit_event_%s', to_char(v_from, 'YYYYMM'));
    if not exists (select 1 from pg_class where relname = v_name and relnamespace = 'platform'::regnamespace) then
      execute format(
        'create table platform.%I partition of platform.audit_event for values from (%L) to (%L)',
        v_name, v_from, v_to);
      execute format('create index %I on platform.%I (tenant_id, occurred_at desc)',
        'ix_' || v_name || '_tenant_time', v_name);
      execute format('create index %I on platform.%I (actor_id, occurred_at desc)',
        'ix_' || v_name || '_actor', v_name);
      execute format('create index %I on platform.%I using gin (subject_ids)',
        'ix_' || v_name || '_subjects', v_name);
      execute format('alter table platform.%I enable row level security', v_name);
    end if;
  end loop;
end
$fn$;
select platform.ensure_audit_partitions(4);

create index ix_audit_event_default_tenant on audit_event_default (tenant_id, occurred_at desc);

create trigger audit_event_append_only
  before update or delete on audit_event
  for each statement execute function platform.deny_write();

-- ---------------------------------------------------------------------
-- Transactional outbox. Modules never call each other's tables; they
-- publish here in the same transaction as the state change.
-- ---------------------------------------------------------------------
create table outbox_message (
  id            bigint generated always as identity primary key,
  message_id    uuid not null,
  tenant_id     uuid not null,
  occurred_at   timestamptz not null default now(),
  source_module text not null,
  message_type  text not null,
  payload       jsonb not null,
  trace_context text,
  processed_at  timestamptz,
  attempts      smallint not null default 0,
  last_error    text,
  constraint ux_outbox_message_id unique (message_id)
);
-- Partial index keeps the hot set tiny regardless of table size.
create index ix_outbox_pending on outbox_message (occurred_at)
  where processed_at is null;

-- ---------------------------------------------------------------------
-- Background jobs. Postgres-backed, claimed with FOR UPDATE SKIP LOCKED.
-- ---------------------------------------------------------------------
create table job (
  id            uuid primary key,
  tenant_id     uuid,                       -- null for platform-wide jobs
  queue         text not null default 'default',
  job_type      text not null,
  payload       jsonb not null default '{}'::jsonb,
  state         text not null default 'ready',
  priority      smallint not null default 100,
  run_after     timestamptz not null default now(),
  attempts      smallint not null default 0,
  max_attempts  smallint not null default 10,
  locked_by     text,
  locked_at     timestamptz,
  lock_expires_at timestamptz,
  last_error    text,
  unique_key    text,
  created_at    timestamptz not null default now(),
  completed_at  timestamptz,
  constraint ck_job_state check (state in ('ready','running','succeeded','failed','cancelled','dead')),
  constraint ck_job_attempts check (attempts >= 0 and attempts <= max_attempts + 1),
  constraint ck_job_lock check ((state = 'running') = (locked_by is not null))
);
-- The claim index: one covering partial index is all the dequeue needs.
create index ix_job_claim on job (queue, priority, run_after)
  where state = 'ready';
create index ix_job_reaper on job (lock_expires_at) where state = 'running';
-- Debounce / singleton jobs (e.g. "reproject listing X") collapse on unique_key.
create unique index ux_job_unique_key on job (unique_key)
  where unique_key is not null and state in ('ready','running');

create table job_schedule (
  id         uuid primary key,
  tenant_id  uuid,
  job_type   text not null,
  cron       text not null,
  timezone   text not null default 'UTC',
  payload    jsonb not null default '{}'::jsonb,
  is_enabled boolean not null default true,
  last_run_at timestamptz,
  next_run_at timestamptz,
  constraint ux_job_schedule unique (tenant_id, job_type, cron)
);

-- ---------------------------------------------------------------------
-- Retention, export, deletion. Self-service rights plus automated purge.
-- ---------------------------------------------------------------------
create table retention_policy (
  tenant_id   uuid not null references tenant(id) on delete cascade,
  data_class  text not null,
  retain_days int  not null,
  action      text not null default 'delete',
  legal_basis text,
  updated_at  timestamptz not null default now(),
  updated_by  uuid,
  constraint pk_retention_policy primary key (tenant_id, data_class),
  constraint ck_retention_days check (retain_days between 1 and 36500),
  constraint ck_retention_action check (action in ('delete','anonymise','archive')),
  constraint ck_retention_data_class check (data_class in
    ('search_log','attendance','application','message','community_post','media',
     'audit','learner_portfolio','field_observation','survey_response','notification_delivery'))
);

create table subject_request (
  id            uuid primary key,
  tenant_id     uuid not null references tenant(id) on delete cascade,
  subject_user_id uuid not null,
  requested_by_user_id uuid not null,
  kind          text not null,
  state         text not null default 'received',
  scope         jsonb not null default '{}'::jsonb,
  verified_at   timestamptz,
  due_at        timestamptz not null,
  completed_at  timestamptz,
  artifact_media_id uuid references media_object(id),
  artifact_expires_at timestamptz,
  note          text,
  created_at    timestamptz not null default now(),
  constraint ck_subject_request_kind check (kind in ('export','delete','correct','access_log')),
  constraint ck_subject_request_state check (state in
    ('received','verifying','in_progress','completed','rejected','withdrawn'))
);
create index ix_subject_request_open on subject_request (tenant_id, due_at)
  where state not in ('completed','rejected','withdrawn');

-- ---------------------------------------------------------------------
-- Public read API clients and widget embeds.
-- ---------------------------------------------------------------------
create table api_client (
  id             uuid primary key,
  tenant_id      uuid not null references tenant(id) on delete cascade,
  name           text not null,
  client_id      citext not null,
  secret_hash    bytea,
  scopes         text[] not null default '{}',
  rate_limit_per_min int not null default 60,
  allowed_origins text[] not null default '{}',
  contact_email  text,
  is_enabled     boolean not null default true,
  created_at     timestamptz not null default now(),
  revoked_at     timestamptz,
  constraint ux_api_client_client_id unique (client_id),
  -- Hard stop: the public API may never be granted a youth-data scope.
  constraint ck_api_client_scopes
    check (scopes <@ array['listings.read','orgs.read','taxonomy.read','geography.read','collections.read','ical.read'])
);

create table blind_index_key (
  id          smallint primary key,
  key_ref     text not null,         -- KMS/vault reference, never the key itself
  algorithm   text not null default 'hmac-sha256',
  activated_at timestamptz not null default now(),
  retired_at  timestamptz,
  constraint ck_blind_index_single_active
    check (retired_at is null or retired_at > activated_at)
);
