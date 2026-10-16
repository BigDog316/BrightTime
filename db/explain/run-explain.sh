#!/usr/bin/env bash
# Produces real query plans for the finder's scenarios, against a loaded database.
#
# Usage:
#   PGPASSWORD=... db/explain/run-explain.sh [connection-uri]
#
# Runs as the finder's login role, the same one the application connects with, so
# row-level security is part of every plan measured. Each scenario runs in the same
# session as its tenant setting, which is the only way the setting reaches the query.
# One file per scenario in db/explain/out/.
set -euo pipefail

CONN="${1:-postgresql://bt_finder_login@127.0.0.1:5433/brighttime}"
TENANT="${TENANT:-0196c3e8-0000-7000-8000-00000000e001}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$HERE/out"
mkdir -p "$OUT"

for scenario in "$HERE"/scenarios/*.sql; do
  name="$(basename "$scenario" .sql)"
  echo "==> $name"
  # -c and -f in one invocation run in order, in one session.
  psql "$CONN" -X -q -v ON_ERROR_STOP=1 \
    -c "set brighttime.tenant_id = '$TENANT';" \
    -f "$scenario" > "$OUT/$name.txt"
done

echo "plans written to $OUT"
