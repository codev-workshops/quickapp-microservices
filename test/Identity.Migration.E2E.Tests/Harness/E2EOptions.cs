namespace Identity.Migration.E2E.Tests.Harness;

/// <summary>
/// Harness switches (all environment variables so the same binary runs in CI and locally):
///   IDENTITY_E2E_FULL_FIDELITY=true   variant A: Kafka + Kafka Connect (Debezium source + JDBC sink) from src/cdc.
///                                     Default (false) is variant B: forward sync emulated with Backfill.RunAsync.
///   IDENTITY_E2E_MONOLITH_REPO=path   checkout of codev-workshops/quickapp-monolith (default: ../quickapp-monolith next to this repo).
///   IDENTITY_E2E_BUILD_MONOLITH=false skip `dotnet build` of QuickApp.Server (use an existing Release build).
///   IDENTITY_E2E_MAVEN_REPO=url       Maven repository the Connect image downloads plugins from (variant A only);
///                                     e.g. https://maven-central.storage-download.googleapis.com/maven2 when repo1 rate-limits.
/// </summary>
public sealed class E2EOptions
{
    public bool FullFidelity { get; } = Flag("IDENTITY_E2E_FULL_FIDELITY");
    public bool BuildMonolith { get; } = !string.Equals(Environment.GetEnvironmentVariable("IDENTITY_E2E_BUILD_MONOLITH"), "false", StringComparison.OrdinalIgnoreCase);

    public string RepoRoot { get; } = FindRepoRoot();
    public string MonolithRepo { get; }
    public string? MavenRepo { get; } = Environment.GetEnvironmentVariable("IDENTITY_E2E_MAVEN_REPO");

    public string BuildConfiguration { get; } =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    public string Variant => FullFidelity ? "A (full-fidelity: Kafka + Debezium CDC)" : "B (CDC-substituted: Backfill.RunAsync)";

    public E2EOptions()
    {
        MonolithRepo = Environment.GetEnvironmentVariable("IDENTITY_E2E_MONOLITH_REPO")
                       ?? Path.GetFullPath(Path.Combine(RepoRoot, "..", "quickapp-monolith"));
    }

    private static bool Flag(string name) =>
        string.Equals(Environment.GetEnvironmentVariable(name), "true", StringComparison.OrdinalIgnoreCase);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "_common.sh")) &&
                Directory.Exists(Path.Combine(dir.FullName, "src", "cdc")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate the quickapp-microservices repository root (scripts/_common.sh).");
    }
}
