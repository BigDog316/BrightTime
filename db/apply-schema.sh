#!/usr/bin/env bash
# Rebuilds a database from the DDL in db/schema, stopping at the first error.
#
# Usage:  db/apply-schema.sh <database> [host] [port] [user]
#
# Drops and recreates <database>. Roles are cluster-wide and are created only if
# absent (00-extensions-roles.sql), so this never alters an existing role.
# Refuses to run against a database named "postgres" or any name not starting
# with "brighttime", so a typo cannot drop someone else's data.
set -euo pipefail

DB="${1:?database name required}"
HOST="${2:-127.0.0.1}"
PORT="${3:-5432}"
USER_NAME="${4:-postgres}"

case "$DB" in
  brighttime*) ;;
  *) echo "refusing to drop '$DB': the name must start with 'brighttime'" >&2; exit 1 ;;
esac

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PSQL=(psql -h "$HOST" -p "$PORT" -U "$USER_NAME" -X -q -v ON_ERROR_STOP=1)

"${PSQL[@]}" -d postgres -c "drop database if exists $DB;"
"${PSQL[@]}" -d postgres -c "create database $DB;"

for f in "$HERE"/schema/*.sql; do
  echo "==> $(basename "$f")"
  "${PSQL[@]}" -d "$DB" -f "$f"
done

echo "schema applied to $DB"
