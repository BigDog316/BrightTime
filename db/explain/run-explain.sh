#!/usr/bin/env bash
# Produces real query plans for the finder's scenarios, against a loaded database.
#
# Usage:
#   PGPASSWORD=... db/explain/run-explain.sh [connection-uri]
#
# Runs as the application's login role, then switches to brighttime_finder exactly
# as SqlListingFinder does (decision D13), so the plans measured are the plans the
# finder gets. Set FINDER_ROLE= (empty) to measure under the login role and RLS
# instead. The tenant setting is kept for the scenario files that read it.
# One file per scenario in db/explain/out/.
set -euo pipefail

CONN="${1:-postgresql://bt_finder_login@127.0.0.1:5433/brighttime}"
TENANT="${TENANT:-0196c3e8-0000-7000-8000-00000000e001}"
FINDER_ROLE="${FINDER_ROLE-brighttime_finder}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$HERE/out"
mkdir -p "$OUT"

for scenario in "$HERE"/scenarios/*.sql; do
  name="$(basename "$scenario" .sql)"
  echo "==> $name"
  # -c and -f in one invocation run in order, in one session.
  # Scenarios named *rls* measure RLS itself and must run under the login role.
  # The finder role cannot read their tables, by design.
  role_switch="select 1;"
  if [ -n "$FINDER_ROLE" ] && [[ "$name" != *rls* ]]; then
    role_switch="set role $FINDER_ROLE;"
  fi
  psql "$CONN" -X -q -v ON_ERROR_STOP=1 \
    -c "set brighttime.tenant_id = '$TENANT';" \
    -c "$role_switch" \
    -f "$scenario" > "$OUT/$name.txt"
done

echo "plans written to $OUT"
