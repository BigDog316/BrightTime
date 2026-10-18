-- =====================================================================
-- BrightTime :: 99 :: row-level security and grants
--
-- Run last. Reviewable in one read, which is the point: the whole
-- isolation surface lives in this file.
--
-- THREE LAYERS
--   1. PERMISSIVE tenant policy on every table that has a tenant_id.
--      Predicate is an indexed equality against platform.current_tenant_id().
--   2. PERMISSIVE parent-derived policy on child tables that have no
--      tenant_id of their own (EXISTS against the already-protected parent).
--   3. RESTRICTIVE "requires actor" policy on youth-record tables.
--      RESTRICTIVE matters: multiple PERMISSIVE policies are OR-ed, so a
--      second permissive policy would WEAKEN isolation, not tighten it.
--
-- brighttime_auth is deliberately absent from layer 3's TO list. Login,
-- registration and password reset have to read identity.app_user before any
-- actor exists, so the authentication path gets its own database principal
-- with grants on the Identity stores and nothing else. The alternative --
-- leaving identity.app_user out of layer 3 -- would expose every user row to
-- an unauthenticated request through any other code path.
--
-- FORCE ROW LEVEL SECURITY is set so that brighttime_owner is bound too.
-- Only brighttime_migrator (BYPASSRLS) can cross tenants, and only for
-- backfills.
-- =====================================================================

-- ---------------------------------------------------------------------
-- Layer 1: tenant isolation, applied to every table carrying tenant_id
-- ---------------------------------------------------------------------
do $$
declare
  r record;
begin
  for r in
    select c.relname as tbl, n.nspname as sch
    from pg_class c
    join pg_namespace n on n.oid = c.relnamespace
    join pg_attribute a on a.attrelid = c.oid and a.attname = 'tenant_id' and a.attnum > 0
    where c.relkind in ('r','p')
      and n.nspname in ('platform','identity','directory','opportunity','enrollment',
                        'attendance','learning','community','workforce','landscape',
                        'analytics','notify','integration')
    order by n.nspname, c.relname
  loop
    execute format('alter table %I.%I enable row level security', r.sch, r.tbl);
    execute format('alter table %I.%I force row level security', r.sch, r.tbl);
    execute format($p$
      create policy tenant_isolation on %I.%I
        as permissive for all
        to brighttime_app, brighttime_jobs, brighttime_readonly, brighttime_auth
        using (tenant_id = platform.current_tenant_id())
        with check (tenant_id = platform.current_tenant_id())
    $p$, r.sch, r.tbl);
  end loop;
end
$$;

-- platform.tenant itself: a request may read only its own tenant row.
alter table platform.tenant enable row level security;
alter table platform.tenant force row level security;
create policy tenant_self on platform.tenant
  as permissive for all
  to brighttime_app, brighttime_jobs, brighttime_readonly, brighttime_auth
  using (id = platform.current_tenant_id())
  with check (id = platform.current_tenant_id());

-- Host -> tenant resolution happens before tenant context exists, so this
-- one table is readable without context. It contains no personal data.
alter table platform.tenant_domain enable row level security;
alter table platform.tenant_domain force row level security;
create policy tenant_domain_lookup on platform.tenant_domain
  as permissive for select
  to brighttime_app, brighttime_jobs, brighttime_auth
  using (true);
create policy tenant_domain_write on platform.tenant_domain
  as permissive for all
  to brighttime_app
  using (tenant_id = platform.current_tenant_id())
  with check (tenant_id = platform.current_tenant_id());

-- The job worker serves every tenant, so it must see every job to claim one. This
-- permissive policy is OR-ed with tenant_isolation for brighttime_jobs only; the
-- application role stays tenant-scoped, so a request can enqueue only for its own
-- tenant. The job table holds identifiers, never personal data (see 10-platform.sql).
alter table platform.job enable row level security;
create policy job_worker_claims on platform.job
  as permissive for all
  to brighttime_jobs
  using (true)
  with check (true);

-- platform.blind_index_key holds no tenant data and no key material.
alter table platform.blind_index_key enable row level security;
create policy blind_index_key_read on platform.blind_index_key
  as permissive for select to brighttime_app, brighttime_jobs using (true);

-- ---------------------------------------------------------------------
-- Layer 2: child tables with no tenant_id, protected through their parent.
--
-- Denormalising tenant_id into these would make the predicate an indexed
-- equality instead of a semi-join. They are all single-parent lookups on
-- a primary key, read in detail views rather than in search, so the
-- semi-join is acceptable. Revisit with real plans (see
-- docs/plan/03-indexing-and-query-plans.md, "RLS cost").
-- ---------------------------------------------------------------------
do $$
declare
  r record;
begin
  -- (child schema, child table, child fk column, parent schema, parent table, parent key column)
  for r in
    select * from (values
      ('platform',   'neighbourhood_name',      'neighbourhood_id',      'platform',   'neighbourhood',       'id'),
      ('platform',   'taxonomy_term_name',      'term_id',               'platform',   'taxonomy_term',       'id'),
      ('identity',   'app_user_role',           'user_id',               'identity',   'app_user',            'id'),
      ('identity',   'app_user_claim',          'user_id',               'identity',   'app_user',            'id'),
      ('identity',   'app_user_login',          'user_id',               'identity',   'app_user',            'id'),
      ('identity',   'app_user_token',          'user_id',               'identity',   'app_user',            'id'),
      ('identity',   'user_credential',         'user_id',               'identity',   'app_user',            'id'),
      ('identity',   'refresh_token',           'user_id',               'identity',   'app_user',            'id'),
      ('identity',   'app_role_claim',          'role_id',               'identity',   'app_role',            'id'),
      ('directory',  'site_hours',              'site_id',               'directory',  'site',                'id'),
      ('directory',  'site_assignment',         'org_membership_id',     'directory',  'org_membership',      'id'),
      ('opportunity','listing_translation',     'listing_id',            'opportunity','listing',             'id'),
      ('opportunity','listing_term',            'listing_id',            'opportunity','listing',             'id'),
      ('opportunity','listing_site',            'listing_id',            'opportunity','listing',             'id'),
      ('opportunity','listing_language',        'listing_id',            'opportunity','listing',             'id'),
      ('opportunity','listing_accommodation',   'listing_id',            'opportunity','listing',             'id'),
      ('opportunity','listing_media',           'listing_id',            'opportunity','listing',             'id'),
      ('opportunity','listing_deadline',        'listing_id',            'opportunity','listing',             'id'),
      ('opportunity','collection_item',         'collection_id',         'opportunity','collection',          'id'),
      ('opportunity','collection_translation',  'collection_id',         'opportunity','collection',          'id'),
      ('opportunity','neighbourhood_page_block','page_id',               'opportunity','neighbourhood_page',  'id'),
      ('opportunity','user_interest',           'user_id',               'identity',   'app_user',            'id'),
      ('enrollment', 'listing_form',            'listing_id',            'opportunity','listing',             'id'),
      ('enrollment', 'application_answer',      'application_id',        'enrollment', 'application',         'id'),
      ('enrollment', 'listing_waiver',          'listing_id',            'opportunity','listing',             'id'),
      ('enrollment', 'sliding_scale_tier',      'fee_schedule_id',       'enrollment', 'fee_schedule',        'id'),
      ('learning',   'project_media',           'project_id',            'learning',   'project',             'id'),
      ('learning',   'issuer_key',              'issuer_id',             'learning',   'issuer_profile',      'id'),
      ('learning',   'playlist_step',           'playlist_id',           'learning',   'playlist',            'id'),
      ('learning',   'playlist_step_progress',  'playlist_enrollment_id','learning',   'playlist_enrollment', 'id'),
      ('community',  'post_media',              'post_id',               'community',  'post',                'id'),
      ('community',  'message_media',           'message_id',            'community',  'message',             'id'),
      ('community',  'conversation_participant','conversation_id',       'community',  'conversation',        'id'),
      ('community',  'feedback_score',          'feedback_id',           'community',  'feedback',            'id'),
      ('community',  'rubric_criterion',        'rubric_id',             'community',  'rubric',              'id'),
      ('workforce',  'course_unit',             'course_id',             'workforce',  'course',              'id'),
      ('workforce',  'course_unit_progress',    'course_enrollment_id',  'workforce',  'course_enrollment',   'id'),
      ('workforce',  'mentor_availability',     'mentor_user_id',        'workforce',  'mentor_profile',      'user_id'),
      ('workforce',  'curriculum_session',      'curriculum_id',         'workforce',  'curriculum',          'id'),
      ('workforce',  'curriculum_resource',     'curriculum_id',         'workforce',  'curriculum',          'id'),
      ('landscape',  'asset_feature',           'asset_id',              'landscape',  'asset',               'id'),
      ('landscape',  'asset_photo',             'asset_id',              'landscape',  'asset',               'id'),
      ('landscape',  'survey_answer',           'response_id',           'landscape',  'survey_response',     'id'),
      ('integration','import_row_error',        'import_run_id',         'integration','import_run',          'id')
    ) as t(child_schema, child_table, fk_column, parent_schema, parent_table, parent_key)
  loop
    execute format('alter table %I.%I enable row level security', r.child_schema, r.child_table);
    execute format('alter table %I.%I force row level security', r.child_schema, r.child_table);
    execute format($p$
      create policy tenant_isolation_via_parent on %1$I.%2$I
        as permissive for all
        to brighttime_app, brighttime_jobs, brighttime_readonly, brighttime_auth
        using (exists (select 1 from %4$I.%5$I p
                        where p.%6$I = %1$I.%2$I.%3$I
                          and p.tenant_id = platform.current_tenant_id()))
        with check (exists (select 1 from %4$I.%5$I p
                             where p.%6$I = %1$I.%2$I.%3$I
                               and p.tenant_id = platform.current_tenant_id()))
    $p$, r.child_schema, r.child_table, r.fk_column,
         r.parent_schema, r.parent_table, r.parent_key);
  end loop;
end
$$;

-- ---------------------------------------------------------------------
-- Layer 3: RESTRICTIVE guard on youth-record tables.
--
-- An unauthenticated request (no brighttime.actor_id) can read the public
-- finder all day. It can never touch a row in these tables, whatever bug
-- or injection reaches the query builder.
-- ---------------------------------------------------------------------
do $$
declare
  r record;
begin
  for r in
    select * from (values
      ('identity',  'app_user'),
      ('identity',  'guardian_link'),
      ('identity',  'parental_consent'),
      ('identity',  'consent_record'),
      ('identity',  'youth_capability'),
      ('identity',  'restricted_contact'),
      ('identity',  'youth_access_grant'),
      ('enrollment','family_profile'),
      ('enrollment','family_member'),
      ('enrollment','family_member_support_need'),
      ('enrollment','family_contact'),
      ('enrollment','profile_share'),
      ('enrollment','application'),
      ('enrollment','enrollment'),
      ('enrollment','waiver_signature'),
      ('enrollment','payment_intent'),
      ('attendance','attendance_record'),
      ('attendance','attendance_sync_batch'),
      ('attendance','checkin_token'),
      ('learning',  'participation_record'),
      ('learning',  'learner_goal'),
      ('learning',  'learner_skill'),
      ('learning',  'playlist_enrollment'),
      ('learning',  'portfolio_export'),
      ('community', 'space_member'),
      ('community', 'post'),
      ('community', 'submission'),
      ('community', 'feedback'),
      ('community', 'conversation'),
      ('community', 'message'),
      ('community', 'report'),
      ('community', 'screening_result'),
      ('landscape', 'survey_response'),
      ('landscape', 'field_observation'),
      ('landscape', 'field_assignment'),
      ('notify',    'notification'),
      ('notify',    'delivery'),
      ('notify',    'push_device'),
      ('notify',    'phone_verification'),
      ('analytics', 'youth_pseudonym'),
      ('analytics', 'participation_youth_day')
    ) as t(sch, tbl)
  loop
    execute format($p$
      create policy requires_authenticated_actor on %I.%I
        as restrictive for all
        to brighttime_app, brighttime_readonly
        using (platform.has_actor())
        with check (platform.has_actor())
    $p$, r.sch, r.tbl);
  end loop;
end
$$;

-- Background jobs act without a human actor, so they are excluded from the
-- restrictive policy above by not being listed in its TO clause. They are
-- still bound by tenant isolation.
--
-- Kiosk check-in is NOT an exception: an enrolled kiosk authenticates as a
-- device principal and the request carries that principal in
-- brighttime.actor_id, so attendance.checkin_token and
-- attendance.attendance_record are reached with an actor like any other
-- write. A kiosk that has been revoked has no principal and therefore no
-- access, which is the intended behaviour.

-- ---------------------------------------------------------------------
-- Grants
-- ---------------------------------------------------------------------
do $$
declare
  s text;
begin
  foreach s in array array['platform','identity','directory','opportunity','enrollment',
                           'attendance','learning','community','workforce','landscape',
                           'analytics','notify','integration']
  loop
    execute format('revoke all on all tables in schema %I from public', s);
    execute format('grant usage on schema %I to brighttime_app, brighttime_jobs', s);
    execute format('grant select, insert, update, delete on all tables in schema %I to brighttime_app', s);
    execute format('grant select, insert, update, delete on all tables in schema %I to brighttime_jobs', s);
    execute format('grant usage, select on all sequences in schema %I to brighttime_app, brighttime_jobs', s);
    execute format('grant execute on all functions in schema %I to brighttime_app, brighttime_jobs', s);
    execute format('alter default privileges for role brighttime_owner in schema %I
                      grant select, insert, update, delete on tables to brighttime_app, brighttime_jobs', s);
    execute format('alter default privileges for role brighttime_owner in schema %I
                      grant usage, select on sequences to brighttime_app, brighttime_jobs', s);
  end loop;
end
$$;

-- Audit partition upkeep runs as the table owner (SECURITY DEFINER), so only the worker may
-- call it; the blanket function grant above would otherwise let the web app create tables.
-- The blanket grants above reached the audit partitions too; take them back. Writes go through
-- platform.audit_event, never a partition by name (see platform.seal_audit_partition).
do $$
declare
  r record;
begin
  for r in
    select c.relname from pg_inherits i
    join pg_class c on c.oid = i.inhrelid
    where i.inhparent = 'platform.audit_event'::regclass
  loop
    perform platform.seal_audit_partition(r.relname);
  end loop;
end
$$;
revoke execute on function platform.seal_audit_partition(text) from public, brighttime_app, brighttime_jobs;

revoke execute on function platform.ensure_audit_partitions(int) from public, brighttime_app;
revoke execute on function platform.audit_default_partition_rows() from public, brighttime_app;
grant execute on function platform.ensure_audit_partitions(int) to brighttime_jobs;
grant execute on function platform.audit_default_partition_rows() to brighttime_jobs;

-- Append-only tables: take the grants away rather than relying on the
-- trigger alone. Two independent controls.
do $$
declare
  r record;
begin
  for r in
    select * from (values
      ('platform',  'audit_event'),
      ('directory', 'organisation_vetting_event'),
      ('opportunity','listing_revision'),
      ('enrollment','waiver_signature'),
      ('community', 'moderation_action')
    ) as t(sch, tbl)
  loop
    execute format('revoke update, delete, truncate on %I.%I from brighttime_app, brighttime_jobs',
                   r.sch, r.tbl);
  end loop;
end
$$;

-- Schema public stays closed to CREATE, which is what the revoke in 00 protects
-- against (functions planted in public). USAGE is granted so the application
-- roles can use what lives there: the pg_trgm % operator and citext comparison.
-- Without it, fuzzy search and case-insensitive email lookups fail for the app.
grant usage on schema public to brighttime_app, brighttime_jobs, brighttime_readonly, brighttime_auth;

-- Authentication role: the Identity stores and the tenant lookup. Nothing
-- else. It has no grant on enrollment, attendance, community, learning or
-- analytics, so even though it is exempt from the actor guard it cannot
-- reach a youth record.
grant usage on schema identity, platform to brighttime_auth;
grant select, insert, update on identity.app_user to brighttime_auth;
grant select on identity.app_role, identity.app_role_claim to brighttime_auth;
grant select, insert, delete on identity.app_user_role to brighttime_auth;
grant select, insert, update, delete on identity.app_user_claim to brighttime_auth;
grant select, insert, delete on identity.app_user_login to brighttime_auth;
grant select, insert, update, delete on identity.app_user_token to brighttime_auth;
grant select, insert, update on identity.user_credential to brighttime_auth;
grant select, insert, update on identity.refresh_token to brighttime_auth;
grant select on identity.youth_capability to brighttime_auth;
grant select on platform.tenant, platform.tenant_domain, platform.tenant_locale to brighttime_auth;
grant select, insert on platform.audit_event to brighttime_auth;
grant execute on function platform.current_tenant_id(), platform.current_actor_id(),
                          platform.has_actor(), platform.blind_index(text, bytea)
  to brighttime_auth;

-- Public finder (decision D13): SELECT on the search read model, nothing else.
-- USAGE on public for pg_trgm and PostGIS, and on platform for the text-search
-- configurations (platform.ts_en and the rest). No table grant outside
-- opportunity.listing_search; the assertion at the end of this file enforces it.
grant usage on schema opportunity, public, platform to brighttime_finder;
grant select on opportunity.listing_search to brighttime_finder;

-- Research / civic role: suppressed views only. No fact table, ever.
grant usage on schema analytics to brighttime_readonly;
revoke all on all tables in schema analytics from brighttime_readonly;
grant select on analytics.v_participation_by_neighbourhood to brighttime_readonly;
grant select on analytics.v_demand_gap to brighttime_readonly;
grant execute on function analytics.suppress(bigint, smallint) to brighttime_readonly;
grant execute on function analytics.min_cell(uuid, text) to brighttime_readonly;
grant usage on schema platform to brighttime_readonly;
grant select on platform.neighbourhood, platform.neighbourhood_name,
                platform.taxonomy_term, platform.taxonomy_term_name to brighttime_readonly;

-- ---------------------------------------------------------------------
-- Verification. This block FAILS the migration if any table in a module
-- schema has no row-level security. A new table without a policy is a
-- deployment error, not a code-review miss.
-- ---------------------------------------------------------------------
do $$
declare
  v_missing text;
begin
  select string_agg(format('%s.%s', n.nspname, c.relname), ', ' order by n.nspname, c.relname)
    into v_missing
  from pg_class c
  join pg_namespace n on n.oid = c.relnamespace
  where c.relkind in ('r','p')
    and n.nspname in ('platform','identity','directory','opportunity','enrollment',
                      'attendance','learning','community','workforce','landscape',
                      'analytics','notify','integration')
    and not c.relrowsecurity
    and c.relispartition = false;

  if v_missing is not null then
    raise exception 'tables without row-level security: %', v_missing
      using hint = 'add a tenant_id column, or register the table in layer 2 or layer 3 of 99-rls-and-grants.sql';
  end if;
end
$$;

-- Partitions inherit policies from the partitioned parent, but a partition
-- created later must also have RLS enabled; platform.ensure_audit_partitions
-- does that. Same for the analytics and search_event partitions.
do $$
declare
  r record;
begin
  for r in
    select n.nspname as sch, c.relname as tbl
    from pg_class c
    join pg_namespace n on n.oid = c.relnamespace
    where c.relispartition
      -- Tables only. A partitioned table's index partitions are relkind 'i' and
      -- cannot take ENABLE ROW SECURITY, so the kind must be restricted here.
      and c.relkind = 'r'
      and n.nspname in ('platform','opportunity','analytics')
  loop
    execute format('alter table %I.%I enable row level security', r.sch, r.tbl);
  end loop;
end
$$;

-- ---------------------------------------------------------------------
-- Verification for decision D13. FAILS the migration if:
--   * any brighttime role other than migrator and finder can bypass RLS, or
--   * brighttime_finder holds any privilege on any table other than SELECT
--     on opportunity.listing_search.
-- A role that bypasses RLS must never be able to reach a youth record. This
-- makes widening it a deployment failure rather than a review miss.
-- ---------------------------------------------------------------------
do $$
declare
  v_bypassers text;
  v_extra text;
begin
  select string_agg(rolname, ', ' order by rolname)
    into v_bypassers
  from pg_roles
  where rolname like 'brighttime\_%'
    and rolbypassrls
    and rolname not in ('brighttime_migrator', 'brighttime_finder');

  if v_bypassers is not null then
    raise exception 'roles that must not bypass RLS do: %', v_bypassers;
  end if;

  select string_agg(format('%s.%s (%s)', n.nspname, c.relname, p.privilege_type), ', ')
    into v_extra
  from pg_class c
  join pg_namespace n on n.oid = c.relnamespace
  cross join (values ('SELECT'), ('INSERT'), ('UPDATE'), ('DELETE'), ('TRUNCATE')) as p(privilege_type)
  where c.relkind in ('r', 'p', 'v', 'm')
    and n.nspname in ('platform','identity','directory','opportunity','enrollment',
                      'attendance','learning','community','workforce','landscape',
                      'analytics','notify','integration')
    and has_table_privilege('brighttime_finder', c.oid, p.privilege_type)
    and not (n.nspname = 'opportunity' and c.relname = 'listing_search' and p.privilege_type = 'SELECT');

  if v_extra is not null then
    raise exception 'brighttime_finder may read only opportunity.listing_search, but also has: %', v_extra;
  end if;
end
$$;
