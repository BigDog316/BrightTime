-- =====================================================================
-- BrightTime :: 60 :: learning
-- Learner portfolio, participation history, skills, projects,
-- Open Badges 3.0 issuance and verification, playlists and unlocks.
-- =====================================================================
set search_path = learning, public;

create table learner_profile (
  user_id        uuid primary key references identity.app_user(id) on delete cascade,
  tenant_id      uuid not null references platform.tenant(id) on delete cascade,
  display_name   text not null,
  headline       text,
  bio            text,
  avatar_media_id uuid references platform.media_object(id),
  visibility     text not null default 'private',
  public_slug    citext,
  total_hours    numeric(8,2) not null default 0,
  created_at     timestamptz not null default now(),
  updated_at     timestamptz not null default now(),
  constraint ck_learner_visibility check (visibility in ('private','mentors','org','link_only','public')),
  constraint ux_learner_public_slug unique (tenant_id, public_slug),
  -- A public portfolio needs a slug and must not carry a legal name; the
  -- application enforces display_name <> legal name, the schema enforces
  -- that nothing public exists without an explicit slug.
  constraint ck_learner_public_slug
    check (visibility not in ('link_only','public') or public_slug is not null)
);
create trigger learner_profile_touch before update on learner_profile
  for each row execute function platform.touch_updated_at();

create table learner_goal (
  id          uuid primary key,
  tenant_id   uuid not null references platform.tenant(id) on delete cascade,
  user_id     uuid not null references identity.app_user(id) on delete cascade,
  title       text not null,
  detail      text,
  target_term_id uuid references platform.taxonomy_term(id),
  target_date date,
  status      text not null default 'active',
  created_at  timestamptz not null default now(),
  achieved_at timestamptz,
  constraint ck_learner_goal_status check (status in ('active','achieved','paused','abandoned'))
);
create index ix_learner_goal_user on learner_goal (user_id) where status = 'active';

-- Participation is the spine of the portfolio: one row per youth per
-- program engagement, with hours either derived from attendance or
-- self-reported and provider-verified.
create table participation_record (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete restrict,
  user_id       uuid not null references identity.app_user(id) on delete cascade,
  listing_id    uuid references opportunity.listing(id) on delete set null,
  enrollment_id uuid references enrollment.enrollment(id) on delete set null,
  org_id        uuid references directory.organisation(id) on delete set null,
  title_snapshot text not null,
  org_name_snapshot text,
  started_on    date not null,
  ended_on      date,
  sessions_attended int not null default 0,
  hours         numeric(7,2) not null default 0,
  source        text not null,
  verified_by   uuid references identity.app_user(id),
  verified_at   timestamptz,
  is_hidden     boolean not null default false,
  created_at    timestamptz not null default now(),
  constraint ck_participation_source check (source in
    ('attendance','provider_entry','self_reported','import','district_feed')),
  constraint ck_participation_hours check (hours >= 0 and hours <= 10000),
  constraint ck_participation_dates check (ended_on is null or ended_on >= started_on)
);
create index ix_participation_user on participation_record (user_id, started_on desc)
  where not is_hidden;
create unique index ux_participation_enrollment
  on participation_record (enrollment_id) where enrollment_id is not null;

comment on column participation_record.title_snapshot is
  'Denormalised on purpose: the portfolio belongs to the young person and must survive the listing or the organisation being deleted.';

create table learner_skill (
  id          uuid primary key,
  tenant_id   uuid not null references platform.tenant(id) on delete cascade,
  user_id     uuid not null references identity.app_user(id) on delete cascade,
  term_id     uuid not null references platform.taxonomy_term(id) on delete restrict,
  level       smallint,
  evidence_kind text,
  evidence_ref_id uuid,
  asserted_by uuid references identity.app_user(id),
  asserted_at timestamptz not null default now(),
  constraint ck_learner_skill_level check (level is null or level between 1 and 4),
  constraint ck_learner_skill_evidence check (evidence_kind is null or evidence_kind in
    ('badge','project','participation','assessment','mentor_endorsement','self_assessment'))
);
create unique index ux_learner_skill
  on learner_skill (user_id, term_id,
                    coalesce(evidence_ref_id, '00000000-0000-0000-0000-000000000000'::uuid));
create index ix_learner_skill_user on learner_skill (user_id);

create table project (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  user_id       uuid not null references identity.app_user(id) on delete cascade,
  title         text not null,
  description   text,
  listing_id    uuid references opportunity.listing(id) on delete set null,
  visibility    text not null default 'private',
  status        text not null default 'draft',
  approved_by   uuid references identity.app_user(id),
  approved_at   timestamptz,
  -- What staff asked to change when they sent it back (D69); cleared when it is approved.
  review_note   text,
  guardian_consent_id uuid references identity.parental_consent(id),
  created_at    timestamptz not null default now(),
  updated_at    timestamptz not null default now(),
  constraint ck_project_visibility check (visibility in ('private','mentors','org','link_only','public')),
  constraint ck_project_status check (status in ('draft','submitted','approved','published','removed')),
  -- Public publication of a minor's work requires both staff approval
  -- and, for under-13, a recorded showcase consent.
  constraint ck_project_public_needs_approval
    check (visibility not in ('link_only','public') or (approved_at is not null and status = 'published'))
);
create index ix_project_user on project (user_id, created_at desc);
create index ix_project_review on project (tenant_id, status) where status = 'submitted';

create table project_media (
  project_id uuid not null references project(id) on delete cascade,
  media_id   uuid not null references platform.media_object(id) on delete restrict,
  caption    text,
  alt_text   text not null,
  sort_order smallint not null default 0,
  constraint pk_project_media primary key (project_id, media_id),
  constraint ck_project_media_alt check (length(btrim(alt_text)) > 0)
);

-- ---------------------------------------------------------------------
-- Open Badges 3.0 (W3C Verifiable Credentials 2.0 data model).
-- Issuer identity is did:web served from /.well-known/did.json per
-- tenant; signing keys are Ed25519 held by reference in the key vault.
-- ---------------------------------------------------------------------
create table issuer_profile (
  id          uuid primary key,
  tenant_id   uuid not null references platform.tenant(id) on delete cascade,
  org_id      uuid references directory.organisation(id) on delete cascade,
  did         text not null,
  name        text not null,
  url         text,
  image_media_id uuid references platform.media_object(id),
  email       citext,
  status      text not null default 'active',
  created_at  timestamptz not null default now(),
  constraint ux_issuer_did unique (did),
  constraint ck_issuer_did check (did ~ '^did:(web|key):'),
  constraint ck_issuer_status check (status in ('active','suspended','retired'))
);

create table issuer_key (
  id            uuid primary key,
  issuer_id     uuid not null references issuer_profile(id) on delete cascade,
  key_id        text not null,
  algorithm     text not null default 'Ed25519',
  public_jwk    jsonb not null,
  private_key_ref text not null,
  status        text not null default 'active',
  activated_at  timestamptz not null default now(),
  retired_at    timestamptz,
  constraint ux_issuer_key unique (issuer_id, key_id),
  -- RS256: what Open Badges 3.0's JWT proofs require issuers to support (D66).
  constraint ck_issuer_key_alg check (algorithm in ('Ed25519','ES256','RS256')),
  constraint ck_issuer_key_status check (status in ('active','retired','compromised'))
);
create unique index ux_issuer_key_active on issuer_key (issuer_id) where status = 'active';

comment on column issuer_key.private_key_ref is
  'KMS/vault URI. Private key material must never exist in this database, in a backup of it, or in a migration.';

create table badge_class (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  issuer_id     uuid not null references issuer_profile(id) on delete restrict,
  slug          citext not null,
  name          text not null,
  description   text not null,
  criteria_md   text not null,
  criteria_url  text,
  image_media_id uuid references platform.media_object(id),
  version       int not null default 1,
  level         text,
  tags          text[] not null default '{}',
  alignment     jsonb not null default '[]'::jsonb,
  expires_after_months smallint,
  status        text not null default 'draft',
  created_at    timestamptz not null default now(),
  constraint ux_badge_class_slug unique (tenant_id, slug, version),
  constraint ck_badge_class_status check (status in ('draft','published','retired')),
  constraint ck_badge_class_criteria check (length(btrim(criteria_md)) > 20),
  constraint ck_badge_alignment check (jsonb_typeof(alignment) = 'array')
);

create table badge_assertion (
  id                uuid primary key,
  tenant_id         uuid not null references platform.tenant(id) on delete restrict,
  badge_class_id    uuid not null references badge_class(id) on delete restrict,
  recipient_user_id uuid not null references identity.app_user(id) on delete cascade,
  -- OB hashed recipient identity: sha256(identifier || salt), as both OB versions specify
  recipient_identity_hash bytea not null,
  recipient_salt    bytea not null,
  issued_on         timestamptz not null default now(),
  expires_on        timestamptz,
  narrative         text,
  evidence          jsonb not null default '[]'::jsonb,
  issuer_key_id     uuid references issuer_key(id),
  credential_jwt    text,
  credential_json   jsonb,
  verify_slug       citext not null,
  revoked_at        timestamptz,
  revocation_reason text,
  awarded_by        uuid references identity.app_user(id),
  -- The enrolment it was awarded for (D66): names the recipient to the organisation only as
  -- the family's share does, and only while it does.
  enrollment_id     uuid references enrollment.enrollment(id) on delete set null,
  source_playlist_enrollment_id uuid,
  constraint ux_badge_assertion_verify_slug unique (verify_slug),
  constraint ux_badge_assertion_once unique (badge_class_id, recipient_user_id, issued_on),
  constraint ck_badge_assertion_expiry check (expires_on is null or expires_on > issued_on),
  constraint ck_badge_assertion_revoked
    check ((revoked_at is null) = (revocation_reason is null)),
  constraint ck_badge_assertion_proof
    check (credential_jwt is not null or credential_json is not null)
);
create index ix_badge_assertion_recipient on badge_assertion (recipient_user_id, issued_on desc)
  where revoked_at is null;
create index ix_badge_assertion_class on badge_assertion (badge_class_id);

comment on table badge_assertion is
  'The public verification endpoint resolves verify_slug and returns the signed credential plus revocation status. It never reveals the recipient''s name or any identifier beyond what the credential itself contains.';

-- ---------------------------------------------------------------------
-- Playlists
-- ---------------------------------------------------------------------
create table playlist (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  org_id        uuid references directory.organisation(id) on delete set null,
  slug          citext not null,
  title         text not null,
  description   text,
  hero_media_id uuid references platform.media_object(id),
  is_ordered    boolean not null default true,
  age_min       smallint,
  age_max       smallint,
  estimated_hours numeric(6,2),
  completion_badge_class_id uuid references badge_class(id) on delete set null,
  required_step_count smallint,
  status        text not null default 'draft',
  created_by    uuid references identity.app_user(id),
  created_at    timestamptz not null default now(),
  updated_at    timestamptz not null default now(),
  constraint ux_playlist_slug unique (tenant_id, slug),
  constraint ck_playlist_status check (status in ('draft','published','archived')),
  constraint ck_playlist_ages check (age_min is null or age_max is null or age_min <= age_max)
);

create table playlist_step (
  id          uuid primary key,
  playlist_id uuid not null references playlist(id) on delete cascade,
  sort_order  smallint not null,
  step_kind   text not null,
  title       text not null,
  instructions_md text,
  listing_id  uuid references opportunity.listing(id) on delete set null,
  external_url text,
  expected_minutes smallint,
  is_required boolean not null default true,
  requires_review boolean not null default false,
  constraint ux_playlist_step_order unique (playlist_id, sort_order),
  constraint ck_playlist_step_kind check (step_kind in
    ('attend_listing','external_activity','upload_artifact','reflection','quiz','mentor_checkin')),
  constraint ck_playlist_step_target check (
    case step_kind
      when 'attend_listing'     then listing_id is not null
      when 'external_activity'  then external_url is not null
      else true
    end)
);

create table playlist_enrollment (
  id           uuid primary key,
  tenant_id    uuid not null references platform.tenant(id) on delete cascade,
  playlist_id  uuid not null references playlist(id) on delete cascade,
  user_id      uuid not null references identity.app_user(id) on delete cascade,
  started_at   timestamptz not null default now(),
  completed_at timestamptz,
  awarded_assertion_id uuid references badge_assertion(id) on delete set null,
  constraint ux_playlist_enrollment unique (playlist_id, user_id)
);
create index ix_playlist_enrollment_user on playlist_enrollment (user_id);

create table playlist_step_progress (
  playlist_enrollment_id uuid not null references playlist_enrollment(id) on delete cascade,
  step_id      uuid not null references playlist_step(id) on delete cascade,
  state        text not null default 'not_started',
  evidence_project_id uuid references project(id) on delete set null,
  reflection   text,
  completed_at timestamptz,
  reviewed_by  uuid references identity.app_user(id),
  reviewed_at  timestamptz,
  review_note  text,
  constraint pk_playlist_step_progress primary key (playlist_enrollment_id, step_id),
  constraint ck_step_progress_state check (state in
    ('not_started','in_progress','submitted','completed','needs_rework','skipped')),
  constraint ck_step_progress_completed check ((state = 'completed') = (completed_at is not null))
);

-- Badges that unlock opportunities or playlists.
create table unlock_rule (
  id              uuid primary key,
  tenant_id       uuid not null references platform.tenant(id) on delete cascade,
  badge_class_id  uuid not null references badge_class(id) on delete cascade,
  unlocks_kind    text not null,
  unlocks_listing_id uuid references opportunity.listing(id) on delete cascade,
  unlocks_playlist_id uuid references playlist(id) on delete cascade,
  constraint ck_unlock_kind check (unlocks_kind in ('listing','playlist')),
  constraint ck_unlock_target check (
    (unlocks_listing_id is not null)::int + (unlocks_playlist_id is not null)::int = 1)
);
create index ix_unlock_rule_badge on unlock_rule (badge_class_id);

create table portfolio_export (
  id          uuid primary key,
  tenant_id   uuid not null references platform.tenant(id) on delete cascade,
  user_id     uuid not null references identity.app_user(id) on delete cascade,
  format      text not null,
  media_id    uuid references platform.media_object(id),
  requested_at timestamptz not null default now(),
  completed_at timestamptz,
  expires_at  timestamptz,
  constraint ck_portfolio_export_format check (format in ('pdf','json_ob3','zip','clr2'))
);
