-- =====================================================================
-- BrightTime :: 50 :: enrollment
-- Reusable family profile, form definitions, applications, eligibility,
-- capacity and waitlists, waivers with e-signature, fees behind a
-- payment-provider interface.
--
-- No cardholder data is stored anywhere in this schema. We keep a
-- provider reference and a status, nothing else.
-- =====================================================================
set search_path = enrollment, public;

-- ---------------------------------------------------------------------
-- Family profile: entered once, reused across every provider. The
-- sharing is explicit and per-organisation (profile_share), so "one
-- application serves many programs" does not become "every provider
-- sees everything".
-- ---------------------------------------------------------------------
create table family_profile (
  id             uuid primary key,
  tenant_id      uuid not null references platform.tenant(id) on delete restrict,
  owner_user_id  uuid not null references identity.app_user(id) on delete cascade,
  household_size smallint,
  primary_language text,
  needs_interpreter boolean not null default false,
  created_at     timestamptz not null default now(),
  updated_at     timestamptz not null default now(),
  constraint ux_family_profile_owner unique (owner_user_id),
  constraint ck_family_household check (household_size is null or household_size between 1 and 30)
);
create trigger family_profile_touch before update on family_profile
  for each row execute function platform.touch_updated_at();

-- Encrypted columns are application AES-256-GCM; the *_enc suffix is the
-- contract the EF value converter and the audit interceptor both key off.
create table family_member (
  id               uuid primary key,
  tenant_id        uuid not null references platform.tenant(id) on delete restrict,
  family_profile_id uuid not null references family_profile(id) on delete cascade,
  user_id          uuid references identity.app_user(id) on delete set null,
  member_role      text not null,
  first_name_enc   bytea not null,
  last_name_enc    bytea not null,
  preferred_name   text,
  date_of_birth_enc bytea,
  birth_month      date,
  grade            smallint,
  school_id        uuid references platform.school(id),
  neighbourhood_id uuid references platform.neighbourhood(id),
  pronouns         text,
  relationship_to_owner text,
  created_at       timestamptz not null default now(),
  updated_at       timestamptz not null default now(),
  constraint ck_family_member_role check (member_role in ('youth','child','guardian','emergency_contact')),
  constraint ck_family_member_grade check (grade is null or grade between -1 and 12),
  constraint ck_family_member_birth_month
    check (birth_month is null or birth_month = date_trunc('month', birth_month)),
  constraint ck_family_member_youth_needs_dob
    check (member_role not in ('youth','child') or date_of_birth_enc is not null)
);
create index ix_family_member_profile on family_member (family_profile_id);
create index ix_family_member_user on family_member (user_id) where user_id is not null;

-- Health / support information. Separate table so it can carry its own
-- retention policy, its own encryption key, and its own access grants.
create table family_member_support_need (
  id              uuid primary key,
  tenant_id       uuid not null references platform.tenant(id) on delete restrict,
  family_member_id uuid not null references family_member(id) on delete cascade,
  need_kind       text not null,
  detail_enc      bytea,
  share_with_providers boolean not null default false,
  created_at      timestamptz not null default now(),
  constraint ck_support_need_kind check (need_kind in
    ('allergy','medication','mobility','sensory','communication','dietary','medical_other','iep_504','none_disclosed'))
);
create index ix_support_need_member on family_member_support_need (family_member_id);

comment on table family_member_support_need is
  'Highest-sensitivity table in the system. Never included in any export, report, aggregate or de-identified dataset. Readable only by an org with an active enrollment and share_with_providers = true.';

create table family_contact (
  id               uuid primary key,
  tenant_id        uuid not null references platform.tenant(id) on delete restrict,
  family_profile_id uuid not null references family_profile(id) on delete cascade,
  name_enc         bytea not null,
  relationship     text not null,
  phone_enc        bytea not null,
  phone_blind_index bytea,
  email_enc        bytea,
  is_emergency     boolean not null default true,
  can_pick_up      boolean not null default false,
  sort_order       smallint not null default 0
);
create index ix_family_contact_profile on family_contact (family_profile_id);

-- Explicit, revocable sharing of profile fields with one organisation (D44).
-- member_ids names the members it covers: applying for one child shares
-- that child, not their siblings. A member removed from the profile drops
-- out of every share at read time; the array is not rewritten.
create table profile_share (
  id               uuid primary key,
  tenant_id        uuid not null references platform.tenant(id) on delete cascade,
  family_profile_id uuid not null references family_profile(id) on delete cascade,
  org_id           uuid not null references directory.organisation(id) on delete cascade,
  listing_id       uuid references opportunity.listing(id) on delete set null,
  member_ids       uuid[] not null,
  shared_fields    text[] not null,
  granted_by_user_id uuid not null references identity.app_user(id),
  granted_at       timestamptz not null default now(),
  expires_at       timestamptz,
  revoked_at       timestamptz,
  constraint ck_profile_share_fields check (cardinality(shared_fields) > 0),
  constraint ck_profile_share_known_fields check (shared_fields <@ array[
    'household','member_names','member_birth_dates','member_grade','member_pronouns',
    'contacts','support_needs']::text[]),
  constraint ck_profile_share_members check (cardinality(member_ids) > 0),
  constraint ck_profile_share_expiry check (expires_at is null or expires_at > granted_at)
);
create unique index ux_profile_share_active
  on profile_share (family_profile_id, org_id, coalesce(listing_id, '00000000-0000-0000-0000-000000000000'::uuid))
  where revoked_at is null;
-- An organisation's inbox of shares: the live ones, newest first.
create index ix_profile_share_org_live
  on profile_share (org_id, granted_at desc)
  where revoked_at is null;

-- ---------------------------------------------------------------------
-- Form definitions. jsonb is correct here: the shape is genuinely
-- provider-authored. Answers are stored BOTH as a jsonb snapshot (for
-- fidelity and dispute) and as normalised rows (for reporting).
-- ---------------------------------------------------------------------
create table form_definition (
  id          uuid primary key,
  tenant_id   uuid not null references platform.tenant(id) on delete cascade,
  org_id      uuid references directory.organisation(id) on delete cascade,
  name        text not null,
  version     int not null default 1,
  status      text not null default 'draft',
  schema_json jsonb not null,
  schema_hash bytea not null,
  created_by  uuid references identity.app_user(id),
  created_at  timestamptz not null default now(),
  published_at timestamptz,
  constraint ux_form_definition_version unique (tenant_id, org_id, name, version),
  constraint ck_form_definition_status check (status in ('draft','published','retired')),
  constraint ck_form_definition_schema check (jsonb_typeof(schema_json -> 'questions') = 'array')
);

create table listing_form (
  listing_id         uuid not null references opportunity.listing(id) on delete cascade,
  form_definition_id uuid not null references form_definition(id) on delete restrict,
  constraint pk_listing_form primary key (listing_id)
);

-- ---------------------------------------------------------------------
-- Eligibility rules. Declarative so they can be shown to a family
-- before they apply ("you must live in the district") and evaluated
-- server-side without provider-written code.
-- ---------------------------------------------------------------------
create table eligibility_rule (
  id         uuid primary key,
  tenant_id  uuid not null references platform.tenant(id) on delete cascade,
  listing_id uuid not null references opportunity.listing(id) on delete cascade,
  rule_kind  text not null,
  config     jsonb not null default '{}'::jsonb,
  is_hard    boolean not null default true,
  message_key text,
  sort_order smallint not null default 0,
  constraint ck_eligibility_rule_kind check (rule_kind in
    ('age_at_start','grade_range','neighbourhood_in','school_in','district_resident',
     'prior_enrollment','badge_required','playlist_completed','guardian_consent','capacity_priority'))
);
create index ix_eligibility_rule_listing on eligibility_rule (listing_id, sort_order);

-- ---------------------------------------------------------------------
-- Capacity. A dedicated counter row per listing (and per session group
-- where a listing has distinct cohorts) taken under FOR UPDATE, so
-- "last seat" and waitlist ordering are correct without SERIALIZABLE.
-- ---------------------------------------------------------------------
create table seat_pool (
  id              uuid primary key,
  tenant_id       uuid not null references platform.tenant(id) on delete cascade,
  listing_id      uuid not null references opportunity.listing(id) on delete cascade,
  label           text not null default 'default',
  capacity        int not null,
  seats_taken     int not null default 0,
  waitlist_capacity int,
  waitlist_taken  int not null default 0,
  next_waitlist_position int not null default 1,
  constraint ux_seat_pool unique (listing_id, label),
  constraint ck_seat_pool_capacity check (capacity > 0),
  constraint ck_seat_pool_taken check (seats_taken between 0 and capacity),
  constraint ck_seat_pool_waitlist check (waitlist_capacity is null or waitlist_taken <= waitlist_capacity)
);

comment on table seat_pool is
  'Claim protocol: SELECT ... FOR UPDATE on the pool row, then insert the enrollment, then increment, all in one transaction. ck_seat_pool_taken makes an over-booking a constraint violation rather than a silent oversell.';

-- ---------------------------------------------------------------------
-- Applications
-- ---------------------------------------------------------------------
create table application (
  id                uuid primary key,
  tenant_id         uuid not null references platform.tenant(id) on delete restrict,
  listing_id        uuid not null references opportunity.listing(id) on delete restrict,
  seat_pool_id      uuid references seat_pool(id) on delete restrict,
  family_profile_id uuid not null references family_profile(id) on delete restrict,
  family_member_id  uuid not null references family_member(id) on delete restrict,
  submitted_by_user_id uuid references identity.app_user(id),
  form_definition_id uuid references form_definition(id),
  status            text not null default 'draft',
  answers_snapshot  jsonb not null default '{}'::jsonb,
  eligibility_result jsonb not null default '{}'::jsonb,
  is_eligible       boolean,
  priority_score    numeric(8,3),
  started_at        timestamptz not null default now(),
  submitted_at      timestamptz,
  decided_at        timestamptz,
  decided_by        uuid references identity.app_user(id),
  decision_note     text,
  withdrawn_at      timestamptz,
  idempotency_key   text,
  created_at        timestamptz not null default now(),
  updated_at        timestamptz not null default now(),
  constraint ck_application_status check (status in
    ('draft','submitted','under_review','waitlisted','offered','accepted','declined','withdrawn','expired','rejected')),
  constraint ck_application_submitted check ((status = 'draft') = (submitted_at is null)),
  constraint ck_application_decided
    check (status not in ('accepted','declined','rejected') or decided_at is not null)
);
-- One live application per member per listing. Withdrawn/expired do not block.
create unique index ux_application_live
  on application (listing_id, family_member_id)
  where status not in ('withdrawn','expired','declined','rejected');
create unique index ux_application_idempotency
  on application (tenant_id, idempotency_key) where idempotency_key is not null;
create index ix_application_queue on application (listing_id, status, submitted_at);
create index ix_application_member on application (family_member_id, submitted_at desc);
create trigger application_touch before update on application
  for each row execute function platform.touch_updated_at();

-- Normalised answers for reporting. value_* columns are mutually exclusive.
create table application_answer (
  application_id uuid not null references application(id) on delete cascade,
  question_key   text not null,
  value_text     text,
  value_number   numeric,
  value_date     date,
  value_bool     boolean,
  value_choices  text[],
  is_sensitive   boolean not null default false,
  constraint pk_application_answer primary key (application_id, question_key),
  constraint ck_application_answer_one_value check (
    (case when value_text    is not null then 1 else 0 end
   + case when value_number  is not null then 1 else 0 end
   + case when value_date    is not null then 1 else 0 end
   + case when value_bool    is not null then 1 else 0 end
   + case when value_choices is not null then 1 else 0 end) <= 1)
);
create index ix_application_answer_key on application_answer (question_key)
  where not is_sensitive;

comment on column application_answer.is_sensitive is
  'Marks answers excluded from every report, export and aggregate: income, immigration status, disability detail, race/ethnicity when collected for a funder.';

-- ---------------------------------------------------------------------
-- Enrollment: the accepted state, and the record attendance hangs off.
-- ---------------------------------------------------------------------
create table enrollment (
  id               uuid primary key,
  tenant_id        uuid not null references platform.tenant(id) on delete restrict,
  listing_id       uuid not null references opportunity.listing(id) on delete restrict,
  seat_pool_id     uuid references seat_pool(id) on delete restrict,
  application_id   uuid references application(id) on delete set null,
  family_member_id uuid not null references family_member(id) on delete restrict,
  youth_user_id    uuid references identity.app_user(id) on delete set null,
  org_id           uuid not null references directory.organisation(id) on delete restrict,
  seat_kind        text not null default 'enrolled',
  waitlist_position int,
  status           text not null default 'active',
  starts_on        date,
  ends_on          date,
  enrolled_at      timestamptz not null default now(),
  ended_at         timestamptz,
  end_reason       text,
  constraint ck_enrollment_seat_kind check (seat_kind in ('enrolled','waitlist')),
  constraint ck_enrollment_status check (status in
    ('active','completed','withdrawn','no_show','removed','expired')),
  constraint ck_enrollment_waitlist_position
    check ((seat_kind = 'waitlist') = (waitlist_position is not null)),
  constraint ck_enrollment_dates check (ends_on is null or starts_on is null or ends_on >= starts_on)
);
create unique index ux_enrollment_active
  on enrollment (listing_id, family_member_id)
  where status = 'active';
create unique index ux_enrollment_waitlist_position
  on enrollment (seat_pool_id, waitlist_position)
  where seat_kind = 'waitlist' and status = 'active';
create index ix_enrollment_org_active on enrollment (org_id, listing_id) where status = 'active';
create index ix_enrollment_youth on enrollment (youth_user_id) where youth_user_id is not null;

-- ---------------------------------------------------------------------
-- Waivers and e-signature
-- ---------------------------------------------------------------------
create table waiver (
  id          uuid primary key,
  tenant_id   uuid not null references platform.tenant(id) on delete cascade,
  org_id      uuid references directory.organisation(id) on delete cascade,
  title       text not null,
  body_md     text not null,
  body_hash   bytea not null,
  version     int not null default 1,
  requires_guardian boolean not null default true,
  status      text not null default 'draft',
  created_at  timestamptz not null default now(),
  constraint ux_waiver_version unique (tenant_id, org_id, title, version),
  constraint ck_waiver_status check (status in ('draft','published','retired'))
);

create table listing_waiver (
  listing_id uuid not null references opportunity.listing(id) on delete cascade,
  waiver_id  uuid not null references waiver(id) on delete restrict,
  is_required boolean not null default true,
  constraint pk_listing_waiver primary key (listing_id, waiver_id)
);

create table waiver_signature (
  id               uuid primary key,
  tenant_id        uuid not null references platform.tenant(id) on delete restrict,
  waiver_id        uuid not null references waiver(id) on delete restrict,
  enrollment_id    uuid references enrollment(id) on delete set null,
  application_id   uuid references application(id) on delete set null,
  family_member_id uuid not null references family_member(id) on delete restrict,
  signed_by_user_id uuid not null references identity.app_user(id) on delete restrict,
  signer_role      text not null,
  typed_name       text not null,
  signature_media_id uuid references platform.media_object(id),
  body_hash_signed bytea not null,
  signed_at        timestamptz not null default now(),
  ip_hash          bytea,
  user_agent_hash  bytea,
  constraint ck_waiver_signature_role check (signer_role in ('guardian','youth_18_plus','staff_witness')),
  constraint ck_waiver_signature_name check (length(btrim(typed_name)) > 1)
);
create index ix_waiver_signature_member on waiver_signature (family_member_id);
create unique index ux_waiver_signature_once
  on waiver_signature (waiver_id, family_member_id, body_hash_signed);
create trigger waiver_signature_append_only
  before update or delete on waiver_signature
  for each statement execute function platform.deny_write();

-- ---------------------------------------------------------------------
-- Fees. Provider-agnostic: amount, status, provider reference. Nothing
-- that would bring the application into PCI DSS scope.
-- ---------------------------------------------------------------------
create table fee_schedule (
  id          uuid primary key,
  tenant_id   uuid not null references platform.tenant(id) on delete cascade,
  listing_id  uuid not null references opportunity.listing(id) on delete cascade,
  label       text not null,
  amount_cents int not null,
  currency    char(3) not null default 'USD',
  cadence     text not null default 'one_time',
  is_default  boolean not null default false,
  constraint ck_fee_amount check (amount_cents >= 0),
  constraint ck_fee_cadence check (cadence in ('one_time','weekly','monthly','per_session','seasonal'))
);
create unique index ux_fee_schedule_default on fee_schedule (listing_id) where is_default;

create table sliding_scale_tier (
  id              uuid primary key,
  fee_schedule_id uuid not null references fee_schedule(id) on delete cascade,
  label           text not null,
  household_size_min smallint,
  household_size_max smallint,
  income_band     text,
  amount_cents    int not null,
  sort_order      smallint not null default 0,
  constraint ck_sliding_amount check (amount_cents >= 0)
);

create table payment_intent (
  id              uuid primary key,
  tenant_id       uuid not null references platform.tenant(id) on delete restrict,
  enrollment_id   uuid references enrollment(id) on delete set null,
  application_id  uuid references application(id) on delete set null,
  fee_schedule_id uuid references fee_schedule(id),
  amount_cents    int not null,
  currency        char(3) not null default 'USD',
  provider        text not null,
  provider_ref    text,
  status          text not null default 'created',
  waived_reason   text,
  created_at      timestamptz not null default now(),
  updated_at      timestamptz not null default now(),
  constraint ck_payment_amount check (amount_cents >= 0),
  constraint ck_payment_status check (status in
    ('created','requires_action','processing','succeeded','failed','cancelled','refunded','waived')),
  constraint ck_payment_waived check (status <> 'waived' or waived_reason is not null),
  constraint ux_payment_provider_ref unique (provider, provider_ref)
);
create index ix_payment_intent_enrollment on payment_intent (enrollment_id);
