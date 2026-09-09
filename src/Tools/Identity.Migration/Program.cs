using Identity.Migration;
using Microsoft.Extensions.Configuration;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args.Skip(1).ToArray())
    .Build();

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

var exitCode = command switch
{
    "backfill" => await new Backfill(configuration).RunAsync(),
    "db-diff" => await new DbDiff(configuration).RunAsync(),
    "verify" => await new ShadowVerify(configuration).RunAsync(),
    "drain-outbox" => await new OutboxDrain(configuration).RunAsync(),
    _ => Help()
};

return exitCode;

static int Help()
{
    Console.WriteLine("""
        Identity.Migration - saga tooling for the monolith -> Identity service strangler migration

          backfill      One-time copy of all Identity + OpenIddict tables from the monolith SQL Server DB
                        into Postgres identitydb (idempotent upsert on GUID/PK, raw hashes/stamps preserved).
          db-diff       Row-count and per-row hash diff of every synced table between the two databases.
          verify        Shadow verification: obtain tokens from both monolith and Identity.API for the seeded
                        accounts, assert the signing key id (kid) matches, and diff /api/account user + role reads.
          drain-outbox  Replay every pending IdentityOutbox row into the monolith DB and exit when empty
                        (run before rollback to guarantee the monolith has all changes made against identitydb).

        Configuration: appsettings.json, environment variables (ConnectionStrings__Monolith, ...) or
        --ConnectionStrings:Monolith=... command-line overrides.
        """);
    return 1;
}
