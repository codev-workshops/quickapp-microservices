using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Identity.Domain.Entities;
using Identity.Infrastructure.Outbox;
using Identity.Migration.E2E.Tests.Harness;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Identity.Migration.E2E.Tests;

public sealed partial class SagaTests
{
    // ====================================================================== PHASE 1 - forward deployment / data migration

    [Fact, Step(10)]
    public Task P1a_SeedMonolithAcrossAllSyncTables() => Step("P1a", "Seed monolith SQL Server DB across all SyncTables.All tables", async () =>
    {
        await fx.BuildMonolithAsync();
        // First start migrates + seeds the monolith DB (admin/user, roles, permission claims, OpenIddict clients, demo shop data).
        await fx.StartMonolithAsync(fx.SharedPfxPath);
        await MonolithSeed.ApplyAsync(fx);

        var counts = new List<string>();
        foreach (var (table, _) in SyncTables.All)
        {
            var count = await fx.SqlCountAsync(table);
            Assert.True(count > 0, $"{table} must be seeded");
            counts.Add($"{table}={count}");
        }

        var ticks = await fx.SqlScalarAsync<DateTime>("SELECT CreatedDate FROM AspNetUsers WHERE UserName = 'user'");
        Assert.NotEqual(0, ticks.Ticks % 10); // sub-microsecond datetime2 precision really present in the source

        if (fx.Options.FullFidelity)
        {
            // Saga step 3/4 (variant A): enable SQL Server CDC and register the Debezium connectors BEFORE the backfill.
            await fx.Cdc!.EnableSqlServerCdcAsync(fx.MonolithConnectionString);
            var registered = await fx.Cdc.RegisterConnectorsAsync();
            Assert.True(registered.ExitCode == 0, $"register-connectors.sh failed: {registered.Combined}");
            Log(registered.StdOut);
            var sourceJson = JsonNode.Parse(File.ReadAllText(Path.Combine(fx.Options.RepoRoot, "src", "cdc", "connectors", "monolith-identity-source.json")))!;
            Assert.Equal("no_data", sourceJson["config"]!["snapshot.mode"]!.GetValue<string>());
        }

        // E7 setup: schema drift arrives after the capture instances exist (variant A) / before the backfill (both).
        await MonolithSeed.AddDriftColumnAsync(fx);
        Assert.NotNull(await fx.SqlScalarAsync<Guid?>($"SELECT TOP 1 [{MonolithSeed.DriftColumn}] FROM AspNetUsers"));

        return string.Join(", ", counts);
    });

    [Fact, Step(11)]
    public Task P1b_BackfillRequiresMigrationThenApplyInitialIdentity() => Step("P1b", "Backfill refuses before EF migration; apply InitialIdentity to identitydb", async () =>
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new Backfill(fx.MigrationConfiguration()).RunAsync());
        Assert.Contains("Apply EF migrations first", ex.Message);
        Log($"Backfill before migration -> {ex.Message}");

        await using (var db = fx.CreateIdentityDbContext())
        {
            await db.Database.MigrateAsync();
            var applied = await db.Database.GetAppliedMigrationsAsync();
            Assert.Contains(applied, m => m.EndsWith("_InitialIdentity", StringComparison.Ordinal));
        }

        foreach (var (table, _) in SyncTables.All)
            Assert.Equal(0, await fx.PgCountAsync(table));
        Assert.Equal(0, await OutboxCountAsync());

        return "InvalidOperationException('Apply EF migrations first') before migration; InitialIdentity applied, all tables empty";
    });

    [Fact, Step(12)]
    public Task P1c_BackfillCopiesRowForRow() => Step("P1c", "Backfill.RunAsync copies every row byte-for-byte; sequences reset to MAX+1", async () =>
    {
        int exitCode;
        if (fx.Options.FullFidelity)
        {
            // E5: forward CDC is live (snapshot.mode=no_data) while the backfill runs; a concurrent monolith write
            // must converge idempotently through both paths.
            await fx.Cdc!.WaitForConnectorsRunningAsync(TimeSpan.FromMinutes(2));
            var backfill = RunToolAsync("backfill", () => new Backfill(fx.MigrationConfiguration()).RunAsync());
            await Task.Delay(200);
            await fx.SqlExecAsync("UPDATE AspNetUsers SET JobTitle = 'cdc-live-during-backfill' WHERE UserName = 'admin'");
            exitCode = (await backfill).ExitCode;
            fx.State["e5.written"] = "true";
        }
        else
        {
            exitCode = (await RunToolAsync("backfill", () => new Backfill(fx.MigrationConfiguration()).RunAsync())).ExitCode;
        }
        Assert.Equal(0, exitCode);

        foreach (var (table, key) in SyncTables.All)
        {
            var sqlCount = await fx.SqlCountAsync(table);
            var pgCount = await fx.PgCountAsync(table);
            Assert.True(sqlCount == pgCount, $"{table}: monolith={sqlCount} identitydb={pgCount}");

            // Primary keys (GUID strings / identity ints / composite) must be identical sets.
            var keyExpr = string.Join(" + '|' + ", key.Select(k => $"CAST([{k}] AS nvarchar(450))"));
            var pgKeyExpr = string.Join(" || '|' || ", key.Select(k => $"\"{k}\"::text"));
            var sqlKeys = (await fx.SqlRowsAsync($"SELECT {keyExpr} AS K FROM [{table}]")).Select(r => (string)r["K"]!).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var pgKeys = (await fx.PgRowsAsync($"SELECT {pgKeyExpr} AS \"K\" FROM \"{table}\"")).Select(r => (string)r["K"]!).OrderBy(k => k, StringComparer.Ordinal).ToList();
            Assert.Equal(sqlKeys, pgKeys);
        }

        // Verbatim invariant (docs invariant 2): credential material is copied byte-for-byte.
        var sqlUsers = await fx.SqlRowsAsync("SELECT Id, PasswordHash, SecurityStamp, ConcurrencyStamp, UserName FROM AspNetUsers ORDER BY Id");
        var pgUsers = await fx.PgRowsAsync("SELECT \"Id\", \"PasswordHash\", \"SecurityStamp\", \"ConcurrencyStamp\", \"UserName\" FROM \"AspNetUsers\" ORDER BY \"Id\"");
        Assert.Equal(sqlUsers.Count, pgUsers.Count);
        for (var i = 0; i < sqlUsers.Count; i++)
            foreach (var column in new[] { "Id", "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "UserName" })
                Assert.True(string.Equals((string?)sqlUsers[i][column], (string?)pgUsers[i][column], StringComparison.Ordinal),
                    $"AspNetUsers.{column} differs for {sqlUsers[i]["UserName"]}");
        await AssertRowIdenticalAsync("AspNetUsers", "Id", RequireState("seed.userId"));
        await AssertRowIdenticalAsync("OpenIddictTokens", "Id", MonolithSeed.TokenWithNullsId);
        await AssertRowIdenticalAsync("OpenIddictAuthorizations", "Id", MonolithSeed.AuthorizationId);

        // Identity sequences: setval(MAX(Id)+1, false) => next nextval() returns MAX+1 without collision.
        var details = new List<string>();
        foreach (var (table, column) in SyncTables.IdentityColumns)
        {
            var max = await fx.PgScalarAsync<int>($"SELECT COALESCE(MAX(\"{column}\"), 0) FROM \"{table}\"");
            var seqName = await fx.PgScalarAsync<string>($"SELECT pg_get_serial_sequence('\"{table}\"', '{column}')");
            var seq = await fx.PgRowsAsync($"SELECT last_value, is_called FROM {seqName}");
            Assert.Single(seq);
            Assert.Equal((long)max + 1, (long)seq[0]["last_value"]!);
            Assert.False((bool)seq[0]["is_called"]!);
            details.Add($"{table}.{column} seq={max + 1}");
        }

        // A subsequent IdentityDbContext insert gets MAX+1 (rolled back so identitydb stays identical for the diff gate).
        await using (var db = fx.CreateIdentityDbContext())
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var maxRoleClaim = await db.RoleClaims.MaxAsync(c => c.Id);
            db.RoleClaims.Add(new IdentityRoleClaim<string> { RoleId = RequireState("seed.userRoleId"), ClaimType = "permission", ClaimValue = "seq.probe" });
            await db.SaveChangesAsync();
            var probe = await db.RoleClaims.SingleAsync(c => c.ClaimValue == "seq.probe");
            Assert.Equal(maxRoleClaim + 1, probe.Id);
            await tx.RollbackAsync();
        }
        Assert.Equal(0, await OutboxCountAsync());

        // E7: the monolith-only column was silently skipped by the source/target column intersection.
        Assert.Equal(0L, await fx.PgScalarAsync<long>($"SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'AspNetUsers' AND column_name = '{MonolithSeed.DriftColumn}'"));

        return $"all {SyncTables.All.Length} tables identical (keys + credential columns); {string.Join(", ", details)}; next IdentityDbContext insert = MAX+1";
    });

    [Fact, Step(13)]
    public Task P1d_DbDiffIsZeroAfterBackfill() => Step("P1d", "DbDiff.RunAsync exit 0 after backfill", async () =>
    {
        if (fx.Options.FullFidelity)
            await WaitForDbDiffZeroAsync("P1d (CDC convergence)", TimeSpan.FromMinutes(2));

        var (exitCode, text) = await RunToolAsync("db-diff", () => new DbDiff(fx.MigrationConfiguration()).RunAsync());
        Gate("Phase 1d", "db-diff", exitCode, 0, "monolith == identitydb after backfill");
        Assert.Contains("Databases are in sync", text);
        return $"db-diff exit {exitCode}";
    });

    // ====================================================================== PHASE 2 - strangler window + reverse sync

    [Fact, Step(20)]
    public Task P2a_GatewayRoutesStranglerCluster() => Step("P2a", "Gateway routes /connect/token + /api/account/** to identity-strangler-cluster (active=Monolith)", async () =>
    {
        var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fx.Options.RepoRoot, "src", "ApiGateway", "appsettings.json")))!;
        var routes = json["ReverseProxy"]!["Routes"]!.AsObject();
        var clusters = json["ReverseProxy"]!["Clusters"]!.AsObject();

        string ClusterFor(string path) => routes.Single(r => r.Value!["Match"]!["Path"]!.GetValue<string>() == path).Value!["ClusterId"]!.GetValue<string>();
        Assert.Equal("identity-strangler-cluster", ClusterFor("/connect/token"));
        Assert.Equal("identity-strangler-cluster", ClusterFor("/api/account/{**catch-all}"));
        Assert.Equal("identity-cluster", ClusterFor("/api/identity/{**catch-all}"));

        var strangler = clusters["identity-strangler-cluster"]!;
        var monolithAddress = strangler["Metadata"]!["MonolithAddress"]!.GetValue<string>();
        Assert.Equal(monolithAddress, strangler["Destinations"]!["active"]!["Address"]!.GetValue<string>());
        Assert.NotEqual(monolithAddress, strangler["Metadata"]!["IdentityServiceAddress"]!.GetValue<string>());

        // The scratch copy the scripts mutate has the same shape; _common.sh must read it back identically.
        fx.WriteGatewaySettings();
        var current = await fx.CommonShAsync("current_destination");
        var meta = await fx.CommonShAsync("cluster_meta MonolithAddress");
        Assert.Equal(0, current.ExitCode);
        Assert.Equal(meta.StdOut.Trim(), current.StdOut.Trim());
        Assert.Equal(fx.SharedIssuer, current.StdOut.Trim());

        return $"token+account routes -> identity-strangler-cluster, active={monolithAddress}; /api/identity/** -> identity-cluster untouched";
    });

    [Fact, Step(21)]
    public Task P2b_EnableReverseSyncWithEmptyOutbox() => Step("P2b", "Identity.API started with ReverseSync enabled; outbox empty", async () =>
    {
        var bound = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseSync:Enabled"] = "true",
            ["ConnectionStrings:MonolithConnection"] = fx.MonolithConnectionString,
        }).Build();
        var options = bound.GetSection(ReverseSyncOptions.SectionName).Get<ReverseSyncOptions>()!;
        options.MonolithConnectionString ??= bound.GetConnectionString("MonolithConnection");
        Assert.True(options.Enabled);
        Assert.Equal(fx.MonolithConnectionString, options.MonolithConnectionString);

        await fx.StartIdentityApiAsync(fx.SharedPfxPath, fx.SharedIssuer, fx.IdentityPort, reverseSyncEnabled: true);
        Assert.Equal(0, await OutboxCountAsync());
        return "ReverseSyncOptions.Enabled=true with MonolithConnection; IdentityOutbox has 0 rows";
    });

    // ====================================================================== PHASE 3 - shadow verification

    [Fact, Step(30)]
    public Task P3a_ShadowVerifyWithSharedCertificate() => Step("P3a", "ShadowVerify exit 0 with shared PFX + identical issuer (same kid, MATCH on cross-validated reads)", async () =>
    {
        await fx.StartGatewayAsync();

        Environment.SetEnvironmentVariable("VERIFY_ADMIN_PASSWORD", SagaFixture.SeedPassword);
        Environment.SetEnvironmentVariable("VERIFY_USER_PASSWORD", SagaFixture.SeedPassword);
        var (exitCode, text) = await RunToolAsync("verify", () => new ShadowVerify(fx.MigrationConfiguration()).RunAsync());
        Log(text);
        Assert.Equal(0, exitCode);
        Assert.Contains("SHARED CERT OK", text);
        Assert.Contains("api/account/users/me", text);
        Assert.Contains("api/account/users ", text);
        Assert.Contains("api/account/roles", text);
        Assert.DoesNotContain("FAIL", text);
        Assert.DoesNotContain("DIFF", text);

        var monolithToken = await AccessTokenAsync(fx.Monolith!.BaseUrl, "admin", SagaFixture.SeedPassword);
        var identityToken = await AccessTokenAsync(fx.IdentityApi!.BaseUrl, "admin", SagaFixture.SeedPassword);
        Assert.Equal(JwtHeader(monolithToken, "kid"), JwtHeader(identityToken, "kid"));

        using var crossA = await SendAsync(HttpMethod.Get, new Uri(fx.Monolith.BaseUrl, "api/account/users/me"), identityToken);
        using var crossB = await SendAsync(HttpMethod.Get, new Uri(fx.IdentityApi.BaseUrl, "api/account/users/me"), monolithToken);
        Assert.Equal(HttpStatusCode.OK, crossA.StatusCode);
        Assert.Equal(HttpStatusCode.OK, crossB.StatusCode);

        return $"verify exit {exitCode}; kid={JwtHeader(monolithToken, "kid")} on both apps; cross-validated reads MATCH";
    });

    [Fact, Step(31)]
    public Task P3b_ShadowVerifyFailsWithDifferentIssuerOrKey() => Step("P3b", "NEGATIVE: different PFX/issuer -> verify reports failure and 401 cross-validation", async () =>
    {
        var port = SagaFixture.FreePort();
        await using var rogue = await fx.StartIdentityApiAsync(fx.OtherPfxPath, $"http://127.0.0.1:{port}/", port, reverseSyncEnabled: false, name: "identity-api-other-key");

        var (exitCode, text) = await RunToolAsync("verify (mismatched)", () =>
            new ShadowVerify(fx.MigrationConfiguration(new Dictionary<string, string?> { ["Verify:IdentityBaseUrl"] = rogue.BaseUrl.ToString() })).RunAsync());
        Log(text);
        Assert.Equal(2, exitCode);
        Assert.Contains("FAIL: different signing/encryption key", text);
        Assert.Contains("401", text);

        var monolithToken = await AccessTokenAsync(fx.Monolith!.BaseUrl, "admin", SagaFixture.SeedPassword);
        using var rejected = await SendAsync(HttpMethod.Get, new Uri(rogue.BaseUrl, "api/account/users/me"), monolithToken);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        return $"verify exit {exitCode} (different kid, 401 on cross-validation) - documents the shared-cert precondition (docs 'Known gaps')";
    });

    // ====================================================================== PHASE 4 - cutover

    [Fact, Step(40)]
    public Task P4a_CutoverRefusedWithoutReverseSync() => Step("P4a", "identity-cutover.sh refuses when REVERSE_SYNC_ENABLED != true", async () =>
    {
        var result = await fx.RunScriptAsync("scripts/identity-cutover.sh", new Dictionary<string, string?> { ["SKIP_VERIFY"] = "true", ["REVERSE_SYNC_ENABLED"] = null });
        Log(result.Combined);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("REFUSING cutover: REVERSE_SYNC_ENABLED is not 'true'", result.StdErr);

        var current = await fx.CommonShAsync("current_destination");
        Assert.Equal(fx.SharedIssuer, current.StdOut.Trim());
        return $"exit {result.ExitCode}; destination still {current.StdOut.Trim()}";
    });

    [Fact, Step(41)]
    public Task P4b_CutoverFlipsToIdentityService() => Step("P4b", "Pause forward sync, flip active destination to IdentityServiceAddress; account traffic served by Identity.API", async () =>
    {
        var details = new List<string>();
        if (fx.Options.FullFidelity)
        {
            var paused = await fx.Cdc!.PauseForwardSyncAsync("pause");
            Assert.Equal(0, paused.ExitCode);
            Assert.Equal("PAUSED", await fx.Cdc.ConnectorStateAsync("identitydb-sink"));
            Assert.Equal("PAUSED", await fx.Cdc.ConnectorStateAsync("monolith-identity-source"));
            details.Add("connectors PAUSED via pause-forward-sync.sh");
        }
        else
        {
            fx.State["forwardSync"] = "paused"; // variant B: no further Backfill.RunAsync until rollback
            details.Add("forward loading (Backfill) stopped");
        }

        fx.State["cutover.utc"] = DateTime.UtcNow.ToString("O");
        var flip = await fx.CommonShAsync("set_destination \"$(cluster_meta IdentityServiceAddress)\"");
        Assert.Equal(0, flip.ExitCode);
        var current = await fx.CommonShAsync("current_destination");
        var expected = (await fx.CommonShAsync("cluster_meta IdentityServiceAddress")).StdOut.Trim();
        Assert.Equal(expected, current.StdOut.Trim());
        Assert.Equal(fx.IdentityApi!.BaseUrl.ToString(), expected);

        // Prove the gateway now serves /api/account from Identity.API: a user created through the gateway lands in
        // identitydb only (reverse sync has not drained yet).
        var adminToken = await AccessTokenAsync(fx.Gateway!.BaseUrl, "admin", SagaFixture.SeedPassword);
        var userName = "cashier.e2e";
        HttpResponseMessage? created = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            created = await SendAsync(HttpMethod.Post, new Uri(fx.Gateway.BaseUrl, "api/account/users"), adminToken, new
            {
                userName, email = $"{userName}@example.com", fullName = "Cashier E2E", jobTitle = "cashier",
                isEnabled = true, newPassword = "Cashier!Pass123", roles = new[] { "user" }
            });
            if (created.StatusCode == HttpStatusCode.Created &&
                await fx.PgScalarAsync<long>("SELECT COUNT(*) FROM \"AspNetUsers\" WHERE \"UserName\" = $1", userName) == 1)
                break;

            if (created.StatusCode == HttpStatusCode.Created)
            {
                // YARP has not reloaded the mutated appsettings.json yet: the monolith served the write. Undo and retry.
                await fx.SqlExecAsync("DELETE FROM AspNetUserRoles WHERE UserId IN (SELECT Id FROM AspNetUsers WHERE UserName = @u); DELETE FROM AspNetUsers WHERE UserName = @u", ("@u", userName));
            }
            await Task.Delay(1000);
        }
        Assert.Equal(HttpStatusCode.Created, created!.StatusCode);
        var cashierId = await fx.PgScalarAsync<string>("SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"UserName\" = $1", userName);
        Assert.NotNull(cashierId);
        Assert.Equal(0, await fx.SqlScalarAsync<int>("SELECT COUNT(*) FROM AspNetUsers WHERE UserName = @u", ("@u", userName)));
        fx.State["cashier.id"] = cashierId;
        Assert.True(await OutboxCountAsync() > 0);

        var viaGateway = await PasswordGrantAsync(fx.Gateway.BaseUrl, userName, "Cashier!Pass123");
        Assert.Equal(200, viaGateway.Status);

        details.Add($"current_destination={current.StdOut.Trim()}; POST api/account/users via gateway -> identitydb only; /connect/token served by Identity.API");
        return string.Join("; ", details);
    });

    // ====================================================================== PHASE 5 - post-cutover writes (fill the outbox)

    [Fact, Step(50)]
    public Task P5a_WritesProduceExactlyOneOutboxRowEach() => Step("P5a", "Each SaveChanges writes exactly one correctly-shaped IdentityOutbox row; commit failure leaves nothing", async () =>
    {
        var userId = RequireState("seed.userId");
        var userRoleId = RequireState("seed.userRoleId");
        var shapes = new List<string>();

        async Task<IdentityOutboxMessage> ExpectOneRowAsync(Func<Task> mutate, string table, OutboxOperation op, params string[] keyColumns)
        {
            var before = await MaxOutboxIdAsync();
            await mutate();
            var rows = await fx.PgRowsAsync($"SELECT \"Id\", \"TableName\", \"Operation\", \"KeyJson\", \"PayloadJson\", \"PublishedAtUtc\", \"Attempts\" FROM \"IdentityOutbox\" WHERE \"Id\" > {before} ORDER BY \"Id\"");
            Assert.True(rows.Count == 1, $"expected exactly one outbox row for {op} {table}, got {rows.Count}");
            var row = rows[0];
            Assert.Equal(table, row["TableName"]);
            Assert.Equal((int)op, row["Operation"]);
            Assert.Null(row["PublishedAtUtc"]);
            Assert.Equal(0, row["Attempts"]);
            var key = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>((string)row["KeyJson"]!)!;
            Assert.Equal(keyColumns.OrderBy(k => k), key.Keys.OrderBy(k => k));
            if (op == OutboxOperation.Delete) Assert.Null(row["PayloadJson"]);
            else Assert.True(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>((string)row["PayloadJson"]!)!.Count >= key.Count);
            shapes.Add($"{op} {table} key={row["KeyJson"]}");
            return new IdentityOutboxMessage { Id = (long)row["Id"]!, TableName = table, Operation = op, KeyJson = (string)row["KeyJson"]!, PayloadJson = (string?)row["PayloadJson"] };
        }

        await using var db = fx.CreateIdentityDbContext();

        var role = new ApplicationRole("e2e-cutover-role", "Created against identitydb after cutover") { NormalizedName = "E2E-CUTOVER-ROLE" };
        await ExpectOneRowAsync(async () => { db.Roles.Add(role); await db.SaveChangesAsync(); }, "AspNetRoles", OutboxOperation.Upsert, "Id");
        fx.State["p5.roleId"] = role.Id;

        var roleClaim = new IdentityRoleClaim<string> { RoleId = role.Id, ClaimType = "permission", ClaimValue = "e2e.cutover" };
        var claimRow = await ExpectOneRowAsync(async () => { db.RoleClaims.Add(roleClaim); await db.SaveChangesAsync(); }, "AspNetRoleClaims", OutboxOperation.Upsert, "Id");
        Assert.Equal(roleClaim.Id, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(claimRow.KeyJson)!["Id"].GetInt32());
        fx.State["p5.roleClaimId"] = roleClaim.Id.ToString();
        fx.State["p5.roleClaimKeyJson"] = claimRow.KeyJson;
        fx.State["p5.roleClaimPayloadJson"] = claimRow.PayloadJson!;

        var newUser = new ApplicationUser { UserName = "cutover.user", Email = "cutover.user@example.com", FullName = "Cutover User", IsEnabled = true, SecurityStamp = Guid.NewGuid().ToString("N") };
        newUser.NormalizedUserName = newUser.UserName.ToUpperInvariant();
        newUser.NormalizedEmail = newUser.Email.ToUpperInvariant();
        newUser.PasswordHash = new PasswordHasher<ApplicationUser>().HashPassword(newUser, "Cutover!Pass123");
        await ExpectOneRowAsync(async () => { db.Users.Add(newUser); await db.SaveChangesAsync(); }, "AspNetUsers", OutboxOperation.Upsert, "Id");
        fx.State["p5.userId"] = newUser.Id;

        await ExpectOneRowAsync(async () => { db.UserClaims.Add(new IdentityUserClaim<string> { UserId = newUser.Id, ClaimType = "department", ClaimValue = "migration" }); await db.SaveChangesAsync(); },
            "AspNetUserClaims", OutboxOperation.Upsert, "Id");
        await ExpectOneRowAsync(async () => { db.UserRoles.Add(new IdentityUserRole<string> { UserId = newUser.Id, RoleId = role.Id }); await db.SaveChangesAsync(); },
            "AspNetUserRoles", OutboxOperation.Upsert, "UserId", "RoleId");

        await ExpectOneRowAsync(async () => { role.Description = "Updated after cutover"; await db.SaveChangesAsync(); }, "AspNetRoles", OutboxOperation.Upsert, "Id");

        var seededClaim = await db.RoleClaims.SingleAsync(c => c.RoleId == userRoleId && c.ClaimValue == "e2e.readonly");
        fx.State["p5.deletedRoleClaimId"] = seededClaim.Id.ToString();
        await ExpectOneRowAsync(async () => { db.RoleClaims.Remove(seededClaim); await db.SaveChangesAsync(); }, "AspNetRoleClaims", OutboxOperation.Delete, "Id");

        var doomed = new ApplicationUser { UserName = "todelete.user", NormalizedUserName = "TODELETE.USER", Email = "todelete@example.com", NormalizedEmail = "TODELETE@EXAMPLE.COM", IsEnabled = true, SecurityStamp = Guid.NewGuid().ToString("N") };
        await ExpectOneRowAsync(async () => { db.Users.Add(doomed); await db.SaveChangesAsync(); }, "AspNetUsers", OutboxOperation.Upsert, "Id");
        await ExpectOneRowAsync(async () => { db.Users.Remove(doomed); await db.SaveChangesAsync(); }, "AspNetUsers", OutboxOperation.Delete, "Id");
        fx.State["p5.deletedUserId"] = doomed.Id;

        // Refresh token + authorization through Identity.API (served by the gateway now).
        var before = await MaxOutboxIdAsync();
        var grant = await PasswordGrantAsync(fx.Gateway!.BaseUrl, "user", SagaFixture.SeedPassword);
        Assert.Equal(200, grant.Status);
        Assert.NotNull(grant.Body!["refresh_token"]);
        var tokenRows = await fx.PgRowsAsync($"SELECT \"TableName\", \"Operation\", \"KeyJson\" FROM \"IdentityOutbox\" WHERE \"Id\" > {before} ORDER BY \"Id\"");
        Assert.Contains(tokenRows, r => (string)r["TableName"]! == "OpenIddictAuthorizations" && (int)r["Operation"]! == (int)OutboxOperation.Upsert);
        Assert.Contains(tokenRows, r => (string)r["TableName"]! == "OpenIddictTokens" && (int)r["Operation"]! == (int)OutboxOperation.Upsert);
        Assert.All(tokenRows, r => Assert.Contains("\"Id\"", (string)r["KeyJson"]!));
        shapes.Add($"password grant -> {tokenRows.Count} rows ({string.Join(",", tokenRows.Select(r => r["TableName"]).Distinct())})");

        // Credential change on Identity for 7c: admin resets 'user' password through the gateway.
        var adminToken = await AccessTokenAsync(fx.Gateway.BaseUrl, "admin", SagaFixture.SeedPassword);
        using var me = await SendAsync(HttpMethod.Get, new Uri(fx.Gateway.BaseUrl, $"api/account/users/{userId}"), adminToken);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var userVm = JsonNode.Parse(await me.Content.ReadAsStringAsync())!.AsObject();
        userVm["newPassword"] = "Rotated!Pass456";
        userVm["jobTitle"] = "rotated-on-identity";
        userVm.Remove("isLockedOut");
        using var updated = await SendAsync(HttpMethod.Put, new Uri(fx.Gateway.BaseUrl, $"api/account/users/{userId}"), adminToken, userVm);
        Assert.True(updated.StatusCode == HttpStatusCode.NoContent, $"PUT users/{userId} -> {updated.StatusCode} {await updated.Content.ReadAsStringAsync()}");
        fx.State["p5.rotatedPassword"] = "Rotated!Pass456";
        Assert.Equal(200, (await PasswordGrantAsync(fx.Gateway.BaseUrl, "user", "Rotated!Pass456")).Status);
        Assert.Equal(400, (await PasswordGrantAsync(fx.Gateway.BaseUrl, "user", SagaFixture.SeedPassword)).Status);
        // Not yet reverse-synced: the monolith still authenticates the OLD password.
        Assert.Equal(200, (await PasswordGrantAsync(fx.Monolith!.BaseUrl, "user", SagaFixture.SeedPassword)).Status);

        // Atomicity: the outbox INSERT happens in the same transaction as the entity change. Failing the second
        // SaveChanges (the outbox write) must roll back the entity too.
        var outboxBefore = await OutboxCountAsync();
        await using (var failing = fx.CreateIdentityDbContext(new FailOutboxCommitInterceptor()))
        {
            failing.Roles.Add(new ApplicationRole("e2e-atomic-role", "must not persist"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing.SaveChangesAsync());
        }
        Assert.Equal(0L, await fx.PgScalarAsync<long>("SELECT COUNT(*) FROM \"AspNetRoles\" WHERE \"Name\" = 'e2e-atomic-role'"));
        Assert.Equal(outboxBefore, await OutboxCountAsync());

        fx.State["p5.outboxTotal"] = (await OutboxCountAsync()).ToString();
        return $"{shapes.Count} mutations, 1 outbox row each: {string.Join(" | ", shapes)}; atomicity: failed outbox commit -> no role, no outbox row";
    });

    /// <summary>Throws while IdentityDbContext writes its outbox rows (second SaveChanges inside the ambient transaction).</summary>
    private sealed class FailOutboxCommitInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var entries = eventData.Context!.ChangeTracker.Entries().ToList();
            if (entries.Count > 0 && entries.All(e => e.Entity is IdentityOutboxMessage || e.State == EntityState.Unchanged))
                throw new InvalidOperationException("simulated outbox commit failure");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    // ====================================================================== PHASE 6 - rollback

    [Fact, Step(60)]
    public Task P6b_RollbackRefusesWhileOutboxNonEmpty() => Step("P6b", "identity-rollback.sh does not flip the route while the outbox cannot be drained (unless FORCE=true)", async () =>
    {
        // A row the publisher cannot replay keeps the outbox non-empty (drain-outbox exit 2).
        await fx.PgExecAsync("""
            INSERT INTO "IdentityOutbox" ("TableName", "Operation", "KeyJson", "PayloadJson", "OccurredAtUtc", "Attempts")
            VALUES ('NotASyncedTable', 1, '{"Id":"blocker"}', '{"Id":"blocker"}', now() at time zone 'utc', 0)
            """);

        var refused = await fx.RunScriptAsync("scripts/identity-rollback.sh");
        Log(refused.Combined);
        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains("Outbox is not empty / has poisoned rows", refused.StdErr);
        var current = await fx.CommonShAsync("current_destination");
        Assert.Equal(fx.IdentityApi!.BaseUrl.ToString(), current.StdOut.Trim());
        var blocker = (await fx.PgRowsAsync("SELECT \"Attempts\", \"LastError\", \"PublishedAtUtc\" FROM \"IdentityOutbox\" WHERE \"TableName\" = 'NotASyncedTable'")).Single();
        Assert.True((int)blocker["Attempts"]! >= 1);
        Assert.NotNull(blocker["LastError"]);
        Assert.Null(blocker["PublishedAtUtc"]);

        var forced = await fx.RunScriptAsync("scripts/identity-rollback.sh", new Dictionary<string, string?> { ["FORCE"] = "true" });
        Log(forced.Combined);
        Assert.Equal(0, forced.ExitCode);
        Assert.Equal(fx.SharedIssuer, (await fx.CommonShAsync("current_destination")).StdOut.Trim());

        // Undo the forced flip so the saga continues from the cut-over state, and reconcile the blocker manually.
        await fx.CommonShAsync("set_destination \"$(cluster_meta IdentityServiceAddress)\"");
        if (fx.Options.FullFidelity) await fx.Cdc!.PauseForwardSyncAsync("pause");
        await fx.PgExecAsync("DELETE FROM \"IdentityOutbox\" WHERE \"TableName\" = 'NotASyncedTable'");

        return $"without FORCE: exit {refused.ExitCode}, route unchanged (blocker Attempts={blocker["Attempts"]}, LastError='{((string)blocker["LastError"]!)[..Math.Min(60, ((string)blocker["LastError"]!).Length)]}'); FORCE=true: exit 0 and route flipped";
    });

    [Fact, Step(61)]
    public Task P6a_RollbackSequence() => Step("P6a", "Rollback: drain-outbox to exit 0 (pending=0, poisoned=0) THEN flip to MonolithAddress THEN resume forward CDC", async () =>
    {
        var (drainExit, drainText) = await RunToolAsync("drain-outbox", () => new OutboxDrain(fx.MigrationConfiguration()).RunAsync());
        Gate("Phase 6a", "drain-outbox", drainExit, 0, "outbox drained before route flip");
        Assert.Contains("Pending=0 Skipped-after-max-attempts=0", drainText);
        Assert.Equal(0, await OutboxCountAsync(unpublishedOnly: true));
        Assert.Equal(0L, await fx.PgScalarAsync<long>("SELECT COUNT(*) FROM \"IdentityOutbox\" WHERE \"PublishedAtUtc\" IS NOT NULL AND \"LastError\" IS NOT NULL AND \"Attempts\" >= 25"));

        var rollback = await fx.RunScriptAsync("scripts/identity-rollback.sh");
        Log(rollback.Combined);
        Assert.Equal(0, rollback.ExitCode);
        var lines = rollback.StdOut.Split('\n');
        Assert.True(Array.FindIndex(lines, l => l.Contains("draining reverse-sync outbox")) < Array.FindIndex(lines, l => l.Contains("flipping gateway route back")));
        Assert.True(Array.FindIndex(lines, l => l.Contains("flipping gateway route back")) < Array.FindIndex(lines, l => l.Contains("resuming forward CDC")));
        Assert.Equal(fx.SharedIssuer, (await fx.CommonShAsync("current_destination")).StdOut.Trim());

        var details = $"drain-outbox exit {drainExit}; identity-rollback.sh exit 0; destination={fx.SharedIssuer}";
        if (fx.Options.FullFidelity)
        {
            Assert.Equal("RUNNING", await fx.Cdc!.ConnectorStateAsync("identitydb-sink"));
            Assert.Equal("RUNNING", await fx.Cdc.ConnectorStateAsync("monolith-identity-source"));
            details += "; connectors RUNNING again";
        }
        else
        {
            Assert.Contains("Kafka Connect unreachable", rollback.StdErr);
            fx.State["forwardSync"] = "running";
            details += "; forward loading resumed (variant B)";
        }
        return details;
    });

    // ====================================================================== PHASE 7 - no-data-loss confirmation

    [Fact, Step(70)]
    public Task P7a_DbDiffZeroAfterRollback() => Step("P7a", "DbDiff exit 0 after rollback: all Identity-side writes present in the monolith", async () =>
    {
        if (fx.Options.FullFidelity)
        {
            await WaitForDbDiffZeroAsync("P7a (CDC convergence)", TimeSpan.FromMinutes(2));
        }
        else
        {
            Assert.Equal("running", RequireState("forwardSync"));
            // Variant B: emulate the resumed forward CDC (monolith -> identitydb) with an idempotent backfill.
            var (backfillExit, _) = await RunToolAsync("backfill (convergence)", () => new Backfill(fx.MigrationConfiguration()).RunAsync());
            Assert.Equal(0, backfillExit);
        }

        var (exitCode, text) = await RunToolAsync("db-diff", () => new DbDiff(fx.MigrationConfiguration()).RunAsync());
        Gate("Phase 7a", "db-diff", exitCode, 0, "no data loss after rollback");
        Assert.Contains("Databases are in sync", text);

        Assert.Equal(1, await fx.SqlScalarAsync<int>("SELECT COUNT(*) FROM AspNetRoles WHERE Id = @id", ("@id", RequireState("p5.roleId"))));
        Assert.Equal(1, await fx.SqlScalarAsync<int>("SELECT COUNT(*) FROM AspNetUsers WHERE Id = @id", ("@id", RequireState("p5.userId"))));
        Assert.Equal(1, await fx.SqlScalarAsync<int>("SELECT COUNT(*) FROM AspNetUsers WHERE Id = @id", ("@id", RequireState("cashier.id"))));
        Assert.Equal(1, await fx.SqlScalarAsync<int>("SELECT COUNT(*) FROM AspNetRoleClaims WHERE Id = @id", ("@id", int.Parse(RequireState("p5.roleClaimId")))));
        var cutover = DateTime.Parse(RequireState("cutover.utc"), null, System.Globalization.DateTimeStyles.RoundtripKind);
        var postCutoverTokens = await fx.SqlRowsAsync($"SELECT Type, COUNT(*) AS N FROM OpenIddictTokens WHERE Subject = '{RequireState("seed.userId")}' AND CreationDate > '{cutover:yyyy-MM-dd HH:mm:ss}' GROUP BY Type");
        Assert.True(postCutoverTokens.Sum(r => (int)r["N"]!) >= 2, "tokens issued by Identity.API after cutover must have been reverse-synced into the monolith");
        return $"db-diff exit {exitCode}; Phase 5 role/user/claim present in SQL Server; post-cutover tokens for 'user' in monolith: {string.Join(", ", postCutoverTokens.Select(r => $"{r["Type"]}={r["N"]}"))}";
    });

    [Fact, Step(71)]
    public Task P7b_RowLevelCreatesPresentDeletesAbsent() => Step("P7b", "Row-level: Phase 5 creates identical in monolith (MERGE); deletes absent (DELETE tombstones)", async () =>
    {
        await AssertRowIdenticalAsync("AspNetRoles", "Id", RequireState("p5.roleId"));
        await AssertRowIdenticalAsync("AspNetUsers", "Id", RequireState("p5.userId"));
        await AssertRowIdenticalAsync("AspNetUsers", "Id", RequireState("cashier.id"));
        await AssertRowIdenticalAsync("AspNetUsers", "Id", RequireState("seed.userId"));
        await AssertRowIdenticalAsync("AspNetRoleClaims", "Id", RequireState("p5.roleClaimId"));
        Assert.Equal("Updated after cutover", await fx.SqlScalarAsync<string>("SELECT Description FROM AspNetRoles WHERE Id = @id", ("@id", RequireState("p5.roleId"))));
        Assert.Equal(1, await fx.SqlScalarAsync<int>("SELECT COUNT(*) FROM AspNetUserRoles WHERE UserId = @u AND RoleId = @r", ("@u", RequireState("p5.userId")), ("@r", RequireState("p5.roleId"))));
        Assert.Equal(1, await fx.SqlScalarAsync<int>("SELECT COUNT(*) FROM AspNetUserClaims WHERE UserId = @u AND ClaimValue = 'migration'", ("@u", RequireState("p5.userId"))));

        Assert.Equal(0, await fx.SqlScalarAsync<int>("SELECT COUNT(*) FROM AspNetRoleClaims WHERE Id = @id", ("@id", int.Parse(RequireState("p5.deletedRoleClaimId")))));
        Assert.Equal(0, await fx.SqlScalarAsync<int>("SELECT COUNT(*) FROM AspNetUsers WHERE Id = @id", ("@id", RequireState("p5.deletedUserId"))));
        Assert.Equal(0L, await fx.PgScalarAsync<long>("SELECT COUNT(*) FROM \"AspNetUsers\" WHERE \"Id\" = $1", RequireState("p5.deletedUserId")));
        return "creates identical column-for-column; deleted role claim + deleted user absent on both sides";
    });

    [Fact, Step(72)]
    public Task P7c_CredentialContinuityAfterRollback() => Step("P7c", "Monolith password grant succeeds with the password rotated on Identity.API (PasswordHash/SecurityStamp survived)", async () =>
    {
        var userId = RequireState("seed.userId");
        var rotated = RequireState("p5.rotatedPassword");

        var sqlUser = (await fx.SqlRowsAsync($"SELECT PasswordHash, SecurityStamp, ConcurrencyStamp, JobTitle FROM AspNetUsers WHERE Id = '{userId}'")).Single();
        var pgUser = (await fx.PgRowsAsync($"SELECT \"PasswordHash\", \"SecurityStamp\", \"ConcurrencyStamp\", \"JobTitle\" FROM \"AspNetUsers\" WHERE \"Id\" = '{userId}'")).Single();
        foreach (var column in new[] { "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "JobTitle" })
            Assert.Equal((string?)pgUser[column], (string?)sqlUser[column]);
        Assert.Equal("rotated-on-identity", sqlUser["JobTitle"]);

        var viaMonolith = await PasswordGrantAsync(fx.Monolith!.BaseUrl, "user", rotated);
        Assert.Equal(200, viaMonolith.Status);
        var viaGateway = await PasswordGrantAsync(fx.Gateway!.BaseUrl, "user", rotated); // gateway is back on the monolith
        Assert.Equal(200, viaGateway.Status);
        Assert.Equal(400, (await PasswordGrantAsync(fx.Monolith.BaseUrl, "user", SagaFixture.SeedPassword)).Status);

        var token = viaGateway.Body!["access_token"]!.GetValue<string>();
        using var me = await SendAsync(HttpMethod.Get, new Uri(fx.Gateway.BaseUrl, "api/account/users/me"), token);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        return "monolith issues a token for the Identity-rotated password; old password rejected; PasswordHash/SecurityStamp identical";
    });

    private async Task WaitForDbDiffZeroAsync(string label, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var (exitCode, text) = await RunToolAsync($"db-diff poll {label}", () => new DbDiff(fx.MigrationConfiguration()).RunAsync());
            if (exitCode == 0) return;
            if (DateTime.UtcNow > deadline)
            {
                var diagnostics = fx.Cdc is null ? "" : "\n" + await fx.Cdc.DiagnosticsAsync();
                throw new TimeoutException($"{label}: db-diff still {exitCode} after {timeout}:\n{text}{diagnostics}");
            }
            await Task.Delay(2000);
        }
    }
}
