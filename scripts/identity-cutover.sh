#!/usr/bin/env bash
# Saga step 7: cut the Identity domain over to Identity.API.
#   1. pre-flight: shadow verification (tokens + user/role diff, shared cert kid) and DB diff must pass
#   2. pause forward CDC (monolith -> identitydb) so only one direction writes during overlap
#   3. flip the gateway destination for /connect/token and /api/account/** to identity-service
# Reverse sync (identitydb -> monolith) must already be enabled (ReverseSync__Enabled=true) and stays on.
source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

target="$(cluster_meta IdentityServiceAddress)"
echo "Current destination: $(current_destination)"
echo "Cutover target:      $target"

if [[ "${SKIP_VERIFY:-false}" != "true" ]]; then
  echo "== pre-flight: shadow verification"; $MIGRATION_TOOL verify
  echo "== pre-flight: database diff";       $MIGRATION_TOOL db-diff
fi

if [[ "${REVERSE_SYNC_ENABLED:-}" != "true" ]]; then
  echo "REFUSING cutover: REVERSE_SYNC_ENABLED is not 'true'. Reverse sync must be running before Identity takes writes," >&2
  echo "otherwise rollback would lose changes made against identitydb." >&2
  exit 1
fi

echo "== pausing forward CDC"
if [[ "$(connector_state identitydb-sink)" != "UNREACHABLE" ]]; then
  "$ROOT/src/cdc/pause-forward-sync.sh" pause
else
  echo "Kafka Connect unreachable at $CONNECT_URL - pause the forward connectors manually before continuing." >&2
  [[ "${FORCE:-false}" == "true" ]] || exit 1
fi

echo "== flipping gateway route"
set_destination "$target"
echo "Cutover complete. Keep reverse sync enabled until Identity is permanently accepted (see docs/identity-migration-saga.md)."
