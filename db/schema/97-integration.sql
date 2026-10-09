-- =====================================================================
-- BrightTime :: 97 :: integration
-- District rostering (OneRoster / Ed-Fi), SSO (Clever / ClassLink),
-- HSDS and CSV import, widget embeds, public API request accounting.
--
-- Every district connection is DISABLED by default and cannot be enabled
-- without a recorded data-sharing agreement. ck_district_enabled_needs_dpa
-- makes that a constraint rather than a policy document.
-- =====================================================================
set search_path = integration, public;

create table district_connection (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  district_id   uuid references platform.school_district(id) on delete set null,
  provider      text not null,
  display_name  text not null,
  base_url      text,
  credential_ref text,
  scopes        text[] not null default '{}',
  is_enabled    boolean not null default false,
  dpa_reference text,
  dpa_signed_on date,
  dpa_expires_on date,
  data_elements text[] not null default '{}',
  purpose       text,
  sync_cron     text,
  last_sync_at  timestamptz,
  last_sync_state text,
  created_at    timestamptz not null default now(),
  constraint ck_district_provider check (provider in ('oneroster','edfi','clever','classlink','csv_sftp')),
  constraint ck_district_enabled_needs_dpa
    check (not is_enabled or (dpa_reference is not null
                              and dpa_signed_on is not null
                              and (dpa_expires_on is null or dpa_expires_on > current_date)
                              and purpose is not null
                              and cardinality(data_elements) > 0)),
  -- The connection may only request elements we have a lawful basis for.
  constraint ck_district_data_elements
    check (data_elements <@ array['student_id','first_name','last_name','grade','school',
                                  'date_of_birth','guardian_email','guardian_phone','enrollment_status'])
);
create unique index ux_district_connection on district_connection (tenant_id, provider, coalesce(district_id, '00000000-0000-0000-0000-000000000000'::uuid));

comment on column district_connection.data_elements is
  'Explicit allowlist of fields the connector may read. The sync job rejects any payload field not named here rather than storing it, so a district widening its feed cannot widen our collection.';

create table roster_sync_run (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  connection_id uuid not null references district_connection(id) on delete cascade,
  started_at    timestamptz not null default now(),
  completed_at  timestamptz,
  state         text not null default 'running',
  records_read  int not null default 0,
  records_applied int not null default 0,
  records_rejected int not null default 0,
  fields_dropped text[] not null default '{}',
  error         text,
  constraint ck_roster_sync_state check (state in ('running','succeeded','partial','failed','cancelled'))
);
create index ix_roster_sync_connection on roster_sync_run (connection_id, started_at desc);

create table external_identity_map (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  connection_id uuid not null references district_connection(id) on delete cascade,
  entity_kind   text not null,
  external_id_hash bytea not null,
  internal_id   uuid not null,
  first_seen_at timestamptz not null default now(),
  last_seen_at  timestamptz not null default now(),
  constraint ux_external_identity unique (connection_id, entity_kind, external_id_hash),
  constraint ck_external_entity_kind check (entity_kind in
    ('student','guardian','teacher','school','class','term','enrollment'))
);
create index ix_external_identity_internal on external_identity_map (internal_id);

comment on column external_identity_map.external_id_hash is
  'The district student identifier is stored only as a keyed hash. We can match a returning student without holding a reusable state student ID.';

create table import_run (
  id           uuid primary key,
  tenant_id    uuid not null references platform.tenant(id) on delete cascade,
  org_id       uuid references directory.organisation(id) on delete cascade,
  import_kind  text not null,
  source_media_id uuid references platform.media_object(id),
  source_url   text,
  submitted_by uuid references identity.app_user(id),
  state        text not null default 'uploaded',
  dry_run      boolean not null default true,
  row_count    int not null default 0,
  created_count int not null default 0,
  updated_count int not null default 0,
  error_count  int not null default 0,
  started_at   timestamptz,
  completed_at timestamptz,
  constraint ck_import_kind check (import_kind in
    ('listing_csv','hsds_json','hsds_csv_zip','organisation_csv','asset_csv','roster_csv')),
  constraint ck_import_state check (state in
    ('uploaded','validating','validated','applying','applied','failed','cancelled'))
);
create index ix_import_run_org on import_run (org_id, created_count);

create table import_row_error (
  id          bigint generated always as identity primary key,
  import_run_id uuid not null references import_run(id) on delete cascade,
  row_number  int not null,
  column_name text,
  error_code  text not null,
  message     text not null,
  raw_value   text
);
create index ix_import_row_error_run on import_row_error (import_run_id, row_number);

create table widget_config (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  api_client_id uuid not null references platform.api_client(id) on delete cascade,
  widget_key    citext not null,
  label         text not null,
  widget_kind   text not null,
  preset_filters jsonb not null default '{}'::jsonb,
  theme         jsonb not null default '{}'::jsonb,
  allowed_origins text[] not null default '{}',
  is_enabled    boolean not null default true,
  created_at    timestamptz not null default now(),
  constraint ux_widget_key unique (tenant_id, widget_key),
  constraint ck_widget_kind check (widget_kind in ('search','collection','map','calendar','single_listing')),
  constraint ck_widget_origins check (cardinality(allowed_origins) > 0)
);

create table api_request_metric (
  id            bigint generated always as identity primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  api_client_id uuid references platform.api_client(id) on delete cascade,
  window_start  timestamptz not null,
  endpoint      text not null,
  status_class  smallint not null,
  request_count int not null default 0,
  p95_ms        int
);
create unique index ux_api_request_metric
  on api_request_metric (tenant_id,
                         coalesce(api_client_id, '00000000-0000-0000-0000-000000000000'::uuid),
                         window_start, endpoint, status_class);
create index ix_api_request_metric_window on api_request_metric (tenant_id, window_start desc);
