#!/usr/bin/env bash
# First-boot initialisation for the local compose database. The postgres image runs this once,
# as the superuser, against an empty data volume; after a change under db/schema, rebuild with
#
#   docker compose -f deploy/compose.yml down -v
#
# It applies the DDL in filename order (00 extensions and roles, 10..97 modules, 99 row-level
# security and grants last), then creates the login roles the hosts connect as. The DDL creates
# only NOLOGIN group roles, so credentials never appear in migrations; these logins and their
# password are local development only.
set -euo pipefail

psql=(psql -v ON_ERROR_STOP=1 -X -q --username "$POSTGRES_USER" --dbname "$POSTGRES_DB")

for f in /brighttime/schema/*.sql; do
  echo "==> $(basename "$f")"
  "${psql[@]}" -f "$f"
done

# web and api: the application's login, which also enters brighttime_finder for search (D13).
# auth: the authentication path, Identity stores only. jobs: the background worker.
"${psql[@]}" -v password="$BRIGHTTIME_LOGIN_PASSWORD" <<'SQL'
create role bt_web_login  login password :'password';
create role bt_auth_login login password :'password';
create role bt_jobs_login login password :'password';
grant brighttime_app, brighttime_finder to bt_web_login;
grant brighttime_auth to bt_auth_login;
grant brighttime_jobs to bt_jobs_login;
SQL

echo "brighttime schema and logins ready"
