#!/usr/bin/env bash
# Pauses forward CDC (monolith -> identitydb). Run at cutover, right before flipping the gateway to Identity,
# so the two directions never write the same table concurrently. Resume with `resume` on rollback.
set -euo pipefail
CONNECT_URL="${CONNECT_URL:-http://localhost:8083}"
action="${1:-pause}"
for c in identitydb-sink monolith-identity-source; do
  echo "$action $c"
  curl -fsS -X PUT "$CONNECT_URL/connectors/$c/$action"
done
sleep 2
for c in identitydb-sink monolith-identity-source; do
  curl -fsS "$CONNECT_URL/connectors/$c/status" | jq -c '{name, state: .connector.state}'
done
