#!/usr/bin/env bash
# Saga step 5: shadow verification against both apps + database diff. Exit 0 only if everything matches.
# Requires VERIFY_ADMIN_PASSWORD / VERIFY_USER_PASSWORD in the environment.
source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"
echo "Gateway identity destination: $(current_destination)"
echo "Forward CDC: source=$(connector_state monolith-identity-source) sink=$(connector_state identitydb-sink)"
$MIGRATION_TOOL verify "$@"
$MIGRATION_TOOL db-diff
