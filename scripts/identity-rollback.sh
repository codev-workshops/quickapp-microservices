#!/usr/bin/env bash
# Rollback: re-point /connect/token and /api/account/** at the monolith.
# Zero data loss: every write made against identitydb since cutover is in the IdentityOutbox and is replayed
# into the monolith DB by the reverse sync. This script drains that outbox to empty BEFORE flipping the route,
# then resumes forward CDC so identitydb keeps tracking the monolith for a later retry.
source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

target="$(cluster_meta MonolithAddress)"
echo "Current destination: $(current_destination)"
echo "Rollback target:     $target"

echo "== draining reverse-sync outbox into the monolith"
if ! $MIGRATION_TOOL drain-outbox; then
  echo "Outbox is not empty / has poisoned rows. Fix the monolith connectivity or reconcile, then re-run." >&2
  [[ "${FORCE:-false}" == "true" ]] || exit 1
fi

echo "== flipping gateway route back to the monolith"
set_destination "$target"

echo "== resuming forward CDC (monolith -> identitydb)"
if [[ "$(connector_state identitydb-sink)" != "UNREACHABLE" ]]; then
  "$ROOT/src/cdc/pause-forward-sync.sh" resume
else
  echo "Kafka Connect unreachable at $CONNECT_URL - resume the forward connectors manually." >&2
fi

echo "Rollback complete. Reverse sync stays ENABLED (harmless while Identity receives no traffic); disable it only"
echo "when the Identity service is decommissioned or permanently accepted."
