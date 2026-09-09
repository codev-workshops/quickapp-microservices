using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using Microsoft.Data.SqlClient;

namespace Identity.Migration.E2E.Tests.Harness;

/// <summary>
/// Variant A forward-sync infrastructure, mirroring the `migration` profile of src/docker-compose.yml:
/// Kafka (KRaft) + Kafka Connect built from src/cdc/Dockerfile.connect (Debezium SQL Server source, Debezium JDBC sink).
/// Connectors are registered with the repo's own src/cdc/register-connectors.sh (snapshot.mode=no_data) and
/// paused/resumed with src/cdc/pause-forward-sync.sh, exactly as identity-cutover.sh / identity-rollback.sh do.
/// </summary>
public sealed class CdcStack(E2EOptions options, INetwork network, string sqlServerAlias, string postgresAlias, string saPassword) : IAsyncDisposable
{
    public const string KafkaAlias = "kafka";

    private IContainer? kafka;
    private IContainer? connect;
    private IFutureDockerImage? connectImage;

    public string ConnectUrl => $"http://{connect!.Hostname}:{connect.GetMappedPublicPort(8083)}";

    public async Task StartAsync()
    {
        kafka = new ContainerBuilder("apache/kafka:3.9.0")
            .WithNetwork(network)
            .WithNetworkAliases(KafkaAlias)
            .WithEnvironment("KAFKA_NODE_ID", "1")
            .WithEnvironment("KAFKA_PROCESS_ROLES", "broker,controller")
            .WithEnvironment("KAFKA_LISTENERS", "PLAINTEXT://:9092,CONTROLLER://:9093")
            .WithEnvironment("KAFKA_ADVERTISED_LISTENERS", $"PLAINTEXT://{KafkaAlias}:9092")
            .WithEnvironment("KAFKA_CONTROLLER_LISTENER_NAMES", "CONTROLLER")
            .WithEnvironment("KAFKA_CONTROLLER_QUORUM_VOTERS", $"1@{KafkaAlias}:9093")
            .WithEnvironment("KAFKA_LISTENER_SECURITY_PROTOCOL_MAP", "CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT")
            .WithEnvironment("KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR", "1")
            .WithEnvironment("KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR", "1")
            .WithEnvironment("KAFKA_TRANSACTION_STATE_LOG_MIN_ISR", "1")
            .WithEnvironment("CLUSTER_ID", "identity-migration-cluster-0001")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Kafka Server started"))
            .Build();
        await kafka.StartAsync();

        var imageBuilder = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(Path.Combine(options.RepoRoot, "src", "cdc"))
            .WithDockerfile("Dockerfile.connect")
            .WithName("identity-e2e/debezium-connect:latest")
            .WithDeleteIfExists(false);
        if (options.MavenRepo is { Length: > 0 })
            imageBuilder = imageBuilder.WithBuildArgument("MAVEN_REPO", options.MavenRepo);
        connectImage = imageBuilder.Build();
        await connectImage.CreateAsync();

        connect = new ContainerBuilder(connectImage)
            .WithNetwork(network)
            .WithNetworkAliases("connect")
            .WithPortBinding(8083, true)
            .WithEnvironment("BOOTSTRAP_SERVERS", $"{KafkaAlias}:9092")
            .WithEnvironment("GROUP_ID", "identity-migration")
            .WithEnvironment("CONFIG_STORAGE_TOPIC", "connect-configs")
            .WithEnvironment("OFFSET_STORAGE_TOPIC", "connect-offsets")
            .WithEnvironment("STATUS_STORAGE_TOPIC", "connect-status")
            .WithEnvironment("CONNECT_CONFIG_PROVIDERS", "env")
            .WithEnvironment("CONNECT_CONFIG_PROVIDERS_ENV_CLASS", "org.apache.kafka.common.config.provider.EnvVarConfigProvider")
            .WithEnvironment("MONOLITH_DB_HOST", sqlServerAlias)
            .WithEnvironment("MONOLITH_DB_PORT", "1433")
            .WithEnvironment("MONOLITH_DB_USER", "sa")
            .WithEnvironment("MONOLITH_DB_PASSWORD", saPassword)
            .WithEnvironment("IDENTITY_DB_HOST", postgresAlias)
            .WithEnvironment("IDENTITY_DB_PORT", "5432")
            .WithEnvironment("IDENTITY_DB_USER", "postgres")
            .WithEnvironment("IDENTITY_DB_PASSWORD", "postgres")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8083).ForPath("/connectors")))
            .Build();
        await connect.StartAsync();
    }

    /// <summary>Saga step 3 prerequisite: src/cdc/sql/enable-cdc.sql against the monolith DB (batches split on GO).</summary>
    public async Task EnableSqlServerCdcAsync(string monolithConnectionString)
    {
        var script = await File.ReadAllTextAsync(Path.Combine(options.RepoRoot, "src", "cdc", "sql", "enable-cdc.sql"));
        await using var sql = new SqlConnection(monolithConnectionString);
        await sql.OpenAsync();
        foreach (var batch in script.Split(["\nGO", "\rGO"], StringSplitOptions.RemoveEmptyEntries))
        {
            var text = batch.Trim();
            if (text.Length == 0 || text.StartsWith("GO", StringComparison.OrdinalIgnoreCase)) continue;
            await using var cmd = new SqlCommand(text, sql) { CommandTimeout = 120 };
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public Task<ShellResult> RegisterConnectorsAsync() =>
        Shell.BashAsync($"\"{Path.Combine(options.RepoRoot, "src", "cdc", "register-connectors.sh")}\" all",
            new Dictionary<string, string?> { ["CONNECT_URL"] = ConnectUrl });

    public Task<ShellResult> PauseForwardSyncAsync(string action) =>
        Shell.BashAsync($"\"{Path.Combine(options.RepoRoot, "src", "cdc", "pause-forward-sync.sh")}\" {action}",
            new Dictionary<string, string?> { ["CONNECT_URL"] = ConnectUrl });

    public async Task<string> ConnectorStateAsync(string name)
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync($"{ConnectUrl}/connectors/{name}/status");
        if (!response.IsSuccessStatusCode) return $"HTTP {(int)response.StatusCode}";
        using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("connector").GetProperty("state").GetString() ?? "UNKNOWN";
    }

    /// <summary>Waits until every connector and task reports RUNNING (Debezium needs a few seconds to read the schema history).</summary>
    public async Task WaitForConnectorsRunningAsync(TimeSpan timeout)
    {
        using var http = new HttpClient();
        var deadline = DateTime.UtcNow + timeout;
        string last = "";
        while (DateTime.UtcNow < deadline)
        {
            var allRunning = true;
            foreach (var name in new[] { "monolith-identity-source", "identitydb-sink" })
            {
                using var response = await http.GetAsync($"{ConnectUrl}/connectors/{name}/status");
                last = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) { allRunning = false; break; }
                using var doc = System.Text.Json.JsonDocument.Parse(last);
                var root = doc.RootElement;
                if (root.GetProperty("connector").GetProperty("state").GetString() != "RUNNING") { allRunning = false; break; }
                var tasks = root.GetProperty("tasks");
                if (tasks.GetArrayLength() == 0 || tasks.EnumerateArray().Any(t => t.GetProperty("state").GetString() != "RUNNING")) { allRunning = false; break; }
            }
            if (allRunning) return;
            await Task.Delay(1000);
        }
        throw new TimeoutException($"Kafka Connect connectors did not reach RUNNING within {timeout}. Last status: {last}");
    }

    /// <summary>Connector status JSON plus the tail of the Connect worker log - attached to convergence failures.</summary>
    public async Task<string> DiagnosticsAsync(int logTailLines = 80)
    {
        using var http = new HttpClient();
        var sb = new System.Text.StringBuilder();
        foreach (var name in new[] { "monolith-identity-source", "identitydb-sink" })
        {
            using var response = await http.GetAsync($"{ConnectUrl}/connectors/{name}/status");
            sb.AppendLine($"{name}: HTTP {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }
        var (stdout, stderr) = await connect!.GetLogsAsync();
        var lines = (stdout + stderr).Split('\n');
        sb.AppendLine("--- connect log tail ---");
        sb.AppendLine(string.Join('\n', lines.Skip(Math.Max(0, lines.Length - logTailLines))));
        return sb.ToString();
    }

    public async Task SaveLogsAsync(string directory)
    {
        if (connect is not null)
        {
            var (stdout, stderr) = await connect.GetLogsAsync();
            await File.WriteAllTextAsync(Path.Combine(directory, "kafka-connect.log"), stdout + stderr);
        }
        if (kafka is not null)
        {
            var (stdout, stderr) = await kafka.GetLogsAsync();
            await File.WriteAllTextAsync(Path.Combine(directory, "kafka.log"), stdout + stderr);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (connect is not null) await connect.DisposeAsync();
        if (kafka is not null) await kafka.DisposeAsync();
    }
}
