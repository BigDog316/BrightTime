-- =====================================================================
-- BrightTime :: 55 :: attendance
-- Roster check-in, QR and kiosk, offline capture with idempotent sync.
--
-- Attendance is the highest-risk dataset in the platform: it says where
-- a named child was at a given hour. Short retention, encrypted at rest
-- at the column level for the free-text note, every read audited.
-- =====================================================================
set search_path = attendance, public;

create table attendance_record (
  id                uuid primary key,
  tenant_id         uuid not null references platform.tenant(id) on delete restrict,
  org_id            uuid not null references directory.organisation(id) on delete restrict,
  listing_id        uuid not null references opportunity.listing(id) on delete restrict,
  enrollment_id     uuid not null references enrollment.enrollment(id) on delete cascade,
  occurrence_starts_at timestamptz not null,
  state             text not null,
  minutes_attended  smallint,
  checked_in_at     timestamptz,
  checked_out_at    timestamptz,
  method            text not null default 'manual',
  recorded_by       uuid references identity.app_user(id),
  recorded_at       timestamptz not null default now(),
  device_id         text,
  client_record_id  uuid,
  synced_at         timestamptz,
  note_enc          bytea,
  superseded_by     uuid references attendance_record(id),
  constraint ck_attendance_state check (state in ('present','absent','late','excused','left_early','unknown')),
  constraint ck_attendance_method check (method in ('manual','qr','kiosk','self_check_in','import','offline_sync')),
  constraint ck_attendance_minutes check (minutes_attended is null or minutes_attended between 0 and 1440),
  constraint ck_attendance_times check (checked_out_at is null or checked_in_at is null
                                        or checked_out_at >= checked_in_at)
);
-- One live record per enrollment per occurrence; corrections supersede.
create unique index ux_attendance_live
  on attendance_record (enrollment_id, occurrence_starts_at)
  where superseded_by is null;
-- Offline dedupe: a phone replaying its queue cannot double-write.
create unique index ux_attendance_client_record
  on attendance_record (device_id, client_record_id)
  where client_record_id is not null;
create index ix_attendance_roster
  on attendance_record (listing_id, occurrence_starts_at)
  where superseded_by is null;
create index ix_attendance_org_day
  on attendance_record (org_id, occurrence_starts_at desc);

comment on column attendance_record.note_enc is
  'Application-encrypted. Staff notes routinely contain health and behaviour detail; it is never exported or aggregated.';

create table attendance_sync_batch (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  device_id     text not null,
  submitted_by  uuid references identity.app_user(id),
  received_at   timestamptz not null default now(),
  captured_from timestamptz,
  captured_to   timestamptz,
  item_count    int not null default 0,
  applied_count int not null default 0,
  conflict_count int not null default 0,
  app_version   text,
  constraint ck_sync_counts check (applied_count + conflict_count <= item_count)
);
create index ix_sync_batch_device on attendance_sync_batch (device_id, received_at desc);

-- Rotating, short-lived, single-use check-in tokens. A QR code never
-- contains a youth identifier.
create table checkin_token (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  enrollment_id uuid references enrollment.enrollment(id) on delete cascade,
  listing_id    uuid references opportunity.listing(id) on delete cascade,
  token_hash    bytea not null,
  token_kind    text not null,
  issued_at     timestamptz not null default now(),
  expires_at    timestamptz not null,
  consumed_at   timestamptz,
  consumed_by   uuid references identity.app_user(id),
  constraint ux_checkin_token_hash unique (token_hash),
  constraint ck_checkin_token_kind check (token_kind in ('youth_badge','session_kiosk','staff_scanner')),
  constraint ck_checkin_token_window check (expires_at > issued_at),
  constraint ck_checkin_token_target check (
    (token_kind = 'youth_badge') = (enrollment_id is not null))
);
create index ix_checkin_token_sweep on checkin_token (expires_at) where consumed_at is null;

create table kiosk_device (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  org_id        uuid not null references directory.organisation(id) on delete cascade,
  site_id       uuid references directory.site(id) on delete set null,
  device_id     text not null,
  label         text not null,
  enrolled_by   uuid references identity.app_user(id),
  enrolled_at   timestamptz not null default now(),
  last_seen_at  timestamptz,
  app_version   text,
  revoked_at    timestamptz,
  revoked_reason text,
  wipe_requested_at timestamptz,
  constraint ux_kiosk_device unique (tenant_id, device_id)
);
create index ix_kiosk_device_org on kiosk_device (org_id) where revoked_at is null;

comment on column kiosk_device.wipe_requested_at is
  'Set when a staff member leaves or a device is lost. The mobile client purges its encrypted offline roster on next contact, and refuses to open without contact after 72h.';
