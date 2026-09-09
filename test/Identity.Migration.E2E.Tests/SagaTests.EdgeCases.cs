using System.Net;
using System.Reflection;
using System.Text.Json;
using Identity.Domain.Entities;
using Identity.Domain.Interfaces;
using Identity.Infrastructure.Data;
using Identity.Infrastructure.Outbox;
using Identity.Migration.E2E.Tests.Harness;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Identity.Migration.E2E.Tests;

public sealed partial class SagaTests
{
    // ====================================================================== E1 / E2 - commit ordering and poison messages

    [Fact, Step(80)]
    public Task E1_BatchStopsAtFirstFailurePreservingCommitOrder() => Step("E1", "DrainOnceAsync stops at the first failed message; later rows stay unpublished", async () =>
    {
        await using var db = fx.CreateIdentityDbContext();

        var first = new ApplicationRole("e2e-order-first", "before the failing message") { NormalizedName = "E2E-ORDER-FIRST" };
        db.Roles.Add(first);
        await db.SaveChangesAsync();                                                   // outbox row A
        await fx.PgExecAsync("""
            INSERT INTO "IdentityOutbox" ("TableName", "Operation", "KeyJson", "PayloadJson", "OccurredAtUtc", "Attempts")
            VALUES ('NoSuchTable', 1, '{"Id":"poison"}', '{"Id":"poison"}', now() at time zone 'utc', 0)
            """);                                                                      // outbox row B (cannot be applied)
        var last = new ApplicationRole("e2e-order-last", "after the failing message") { NormalizedName = "E2E-ORDER-LAST" };
        db.Roles.Add(last);
        await db.SaveChangesAsync();                                                   // outbox row C
        fx.State["e1.firstRoleId"] = first.Id;
        fx.State["e1.lastRoleId"] = last.Id;

        var (publisher, options, logs) = CreatePublisher(maxAttempts: 3);
        var processed = await publisher.DrainOnceAsync(options, CancellationToken.None);
        Assert.Equal(1, processed);

        var rows = await fx.PgRowsAsync("SELECT \"TableName\", \"KeyJson\", \"PublishedAtUtc\", \"Attempts\", \"LastError\" FROM \"IdentityOutbox\" WHERE \"PublishedAtUtc\" IS NULL OR \"TableName\" = 'NoSuchTable' ORDER BY \"Id\"");
        Assert.Equal(2, rows.Count);
        Assert.Equal("NoSuchTable", rows[0]["TableName"]);
        Assert.Equal(1, rows[0]["Attempts"]);
        Assert.NotNull(rows[0]["LastError"]);
        Assert.Null(rows[0]["PublishedAtUtc"]);
        Assert.Equal("AspNetRoles", rows[1]["TableName"]);
        Assert.Contains(last.Id, (string)rows[1]["KeyJson"]!);
        Assert.Null(rows[1]["PublishedAtUtc"]);
        Assert.Equal(0, rows[1]["Attempts"]);

        Assert.Equal(1, await fx.SqlScalarAsync<int>("SELECT COUNT(*) FROM AspNetRoles WHERE Id = @id", ("@id", first.Id)));
        Assert.Equal(0, await fx.SqlScalarAsync<int>("SELECT COUNT(*) FROM AspNetRoles WHERE Id = @id", ("@id", last.Id)));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error || e.Level == LogLevel.Warning);

        return $"processed=1; row B Attempts=1 LastError='{Trunc((string)rows[0]["LastError"]!)}'; row C still unpublished; monolith has first role only";
    });

    [Fact, Step(81)]
    public Task E2_PoisonMessageBlocksRollback() => Step("E2", "Poison row: Attempts increments, LastError kept, marked published after MaxAttempts + critical log; drain-outbox exit 2 blocks rollback", async () =>
    {
        var (publisher, options, logs) = CreatePublisher(maxAttempts: 3);
        await publisher.DrainOnceAsync(options, CancellationToken.None); // attempt 2 -> still breaks the batch
        var afterSecond = (await fx.PgRowsAsync("SELECT \"Attempts\", \"PublishedAtUtc\" FROM \"IdentityOutbox\" WHERE \"TableName\" = 'NoSuchTable'")).Single();
        Assert.Equal(2, afterSecond["Attempts"]);
        Assert.Null(afterSecond["PublishedAtUtc"]);
        Assert.Equal(0, await fx.SqlScalarAsync<int>("SELECT COUNT(*) FROM AspNetRoles WHERE Id = @id", ("@id", RequireState("e1.lastRoleId"))));

        var processed = await publisher.DrainOnceAsync(options, CancellationToken.None); // attempt 3 == MaxAttempts -> poisoned, batch continues
        var poison = (await fx.PgRowsAsync("SELECT \"Attempts\", \"PublishedAtUtc\", \"LastError\" FROM \"IdentityOutbox\" WHERE \"TableName\" = 'NoSuchTable'")).Single();
        Assert.Equal(3, poison["Attempts"]);
        Assert.NotNull(poison["PublishedAtUtc"]);
        Assert.NotNull(poison["LastError"]);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Critical);
        Assert.Equal(1, processed); // row C was applied once the poison row stopped blocking
        Assert.Equal(1, await fx.SqlScalarAsync<int>("SELECT COUNT(*) FROM AspNetRoles WHERE Id = @id", ("@id", RequireState("e1.lastRoleId"))));

        var (drainExit, drainText) = await RunToolAsync("drain-outbox (poisoned)", () => new OutboxDrain(fx.MigrationConfiguration()).RunAsync());
        Gate("E2", "drain-outbox", drainExit, 2, "poisoned>0 -> rollback BLOCKED (manual reconciliation needed)");
        Assert.Contains("Skipped-after-max-attempts=1", drainText);

        var rollback = await fx.RunScriptAsync("scripts/identity-rollback.sh");
        Log(rollback.Combined);
        Assert.NotEqual(0, rollback.ExitCode);
        Assert.Contains("Outbox is not empty / has poisoned rows", rollback.StdErr);

        // Manual reconciliation: the poison row is the operator's problem - remove it, then the gate opens again.
        await fx.PgExecAsync("DELETE FROM \"IdentityOutbox\" WHERE \"TableName\" = 'NoSuchTable'");
        var (afterExit, _) = await RunToolAsync("drain-outbox (reconciled)", () => new OutboxDrain(fx.MigrationConfiguration()).RunAsync());
        Gate("E2", "drain-outbox", afterExit, 0, "after manual reconciliation of the poison row");

        return $"Attempts 1->2->3, PublishedAtUtc set at MaxAttempts=3, LastError='{Trunc((string)poison["LastError"]!)}', critical logged; drain-outbox exit {drainExit} -> identity-rollback.sh exit {rollback.ExitCode}";
    });

    // ====================================================================== E3 - idempotent replay

    [Fact, Step(82)]
    public Task E3_IdempotentReplay() => Step("E3", "Re-running DrainOnceAsync / Backfill and replaying identical upserts (incl. IDENTITY_INSERT tables) creates no duplicates", async () =>
    {
        // Converge first (forward sync catches up with the tokens the monolith issued in P7c), then take the baseline.
        if (!fx.Options.FullFidelity)
            Assert.Equal(0, (await RunToolAsync("backfill (catch-up)", () => new Backfill(fx.MigrationConfiguration()).RunAsync())).ExitCode);
        else
            await WaitForDbDiffZeroAsync("E3 (CDC convergence)", TimeSpan.FromMinutes(2));
        var before = await CountsAsync();

        var (publisher, options, _) = CreatePublisher(maxAttempts: 3);
        Assert.Equal(0, await publisher.DrainOnceAsync(options, CancellationToken.None));
        Assert.Equal(0, await publisher.DrainOnceAsync(options, CancellationToken.None));

        // Replay the Phase 5 role-claim upsert twice (SET IDENTITY_INSERT + MERGE ... WITH (HOLDLOCK)).
        for (var i = 0; i < 2; i++)
            await fx.PgExecAsync("""
                INSERT INTO "IdentityOutbox" ("TableName", "Operation", "KeyJson", "PayloadJson", "OccurredAtUtc", "Attempts")
                VALUES ('AspNetRoleClaims', 1, $1, $2, now() at time zone 'utc', 0)
                """, RequireState("p5.roleClaimKeyJson"), RequireState("p5.roleClaimPayloadJson"));
        Assert.Equal(2, await publisher.DrainOnceAsync(options, CancellationToken.None));
        Assert.Equal(1, await fx.SqlScalarAsync<int>("SELECT COUNT(*) FROM AspNetRoleClaims WHERE Id = @id", ("@id", int.Parse(RequireState("p5.roleClaimId")))));

        var (b1, _) = await RunToolAsync("backfill #1", () => new Backfill(fx.MigrationConfiguration()).RunAsync());
        var (b2, _) = await RunToolAsync("backfill #2", () => new Backfill(fx.MigrationConfiguration()).RunAsync());
        Assert.Equal(0, b1);
        Assert.Equal(0, b2);

        var after = await CountsAsync();
        Assert.Equal(before, after);
        var (diffExit, _) = await RunToolAsync("db-diff", () => new DbDiff(fx.MigrationConfiguration()).RunAsync());
        Gate("E3", "db-diff", diffExit, 0, "after repeated drain + backfill");

        return $"row counts unchanged across {SyncTables.All.Length} tables; db-diff exit {diffExit}";
    });

    // ====================================================================== E4 - type / precision boundaries

    [Fact, Step(83)]
    public Task E4_TypeAndPrecisionBoundaries() => Step("E4", "datetime2(100ns) vs Postgres(us) compare equal; NULL/bit/uniqueidentifier round-trip through ConvertValue", async () =>
    {
        // Microseconds.Round: half-up to microseconds (pgjdbc semantics) so Backfill, the Debezium JDBC sink and DbDiff agree.
        var baseTicks = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).Ticks;
        long RoundTicks(long fraction) => Microseconds.Round(new DateTime(baseTicks + fraction, DateTimeKind.Utc)).Ticks - baseTicks;
        Assert.Equal(1234570, RoundTicks(1234567));
        Assert.Equal(1234560, RoundTicks(1234564));
        Assert.Equal(1234560, RoundTicks(1234560));
        Assert.Equal(1234570, RoundTicks(1234565));
        Assert.Equal(TimeSpan.TicksPerSecond, RoundTicks(9999995)); // carries into the next second

        // Backfill.ConvertValue (public)
        Assert.Same(DBNull.Value, Backfill.ConvertValue(DBNull.Value));
        var unspecified = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified).AddTicks(1234567);
        var converted = (DateTime)Backfill.ConvertValue(unspecified);
        Assert.Equal(DateTimeKind.Utc, converted.Kind);
        Assert.Equal(Microseconds.Round(unspecified).Ticks, converted.Ticks);
        var dto = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.FromHours(2)).AddTicks(1234567);
        Assert.Equal(Microseconds.Round(dto.UtcDateTime), ((DateTimeOffset)Backfill.ConvertValue(dto)).DateTime);
        Assert.Equal(TimeSpan.Zero, ((DateTimeOffset)Backfill.ConvertValue(dto)).Offset);
        Assert.Equal(true, Backfill.ConvertValue(true));
        var guid = Guid.NewGuid();
        Assert.Equal(guid, Backfill.ConvertValue(guid));

        // DbDiff.Normalize (private static; located by reflection)
        var normalize = typeof(DbDiff).GetMethod("Normalize", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.NotNull(normalize);
        var sqlServerValue = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234567); // .1234567 (100ns)
        var postgresValue = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234570);  // .123457  (us, as Postgres stores it)
        Assert.Equal((string)normalize.Invoke(null, [postgresValue])!, (string)normalize.Invoke(null, [sqlServerValue])!);
        Assert.NotEqual((string)normalize.Invoke(null, [postgresValue.AddTicks(-10)])!, (string)normalize.Invoke(null, [sqlServerValue])!);
        Assert.Equal("<null>", (string)normalize.Invoke(null, [DBNull.Value])!);
        Assert.Equal("1", (string)normalize.Invoke(null, [true])!);
        Assert.Equal("0", (string)normalize.Invoke(null, [false])!);
        Assert.Equal(guid.ToString(), (string)normalize.Invoke(null, [guid])!);
        Assert.Equal((string)normalize.Invoke(null, [sqlServerValue])!, (string)normalize.Invoke(null, [new DateTimeOffset(sqlServerValue)])!);

        // ReverseSyncPublisher.ConvertValue(sqlType, JsonElement) (private static)
        var rsConvert = typeof(ReverseSyncPublisher).GetMethod("ConvertValue", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.NotNull(rsConvert);
        object? Convert(string sqlType, string json) => rsConvert.Invoke(null, [sqlType, JsonDocument.Parse(json).RootElement]);
        Assert.Null(Convert("nvarchar", "null"));
        Assert.Null(Convert("datetime2", "null"));
        Assert.Equal(true, Convert("bit", "true"));
        Assert.Equal(false, Convert("bit", "false"));
        Assert.Equal(guid, Convert("uniqueidentifier", $"\"{guid}\""));
        Assert.Equal(sqlServerValue, Convert("datetime2", "\"2026-01-02T03:04:05.1234567Z\""));
        Assert.Equal(dto, Convert("datetimeoffset", $"\"{dto:O}\""));
        Assert.Equal(42, Convert("int", "42"));

        // End-to-end: the seeded 100ns row survived Backfill and reverse sync and compares equal on both sides.
        var userId = RequireState("seed.userId");
        var sqlCreated = await fx.SqlScalarAsync<DateTime>("SELECT CreatedDate FROM AspNetUsers WHERE Id = @id", ("@id", userId));
        var pgCreated = await fx.PgScalarAsync<DateTime>("SELECT \"CreatedDate\" FROM \"AspNetUsers\" WHERE \"Id\" = $1", userId);
        Assert.Equal(Microseconds.Round(sqlCreated).Ticks, pgCreated.Ticks);
        var sqlLockout = await fx.SqlScalarAsync<DateTimeOffset?>("SELECT LockoutEnd FROM AspNetUsers WHERE Id = @id", ("@id", userId));
        var pgLockout = await fx.PgScalarAsync<DateTime?>("SELECT \"LockoutEnd\" FROM \"AspNetUsers\" WHERE \"Id\" = $1", userId);
        Assert.NotNull(sqlLockout);
        Assert.Equal(Microseconds.Round(sqlLockout.Value.UtcDateTime).Ticks, pgLockout!.Value.Ticks);
        Assert.Null(await fx.PgScalarAsync<string>("SELECT \"ClaimValue\" FROM \"AspNetUserClaims\" WHERE \"ClaimType\" = 'nullable-claim'"));
        Assert.Null(await fx.SqlScalarAsync<string>("SELECT ClaimValue FROM AspNetUserClaims WHERE ClaimType = 'nullable-claim'"));
        Assert.True(await fx.PgScalarAsync<bool>("SELECT \"PhoneNumberConfirmed\" FROM \"AspNetUsers\" WHERE \"Id\" = $1", userId));
        Assert.False(await fx.PgScalarAsync<bool>("SELECT \"TwoFactorEnabled\" FROM \"AspNetUsers\" WHERE \"Id\" = $1", userId));
        await AssertRowIdenticalAsync("OpenIddictTokens", "Id", MonolithSeed.TokenWithNullsId);

        return "Backfill.ConvertValue / DbDiff.Normalize / ReverseSyncPublisher.ConvertValue boundaries hold; seeded 100ns/NULL/bit/datetimeoffset rows equal on both sides";
    });

    // ====================================================================== E5 - backfill during live CDC (variant A only)

    [SkippableFact, Step(84)]
    public Task E5_BackfillDuringLiveCdcConverges() => Step("E5", "Monolith change made mid-backfill (snapshot.mode=no_data) converges idempotently", async () =>
    {
        Skip.IfNot(fx.Options.FullFidelity, "variant A only (set IDENTITY_E2E_FULL_FIDELITY=true): needs Kafka + Kafka Connect");
        Assert.Equal("true", RequireState("e5.written"));

        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        string? pgJobTitle;
        do
        {
            pgJobTitle = await fx.PgScalarAsync<string>("SELECT \"JobTitle\" FROM \"AspNetUsers\" WHERE \"UserName\" = 'admin'");
            if (pgJobTitle == "cdc-live-during-backfill") break;
            await Task.Delay(1000);
        } while (DateTime.UtcNow < deadline);
        Assert.Equal("cdc-live-during-backfill", pgJobTitle);
        Assert.Equal(1L, await fx.PgScalarAsync<long>("SELECT COUNT(*) FROM \"AspNetUsers\" WHERE \"UserName\" = 'admin'"));

        var (diffExit, _) = await RunToolAsync("db-diff", () => new DbDiff(fx.MigrationConfiguration()).RunAsync());
        Gate("E5", "db-diff", diffExit, 0, "after concurrent CDC + backfill");
        return $"admin.JobTitle converged via CDC + backfill without duplicates; db-diff exit {diffExit}";
    });

    // ====================================================================== E6 - deletion-guard cross-context gap

    [Fact, Step(85)]
    public Task E6_DeletionGuardGap() => Step("E6", "No IUserDeletionGuard: DELETE user with orders succeeds (gap); stub guard -> 400 with blockers", async () =>
    {
        var cashierId = RequireState("cashier.id");
        // The user created on Identity after cutover now owns an order in the monolith (cross-context relationship).
        var customerId = await fx.SqlScalarAsync<int>("SELECT TOP 1 Id FROM AppCustomers ORDER BY Id");
        Assert.True(customerId > 0, "monolith demo seed must contain customers");
        await fx.SqlExecAsync("INSERT INTO AppOrders (Discount, Comments, CashierId, CustomerId, CreatedDate, UpdatedDate) VALUES (0, 'e2e cross-context order', @c, @cust, SYSUTCDATETIME(), SYSUTCDATETIME())",
            ("@c", cashierId), ("@cust", customerId));
        Assert.Equal(1, await fx.SqlScalarAsync<int>("SELECT COUNT(*) FROM AppOrders WHERE CashierId = @c", ("@c", cashierId)));

        // (a) current behaviour: Identity.API has no IUserDeletionGuard registered -> the delete is accepted.
        var adminToken = await AccessTokenAsync(fx.IdentityApi!.BaseUrl, "admin", SagaFixture.SeedPassword);
        using var deleted = await SendAsync(HttpMethod.Delete, new Uri(fx.IdentityApi.BaseUrl, $"api/account/users/{cashierId}"), adminToken);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(0L, await fx.PgScalarAsync<long>("SELECT COUNT(*) FROM \"AspNetUsers\" WHERE \"Id\" = $1", cashierId));

        // The tombstone cannot be replayed into the monolith while AppOrders references the cashier: the gap surfaces as a stuck outbox.
        var (drainExit, drainText) = await RunToolAsync("drain-outbox (FK blocked)", () => new OutboxDrain(fx.MigrationConfiguration()).RunAsync());
        Gate("E6", "drain-outbox", drainExit, 2, "user delete accepted by Identity but rejected by monolith FK (AppOrders.CashierId)");
        var stuck = (await fx.PgRowsAsync($"SELECT \"LastError\" FROM \"IdentityOutbox\" WHERE \"PublishedAtUtc\" IS NULL AND \"TableName\" = 'AspNetUsers' AND \"KeyJson\" LIKE '%{cashierId}%'")).Single();
        Assert.Contains("REFERENCE", (string)stuck["LastError"]!, StringComparison.OrdinalIgnoreCase);

        // Manual reconciliation (what the docs' "Known gaps" asks operators to do): remove the dependent rows, then drain.
        await fx.SqlExecAsync("DELETE FROM AppOrders WHERE CashierId = @c", ("@c", cashierId));
        var (afterExit, _) = await RunToolAsync("drain-outbox (reconciled)", () => new OutboxDrain(fx.MigrationConfiguration()).RunAsync());
        Gate("E6", "drain-outbox", afterExit, 0, "after removing the orphaned order");
        Assert.Equal(0, await fx.SqlScalarAsync<int>("SELECT COUNT(*) FROM AspNetUsers WHERE Id = @id", ("@id", cashierId)));

        // (b) with a guard that reports blockers, UserAccountService.TestCanDeleteUserAsync fails and the API returns 400.
        var guardedId = await CreateUserDirectAsync("guarded.user");
        await using var factory = new WebApplicationFactory<Identity.API.Services.SystemUserIdAccessor>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("ConnectionStrings:DefaultConnection", fx.IdentityConnectionString);
                builder.UseSetting("ConnectionStrings:MonolithConnection", fx.MonolithConnectionString);
                builder.UseSetting("Database:MigrateOnStartup", "false");
                builder.UseSetting("OIDC:Issuer", fx.SharedIssuer);
                builder.UseSetting("OIDC:Certificates:Path", fx.SharedPfxPath);
                builder.UseSetting("OIDC:Certificates:Password", SagaFixture.PfxPassword);
                builder.UseSetting("ReverseSync:Enabled", "false");
                builder.UseSetting("Logging:LogLevel:Default", "Warning");
                builder.ConfigureTestServices(services => services.AddScoped<IUserDeletionGuard, OrdersStubDeletionGuard>());
            });
        using var client = factory.CreateClient();
        using var tokenResponse = await client.PostAsync("connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password", ["client_id"] = "quickapp_spa", ["username"] = "admin", ["password"] = SagaFixture.SeedPassword,
            ["scope"] = "openid email phone profile offline_access roles"
        }));
        Assert.True(tokenResponse.IsSuccessStatusCode, $"in-process token: {tokenResponse.StatusCode} {await tokenResponse.Content.ReadAsStringAsync()}");
        var token = System.Text.Json.Nodes.JsonNode.Parse(await tokenResponse.Content.ReadAsStringAsync())!["access_token"]!.GetValue<string>();
        using var guardedRequest = new HttpRequestMessage(HttpMethod.Delete, $"api/account/users/{guardedId}");
        guardedRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var blocked = await client.SendAsync(guardedRequest);
        var body = await blocked.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Contains(OrdersStubDeletionGuard.Blocker, body);
        Assert.Equal(1L, await fx.PgScalarAsync<long>("SELECT COUNT(*) FROM \"AspNetUsers\" WHERE \"Id\" = $1", guardedId));

        var (finalExit, _) = await RunToolAsync("drain-outbox", () => new OutboxDrain(fx.MigrationConfiguration()).RunAsync());
        Assert.Equal(0, finalExit);
        return $"no guard: DELETE -> 200 although the user has an order (drain-outbox exit {drainExit} until reconciled); stub guard: DELETE -> 400 '{OrdersStubDeletionGuard.Blocker}'";
    });

    private sealed class OrdersStubDeletionGuard : IUserDeletionGuard
    {
        public const string Blocker = "User has associated orders";
        public Task<IEnumerable<string>> GetDeletionBlockersAsync(string userId) => Task.FromResult<IEnumerable<string>>([Blocker]);
    }

    // ====================================================================== E7 - schema drift

    [Fact, Step(86)]
    public Task E7_MonolithOnlyColumnSkipped() => Step("E7", "Monolith-only column is skipped by the column intersection in Backfill, DbDiff and reverse sync", async () =>
    {
        Assert.Equal(1, await fx.SqlScalarAsync<int>($"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'AspNetUsers' AND COLUMN_NAME = '{MonolithSeed.DriftColumn}'"));
        Assert.Equal(0L, await fx.PgScalarAsync<long>($"SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'AspNetUsers' AND column_name = '{MonolithSeed.DriftColumn}'"));

        // Backfill (P1c) and every re-run copied all users; the drift column was never referenced.
        var (backfillExit, backfillText) = await RunToolAsync("backfill (drift)", () => new Backfill(fx.MigrationConfiguration()).RunAsync());
        Assert.Equal(0, backfillExit);
        Assert.DoesNotContain(MonolithSeed.DriftColumn, backfillText);

        // Reverse-sync MERGE of the rotated 'user' row only touched payload columns: the monolith-only value is intact.
        var drift = await fx.SqlScalarAsync<Guid?>($"SELECT [{MonolithSeed.DriftColumn}] FROM AspNetUsers WHERE Id = @id", ("@id", RequireState("seed.userId")));
        Assert.NotNull(drift);
        // Users created on Identity have no value for it (nullable) - the MERGE INSERT omitted the column.
        Assert.Null(await fx.SqlScalarAsync<Guid?>($"SELECT [{MonolithSeed.DriftColumn}] FROM AspNetUsers WHERE Id = @id", ("@id", RequireState("p5.userId"))));

        var cdcNote = "";
        if (fx.Options.FullFidelity)
        {
            // The column was added after the capture instances were created (P1a), so SQL Server CDC never emits it and the
            // JDBC sink (schema.evolution=none) keeps running instead of failing with "Cannot alter table".
            await fx.SqlExecAsync($"UPDATE AspNetUsers SET [{MonolithSeed.DriftColumn}] = NEWID(), UpdatedDate = SYSUTCDATETIME() WHERE Id = @id", ("@id", RequireState("seed.dormantId")));
            await WaitForDbDiffZeroAsync("E7 (CDC convergence)", TimeSpan.FromMinutes(2));
            Assert.Equal("RUNNING", await fx.Cdc!.ConnectorStateAsync("identitydb-sink"));
            cdcNote = "; CDC: drift column absent from the capture instance, sink task still RUNNING";
        }

        var (diffExit, _) = await RunToolAsync("db-diff", () => new DbDiff(fx.MigrationConfiguration()).RunAsync());
        Gate("E7", "db-diff", diffExit, 0, "monolith-only column excluded from comparison");
        return $"{MonolithSeed.DriftColumn} exists only in SQL Server, ignored by backfill/diff/MERGE; db-diff exit {diffExit}{cdcNote}";
    });

    // ====================================================================== report

    [Fact, Step(99)]
    public void Z_WriteReport()
    {
        var path = fx.Report.Write();
        output.WriteLine(fx.Report.Render());
        output.WriteLine($"Report written to {path}");
        foreach (var gate in fx.Report.Gates)
            output.WriteLine($"GATE {gate.Phase} {gate.Tool} exit={gate.ExitCode} expected={gate.Expected}");
    }

    // ====================================================================== helpers

    private (ReverseSyncPublisher Publisher, ReverseSyncOptions Options, CapturingLoggerProvider Logs) CreatePublisher(int maxAttempts)
    {
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Debug));
        services.AddScoped<IUserIdAccessor, SagaFixture.TestUserIdAccessor>();
        services.AddDbContext<IdentityDbContext>(o => o.UseNpgsql(fx.IdentityConnectionString).UseOpenIddict());
        var options = new ReverseSyncOptions { Enabled = true, MonolithConnectionString = fx.MonolithConnectionString, MaxAttempts = maxAttempts, BatchSize = 200 };
        services.AddSingleton<IOptionsMonitor<ReverseSyncOptions>>(new StaticOptionsMonitor(options));
        services.AddSingleton<ReverseSyncPublisher>();
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<ReverseSyncPublisher>(), options, logs);
    }

    private async Task<string> CreateUserDirectAsync(string userName)
    {
        await using var db = fx.CreateIdentityDbContext();
        var user = new ApplicationUser { UserName = userName, NormalizedUserName = userName.ToUpperInvariant(), Email = $"{userName}@example.com", NormalizedEmail = $"{userName}@example.com".ToUpperInvariant(), IsEnabled = true, SecurityStamp = Guid.NewGuid().ToString("N") };
        user.PasswordHash = new PasswordHasher<ApplicationUser>().HashPassword(user, "Guarded!Pass123");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static string Trunc(string s) => s.Length <= 80 ? s : s[..77] + "...";

    private sealed class StaticOptionsMonitor(ReverseSyncOptions value) : IOptionsMonitor<ReverseSyncOptions>
    {
        public ReverseSyncOptions CurrentValue => value;
        public ReverseSyncOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<ReverseSyncOptions, string?> listener) => null;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }

        private sealed class Logger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Entries) owner.Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
