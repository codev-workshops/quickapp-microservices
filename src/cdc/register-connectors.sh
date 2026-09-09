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
  curl -fsS "$CONNECT_URL/connectors/$name/status" | jq -c '{name, state: .connector.state, tasks: [.tasks[].state]}'
}

[[ "$what" == source || "$what" == all ]] && register "$DIR/connectors/monolith-identity-source.json"
[[ "$what" == sink   || "$what" == all ]] && register "$DIR/connectors/identitydb-sink.json"
exit 0
