#!/usr/bin/env bash
# Produces the real query plans for the finder against a seeded database.
#
# Usage:  db/explain/run-explain.sh [connection-string]
# Default connection assumes the compose stack from deploy/compose.yml.
#
# Output lands in db/explain/out/ as one file per scenario, each with
# EXPLAIN (ANALYZE, BUFFERS, VERBOSE, SETTINGS, WAL) so that the plan,
# the row estimates, the buffer counts and the non-default GUCs are all
# captured together. Commit them: a plan change in a PR diff is a
# reviewable event.
set -euo pipefail

CONN="${1:-postgresql://brighttime_app@localhost:5432/brighttime}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$HERE/out"
mkdir -p "$OUT"

run() {
  local name="$1"; shift
  echo "==> $name"
  psql "$CONN" -X -q -v ON_ERROR_STOP=1 \
    -c "set local brighttime.tenant_id = '$TENANT';" \
    -f "$HERE/scenarios/$name.sql" > "$OUT/$name.txt"
}

TENANT="${TENANT:?set TENANT to the seeded tenant uuid}"

psql "$CONN" -X -q -c "analyze;"

for f in "$HERE"/scenarios/*.sql; do
  run "$(basename "$f" .sql)"
done

echo
echo "Plans written to $OUT"
echo "Review checklist:"
echo "  * no Seq Scan on opportunity.listing_search"
echo "  * BitmapAnd combines the facet GIN with at most one other index"
echo "  * estimated vs actual rows within 10x at every node"
echo "  * shared read buffers for a warm cache query under 2000"
