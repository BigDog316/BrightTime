-- =====================================================================
-- BrightTime :: 20 :: identity
-- ASP.NET Core Identity stores, passkeys, guardianship, COPPA consent,
-- youth capability gating, restricted contacts.
--
-- Data minimisation notes that are enforced here, not just documented:
--   * No youth home address or home coordinates column exists anywhere.
--     "Distance from my address" geocodes in-request and discards.
--   * date_of_birth is application-encrypted; birth_month (first of month)
--     is the only plaintext derivative, enough for age-band search.
--   * phone is encrypted with an HMAC blind index for lookup.
-- =====================================================================
set search_path = identity, public;

-- ---------------------------------------------------------------------
-- Users. Per-tenant, not global: a white-label region is a separate
-- data controller, so one person in two regions is two accounts.
-- See docs/plan/08-decisions.md D9.
-- ---------------------------------------------------------------------
create table app_user (
  id                    uuid primary key,
  tenant_id             uuid not null references platform.tenant(id) on delete restrict,
  user_name             citext not null,
  normalized_user_name  citext not null,
  email                 citext,
  normalized_email      citext,
  email_confirmed       boolean not null default false,
  password_hash         text,
  security_stamp        text not null,
  concurrency_stamp     text not null,
  phone_enc             bytea,
  phone_blind_index     bytea,
  phone_key_id          smallint references platform.blind_index_key(id),
  phone_confirmed       boolean not null default false,
  two_factor_enabled    boolean not null default false,
  lockout_end           timestamptz,
  lockout_enabled       boolean not null default true,
  access_failed_count   int not null default 0,
  -- BrightTime additions
  account_kind          text not null default 'adult',
  status                text not null default 'active',
  locale                text,
  date_of_birth_enc     bytea,
  birth_month           date,
  age_band              text,
  neighbourhood_id      uuid references platform.neighbourhood(id),
  home_school_id        uuid references platform.school(id),
  last_login_at         timestamptz,
  terms_accepted_at     timestamptz,
  created_at            timestamptz not null default now(),
  updated_at            timestamptz not null default now(),
  deleted_at            timestamptz,
  constraint ux_app_user_name unique (tenant_id, normalized_user_name),
  constraint ux_app_user_email unique (tenant_id, normalized_email),
  constraint ck_app_user_kind check (account_kind in ('adult','youth','child')),
  constraint ck_app_user_status check (status in
    ('pending_consent','active','suspended','locked','deactivated','deleted')),
  constraint ck_app_user_age_band check (age_band is null or age_band in
    ('under_6','6_8','9_12','13_14','15_17','18_24','adult')),
  constraint ck_app_user_birth_month check (birth_month is null or birth_month = date_trunc('month', birth_month)),
  -- A child account cannot be active without consent; enforced jointly with
  -- the trigger below. A child account never gets a password of its own.
  constraint ck_app_user_child_no_password
    check (account_kind <> 'child' or password_hash is null),
  constraint ck_app_user_phone_pair
    check ((phone_enc is null) = (phone_blind_index is null)),
  constraint ck_app_user_deleted
    check ((status = 'deleted') = (deleted_at is not null))
);
create index ix_app_user_tenant_status on app_user (tenant_id, status);
create index ix_app_user_phone_bi on app_user (tenant_id, phone_blind_index)
  where phone_blind_index is not null;
create index ix_app_user_birth_month on app_user (tenant_id, birth_month)
  where account_kind in ('youth','child');
create trigger app_user_touch before update on app_user
  for each row execute function platform.touch_updated_at();

comment on column app_user.birth_month is
  'Month of birth only. Exact DOB lives encrypted in date_of_birth_enc and is decrypted solely for eligibility evaluation.';

create table app_role (
  id                uuid primary key,
  tenant_id         uuid not null references platform.tenant(id) on delete cascade,
  name              citext not null,
  normalized_name   citext not null,
  concurrency_stamp text not null,
  is_system         boolean not null default false,
  constraint ux_app_role_name unique (tenant_id, normalized_name)
);

create table app_user_role (
  user_id uuid not null references app_user(id) on delete cascade,
  role_id uuid not null references app_role(id) on delete cascade,
  granted_at timestamptz not null default now(),
  granted_by uuid references app_user(id),
  constraint pk_app_user_role primary key (user_id, role_id)
);
create index ix_app_user_role_role on app_user_role (role_id);

create table app_role_claim (
  id         bigint generated always as identity primary key,
  role_id    uuid not null references app_role(id) on delete cascade,
  claim_type text not null,
  claim_value text not null,
  constraint ux_app_role_claim unique (role_id, claim_type, claim_value)
);

create table app_user_claim (
  id         bigint generated always as identity primary key,
  user_id    uuid not null references app_user(id) on delete cascade,
  claim_type text not null,
  claim_value text not null,
  constraint ux_app_user_claim unique (user_id, claim_type, claim_value)
);

create table app_user_login (
  login_provider        text not null,
  provider_key          text not null,
  provider_display_name text,
  user_id               uuid not null references app_user(id) on delete cascade,
  linked_at             timestamptz not null default now(),
  constraint pk_app_user_login primary key (login_provider, provider_key)
);
create index ix_app_user_login_user on app_user_login (user_id);

create table app_user_token (
  user_id        uuid not null references app_user(id) on delete cascade,
  login_provider text not null,
  name           text not null,
  value          text,
  constraint pk_app_user_token primary key (user_id, login_provider, name)
);

-- ---------------------------------------------------------------------
-- Passkeys (WebAuthn level 2). Preferred credential for youth accounts:
-- nothing to phish, nothing to reuse from another site.
-- ---------------------------------------------------------------------
create table user_credential (
  id              uuid primary key,
  user_id         uuid not null references app_user(id) on delete cascade,
  credential_id   bytea not null,
  public_key      bytea not null,
  signature_counter bigint not null default 0,
  aaguid          uuid,
  transports      text[] not null default '{}',
  attestation_fmt text,
  is_backed_up    boolean not null default false,
  is_user_verifying boolean not null default false,
  friendly_name   text,
  created_at      timestamptz not null default now(),
  last_used_at    timestamptz,
  revoked_at      timestamptz,
  constraint ux_user_credential_cred_id unique (credential_id)
);
create index ix_user_credential_user on user_credential (user_id) where revoked_at is null;

-- ---------------------------------------------------------------------
-- Refresh tokens for the mobile app. Rotating, family-chained so that a
-- replayed token invalidates the whole family.
-- ---------------------------------------------------------------------
create table refresh_token (
  id            uuid primary key,
  user_id       uuid not null references app_user(id) on delete cascade,
  family_id     uuid not null,
  token_hash    bytea not null,
  device_id     text,
  -- How the family's sign-in was proved. Every token in a family carries it, so a refreshed
  -- session keeps its original method and time and never counts as a fresh sign-in.
  auth_method   text not null default 'password',
  issued_at     timestamptz not null default now(),
  expires_at    timestamptz not null,
  used_at       timestamptz,
  revoked_at    timestamptz,
  revoked_reason text,
  constraint ux_refresh_token_hash unique (token_hash),
  constraint ck_refresh_token_expiry check (expires_at > issued_at)
);
create index ix_refresh_token_family on refresh_token (family_id);
-- Signing out everywhere, and a password reset, revoke every live token a person holds.
create index ix_refresh_token_user on refresh_token (user_id) where revoked_at is null;
create index ix_refresh_token_sweep on refresh_token (expires_at) where revoked_at is null;

-- ---------------------------------------------------------------------
-- Guardianship
-- ---------------------------------------------------------------------
create table guardian_link (
  id                  uuid primary key,
  tenant_id           uuid not null references platform.tenant(id) on delete cascade,
  guardian_user_id    uuid not null references app_user(id) on delete cascade,
  child_user_id       uuid not null references app_user(id) on delete cascade,
  relationship        text not null,
  status              text not null default 'pending',
  verification_method text,
  verified_at         timestamptz,
  verified_by         uuid references app_user(id),
  is_primary          boolean not null default false,
  can_manage_profile  boolean not null default true,
  can_enroll          boolean not null default true,
  can_view_activity   boolean not null default true,
  revoked_at          timestamptz,
  revoked_reason      text,
  created_at          timestamptz not null default now(),
  constraint ux_guardian_link unique (guardian_user_id, child_user_id),
  constraint ck_guardian_link_self check (guardian_user_id <> child_user_id),
  constraint ck_guardian_relationship check (relationship in
    ('parent','legal_guardian','foster_parent','kinship_caregiver','case_worker','other')),
  constraint ck_guardian_status check (status in ('pending','verified','rejected','revoked','expired')),
  constraint ck_guardian_verified check ((status = 'verified') = (verified_at is not null)),
  constraint ck_guardian_method check (verification_method is null or verification_method in
    ('email_plus','payment_card_verification','government_id_match','signed_form_review','org_attested','district_roster'))
);
create index ix_guardian_link_child on guardian_link (child_user_id) where status = 'verified';
create index ix_guardian_link_guardian on guardian_link (guardian_user_id) where status = 'verified';
create unique index ux_guardian_link_primary on guardian_link (child_user_id)
  where is_primary and status = 'verified';

-- ---------------------------------------------------------------------
-- COPPA verifiable parental consent, scoped and revocable.
-- One row per (child, scope). Absence of a row means "not consented".
-- ---------------------------------------------------------------------
create table parental_consent (
  id                uuid primary key,
  tenant_id         uuid not null references platform.tenant(id) on delete cascade,
  child_user_id     uuid not null references app_user(id) on delete cascade,
  guardian_user_id  uuid not null references app_user(id) on delete restrict,
  guardian_link_id  uuid not null references guardian_link(id) on delete restrict,
  scope             text not null,
  method            text not null,
  granted_at        timestamptz not null default now(),
  expires_at        timestamptz,
  revoked_at        timestamptz,
  revoked_reason    text,
  evidence_media_id uuid references platform.media_object(id),
  consent_text_hash bytea not null,
  ip_hash           bytea,
  user_agent_hash   bytea,
  constraint ck_parental_consent_scope check (scope in
    ('account','enrollment','attendance_photo','community_participation','showcase_publication',
     'media_capture','messaging','district_data_share','research_use','location_use')),
  constraint ck_parental_consent_method check (method in
    ('email_plus','payment_card_verification','government_id_match','signed_form_review','video_call_review')),
  constraint ck_parental_consent_expiry check (expires_at is null or expires_at > granted_at)
);
create unique index ux_parental_consent_active
  on parental_consent (child_user_id, scope)
  where revoked_at is null;
create index ix_parental_consent_expiring on parental_consent (expires_at)
  where revoked_at is null and expires_at is not null;

comment on table parental_consent is
  'COPPA 16 CFR 312.5. consent_text_hash pins the exact disclosure the guardian saw, so a later text change does not retro-justify collection.';

-- Teen self-consent / assent record for 13+ accounts.
create table consent_record (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  user_id       uuid not null references app_user(id) on delete cascade,
  document_kind text not null,
  document_version text not null,
  document_hash bytea not null,
  accepted_at   timestamptz not null default now(),
  ip_hash       bytea,
  constraint ck_consent_record_kind check (document_kind in
    ('terms','privacy','parents_bill_of_rights','community_guidelines','sms_terms','research_participation'))
);
create index ix_consent_record_user on consent_record (user_id, document_kind);

-- ---------------------------------------------------------------------
-- Capability gate. One row per youth/child account, maintained by the
-- Identity module whenever consent or age changes. The application reads
-- this instead of recomputing consent logic in twelve places.
-- ---------------------------------------------------------------------
create table youth_capability (
  user_id                uuid primary key references app_user(id) on delete cascade,
  tenant_id              uuid not null references platform.tenant(id) on delete cascade,
  is_under_13            boolean not null,
  has_account_consent    boolean not null default false,
  can_self_enroll        boolean not null default false,
  can_post_in_community  boolean not null default false,
  can_upload_media       boolean not null default false,
  can_publish_showcase   boolean not null default false,
  can_receive_sms        boolean not null default false,
  can_be_messaged_by_org boolean not null default false,
  recomputed_at          timestamptz not null default now(),
  -- Under-13 accounts may never post, publish or receive SMS, whatever a
  -- consent row says. Belt and braces with the application policy.
  constraint ck_youth_capability_under13
    check (not is_under_13 or (not can_post_in_community
                               and not can_publish_showcase
                               and not can_receive_sms))
);

-- Enforce: no active child account without an unrevoked 'account' consent.
create or replace function identity.enforce_child_consent() returns trigger
  language plpgsql as $fn$
begin
  if new.account_kind = 'child' and new.status = 'active' then
    if not exists (
      select 1 from identity.parental_consent pc
      where pc.child_user_id = new.id
        and pc.scope = 'account'
        and pc.revoked_at is null
        and (pc.expires_at is null or pc.expires_at > now())
    ) then
      raise exception 'child account % cannot be active without verifiable parental consent', new.id
        using errcode = '23514';
    end if;
  end if;
  return new;
end
$fn$;
create constraint trigger app_user_child_consent
  after insert or update on app_user
  deferrable initially deferred
  for each row execute function identity.enforce_child_consent();

-- ---------------------------------------------------------------------
-- Restricted contacts: custody orders, safety plans, prior reports.
-- Blocks visibility and all communication in both directions.
-- ---------------------------------------------------------------------
create table restricted_contact (
  id              uuid primary key,
  tenant_id       uuid not null references platform.tenant(id) on delete cascade,
  protected_user_id uuid not null references app_user(id) on delete cascade,
  restricted_user_id uuid references app_user(id) on delete cascade,
  restricted_org_id uuid,
  reason_kind     text not null,
  note            text,
  created_by      uuid not null references app_user(id),
  created_at      timestamptz not null default now(),
  expires_at      timestamptz,
  constraint ck_restricted_contact_target
    check ((restricted_user_id is not null) <> (restricted_org_id is not null)),
  constraint ck_restricted_contact_self
    check (restricted_user_id is null or restricted_user_id <> protected_user_id),
  constraint ck_restricted_contact_reason check (reason_kind in
    ('custody_order','safety_plan','user_block','moderation_action','guardian_request'))
);
create index ix_restricted_contact_protected on restricted_contact (protected_user_id);
create index ix_restricted_contact_restricted on restricted_contact (restricted_user_id)
  where restricted_user_id is not null;

-- ---------------------------------------------------------------------
-- Who can see a youth's record, and why. Written by Enrollment and
-- Community; read by the authorisation layer. This is the "need to know"
-- table that stops a provider browsing youth it never served.
-- ---------------------------------------------------------------------
create table youth_access_grant (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  youth_user_id uuid not null references app_user(id) on delete cascade,
  org_id        uuid not null,
  basis         text not null,
  basis_ref_id  uuid,
  fields        text[] not null default '{}',
  starts_at     timestamptz not null default now(),
  ends_at       timestamptz,
  created_at    timestamptz not null default now(),
  constraint ck_youth_access_basis check (basis in
    ('application','enrollment','waitlist','cohort_membership','gig_assignment','district_roster','guardian_grant')),
  constraint ck_youth_access_window check (ends_at is null or ends_at > starts_at)
);
create index ix_youth_access_lookup
  on youth_access_grant (tenant_id, org_id, youth_user_id, ends_at);
create index ix_youth_access_youth on youth_access_grant (youth_user_id);

comment on table youth_access_grant is
  'Access to a youth record is time-boxed and reason-bearing. ends_at is set to enrollment end + grace (default 180d, per tenant retention policy), after which the provider sees only suppressed aggregates.';
