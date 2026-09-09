using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using Identity.Domain.Interfaces;
using Identity.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Identity.Migration.E2E.Tests.Harness;

/// <summary>
/// Shared state for the ordered saga test: SQL Server (monolith DB) + Postgres (identitydb) Testcontainers,
/// generated OIDC PFX files, the monolith / Identity.API / ApiGateway processes, a scratch copy of the gateway
/// appsettings.json that scripts/_common.sh mutates, and the optional Kafka/Connect stack (variant A).
/// </summary>
public sealed class SagaFixture : IAsyncLifetime
{
    public const string SaPassword = "YourStrong!Passw0rd";
    public const string SeedPassword = "tempP@ss123";
    public const string PfxPassword = "e2e-shared-pfx";
    public const string SqlServerAlias = "sqlserver";
    public const string PostgresAlias = "postgres";

    public E2EOptions Options { get; } = new();
    public TestReport Report { get; }
    public Dictionary<string, string> State { get; } = new(StringComparer.Ordinal);

    public string ScratchDir { get; }
    public string SharedPfxPath { get; }
    public string OtherPfxPath { get; }
    public string GatewaySettingsPath { get; }

    public MsSqlContainer SqlServer { get; private set; } = null!;
    public PostgreSqlContainer Postgres { get; private set; } = null!;
    public INetwork? Network { get; private set; }
    public CdcStack? Cdc { get; private set; }

    public string MonolithConnectionString { get; private set; } = null!;
    public string IdentityConnectionString { get; private set; } = null!;

    public int MonolithPort { get; } = FreePort();
    public int IdentityPort { get; } = FreePort();
    public int GatewayPort { get; } = FreePort();

    public AppHost? Monolith { get; private set; }
    public AppHost? IdentityApi { get; private set; }
    public AppHost? Gateway { get; private set; }

    public string MonolithDll => Path.Combine(Options.MonolithRepo, "QuickApp.Server", "bin", "Release", "net10.0", "QuickApp.Server.dll");
    public string IdentityApiDll => Path.Combine(Options.RepoRoot, "src", "Services", "Identity", "Identity.API", "bin", Options.BuildConfiguration, "net10.0", "Identity.API.dll");
    public string GatewayDll => Path.Combine(Options.RepoRoot, "src", "ApiGateway", "bin", Options.BuildConfiguration, "net10.0", "ApiGateway.dll");
    public string MigrationDll => Path.Combine(Options.RepoRoot, "src", "Tools", "Identity.Migration", "bin", Options.BuildConfiguration, "net10.0", "Identity.Migration.dll");

    /// <summary>
    /// Public issuer both apps must agree on. The monolith (no OIDC:Issuer setting, HTTPS-only OpenIddict server in
    /// Production) derives it from the request, so Identity.API is configured with the monolith's HTTPS URL.
    /// </summary>
    public string SharedIssuer => $"https://127.0.0.1:{MonolithPort}/";

    public SagaFixture()
    {
        ScratchDir = Path.Combine(Options.RepoRoot, "test", "Identity.Migration.E2E.Tests", "TestResults", $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(ScratchDir);
        Report = new TestReport(Options.Variant, Path.Combine(Options.RepoRoot, "test", "Identity.Migration.E2E.Tests", "TestResults", "identity-migration-e2e-report.md"));
        SharedPfxPath = Path.Combine(ScratchDir, "shared-oidc.pfx");
        OtherPfxPath = Path.Combine(ScratchDir, "other-oidc.pfx");
        GatewaySettingsPath = Path.Combine(ScratchDir, "gateway", "appsettings.json");
    }

    public async Task InitializeAsync()
    {
        WritePfx(SharedPfxPath, "CN=quickapp-e2e-shared");
        WritePfx(OtherPfxPath, "CN=quickapp-e2e-other");

        var sqlBuilder = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").WithPassword(SaPassword);
        var pgBuilder = new PostgreSqlBuilder("postgres:16-alpine").WithDatabase("identitydb").WithUsername("postgres").WithPassword("postgres");

        if (Options.FullFidelity)
        {
            Network = new NetworkBuilder().Build();
            await Network.CreateAsync();
            // SQL Server Agent runs the CDC capture jobs Debezium reads from.
            sqlBuilder = sqlBuilder.WithNetwork(Network).WithNetworkAliases(SqlServerAlias).WithEnvironment("MSSQL_AGENT_ENABLED", "true");
            pgBuilder = pgBuilder.WithNetwork(Network).WithNetworkAliases(PostgresAlias);
        }

        SqlServer = sqlBuilder.Build();
        Postgres = pgBuilder.Build();
        await Task.WhenAll(SqlServer.StartAsync(), Postgres.StartAsync());

        MonolithConnectionString = $"Server={SqlServer.Hostname},{SqlServer.GetMappedPublicPort(MsSqlBuilder.MsSqlPort)};Database=QuickApp;User Id=sa;Password={SaPassword};TrustServerCertificate=True;MultipleActiveResultSets=true";
        IdentityConnectionString = $"Host={Postgres.Hostname};Port={Postgres.GetMappedPublicPort(PostgreSqlBuilder.PostgreSqlPort)};Database=identitydb;Username=postgres;Password=postgres";

        if (Options.FullFidelity)
        {
            Cdc = new CdcStack(Options, Network!, SqlServerAlias, PostgresAlias, SaPassword);
            await Cdc.StartAsync();
        }

        Report.Log($"Monolith DB:  {Redact(MonolithConnectionString)}");
        Report.Log($"identitydb:   {Redact(IdentityConnectionString)}");
        Report.Log($"Variant:      {Options.Variant}");
    }

    public async Task DisposeAsync()
    {
        var reportPath = Report.Write();
        Console.WriteLine(Report.Render());
        Console.WriteLine($"Report written to {reportPath}");

        foreach (var host in new[] { Gateway, IdentityApi, Monolith })
        {
            if (host is null) continue;
            await File.WriteAllTextAsync(Path.Combine(ScratchDir, $"{host.Name}.log"), host.Output);
            await host.DisposeAsync();
        }
        if (Cdc is not null)
        {
            await Cdc.SaveLogsAsync(ScratchDir);
            await Cdc.DisposeAsync();
        }
        await SqlServer.DisposeAsync();
        await Postgres.DisposeAsync();
        if (Network is not null) await Network.DisposeAsync();
    }

    // ---------------------------------------------------------------- configuration for the migration tool classes

    /// <summary>The same shape as src/Tools/Identity.Migration/appsettings.json, pointed at the containers.</summary>
    public IConfiguration MigrationConfiguration(IDictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Monolith"] = MonolithConnectionString,
            ["ConnectionStrings:Identity"] = IdentityConnectionString,
            ["Verify:MonolithBaseUrl"] = Monolith?.BaseUrl.ToString(),
            ["Verify:IdentityBaseUrl"] = IdentityApi?.BaseUrl.ToString(),
            ["Verify:ClientId"] = "quickapp_spa",
            ["Verify:AllowUntrustedTls"] = "true",
            ["Verify:Accounts:0:UserName"] = "admin",
            ["Verify:Accounts:0:PasswordEnv"] = "VERIFY_ADMIN_PASSWORD",
            ["Verify:Accounts:1:UserName"] = "user",
            ["Verify:Accounts:1:PasswordEnv"] = "VERIFY_USER_PASSWORD",
        };
        if (overrides is not null)
            foreach (var (k, v) in overrides) values[k] = v;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    /// <summary>Environment for running the Identity.Migration CLI (scripts use $MIGRATION_TOOL drain-outbox).</summary>
    public Dictionary<string, string?> MigrationToolEnvironment() => new()
    {
        ["ConnectionStrings__Monolith"] = MonolithConnectionString,
        ["ConnectionStrings__Identity"] = IdentityConnectionString,
    };

    public Dictionary<string, string?> ScriptEnvironment(IDictionary<string, string?>? extra = null)
    {
        var env = MigrationToolEnvironment();
        env["GATEWAY_SETTINGS"] = GatewaySettingsPath;
        env["MIGRATION_TOOL"] = $"dotnet {MigrationDll}";
        env["CONNECT_URL"] = Cdc?.ConnectUrl ?? $"http://127.0.0.1:{FreePort()}"; // variant B: Kafka Connect deliberately unreachable
        if (extra is not null)
            foreach (var (k, v) in extra) env[k] = v;
        return env;
    }

    public Task<ShellResult> RunScriptAsync(string scriptRelativePath, IDictionary<string, string?>? extra = null) =>
        Shell.BashAsync($"\"{Path.Combine(Options.RepoRoot, scriptRelativePath)}\"", ScriptEnvironment(extra), Options.RepoRoot);

    public Task<ShellResult> CommonShAsync(string function) =>
        Shell.BashAsync($"source \"{Path.Combine(Options.RepoRoot, "scripts", "_common.sh")}\"; {function}", ScriptEnvironment(), Options.RepoRoot);

    // ---------------------------------------------------------------- database helpers

    public async Task<SqlConnection> OpenSqlAsync()
    {
        var c = new SqlConnection(MonolithConnectionString);
        await c.OpenAsync();
        return c;
    }

    public async Task<NpgsqlConnection> OpenPgAsync()
    {
        var c = new NpgsqlConnection(IdentityConnectionString);
        await c.OpenAsync();
        return c;
    }

    public async Task<T?> SqlScalarAsync<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var c = await OpenSqlAsync();
        await using var cmd = new SqlCommand(sql, c);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    public async Task<int> SqlExecAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var c = await OpenSqlAsync();
        await using var cmd = new SqlCommand(sql, c);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<T?> PgScalarAsync<T>(string sql, params object[] positional)
    {
        await using var c = await OpenPgAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        foreach (var p in positional) cmd.Parameters.Add(new NpgsqlParameter { Value = p });
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    public async Task<int> PgExecAsync(string sql, params object[] positional)
    {
        await using var c = await OpenPgAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        foreach (var p in positional) cmd.Parameters.Add(new NpgsqlParameter { Value = p });
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<Dictionary<string, object?>>> SqlRowsAsync(string sql)
    {
        await using var c = await OpenSqlAsync();
        await using var cmd = new SqlCommand(sql, c);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await ReadRowsAsync(reader);
    }

    public async Task<List<Dictionary<string, object?>>> PgRowsAsync(string sql)
    {
        await using var c = await OpenPgAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await ReadRowsAsync(reader);
    }

    private static async Task<List<Dictionary<string, object?>>> ReadRowsAsync(System.Data.Common.DbDataReader reader)
    {
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    public Task<long> SqlCountAsync(string table) => SqlScalarAsync<int>($"SELECT COUNT(*) FROM [dbo].[{table}]").ContinueWith(t => (long)t.Result);
    public Task<long> PgCountAsync(string table) => PgScalarAsync<long>($"SELECT COUNT(*) FROM \"{table}\"");

    /// <summary>A real IdentityDbContext (the outbox-writing context Identity.API uses) against identitydb.</summary>
    public IdentityDbContext CreateIdentityDbContext(params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(IdentityConnectionString)
            .UseOpenIddict();
        if (interceptors.Length > 0)
            builder.AddInterceptors(interceptors);
        return new IdentityDbContext(builder.Options, new TestUserIdAccessor());
    }

    public sealed class TestUserIdAccessor : IUserIdAccessor
    {
        public string? GetCurrentUserId() => "e2e-test";
    }

    // ---------------------------------------------------------------- processes

    public async Task BuildMonolithAsync()
    {
        var project = Path.Combine(Options.MonolithRepo, "QuickApp.Server", "QuickApp.Server.csproj");
        if (!File.Exists(project))
            throw new FileNotFoundException($"Monolith checkout not found at {Options.MonolithRepo} (set IDENTITY_E2E_MONOLITH_REPO).", project);

        if (!Options.BuildMonolith && File.Exists(MonolithDll))
            return;

        var result = await Shell.RunAsync("dotnet", ["build", project, "-c", "Release", "--nologo", "-v", "q"], timeout: TimeSpan.FromMinutes(20));
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Building the monolith failed:\n{result.Combined}");
    }

    /// <summary>Starts QuickApp.Server (Production, shared PFX). On first start EF migrates + seeds the monolith DB.</summary>
    public async Task<AppHost> StartMonolithAsync(string pfxPath)
    {
        if (Monolith is not null) await Monolith.DisposeAsync();
        Monolith = await AppHost.StartAsync("monolith", MonolithDll, MonolithPort, new Dictionary<string, string?>
        {
            ["ConnectionStrings__DefaultConnection"] = MonolithConnectionString,
            ["OIDC__Certificates__Path"] = pfxPath,
            ["OIDC__Certificates__Password"] = PfxPassword,
            ["Kestrel__Certificates__Default__Path"] = SharedPfxPath,
            ["Kestrel__Certificates__Default__Password"] = PfxPassword,
            ["Logging__LogLevel__Default"] = "Warning",
        }, readinessPath: "api/account/users/me", timeout: TimeSpan.FromMinutes(5), https: true);
        return Monolith;
    }

    public async Task<AppHost> StartIdentityApiAsync(string pfxPath, string issuer, int port, bool reverseSyncEnabled, string name = "identity-api")
    {
        var host = await AppHost.StartAsync(name, IdentityApiDll, port, new Dictionary<string, string?>
        {
            ["ConnectionStrings__DefaultConnection"] = IdentityConnectionString,
            ["ConnectionStrings__MonolithConnection"] = MonolithConnectionString,
            ["Database__MigrateOnStartup"] = "false", // schema + data come from the EF migration (1b) and Backfill (1c)
            ["OIDC__Issuer"] = issuer,
            ["OIDC__Certificates__Path"] = pfxPath,
            ["OIDC__Certificates__Password"] = PfxPassword,
            ["OIDC__Certificates__RequireShared"] = "true",
            ["ReverseSync__Enabled"] = reverseSyncEnabled ? "true" : "false",
            // The hosted publisher drains once at startup and then sleeps; the test drives every later drain through
            // OutboxDrain (what identity-rollback.sh runs) so commit ordering / poison behaviour are deterministic.
            ["ReverseSync__PollIntervalMs"] = "3600000",
            ["Logging__LogLevel__Default"] = "Warning",
        }, readinessPath: "healthz");
        if (name == "identity-api") IdentityApi = host;
        return host;
    }

    /// <summary>Writes a scratch copy of src/ApiGateway/appsettings.json whose strangler metadata points at the local processes.</summary>
    public void WriteGatewaySettings()
    {
        var source = Path.Combine(Options.RepoRoot, "src", "ApiGateway", "appsettings.json");
        var json = JsonNode.Parse(File.ReadAllText(source))!.AsObject();
        var cluster = json["ReverseProxy"]!["Clusters"]!["identity-strangler-cluster"]!;
        cluster["Metadata"]!["MonolithAddress"] = SharedIssuer;
        cluster["Metadata"]!["IdentityServiceAddress"] = $"http://127.0.0.1:{IdentityPort}/";
        cluster["Destinations"]!["active"]!["Address"] = SharedIssuer;
        cluster["HttpClient"] = new JsonObject { ["DangerousAcceptAnyServerCertificate"] = true };
        Directory.CreateDirectory(Path.GetDirectoryName(GatewaySettingsPath)!);
        File.WriteAllText(GatewaySettingsPath, json.ToJsonString(new() { WriteIndented = true }));
    }

    public async Task<AppHost> StartGatewayAsync()
    {
        Gateway = await AppHost.StartAsync("api-gateway", GatewayDll, GatewayPort, new Dictionary<string, string?>(),
            readinessPath: "healthz", extraArgs: ["--contentRoot", Path.GetDirectoryName(GatewaySettingsPath)!]);
        return Gateway;
    }

    // ---------------------------------------------------------------- misc

    private static void WritePfx(string path, string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, PfxPassword));
    }

    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string Redact(string connectionString) =>
        System.Text.RegularExpressions.Regex.Replace(connectionString, "(Password=)[^;]+", "$1***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}

[CollectionDefinition(Name)]
public sealed class SagaCollection : ICollectionFixture<SagaFixture>
{
    public const string Name = "identity-migration-saga";
}
