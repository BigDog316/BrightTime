-- =====================================================================
-- BrightTime :: 80 :: workforce
-- Mentor profiles and availability, training catalogue and credentials,
-- vetted curriculum library, gig board with matching and hours logging.
-- Clearance status lives in directory.staff_clearance (status + expiry only).
-- =====================================================================
set search_path = workforce, public;

create table mentor_profile (
  user_id        uuid primary key references identity.app_user(id) on delete cascade,
  tenant_id      uuid not null references platform.tenant(id) on delete cascade,
  headline       text,
  bio            text,
  avatar_media_id uuid references platform.media_object(id),
  languages      text[] not null default '{}',
  skill_term_ids uuid[] not null default '{}',
  home_neighbourhood_id uuid references platform.neighbourhood(id),
  max_travel_m   int,
  willing_virtual boolean not null default true,
  weekly_hours_sought smallint,
  status         text not null default 'draft',
  is_searchable  boolean not null default false,
  created_at     timestamptz not null default now(),
  updated_at     timestamptz not null default now(),
  constraint ck_mentor_status check (status in ('draft','pending_review','active','paused','suspended','retired')),
  constraint ck_mentor_travel check (max_travel_m is null or max_travel_m between 500 and 160000),
  constraint ck_mentor_hours check (weekly_hours_sought is null or weekly_hours_sought between 1 and 60),
  -- A mentor is only discoverable once active. Clearance currency is
  -- checked by the module against directory.staff_clearance before a match.
  constraint ck_mentor_searchable check (not is_searchable or status = 'active')
);
create index ix_mentor_skills on mentor_profile using gin (skill_term_ids) where is_searchable;
create index ix_mentor_hood on mentor_profile (tenant_id, home_neighbourhood_id) where is_searchable;

create table mentor_availability (
  id          uuid primary key,
  mentor_user_id uuid not null references mentor_profile(user_id) on delete cascade,
  day_of_week smallint not null,
  starts_at_local time not null,
  ends_at_local   time not null,
  effective_from date,
  effective_to   date,
  constraint ck_mentor_avail_dow check (day_of_week between 0 and 6),
  constraint ck_mentor_avail_order check (ends_at_local > starts_at_local)
);
create index ix_mentor_avail_user on mentor_availability (mentor_user_id, day_of_week);

-- ---------------------------------------------------------------------
-- Training
-- ---------------------------------------------------------------------
create table course (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  org_id        uuid references directory.organisation(id) on delete set null,
  slug          citext not null,
  title         text not null,
  summary       text,
  provider_name text,
  delivery      text not null default 'self_paced',
  hours         numeric(5,2),
  validity_months smallint,
  badge_class_id uuid references learning.badge_class(id) on delete set null,
  status        text not null default 'draft',
  created_at    timestamptz not null default now(),
  constraint ux_course_slug unique (tenant_id, slug),
  constraint ck_course_delivery check (delivery in ('self_paced','live_virtual','in_person','blended')),
  constraint ck_course_status check (status in ('draft','published','retired'))
);

create table course_unit (
  id         uuid primary key,
  course_id  uuid not null references course(id) on delete cascade,
  sort_order smallint not null,
  title      text not null,
  body_md    text,
  media_id   uuid references platform.media_object(id),
  external_url text,
  minutes    smallint,
  constraint ux_course_unit_order unique (course_id, sort_order)
);

create table course_enrollment (
  id          uuid primary key,
  tenant_id   uuid not null references platform.tenant(id) on delete cascade,
  course_id   uuid not null references course(id) on delete cascade,
  user_id     uuid not null references identity.app_user(id) on delete cascade,
  started_at  timestamptz not null default now(),
  completed_at timestamptz,
  score       numeric(5,2),
  certificate_media_id uuid references platform.media_object(id),
  badge_assertion_id uuid references learning.badge_assertion(id) on delete set null,
  expires_on  date,
  constraint ux_course_enrollment unique (course_id, user_id),
  constraint ck_course_score check (score is null or score between 0 and 100)
);
create index ix_course_enrollment_user on course_enrollment (user_id);
create index ix_course_enrollment_expiry on course_enrollment (tenant_id, expires_on)
  where completed_at is not null and expires_on is not null;

create table course_unit_progress (
  course_enrollment_id uuid not null references course_enrollment(id) on delete cascade,
  unit_id     uuid not null references course_unit(id) on delete cascade,
  completed_at timestamptz,
  constraint pk_course_unit_progress primary key (course_enrollment_id, unit_id)
);

-- Training a tenant requires before a mentor may be matched to youth.
create table training_requirement (
  tenant_id  uuid not null references platform.tenant(id) on delete cascade,
  applies_to text not null,
  course_id  uuid not null references course(id) on delete restrict,
  is_blocking boolean not null default true,
  grace_days smallint not null default 0,
  constraint pk_training_requirement primary key (tenant_id, applies_to, course_id),
  constraint ck_training_applies check (applies_to in ('mentor','staff','org_admin','field_collector','kiosk_operator'))
);

-- ---------------------------------------------------------------------
-- Curriculum library
-- ---------------------------------------------------------------------
create table curriculum (
  id          uuid primary key,
  tenant_id   uuid not null references platform.tenant(id) on delete cascade,
  slug        citext not null,
  title       text not null,
  summary     text not null,
  subject_term_ids uuid[] not null default '{}',
  age_min     smallint,
  age_max     smallint,
  total_hours numeric(6,2),
  session_count smallint,
  licence     text not null default 'cc-by-nc-sa-4.0',
  author_org_id uuid references directory.organisation(id) on delete set null,
  review_state text not null default 'submitted',
  reviewed_by uuid references identity.app_user(id),
  reviewed_at timestamptz,
  status      text not null default 'draft',
  created_at  timestamptz not null default now(),
  constraint ux_curriculum_slug unique (tenant_id, slug),
  constraint ck_curriculum_review check (review_state in ('submitted','in_review','vetted','changes_requested','rejected')),
  constraint ck_curriculum_status check (status in ('draft','published','archived')),
  constraint ck_curriculum_published_vetted
    check (status <> 'published' or review_state = 'vetted'),
  constraint ck_curriculum_ages check (age_min is null or age_max is null or age_min <= age_max)
);
create index ix_curriculum_subjects on curriculum using gin (subject_term_ids);

create table curriculum_session (
  id            uuid primary key,
  curriculum_id uuid not null references curriculum(id) on delete cascade,
  sort_order    smallint not null,
  title         text not null,
  objectives_md text,
  plan_md       text,
  minutes       smallint,
  constraint ux_curriculum_session_order unique (curriculum_id, sort_order)
);

create table curriculum_resource (
  id            uuid primary key,
  curriculum_id uuid not null references curriculum(id) on delete cascade,
  session_id    uuid references curriculum_session(id) on delete cascade,
  label         text not null,
  media_id      uuid references platform.media_object(id),
  external_url  text,
  resource_kind text not null default 'handout',
  constraint ck_curriculum_resource_target check (media_id is not null or external_url is not null)
);

create table curriculum_adoption (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  curriculum_id uuid not null references curriculum(id) on delete restrict,
  org_id        uuid not null references directory.organisation(id) on delete cascade,
  listing_id    uuid references opportunity.listing(id) on delete set null,
  adopted_by    uuid references identity.app_user(id),
  adopted_at    timestamptz not null default now(),
  note          text
);
create unique index ux_curriculum_adoption
  on curriculum_adoption (curriculum_id, org_id,
                          coalesce(listing_id, '00000000-0000-0000-0000-000000000000'::uuid));

-- ---------------------------------------------------------------------
-- Gig board
-- ---------------------------------------------------------------------
create table gig (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  org_id        uuid not null references directory.organisation(id) on delete cascade,
  site_id       uuid references directory.site(id) on delete set null,
  listing_id    uuid references opportunity.listing(id) on delete set null,
  title         text not null,
  description   text not null,
  compensation_kind text not null,
  pay_rate_cents int,
  pay_unit      text,
  stipend_cents int,
  hours_per_week numeric(4,1),
  starts_on     date,
  ends_on       date,
  openings      smallint not null default 1,
  filled_count  smallint not null default 0,
  required_skill_term_ids uuid[] not null default '{}',
  required_clearances text[] not null default '{}',
  required_course_ids uuid[] not null default '{}',
  min_age       smallint not null default 18,
  is_virtual    boolean not null default false,
  neighbourhood_id uuid references platform.neighbourhood(id),
  status        text not null default 'draft',
  closes_at     timestamptz,
  created_by    uuid references identity.app_user(id),
  created_at    timestamptz not null default now(),
  constraint ck_gig_compensation check (compensation_kind in ('hourly','stipend','volunteer','course_credit')),
  constraint ck_gig_pay check (
    case compensation_kind
      when 'hourly'  then pay_rate_cents is not null and pay_unit = 'hour'
      when 'stipend' then stipend_cents is not null
      else true
    end),
  constraint ck_gig_pay_unit check (pay_unit is null or pay_unit in ('hour','session','week','month','project')),
  constraint ck_gig_status check (status in ('draft','open','paused','filled','closed','expired')),
  constraint ck_gig_openings check (openings > 0 and filled_count between 0 and openings),
  constraint ck_gig_dates check (ends_on is null or starts_on is null or ends_on >= starts_on),
  -- Anyone working directly with youth must be 18+.
  constraint ck_gig_min_age check (min_age >= 16)
);
create index ix_gig_open on gig (tenant_id, created_at desc) where status = 'open';
create index ix_gig_skills on gig using gin (required_skill_term_ids) where status = 'open';
create index ix_gig_org on gig (org_id);

create table gig_application (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  gig_id        uuid not null references gig(id) on delete cascade,
  mentor_user_id uuid not null references mentor_profile(user_id) on delete cascade,
  cover_note    text,
  match_score   numeric(5,2),
  match_explanation jsonb not null default '{}'::jsonb,
  state         text not null default 'submitted',
  applied_at    timestamptz not null default now(),
  decided_at    timestamptz,
  decided_by    uuid references identity.app_user(id),
  decision_note text,
  constraint ux_gig_application unique (gig_id, mentor_user_id),
  constraint ck_gig_application_state check (state in
    ('submitted','shortlisted','interviewing','offered','accepted','declined','withdrawn','rejected'))
);
create index ix_gig_application_gig on gig_application (gig_id, state);
create index ix_gig_application_mentor on gig_application (mentor_user_id, applied_at desc);

comment on column gig_application.match_explanation is
  'Human-readable factor breakdown: skill overlap, distance band, schedule overlap, training currency. Shown to both sides. No sensitive attribute is an input.';

create table gig_assignment (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  gig_id        uuid not null references gig(id) on delete restrict,
  mentor_user_id uuid not null references identity.app_user(id) on delete restrict,
  org_id        uuid not null references directory.organisation(id) on delete restrict,
  application_id uuid references gig_application(id) on delete set null,
  started_on    date not null,
  ended_on      date,
  status        text not null default 'active',
  clearance_verified_at timestamptz,
  training_verified_at  timestamptz,
  constraint ck_gig_assignment_status check (status in ('active','completed','ended_early','suspended')),
  constraint ck_gig_assignment_dates check (ended_on is null or ended_on >= started_on),
  -- No assignment goes active without both gates recorded.
  constraint ck_gig_assignment_gates
    check (status <> 'active' or (clearance_verified_at is not null and training_verified_at is not null))
);
create unique index ux_gig_assignment_active on gig_assignment (gig_id, mentor_user_id)
  where status = 'active';
create index ix_gig_assignment_mentor on gig_assignment (mentor_user_id);

create table hours_log (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  assignment_id uuid not null references gig_assignment(id) on delete cascade,
  worked_on     date not null,
  minutes       smallint not null,
  activity      text,
  note          text,
  submitted_at  timestamptz not null default now(),
  approved_by   uuid references identity.app_user(id),
  approved_at   timestamptz,
  rejected_reason text,
  constraint ck_hours_minutes check (minutes between 1 and 960),
  constraint ck_hours_not_future check (worked_on <= (now() + interval '1 day')::date)
);
create unique index ux_hours_log_day on hours_log (assignment_id, worked_on, activity);
create index ix_hours_log_approval on hours_log (tenant_id, submitted_at) where approved_at is null;
