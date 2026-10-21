-- =====================================================================
-- BrightTime :: 70 :: community
-- Private cohort spaces, posts, challenges, submissions, mentor
-- feedback with rubrics, showcase, moderation.
--
-- THE SAFETY RULES IN THIS SCHEMA ARE NOT CONFIGURABLE.
--   * There is no table that can hold a 1:1 adult-to-minor conversation.
--     conversation has no "pair" shape; enforce_safe_conversation() rejects
--     any participant set that is one adult and one minor.
--   * Every adult-to-youth message lands in a conversation whose
--     org_id is set, and org admins are implicit readers (org_visible).
--   * Every post and submission starts life unpublished and passes
--     automated screening before it is visible to anyone but its author.
-- =====================================================================
set search_path = community, public;

create table space (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  org_id        uuid not null references directory.organisation(id) on delete cascade,
  listing_id    uuid references opportunity.listing(id) on delete set null,
  space_kind    text not null,
  name          text not null,
  description   text,
  visibility    text not null default 'members',
  status        text not null default 'active',
  min_age       smallint,
  created_by    uuid references identity.app_user(id),
  created_at    timestamptz not null default now(),
  archived_at   timestamptz,
  constraint ck_space_kind check (space_kind in ('cohort','program','challenge','interest_club','staff_room')),
  constraint ck_space_visibility check (visibility in ('members','org','tenant')),
  constraint ck_space_status check (status in ('active','archived','suspended'))
);
create index ix_space_org on space (org_id) where status = 'active';
create index ix_space_listing on space (listing_id) where listing_id is not null;

create table space_member (
  id          uuid primary key,
  tenant_id   uuid not null references platform.tenant(id) on delete cascade,
  space_id    uuid not null references space(id) on delete cascade,
  user_id     uuid not null references identity.app_user(id) on delete cascade,
  member_role text not null,
  is_minor    boolean not null,
  joined_at   timestamptz not null default now(),
  left_at     timestamptz,
  muted_until timestamptz,
  constraint ck_space_member_role check (member_role in ('youth','mentor','staff','admin','observer')),
  -- A minor can only ever hold the youth role.
  constraint ck_space_member_minor_role check (not is_minor or member_role = 'youth')
);
create unique index ux_space_member_active on space_member (space_id, user_id)
  where left_at is null;
create index ix_space_member_user on space_member (user_id) where left_at is null;
-- A space containing minors must have at least two adults; enforced by the
-- module on add/remove, with this index to make the check a single lookup.
create index ix_space_member_adults on space_member (space_id)
  where left_at is null and not is_minor;

create table post (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  space_id      uuid not null references space(id) on delete cascade,
  author_user_id uuid not null references identity.app_user(id) on delete cascade,
  author_is_minor boolean not null,
  body          text,
  state         text not null default 'screening',
  screen_result jsonb,
  screened_at   timestamptz,
  published_at  timestamptz,
  held_reason   text,
  removed_at    timestamptz,
  removed_by    uuid references identity.app_user(id),
  reply_to_id   uuid references post(id) on delete cascade,
  reply_count   int not null default 0,
  created_at    timestamptz not null default now(),
  edited_at     timestamptz,
  constraint ck_post_state check (state in ('screening','published','held','removed','deleted_by_author')),
  constraint ck_post_published check ((state = 'published') = (published_at is not null)),
  constraint ck_post_held check (state <> 'held' or held_reason is not null),
  constraint ck_post_body check (body is null or length(body) <= 8000)
);
create index ix_post_space_feed on post (space_id, published_at desc) where state = 'published';
create index ix_post_moderation on post (tenant_id, created_at) where state in ('screening','held');
create index ix_post_author on post (author_user_id);

create table post_media (
  post_id  uuid not null references post(id) on delete cascade,
  media_id uuid not null references platform.media_object(id) on delete restrict,
  alt_text text not null,
  constraint pk_post_media primary key (post_id, media_id),
  constraint ck_post_media_alt check (length(btrim(alt_text)) > 0)
);

create table challenge (
  id          uuid primary key,
  tenant_id   uuid not null references platform.tenant(id) on delete cascade,
  space_id    uuid not null references space(id) on delete cascade,
  title       text not null,
  brief_md    text not null,
  rubric_id   uuid,
  opens_at    timestamptz,
  due_at      timestamptz,
  badge_class_id uuid references learning.badge_class(id) on delete set null,
  status      text not null default 'draft',
  created_by  uuid references identity.app_user(id),
  constraint ck_challenge_status check (status in ('draft','open','closed','archived')),
  constraint ck_challenge_window check (due_at is null or opens_at is null or due_at > opens_at)
);

create table rubric (
  id         uuid primary key,
  tenant_id  uuid not null references platform.tenant(id) on delete cascade,
  org_id     uuid references directory.organisation(id) on delete cascade,
  name       text not null,
  scale_max  smallint not null default 4,
  created_at timestamptz not null default now(),
  constraint ck_rubric_scale check (scale_max between 2 and 10)
);
alter table challenge
  add constraint fk_challenge_rubric foreign key (rubric_id) references rubric(id) on delete set null;

create table rubric_criterion (
  id         uuid primary key,
  rubric_id  uuid not null references rubric(id) on delete cascade,
  code       text not null,
  label      text not null,
  descriptor_md text,
  weight     numeric(5,2) not null default 1,
  sort_order smallint not null default 0,
  constraint ux_rubric_criterion unique (rubric_id, code),
  constraint ck_rubric_weight check (weight > 0)
);

create table submission (
  id           uuid primary key,
  tenant_id    uuid not null references platform.tenant(id) on delete cascade,
  challenge_id uuid not null references challenge(id) on delete cascade,
  author_user_id uuid not null references identity.app_user(id) on delete cascade,
  project_id   uuid references learning.project(id) on delete set null,
  body         text,
  state        text not null default 'draft',
  submitted_at timestamptz,
  screen_result jsonb,
  screened_at  timestamptz,
  created_at   timestamptz not null default now(),
  constraint ux_submission_once unique (challenge_id, author_user_id),
  constraint ck_submission_state check (state in
    ('draft','screening','submitted','feedback_given','accepted','needs_rework','removed'))
);
create index ix_submission_challenge on submission (challenge_id, state);

create table feedback (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  submission_id uuid not null references submission(id) on delete cascade,
  mentor_user_id uuid not null references identity.app_user(id) on delete restrict,
  comment       text,
  is_org_visible boolean not null default true,
  created_at    timestamptz not null default now(),
  -- Mentor feedback on a minor's work is always visible to org admins.
  constraint ck_feedback_org_visible check (is_org_visible)
);
create index ix_feedback_submission on feedback (submission_id);

create table feedback_score (
  feedback_id  uuid not null references feedback(id) on delete cascade,
  criterion_id uuid not null references rubric_criterion(id) on delete restrict,
  score        smallint not null,
  note         text,
  constraint pk_feedback_score primary key (feedback_id, criterion_id),
  constraint ck_feedback_score check (score >= 0)
);

-- ---------------------------------------------------------------------
-- Messaging. Group- or org-scoped only.
-- ---------------------------------------------------------------------
create table conversation (
  id          uuid primary key,
  tenant_id   uuid not null references platform.tenant(id) on delete cascade,
  org_id      uuid not null references directory.organisation(id) on delete cascade,
  space_id    uuid references space(id) on delete cascade,
  listing_id  uuid references opportunity.listing(id) on delete set null,
  conversation_kind text not null,
  subject     text,
  is_org_visible boolean not null default true,
  includes_minor boolean not null default false,
  adult_participant_count smallint not null default 0,
  created_by  uuid references identity.app_user(id),
  created_at  timestamptz not null default now(),
  closed_at   timestamptz,
  constraint ck_conversation_kind check (conversation_kind in
    ('space_group','org_to_family','org_broadcast','staff_internal','mentor_to_org')),
  -- Any conversation that includes a minor must be org-visible and carry
  -- at least two adults. There is no configuration that relaxes this.
  constraint ck_conversation_minor_safety
    check (not includes_minor or (is_org_visible and adult_participant_count >= 2)),
  constraint ck_conversation_broadcast check (conversation_kind <> 'org_broadcast' or space_id is null)
);
create index ix_conversation_org on conversation (org_id, created_at desc);
create index ix_conversation_space on conversation (space_id) where space_id is not null;

create table conversation_participant (
  conversation_id uuid not null references conversation(id) on delete cascade,
  user_id         uuid not null references identity.app_user(id) on delete cascade,
  participant_role text not null,
  is_minor        boolean not null,
  is_implicit_reader boolean not null default false,
  added_at        timestamptz not null default now(),
  removed_at      timestamptz,
  last_read_at    timestamptz,
  constraint pk_conversation_participant primary key (conversation_id, user_id),
  constraint ck_participant_role check (participant_role in
    ('youth','guardian','staff','mentor','org_admin_observer')),
  constraint ck_participant_minor_role check (not is_minor or participant_role = 'youth')
);
create index ix_participant_user on conversation_participant (user_id) where removed_at is null;

-- Who is a minor is the database's answer, not the caller's (D53): a youth or
-- child account is a minor whatever is_minor says, so no write can pass a minor
-- off as an adult to make a pair look like a group. A participant whose account
-- isn't visible is refused rather than guessed at.
create or replace function community.set_participant_minor() returns trigger
  language plpgsql as $fn$
declare
  v_kind text;
begin
  select account_kind into v_kind from identity.app_user where id = new.user_id;
  if v_kind is null then
    raise exception 'conversation participant % is not a known account', new.user_id
      using errcode = '23503';
  end if;
  new.is_minor := v_kind in ('youth', 'child');
  return new;
end
$fn$;
create trigger conversation_participant_minor
  before insert or update of user_id, is_minor on conversation_participant
  for each row execute function community.set_participant_minor();

-- Maintains conversation.includes_minor / adult_participant_count and
-- rejects the adult-minor pair shape outright.
create or replace function community.enforce_safe_conversation() returns trigger
  language plpgsql as $fn$
declare
  v_conversation_id uuid := coalesce(new.conversation_id, old.conversation_id);
  v_minors  int;
  v_adults  int;
begin
  select count(*) filter (where is_minor),
         count(*) filter (where not is_minor)
    into v_minors, v_adults
  from community.conversation_participant
  where conversation_id = v_conversation_id
    and removed_at is null;

  update community.conversation
     set includes_minor = (v_minors > 0),
         adult_participant_count = v_adults,
         is_org_visible = case when v_minors > 0 then true else is_org_visible end
   where id = v_conversation_id;

  if v_minors > 0 and v_adults < 2 then
    raise exception
      'a conversation including a minor requires at least two adult participants (found % adult(s))', v_adults
      using errcode = '23514',
            hint = 'add the organisation admin as an observer, or use a space-scoped group conversation';
  end if;
  return null;
end
$fn$;
create constraint trigger conversation_participant_safety
  after insert or update or delete on conversation_participant
  deferrable initially deferred
  for each row execute function community.enforce_safe_conversation();

create table message (
  id              uuid primary key,
  tenant_id       uuid not null references platform.tenant(id) on delete cascade,
  conversation_id uuid not null references conversation(id) on delete cascade,
  sender_user_id  uuid not null references identity.app_user(id) on delete restrict,
  body            text not null,
  state           text not null default 'screening',
  screen_result   jsonb,
  sent_at         timestamptz not null default now(),
  edited_at       timestamptz,
  removed_at      timestamptz,
  removed_by      uuid references identity.app_user(id),
  constraint ck_message_state check (state in ('screening','sent','held','removed')),
  constraint ck_message_body check (length(body) between 1 and 8000)
);
create index ix_message_conversation on message (conversation_id, sent_at desc) where state = 'sent';
create index ix_message_moderation on message (tenant_id, sent_at) where state in ('screening','held');

create table message_media (
  message_id uuid not null references message(id) on delete cascade,
  media_id   uuid not null references platform.media_object(id) on delete restrict,
  constraint pk_message_media primary key (message_id, media_id)
);

-- ---------------------------------------------------------------------
-- Screening, reporting, moderation
-- ---------------------------------------------------------------------
create table screening_result (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  target_kind   text not null,
  target_id     uuid not null,
  provider      text not null,
  decision      text not null,
  labels        jsonb not null default '{}'::jsonb,
  pii_spans     jsonb not null default '[]'::jsonb,
  model_version text,
  latency_ms    int,
  created_at    timestamptz not null default now(),
  constraint ck_screening_target check (target_kind in ('post','message','submission','project','asset_photo','profile_text')),
  constraint ck_screening_decision check (decision in ('pass','redact','hold','block','error'))
);
create index ix_screening_target on screening_result (target_kind, target_id);

comment on column screening_result.pii_spans is
  'Character offsets of detected phone numbers, addresses, emails and handles in youth-authored text. The module redacts or holds; it never silently publishes.';

create table report (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  target_kind   text not null,
  target_id     uuid not null,
  target_space_id uuid references space(id) on delete set null,
  reporter_user_id uuid references identity.app_user(id) on delete set null,
  reporter_is_minor boolean not null default false,
  reason_code   text not null,
  details       text,
  severity      text not null default 'normal',
  state         text not null default 'open',
  assigned_to   uuid references identity.app_user(id),
  acknowledged_at timestamptz,
  resolved_at   timestamptz,
  resolution    text,
  escalated_at  timestamptz,
  escalation_note text,
  created_at    timestamptz not null default now(),
  constraint ck_report_target check (target_kind in
    ('post','message','submission','project','user','space','listing','asset_photo')),
  constraint ck_report_reason check (reason_code in
    ('harassment','sexual_content','grooming_concern','self_harm','violence','hate_speech',
     'personal_information','spam','off_topic','copyright','safety_other','inaccurate_listing')),
  constraint ck_report_severity check (severity in ('normal','high','critical')),
  constraint ck_report_state check (state in ('open','triaged','actioned','dismissed','escalated')),
  constraint ck_report_resolved check (state not in ('actioned','dismissed') or resolved_at is not null)
);
create index ix_report_queue on report (tenant_id, severity desc, created_at)
  where state in ('open','triaged');
create index ix_report_target on report (target_kind, target_id);

comment on constraint ck_report_reason on report is
  'grooming_concern and self_harm are routed to the critical queue with a 1-hour acknowledgement SLA and a mandated-reporter escalation path, independent of the reporter''s chosen severity.';

create table moderation_action (
  id          uuid primary key,
  tenant_id   uuid not null references platform.tenant(id) on delete cascade,
  report_id   uuid references report(id) on delete set null,
  target_kind text not null,
  target_id   uuid not null,
  action      text not null,
  actor_user_id uuid not null references identity.app_user(id) on delete restrict,
  reason      text not null,
  duration_hours int,
  occurred_at timestamptz not null default now(),
  constraint ck_moderation_action check (action in
    ('publish','redact','hold','remove','warn','mute','suspend_user','remove_from_space',
     'suspend_org','escalate_external','no_action','restore')),
  constraint ck_moderation_reason check (length(btrim(reason)) > 0)
);
create index ix_moderation_action_target on moderation_action (target_kind, target_id, occurred_at desc);
create index ix_moderation_action_actor on moderation_action (actor_user_id, occurred_at desc);
create trigger moderation_action_append_only
  before update or delete on moderation_action
  for each statement execute function platform.deny_write();

create table showcase_item (
  id            uuid primary key,
  tenant_id     uuid not null references platform.tenant(id) on delete cascade,
  project_id    uuid references learning.project(id) on delete cascade,
  submission_id uuid references submission(id) on delete cascade,
  display_name  text not null,
  caption       text,
  approved_by   uuid not null references identity.app_user(id) on delete restrict,
  approved_at   timestamptz not null default now(),
  guardian_consent_id uuid references identity.parental_consent(id),
  author_is_minor boolean not null,
  published_at  timestamptz,
  unpublished_at timestamptz,
  constraint ck_showcase_source check (
    (project_id is not null)::int + (submission_id is not null)::int = 1),
  -- A minor's work may not be published without a recorded showcase consent.
  constraint ck_showcase_minor_consent
    check (not author_is_minor or guardian_consent_id is not null)
);
create index ix_showcase_published on showcase_item (tenant_id, published_at desc)
  where published_at is not null and unpublished_at is null;
