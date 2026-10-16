-- =====================================================================
-- BrightTime :: 00 :: extensions, roles, text-search configs, helpers
-- Target: PostgreSQL 17 (minimum 15 for UNIQUE NULLS NOT DISTINCT,
--         minimum 13 for btree_gin uuid support). PostGIS 3.4+.
-- Run as superuser. Everything after this file runs as brighttime_migrator.
-- =====================================================================

-- ---------------------------------------------------------------------
-- Extensions
-- ---------------------------------------------------------------------
create extension if not exists postgis;          -- geography, geometry
create extension if not exists pg_trgm;          -- fuzzy / typo tolerance
create extension if not exists btree_gin;        -- scalar keys inside GIN indexes
create extension if not exists btree_gist;       -- scalar keys inside GiST / EXCLUDE
create extension if not exists unaccent;         -- diacritic folding
create extension if not exists citext;           -- case-insensitive slugs, emails
create extension if not exists pgcrypto;         -- digest() for blind indexes/hashes

-- NOTE: we deliberately do NOT use pgcrypto's pgp_sym_encrypt for field-level
-- encryption. Application-side AES-256-GCM keeps key material out of the
-- database process, out of query text, and out of WAL. See docs/plan/07-threat-model.md.

-- ---------------------------------------------------------------------
-- Roles
--   brighttime_owner     : owns every object. No login.
--   brighttime_migrator  : runs DDL and data backfills. BYPASSRLS so that
--                          backfills can touch all tenants. No login.
--   brighttime_app        : the runtime connection. NOT an owner, NOT BYPASSRLS,
--                          so FORCE ROW LEVEL SECURITY actually binds it.
--   brighttime_auth       : the authentication path ONLY. Needed because
--                          login, registration and password reset must read
--                          identity.app_user BEFORE an actor exists, so this
--                          role is the one principal exempt from the
--                          "requires authenticated actor" RESTRICTIVE policy.
--                          Its grants are narrow (see 99-rls-and-grants.sql):
--                          the Identity stores and nothing else.
--   brighttime_finder     : the public finder's read path, and nothing else.
--                          BYPASSRLS, with SELECT on opportunity.listing_search
--                          only. Decision D13: the tenant policy is a security
--                          barrier that stops the planner using the search
--                          indexes. Isolation on this path rests on the tenant
--                          predicate SearchQueryBuilder always emits.
--                          Attributes are not inherited, so the application
--                          enters it with SET LOCAL ROLE inside the finder's
--                          own transaction; the bypass ends when it commits.
--                          99-rls-and-grants.sql fails the migration if this
--                          role ever gains a privilege on any other table.
--   brighttime_readonly   : analytics / research exports. SELECT on
--                          analytics schema only.
--   brighttime_jobs       : background worker. Same RLS posture as _app but
--                          may set tenant context per job.
-- Login users are created by deployment and GRANTed into these group roles,
-- so credentials never appear in migrations.
-- ---------------------------------------------------------------------
do $$
begin
  if not exists (select 1 from pg_roles where rolname = 'brighttime_owner') then
    create role brighttime_owner nologin;
  end if;
  if not exists (select 1 from pg_roles where rolname = 'brighttime_migrator') then
    create role brighttime_migrator nologin bypassrls;
  end if;
  if not exists (select 1 from pg_roles where rolname = 'brighttime_app') then
    create role brighttime_app nologin noinherit;
  end if;
  if not exists (select 1 from pg_roles where rolname = 'brighttime_jobs') then
    create role brighttime_jobs nologin noinherit;
  end if;
  if not exists (select 1 from pg_roles where rolname = 'brighttime_auth') then
    create role brighttime_auth nologin noinherit;
  end if;
  if not exists (select 1 from pg_roles where rolname = 'brighttime_finder') then
    create role brighttime_finder nologin noinherit bypassrls;
  end if;
  if not exists (select 1 from pg_roles where rolname = 'brighttime_readonly') then
    create role brighttime_readonly nologin noinherit;
  end if;
end
$$;

grant brighttime_owner to brighttime_migrator;

-- ---------------------------------------------------------------------
-- Schemas (one per module; see docs/plan/01-architecture.md)
-- ---------------------------------------------------------------------
create schema if not exists platform    authorization brighttime_owner;
create schema if not exists identity    authorization brighttime_owner;
create schema if not exists directory   authorization brighttime_owner;
create schema if not exists opportunity authorization brighttime_owner;
create schema if not exists enrollment  authorization brighttime_owner;
create schema if not exists attendance  authorization brighttime_owner;
create schema if not exists learning    authorization brighttime_owner;
create schema if not exists community   authorization brighttime_owner;
create schema if not exists workforce   authorization brighttime_owner;
create schema if not exists landscape   authorization brighttime_owner;
create schema if not exists analytics   authorization brighttime_owner;
create schema if not exists notify      authorization brighttime_owner;
create schema if not exists integration authorization brighttime_owner;

-- Nobody gets anything by default; 99-rls-and-grants.sql grants explicitly.
revoke all on schema public from public;

-- ---------------------------------------------------------------------
-- Text search configurations
--
-- Only English and Spanish have real Snowball stemmers in core Postgres.
-- Chinese, Bengali, Haitian Creole and Arabic get `simple` + unaccent, and
-- lean on the pg_trgm index for recall. See docs/plan/08-decisions.md D6.
-- ---------------------------------------------------------------------
create text search configuration platform.ts_en (copy = pg_catalog.english);
alter text search configuration platform.ts_en
  alter mapping for hword, hword_part, word with unaccent, english_stem;

create text search configuration platform.ts_es (copy = pg_catalog.spanish);
alter text search configuration platform.ts_es
  alter mapping for hword, hword_part, word with unaccent, spanish_stem;

create text search configuration platform.ts_simple (copy = pg_catalog.simple);
alter text search configuration platform.ts_simple
  alter mapping for hword, hword_part, word with unaccent, simple;

-- ---------------------------------------------------------------------
-- Session context helpers.
--
-- The application sets these with set_config(key, value, is_local => true)
-- inside the request transaction, never at session level: a pooled
-- connection must not carry tenant context into the next request.
-- STABLE + PARALLEL SAFE so the planner can inline them into RLS
-- predicates. We cannot mark them LEAKPROOF (superuser only); the
-- consequence is that RLS-qualified views may not push some quals down.
-- ---------------------------------------------------------------------
create or replace function platform.current_tenant_id() returns uuid
  language sql stable parallel safe
as $fn$
  select nullif(current_setting('brighttime.tenant_id', true), '')::uuid
$fn$;

create or replace function platform.current_actor_id() returns uuid
  language sql stable parallel safe
as $fn$
  select nullif(current_setting('brighttime.actor_id', true), '')::uuid
$fn$;

-- True when a human (not an unauthenticated public request) is behind the
-- transaction. Used by the extra RLS guard on youth-record tables.
create or replace function platform.has_actor() returns boolean
  language sql stable parallel safe
as $fn$
  select platform.current_actor_id() is not null
$fn$;

-- ---------------------------------------------------------------------
-- Generic triggers
-- ---------------------------------------------------------------------
create or replace function platform.touch_updated_at() returns trigger
  language plpgsql
as $fn$
begin
  new.updated_at := now();
  return new;
end
$fn$;

create or replace function platform.deny_write() returns trigger
  language plpgsql
as $fn$
begin
  raise exception 'relation %.% is append-only', tg_table_schema, tg_table_name
    using errcode = '42501';
end
$fn$;

-- Blind index for equality lookup on encrypted values (phone, external ids).
-- Keyed HMAC, not a bare digest, so the output is not a dictionary target.
-- The key lives in platform.blind_index_key and is rotated with a re-index job.
create or replace function platform.blind_index(p_value text, p_key bytea)
  returns bytea
  language sql immutable parallel safe
as $fn$
  select hmac(convert_to(lower(btrim(p_value)), 'UTF8'), p_key, 'sha256')
$fn$;

comment on function platform.blind_index(text, bytea) is
  'Deterministic lookup token for an application-encrypted column. Never a substitute for encryption.';
