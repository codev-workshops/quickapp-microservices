# Identity domain migration: monolith → `Identity.API` (strangler fig + saga + CDC)

The Identity/Account domain (`connect/token`, `api/account/**`) moves from `quickapp-monolith` (SQL Server,
`ApplicationDbContext`) to the `Identity` service (Postgres `identitydb`). Traffic is controlled by the YARP
gateway; data flows through CDC in both directions so the cutover can be rolled back at any time without
losing data.

```
                 ┌──────────── API gateway (YARP) ────────────┐
   clients ────► │ /connect/token, /api/account/**             │
                 │   identity-strangler-cluster.active ────────┼──► monolith :5225   (before cutover)
                 │                                     └───────┼──► identity-service :5001 (after cutover)
                 └────────────────────────────────────────────┘
   monolith SQL Server ── Debezium (forward CDC) ──► Kafka ──► JDBC sink ──► identitydb (Postgres)
   identitydb  ── IdentityOutbox ── ReverseSyncPublisher (MERGE/DELETE) ──► monolith SQL Server
```

## Components in this repo

| Piece | Location |
|---|---|
| Ported domain, `IdentityDbContext<ApplicationUser, ApplicationRole, string>` + OpenIddict EF, outbox capture | `src/Services/Identity/Identity.Domain`, `src/Services/Identity/Identity.Infrastructure` |
| Identity.API (`connect/token`, `api/account`), OpenIddict server/validation, shared PFX loader | `src/Services/Identity/Identity.API` |
| EF migration for `identitydb` (`InitialIdentity`) | `src/Services/Identity/Identity.Infrastructure/Data/Migrations` |
| Reverse sync publisher (identitydb → monolith) | `src/Services/Identity/Identity.Infrastructure/Outbox/ReverseSyncPublisher.cs` |
| Forward CDC (Debezium SQL Server source → JDBC sink) | `src/cdc/` |
| Saga tool: `backfill`, `db-diff`, `verify`, `drain-outbox` | `src/Tools/Identity.Migration` |
| Gateway routes `identity-token-route`, `identity-account-route` → `identity-strangler-cluster` | `src/ApiGateway/appsettings.json` |
| Scripts: backfill, verify, cutover, rollback | `scripts/identity-*.sh` |

## Invariants that make rollback lossless

1. **Same primary keys everywhere.** All Identity/OpenIddict rows keep their string GUID PKs (and the
   composite PKs of `AspNetUserRoles`, `AspNetUserLogins`, `AspNetUserTokens`). Both sync directions are
   idempotent upserts keyed on those PKs (`INSERT … ON CONFLICT DO UPDATE` into Postgres, `MERGE` into SQL
   Server, `DELETE` for deletes), so replays and overlaps converge.
2. **Raw credentials copied verbatim.** `PasswordHash`, `SecurityStamp`, `ConcurrencyStamp` are copied
   as-is by the backfill and by both CDC directions; no user is forced to reset a password.
3. **One writer per direction at a time.** Before cutover only forward CDC writes `identitydb` (via JDBC,
   bypassing EF, so nothing lands in the outbox). At cutover forward CDC is *paused* and Identity.API becomes
   the only writer; every `SaveChanges` in `IdentityDbContext` records the changed row in `IdentityOutbox`
   inside the same transaction, and `ReverseSyncPublisher` replays it to the monolith in order.
4. **Rollback drains first.** `identity-rollback.sh` runs `drain-outbox` and refuses to flip the route until
   the outbox is empty (or `FORCE=true`), then resumes forward CDC.
5. **Shared OpenIddict certificate.** Both apps must load the same PFX (`OIDC:Certificates:Path/Password`)
   and advertise the same issuer so tokens issued by either side validate on the other. `Identity.API`
   refuses to start outside Development unless `OIDC:Certificates:Path` is set
   (`OIDC:Certificates:RequireShared=true` by default). `verify` asserts the `kid` of tokens from both
   apps matches and cross-validates each token against the other app.

## Saga steps

| # | Step | Command / action | Compensation |
|---|---|---|---|
| 0 | Prerequisites | Provision the shared PFX and set `OIDC:Certificates:Path/Password` (+ `OIDC:Issuer`) identically in the monolith (`QuickApp.Server/appsettings*.json` or secrets) and `Identity.API`. Register the same OpenIddict client (`quickapp_spa`) in both. Enable SQL Server CDC: `sqlcmd -i src/cdc/sql/enable-cdc.sql`. | – |
| 1 | Provision `identitydb` | Start Identity.API (`Database:MigrateOnStartup=true`) or `dotnet ef database update --project src/Services/Identity/Identity.Infrastructure --startup-project src/Services/Identity/Identity.API`. The seeder is a no-op if any user already exists, so it never overwrites backfilled data. | Drop `identitydb`. |
| 2 | Forward CDC + backfill | `docker compose --profile migration up -d` then `src/cdc/register-connectors.sh` (source uses `snapshot.mode=no_data`, so it streams from *now*). Then `scripts/identity-backfill.sh`. Registering the source *before* the backfill means any change made during the copy is re-applied idempotently afterwards. | Delete connectors; truncate identitydb tables. |
| 3 | Strangler window | Gateway routes already exist and point at the monolith (`identity-strangler-cluster.active = MonolithAddress`). Identity.API runs but takes no client traffic; forward CDC keeps `identitydb` current. `scripts/identity-verify.sh` may be run repeatedly (`db-diff` must be clean). | – |
| 4 | Enable reverse sync | Set `ConnectionStrings:MonolithConnection` and `ReverseSync:Enabled=true` on Identity.API (compose: `MONOLITH_CONNECTION`, `REVERSE_SYNC_ENABLED=true`). Nothing is published yet because Identity.API has no writers, but the pipeline is proven live. | Set `ReverseSync:Enabled=false`. |
| 5 | Shadow verification | `VERIFY_ADMIN_PASSWORD=… VERIFY_USER_PASSWORD=… scripts/identity-verify.sh`. Issues password-grant tokens for `admin` and `user` on both apps, asserts same `kid` and `sub`, and diffs `api/account/users/me`, `api/account/users`, `api/account/roles` (each read on one app with the *other* app's token). Must exit 0. | – |
| 6 | Cutover | `REVERSE_SYNC_ENABLED=true scripts/identity-cutover.sh` → re-runs step 5 + `db-diff`, pauses forward CDC, flips `identity-strangler-cluster.active` to `IdentityServiceAddress`. YARP hot-reloads `appsettings.json`; in compose set `IDENTITY_STRANGLER_DESTINATION=http://identity-service:5001/` and `docker compose up -d api-gateway`. Existing tokens keep working because the certificate is shared. | Step R below. |
| 7 | Soak | Monitor `IdentityOutbox` (`PublishedAtUtc IS NULL` count should hover near 0, `LastError` empty) and `scripts/identity-verify.sh`/`db-diff`. | Step R. |
| 8 | Accept | Only once Identity is permanently accepted: `ReverseSync:Enabled=false`, remove forward connectors, stop the monolith Identity endpoints. | – |

### R. Rollback (any time after step 6)

```bash
scripts/identity-rollback.sh          # 1) drain-outbox until empty  2) route -> monolith  3) resume forward CDC
```

Because the monolith DB has received every change made against `identitydb` (step invariants 3–4), users,
roles, claims, refresh tokens and authorizations created while Identity was live are all present in the
monolith; nobody is forced to log in again. Reverse sync stays enabled after rollback (it has nothing to
publish while Identity takes no traffic) and is disabled only when the service is decommissioned or step 8
is reached.

## Verification checklist before step 6

- [ ] `verify` prints `SHARED CERT OK` for both accounts (same `kid` on tokens from both apps) and `MATCH`
      for every `api/account` read. A `401` when reading one app with the other app's token means the
      certificate or issuer is **not** shared — stop.
- [ ] `db-diff` reports `Databases are in sync.`
- [ ] Gateway config contains exactly one route each for `/connect/token` and `/api/account/{**catch-all}`
      (`identity-token-route`, `identity-account-route`). The pre-existing `identity-route`
      (`/api/identity/**` → `identity-cluster`) is unrelated and was left untouched.
- [ ] `ReverseSync:Enabled=true` on Identity.API and `IdentityOutbox` is empty.
- [ ] Forward connectors `RUNNING` (`curl :8083/connectors/identitydb-sink/status`).

## Configuration reference

Identity.API (`appsettings.json` / env):

| Key | Purpose |
|---|---|
| `ConnectionStrings:DefaultConnection` | Postgres `identitydb` |
| `ConnectionStrings:MonolithConnection` | SQL Server used by reverse sync |
| `ReverseSync:Enabled`, `PollIntervalMs`, `BatchSize`, `MaxAttempts` | Outbox publisher |
| `OIDC:Issuer` | Must equal the monolith's issuer for cross-validation |
| `OIDC:Certificates:Path`, `Password` | Shared PFX (same keys as the monolith) |
| `OIDC:Certificates:RequireShared` | `true` (default) fails startup outside Development without a PFX |
| `Database:MigrateOnStartup` | Apply migrations + idempotent seed at boot |

Identity.Migration tool (`src/Tools/Identity.Migration/appsettings.json`, env `ConnectionStrings__Monolith`,
`ConnectionStrings__Identity`, `Verify__MonolithBaseUrl`, `Verify__IdentityBaseUrl`; passwords only via
`VERIFY_ADMIN_PASSWORD` / `VERIFY_USER_PASSWORD`; `Verify__AllowUntrustedTls=true` only for local dev certificates).

## Known gaps / follow-ups on the monolith side

- The monolith currently ships with empty `OIDC:Certificates:Path/Password` and uses OpenIddict development
  certificates. A persisted PFX must be generated once, stored as a secret, and configured in **both** apps
  before step 5 can pass. `verify` will flag this (`FAIL: different signing/encryption key`).
- A shared key is *not* sufficient: OpenIddict validation also checks `iss`. The monolith never calls
  `SetIssuer`, so it infers the issuer from the request host, which differs between the monolith's own URL
  and the gateway URL (YARP rewrites `Host` by default). Set the same `OIDC:Issuer` (the public gateway
  origin) in both apps, and pass it via `SetIssuer` in `QuickApp.Server/Program.cs`. Verified locally:
  with a shared key but different issuers `verify` reports `401 on both sides`; with matching issuers all
  cross-validations return 200.
- The monolith's `UserAccountService.DeleteUserAsync` blocks deletion when the user has orders. Identity
  exposes `IUserDeletionGuard` for the Order service to plug the same check in; no implementation is
  registered yet, so during the overlap the Identity side allows deleting cashiers with orders (the monolith
  side still blocks). Register a guard before cutover if this matters.
- Reverse sync writes straight into the monolith's tables with `MERGE`, bypassing the monolith's EF audit
  fields; `CreatedBy/UpdatedBy/*Date` are copied from `identitydb` as-is.
