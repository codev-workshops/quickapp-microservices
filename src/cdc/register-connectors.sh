#!/usr/bin/env bash
# Registers (or updates) the forward-sync connectors: monolith SQL Server -> Kafka -> identitydb.
# Usage: CONNECT_URL=http://localhost:8083 ./register-connectors.sh [source|sink|all]
set -euo pipefail
CONNECT_URL="${CONNECT_URL:-http://localhost:8083}"
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
what="${1:-all}"

register() {
  local file="$1" name
  name="$(jq -r .name "$file")"
  echo "Registering connector $name"
  jq .config "$file" | curl -fsS -X PUT -H 'Content-Type: application/json' \
    --data @- "$CONNECT_URL/connectors/$name/config" >/dev/null
  # A freshly created connector 404s on /status until the worker has processed the config topic.
  local status i
  for i in $(seq 1 30); do
    if status="$(curl -fsS "$CONNECT_URL/connectors/$name/status" 2>/dev/null)"; then
      jq -c '{name, state: .connector.state, tasks: [.tasks[].state]}' <<<"$status"
      return 0
    fi
    sleep 1
  done
  echo "Connector $name did not report a status within 30s" >&2
  return 1
}

[[ "$what" == source || "$what" == all ]] && register "$DIR/connectors/monolith-identity-source.json"
[[ "$what" == sink   || "$what" == all ]] && register "$DIR/connectors/identitydb-sink.json"
exit 0
