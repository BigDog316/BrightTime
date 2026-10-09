-- =====================================================================
-- BrightTime :: 30 :: directory
-- Provider organisations, vetting workflow, sites, staff, clearances.
-- =====================================================================
set search_path = directory, public;

create table organisation (
  id                uuid primary key,
  tenant_id         uuid not null references platform.tenant(id) on delete restrict,
  legal_name        text not null,
  display_name      text not null,
  slug              citext not null,
  status            text not null default 'applied',
  org_kind          text not null,
  ein_enc           bytea,
  ein_blind_index   bytea,
  website_url       text,
  mission           text,
  logo_media_id     uuid references platform.media_object(id),
  primary_contact_user_id uuid references identity.app_user(id),
  public_email       citext,
  public_phone       text,
  languages_served   text[] not null default '{}',
  submitted_at      timestamptz,
  reviewed_at       timestamptz,
  reviewed_by       uuid references identity.app_user(id),
  review_note       text,
  approved_at       timestamptz,
  suspended_at      timestamptz,
  suspension_reason text,
  insurance_expires_on date,
  created_at        timestamptz not null default now(),
  updated_at        timestamptz not null default now(),
  constraint ux_organisation_slug unique (tenant_id, slug),
  constraint ck_organisation_status check (status in
    ('draft','applied','under_review','changes_requested','approved','suspended','withdrawn','rejected')),
  constraint ck_organisation_kind check (org_kind in
    ('nonprofit','public_agency','school','library','faith_based','business','higher_ed','informal_group')),
  constraint ck_organisation_approved check ((status = 'approved') = (approved_at is not null)),
  constraint ck_organisation_suspended
    check (status <> 'suspended' or (suspended_at is not null and suspension_reason is not null)),
  constraint ck_organisation_url check (website_url is null or website_url ~* '^https?://')
);
create index ix_organisation_tenant_status on organisation (tenant_id, status);
create index ix_organisation_review_queue on organisation (tenant_id, submitted_at)
  where status in ('applied','under_review','changes_requested');
create index ix_organisation_name_trgm on organisation using gin (display_name gin_trgm_ops);
create trigger organisation_touch before update on organisation
  for each row execute function platform.touch_updated_at();

create table organisation_vetting_event (
  id         uuid primary key,
  tenant_id  uuid not null references platform.tenant(id) on delete cascade,
  org_id     uuid not null references organisation(id) on delete cascade,
  from_status text,
  to_status   text not null,
  actor_id    uuid references identity.app_user(id),
  note        text,
  checklist   jsonb not null default '{}'::jsonb,
  occurred_at timestamptz not null default now()
);
create index ix_org_vetting_org on organisation_vetting_event (org_id, occurred_at desc);
create trigger organisation_vetting_append_only
  before update or delete on organisation_vetting_event
  for each statement execute function platform.deny_write();

create table organisation_document (
  id           uuid primary key,
  tenant_id    uuid not null references platform.tenant(id) on delete cascade,
  org_id       uuid not null references organisation(id) on delete cascade,
  doc_kind     text not null,
  media_id     uuid not null references platform.media_object(id),
  issued_on    date,
  expires_on   date,
  verified_at  timestamptz,
  verified_by  uuid references identity.app_user(id),
  created_at   timestamptz not null default now(),
  constraint ck_org_document_kind check (doc_kind in
    ('insurance_certificate','irs_determination','licence','dpa','w9','safety_policy','other')),
  constraint ck_org_document_dates check (expires_on is null or issued_on is null or expires_on > issued_on)
);
create index ix_org_document_expiry on organisation_document (tenant_id, expires_on)
  where expires_on is not null;

create table site (
  id               uuid primary key,
  tenant_id        uuid not null references platform.tenant(id) on delete restrict,
  org_id           uuid not null references organisation(id) on delete cascade,
  name             text not null,
  address_line1    text,
  address_line2    text,
  locality         text,
  region_code      text,
  postal_code      text,
  country_code     char(2) not null default 'US',
  location         geography(Point, 4326),
  geocode_accuracy text,
  neighbourhood_id uuid references platform.neighbourhood(id),
  nearest_school_id uuid references platform.school(id),
  is_address_public boolean not null default true,
  entrance_note    text,
  transit_note     text,
  parking_note     text,
  is_wheelchair_accessible boolean,
  has_accessible_restroom  boolean,
  has_elevator     boolean,
  has_quiet_space  boolean,
  is_active        boolean not null default true,
  created_at       timestamptz not null default now(),
  updated_at       timestamptz not null default now(),
  constraint ck_site_geocode check (geocode_accuracy is null or geocode_accuracy in
    ('rooftop','parcel','interpolated','street','locality','manual')),
  constraint ck_site_country check (country_code ~ '^[A-Z]{2}$'),
  -- A non-public address must still be geocoded for distance search, but the
  -- API serialiser only emits the neighbourhood when is_address_public = false.
  constraint ck_site_private_needs_hood
    check (is_address_public or neighbourhood_id is not null)
);
create index ix_site_location on site using gist (location) where is_active;
create index ix_site_org on site (org_id) where is_active;
create index ix_site_hood on site (tenant_id, neighbourhood_id) where is_active;
create trigger site_touch before update on site
  for each row execute function platform.touch_updated_at();

create table site_hours (
  site_id     uuid not null references site(id) on delete cascade,
  day_of_week smallint not null,
  opens_at    time not null,
  closes_at   time not null,
  season      text not null default 'all_year',
  constraint pk_site_hours primary key (site_id, day_of_week, season, opens_at),
  constraint ck_site_hours_dow check (day_of_week between 0 and 6),
  constraint ck_site_hours_order check (closes_at > opens_at),
  constraint ck_site_hours_season check (season in ('all_year','school_year','summer','winter_break'))
);

-- ---------------------------------------------------------------------
-- Staff. Roles are per-organisation; a person can be staff at two orgs
-- and a mentor at a third.
-- ---------------------------------------------------------------------
create table org_membership (
  id           uuid primary key,
  tenant_id    uuid not null references platform.tenant(id) on delete cascade,
  org_id       uuid not null references organisation(id) on delete cascade,
  user_id      uuid not null references identity.app_user(id) on delete cascade,
  org_role     text not null,
  title        text,
  status       text not null default 'invited',
  invited_by   uuid references identity.app_user(id),
  invited_at   timestamptz not null default now(),
  joined_at    timestamptz,
  ended_at     timestamptz,
  ended_reason text,
  constraint ck_org_membership_role check (org_role in ('owner','admin','staff','mentor','viewer')),
  constraint ck_org_membership_status check (status in ('invited','active','suspended','ended','declined')),
  constraint ck_org_membership_active check ((status = 'active') = (joined_at is not null and ended_at is null))
);
create unique index ux_org_membership_active on org_membership (org_id, user_id)
  where status in ('invited','active','suspended');
create index ix_org_membership_user on org_membership (user_id) where status = 'active';
-- Every organisation must keep at least one owner; enforced in the module.
create unique index ux_org_membership_owner_unique on org_membership (org_id, user_id)
  where org_role = 'owner' and status = 'active';

create table site_assignment (
  org_membership_id uuid not null references org_membership(id) on delete cascade,
  site_id           uuid not null references site(id) on delete cascade,
  constraint pk_site_assignment primary key (org_membership_id, site_id)
);

-- ---------------------------------------------------------------------
-- Clearances. STATUS AND EXPIRY ONLY. There is deliberately no column
-- that could hold a background-check report, a disposition, a case
-- number or a narrative, and no media_id. Do not add one.
-- ---------------------------------------------------------------------
create table staff_clearance (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  user_id       uuid not null references identity.app_user(id) on delete cascade,
  org_id        uuid references organisation(id) on delete cascade,
  clearance_kind text not null,
  status        text not null,
  vendor        text,
  vendor_ref_hash bytea,
  completed_on  date,
  expires_on    date,
  recorded_by   uuid references identity.app_user(id),
  recorded_at   timestamptz not null default now(),
  constraint ck_staff_clearance_kind check (clearance_kind in
    ('background_check','fingerprint','child_abuse_registry','sex_offender_registry',
     'mandated_reporter_training','safeguarding_training','cpr_first_aid','driving_record')),
  constraint ck_staff_clearance_status check (status in
    ('not_started','pending','cleared','conditional','not_cleared','expired','withdrawn')),
  constraint ck_staff_clearance_cleared_dates
    check (status <> 'cleared' or completed_on is not null)
);
create unique index ux_staff_clearance_current
  on staff_clearance (user_id, coalesce(org_id, '00000000-0000-0000-0000-000000000000'::uuid), clearance_kind);
create index ix_staff_clearance_expiry on staff_clearance (tenant_id, expires_on)
  where status in ('cleared','conditional');

comment on table staff_clearance is
  'Clearance STATUS and EXPIRY only. Storing the underlying report, disposition or case detail in this system is out of scope by design and prohibited by the data policy.';
