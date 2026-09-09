using Microsoft.Data.SqlClient;

namespace Identity.Migration.E2E.Tests.Harness;

/// <summary>
/// Extends the monolith's own DatabaseSeeder output (admin + user, administrator/user roles with permission
/// claims, quickapp_spa / swagger_ui OpenIddict clients, demo shop data) so that EVERY table in SyncTables.All
/// has rows, including NULL columns, sub-microsecond datetime2 ticks, datetimeoffset, bit, composite keys and a
/// monolith-only uniqueidentifier column (schema drift, E7).
/// </summary>
public static class MonolithSeed
{
    public const string DriftColumn = "LegacyTenantId";
    public const string ScopeId = "e2e-scope-0001";
    public const string AuthorizationId = "e2e-authz-0001";
    public const string TokenId = "e2e-token-0001";
    public const string TokenWithNullsId = "e2e-token-0002";
    public const string SubMicrosecondCreated = "2026-01-02T03:04:05.1234567";

    public static async Task ApplyAsync(SagaFixture fx)
    {
        var userId = await fx.SqlScalarAsync<string>("SELECT Id FROM AspNetUsers WHERE UserName = 'user'")
                     ?? throw new InvalidOperationException("monolith seed did not create 'user'");
        var adminId = await fx.SqlScalarAsync<string>("SELECT Id FROM AspNetUsers WHERE UserName = 'admin'")
                      ?? throw new InvalidOperationException("monolith seed did not create 'admin'");
        var userRoleId = await fx.SqlScalarAsync<string>("SELECT Id FROM AspNetRoles WHERE Name = 'user'")
                         ?? throw new InvalidOperationException("monolith seed did not create role 'user'");
        var spaAppId = await fx.SqlScalarAsync<string>("SELECT Id FROM OpenIddictApplications WHERE ClientId = 'quickapp_spa'")
                       ?? throw new InvalidOperationException("monolith seed did not register quickapp_spa");

        fx.State["seed.userId"] = userId;
        fx.State["seed.adminId"] = adminId;
        fx.State["seed.userRoleId"] = userRoleId;
        fx.State["seed.spaAppId"] = spaAppId;

        await using var sql = await fx.OpenSqlAsync();

        // Type/precision boundaries on an existing user: NULL, datetime2(7) with 100ns ticks, datetimeoffset, bit.
        await Exec(sql, $"""
            UPDATE dbo.AspNetUsers
               SET JobTitle = NULL,
                   Configuration = NULL,
                   CreatedDate = '{SubMicrosecondCreated}',
                   LockoutEnd = '2020-01-01 00:00:00.1234567 +02:00',
                   LockoutEnabled = 1,
                   PhoneNumberConfirmed = 1,
                   TwoFactorEnabled = 0,
                   AccessFailedCount = 3
             WHERE Id = @id
            """, ("@id", userId));

        await Exec(sql, "INSERT INTO dbo.AspNetRoleClaims (RoleId, ClaimType, ClaimValue) VALUES (@r, 'permission', 'e2e.readonly')", ("@r", userRoleId));

        // A dormant legacy account (never logs in): carries the NULL-valued claim, which ASP.NET Identity cannot
        // materialise into a System.Security.Claims.Claim for a signing-in user, but which the sync must still copy.
        var dormantId = Guid.NewGuid().ToString();
        await Exec(sql, $"""
            INSERT INTO dbo.AspNetUsers (Id, UserName, NormalizedUserName, Email, NormalizedEmail, EmailConfirmed, PasswordHash, SecurityStamp,
                                         ConcurrencyStamp, PhoneNumber, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnd, LockoutEnabled, AccessFailedCount,
                                         FullName, JobTitle, Configuration, IsEnabled, CreatedBy, UpdatedBy, CreatedDate, UpdatedDate)
            SELECT '{dormantId}', 'dormant.legacy', 'DORMANT.LEGACY', 'dormant.legacy@example.com', 'DORMANT.LEGACY@EXAMPLE.COM',
                   EmailConfirmed, PasswordHash, CAST(NEWID() AS nvarchar(64)), CAST(NEWID() AS nvarchar(64)), NULL, 0, 0, NULL, 1, 0,
                   'Dormant Legacy', 'archived', Configuration, 0, CreatedBy, UpdatedBy, CreatedDate, UpdatedDate
              FROM dbo.AspNetUsers WHERE Id = @u
            """, ("@u", userId));
        fx.State["seed.dormantId"] = dormantId;

        await Exec(sql, """
            INSERT INTO dbo.AspNetUserClaims (UserId, ClaimType, ClaimValue) VALUES
              (@u, 'department', 'engineering'),
              (@d, 'nullable-claim', NULL)
            """, ("@u", userId), ("@d", dormantId));

        await Exec(sql, """
            INSERT INTO dbo.AspNetUserLogins (LoginProvider, ProviderKey, ProviderDisplayName, UserId) VALUES
              ('Google', 'google-e2e-1', 'Google', @u),
              ('Google', 'google-e2e-2', NULL, @a)
            """, ("@u", userId), ("@a", adminId));

        await Exec(sql, """
            INSERT INTO dbo.AspNetUserTokens (UserId, LoginProvider, Name, Value) VALUES
              (@u, '[AspNetUserStore]', 'AuthenticatorKey', 'E2EAUTHKEY'),
              (@u, '[AspNetUserStore]', 'RecoveryCodes', NULL)
            """, ("@u", userId));

        await Exec(sql, """
            INSERT INTO dbo.OpenIddictScopes (Id, ConcurrencyToken, Description, Descriptions, DisplayName, DisplayNames, Name, Properties, Resources)
            VALUES (@id, @ct, 'E2E scope', NULL, 'Inventory', NULL, 'inventory', NULL, '["inventory_api"]')
            """, ("@id", ScopeId), ("@ct", Guid.NewGuid().ToString()));

        await Exec(sql, $"""
            INSERT INTO dbo.OpenIddictAuthorizations (Id, ApplicationId, ConcurrencyToken, CreationDate, Properties, Scopes, Status, Subject, Type)
            VALUES (@id, @app, @ct, '{SubMicrosecondCreated}', NULL, '["openid","offline_access"]', 'valid', @u, 'permanent')
            """, ("@id", AuthorizationId), ("@app", spaAppId), ("@ct", Guid.NewGuid().ToString()), ("@u", userId));

        await Exec(sql, $"""
            INSERT INTO dbo.OpenIddictTokens (Id, ApplicationId, AuthorizationId, ConcurrencyToken, CreationDate, ExpirationDate, Payload, Properties, RedemptionDate, ReferenceId, Status, Subject, Type)
            VALUES
              (@t1, @app, @authz, @ct1, '{SubMicrosecondCreated}', '2036-01-02T03:04:05.7654321', 'opaque-payload', NULL, NULL, 'ref-e2e-1', 'valid', @u, 'refresh_token'),
              (@t2, NULL, NULL, @ct2, NULL, NULL, NULL, NULL, NULL, NULL, 'redeemed', @u, 'access_token')
            """, ("@t1", TokenId), ("@t2", TokenWithNullsId), ("@app", spaAppId), ("@authz", AuthorizationId),
            ("@ct1", Guid.NewGuid().ToString()), ("@ct2", Guid.NewGuid().ToString()), ("@u", userId));
    }

    /// <summary>
    /// E7: monolith-only column (uniqueidentifier) that identitydb does not have. In variant A it is added AFTER the CDC
    /// capture instances exist, which is how schema drift reaches a live pipeline: SQL Server CDC does not track columns
    /// added to an existing capture instance, so the JDBC sink never sees them (schema.evolution=none would kill it).
    /// </summary>
    public static async Task AddDriftColumnAsync(SagaFixture fx)
    {
        await using var sql = await fx.OpenSqlAsync();
        await Exec(sql, $"IF COL_LENGTH('dbo.AspNetUsers', '{DriftColumn}') IS NULL ALTER TABLE dbo.AspNetUsers ADD [{DriftColumn}] uniqueidentifier NULL");
        await Exec(sql, $"UPDATE dbo.AspNetUsers SET [{DriftColumn}] = NEWID()");
    }

    private static async Task Exec(SqlConnection sql, string text, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = new SqlCommand(text, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }
}
