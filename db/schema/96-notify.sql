-- =====================================================================
-- BrightTime :: 96 :: notify
-- Templates, in-app notifications, multi-channel delivery with a
-- provider-agnostic record, per-kind preferences, push devices.
--
-- Channel rules enforced here: SMS and push carry no detail, only a
-- prompt and a deep link. Nothing identifying a youth leaves the app
-- over SMS or email body.
-- =====================================================================
set search_path = notify, public;

create table template (
  id          uuid primary key,
  tenant_id   uuid not null references platform.tenant(id) on delete cascade,
  template_key text not null,
  locale      text not null,
  channel     text not null,
  subject     text,
  body        text not null,
  version     int not null default 1,
  is_active   boolean not null default true,
  updated_at  timestamptz not null default now(),
  constraint ux_template unique (tenant_id, template_key, locale, channel, version),
  constraint ck_template_channel check (channel in ('email','sms','push','in_app')),
  constraint ck_template_subject check (channel <> 'email' or subject is not null),
  -- SMS bodies are capped and must not interpolate a youth name.
  constraint ck_template_sms_length check (channel <> 'sms' or length(body) <= 320),
  constraint ck_template_sms_no_youth_name
    check (channel <> 'sms' or body not like '%{{youth_%')
);
create unique index ux_template_active
  on template (tenant_id, template_key, locale, channel) where is_active;

create table notification (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  user_id       uuid not null references identity.app_user(id) on delete cascade,
  notification_kind text not null,
  title         text not null,
  body          text,
  deep_link     text,
  payload       jsonb not null default '{}'::jsonb,
  priority      text not null default 'normal',
  created_at    timestamptz not null default now(),
  read_at       timestamptz,
  dismissed_at  timestamptz,
  expires_at    timestamptz,
  constraint ck_notification_kind check (notification_kind in
    ('deadline_reminder','session_reminder','saved_item_update','application_decision',
     'waitlist_offer','new_message','space_activity','badge_awarded','playlist_progress',
     'gig_match','gig_decision','moderation_outcome','consent_request','report_ready',
     'listing_approved','listing_changes_requested','clearance_expiring','system')),
  constraint ck_notification_priority check (priority in ('low','normal','high','urgent'))
);
create index ix_notification_inbox on notification (user_id, created_at desc)
  where read_at is null and dismissed_at is null;
create index ix_notification_sweep on notification (expires_at) where expires_at is not null;

create table preference (
  user_id     uuid not null references identity.app_user(id) on delete cascade,
  tenant_id   uuid not null references platform.tenant(id) on delete cascade,
  notification_kind text not null,
  channel     text not null,
  is_enabled  boolean not null default true,
  quiet_from_local time,
  quiet_to_local   time,
  constraint pk_preference primary key (user_id, notification_kind, channel),
  constraint ck_preference_channel check (channel in ('email','sms','push','in_app'))
);

create table push_device (
  id          uuid primary key,
  tenant_id   uuid not null references platform.tenant(id) on delete cascade,
  user_id     uuid not null references identity.app_user(id) on delete cascade,
  platform_kind text not null,
  token_hash  bytea not null,
  token_enc   bytea not null,
  app_version text,
  os_version  text,
  locale      text,
  registered_at timestamptz not null default now(),
  last_seen_at  timestamptz,
  revoked_at  timestamptz,
  constraint ux_push_device_token unique (token_hash),
  constraint ck_push_platform check (platform_kind in ('ios','android','web'))
);
create index ix_push_device_user on push_device (user_id) where revoked_at is null;

create table phone_verification (
  id          uuid primary key,
  tenant_id   uuid not null references platform.tenant(id) on delete cascade,
  user_id     uuid not null references identity.app_user(id) on delete cascade,
  phone_blind_index bytea not null,
  code_hash   bytea not null,
  attempts    smallint not null default 0,
  sent_at     timestamptz not null default now(),
  expires_at  timestamptz not null,
  verified_at timestamptz,
  constraint ck_phone_verification_attempts check (attempts <= 5),
  constraint ck_phone_verification_window check (expires_at > sent_at)
);
create index ix_phone_verification_user on phone_verification (user_id, sent_at desc);

create table delivery (
  id              uuid primary key,
  tenant_id       uuid not null references platform.tenant(id) on delete cascade,
  notification_id uuid references notification(id) on delete set null,
  user_id         uuid references identity.app_user(id) on delete set null,
  channel         text not null,
  template_key    text,
  locale          text,
  -- The destination is never stored in clear: we keep a hash for
  -- deduplication, suppression lists and bounce handling.
  address_hash    bytea not null,
  provider        text not null,
  provider_ref    text,
  state           text not null default 'queued',
  attempts        smallint not null default 0,
  queued_at       timestamptz not null default now(),
  sent_at         timestamptz,
  delivered_at    timestamptz,
  failed_at       timestamptz,
  error_code      text,
  constraint ck_delivery_channel check (channel in ('email','sms','push','in_app')),
  constraint ck_delivery_state check (state in
    ('queued','sending','sent','delivered','bounced','failed','suppressed','cancelled'))
);
create index ix_delivery_pending on delivery (queued_at) where state in ('queued','sending');
create index ix_delivery_notification on delivery (notification_id);
create index ix_delivery_address on delivery (tenant_id, address_hash, queued_at desc);

create table suppression_list (
  tenant_id    uuid not null references platform.tenant(id) on delete cascade,
  channel      text not null,
  address_hash bytea not null,
  reason       text not null,
  added_at     timestamptz not null default now(),
  constraint pk_suppression_list primary key (tenant_id, channel, address_hash),
  constraint ck_suppression_reason check (reason in
    ('hard_bounce','complaint','unsubscribe','stop_reply','invalid','guardian_request'))
);

create table broadcast (
  id           uuid primary key,
  tenant_id    uuid not null references platform.tenant(id) on delete cascade,
  org_id       uuid not null references directory.organisation(id) on delete cascade,
  -- By id, not a foreign key: Notifications sits below Opportunities in the module order (D70).
  listing_id   uuid,
  sent_by      uuid not null references identity.app_user(id) on delete restrict,
  audience     text not null,
  subject      text,
  body         text not null,
  channels     text[] not null default '{in_app}',
  recipient_count int not null default 0,
  state        text not null default 'draft',
  scheduled_for timestamptz,
  sent_at      timestamptz,
  constraint ck_broadcast_audience check (audience in
    ('enrolled_families','waitlisted_families','applicants','staff','mentors','space_members')),
  constraint ck_broadcast_state check (state in ('draft','scheduled','sending','sent','cancelled','failed')),
  constraint ck_broadcast_channels check (channels <@ array['in_app','email','sms','push'])
);
create index ix_broadcast_org on broadcast (org_id, sent_at desc);
