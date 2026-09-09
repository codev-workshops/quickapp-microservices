# shellcheck shell=bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GATEWAY_SETTINGS="${GATEWAY_SETTINGS:-$ROOT/src/ApiGateway/appsettings.json}"
CONNECT_URL="${CONNECT_URL:-http://localhost:8083}"
MIGRATION_TOOL="${MIGRATION_TOOL:-dotnet run --project $ROOT/src/Tools/Identity.Migration --}"

need() { command -v "$1" >/dev/null || { echo "missing dependency: $1" >&2; exit 1; }; }
need jq

cluster_meta() { jq -r ".ReverseProxy.Clusters[\"identity-strangler-cluster\"].Metadata[\"$1\"]" "$GATEWAY_SETTINGS"; }
current_destination() { jq -r '.ReverseProxy.Clusters["identity-strangler-cluster"].Destinations.active.Address' "$GATEWAY_SETTINGS"; }

# Flip the single destination of identity-strangler-cluster. YARP reloads appsettings.json on change,
# so a running gateway picks the new destination up without a restart. When the gateway runs in compose,
# IDENTITY_STRANGLER_DESTINATION (env override) must be set to the same value and the service recreated.
set_destination() {
  local address="$1" tmp
  tmp="$(mktemp)"
  jq --arg a "$address" '.ReverseProxy.Clusters["identity-strangler-cluster"].Destinations.active.Address = $a' \
    "$GATEWAY_SETTINGS" > "$tmp" && mv "$tmp" "$GATEWAY_SETTINGS"
  echo "identity-strangler-cluster -> $address"
  echo "compose:  IDENTITY_STRANGLER_DESTINATION='$address' docker compose -f $ROOT/src/docker-compose.yml up -d api-gateway"
}

connector_state() {
  curl -fsS "$CONNECT_URL/connectors/$1/status" 2>/dev/null | jq -r '.connector.state' || echo "UNREACHABLE"
}
