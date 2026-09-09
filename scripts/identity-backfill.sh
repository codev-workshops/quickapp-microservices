#!/usr/bin/env bash
# Saga step 2: one-time backfill monolith SQL Server -> identitydb, then a DB diff to prove convergence.
# Run AFTER Identity.API has applied its EF migrations (or `dotnet ef database update`) and AFTER the
# forward CDC source connector is registered (so no change between snapshot and streaming is lost).
source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"
$MIGRATION_TOOL backfill "$@"
$MIGRATION_TOOL db-diff
