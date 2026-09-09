using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Identity.Migration.E2E.Tests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace Identity.Migration.E2E.Tests;

/// <summary>
/// Ordered end-to-end run of the Identity strangler migration saga (docs/identity-migration-saga.md "Saga steps"):
/// Phase 1 forward migration, Phase 2 strangler window, Phase 3 shadow verification, Phase 4 cutover,
/// Phase 5 post-cutover writes, Phase 6 rollback, Phase 7 no-data-loss confirmation, then edge cases E1-E7.
/// Every step records its outcome in <see cref="TestReport"/>; the final step prints the report.
/// </summary>
[Collection(SagaCollection.Name)]
[TestCaseOrderer(StepOrderer.TypeName, StepOrderer.AssemblyName)]
public sealed partial class SagaTests(SagaFixture fx, ITestOutputHelper output)
{
    private static readonly HttpClient Http = AppHost.NewInsecureClient();

    // ------------------------------------------------------------------ step bookkeeping

    private async Task Step(string id, string title, Func<Task<string>> body)
    {
        output.WriteLine($"==== {id} {title} ====");
        try
        {
            var details = await body();
            fx.Report.Record(id, title, Outcome.Passed, details);
            output.WriteLine($"PASS {id}: {details}");
        }
        catch (Xunit.SkipException ex)
        {
            fx.Report.Record(id, title, Outcome.Skipped, ex.Message);
            output.WriteLine($"SKIP {id}: {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            fx.Report.Record(id, title, Outcome.Failed, ex.Message);
            output.WriteLine($"FAIL {id}: {ex}");
            throw;
        }
    }

    private void Log(string line)
    {
        output.WriteLine(line);
        fx.Report.Log(line);
    }

    private void Gate(string phase, string tool, int exitCode, int expected, string note = "")
    {
        output.WriteLine($"[GATE] {phase} {tool} exit code = {exitCode} (expected {expected}) {note}");
        fx.Report.Gate(phase, tool, exitCode, expected, note);
        Assert.Equal(expected, exitCode);
    }

    /// <summary>Runs one of the migration tool commands in-process, capturing what it prints to the console.</summary>
    private async Task<(int ExitCode, string Output)> RunToolAsync(string name, Func<Task<int>> run)
    {
        var original = Console.Out;
        var originalErr = Console.Error;
        var writer = new StringWriter();
        Console.SetOut(writer);
        Console.SetError(writer);
        int exitCode;
        try
        {
            exitCode = await run();
        }
        finally
        {
            Console.SetOut(original);
            Console.SetError(originalErr);
        }

        var text = writer.ToString();
        output.WriteLine($"--- {name} (exit {exitCode}) ---");
        output.WriteLine(text);
        return (exitCode, text);
    }

    private string RequireState(string key) =>
        fx.State.TryGetValue(key, out var value) ? value : throw new InvalidOperationException($"State '{key}' missing - an earlier phase failed.");

    // ------------------------------------------------------------------ HTTP helpers

    private static async Task<(int Status, JsonNode? Body)> PasswordGrantAsync(Uri baseUrl, string userName, string password)
    {
        using var response = await Http.PostAsync(new Uri(baseUrl, "connect/token"), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "quickapp_spa",
            ["username"] = userName,
            ["password"] = password,
            ["scope"] = "openid email phone profile offline_access roles"
        }));
        var body = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, body.Length == 0 ? null : JsonNode.Parse(body));
    }

    private static async Task<string> AccessTokenAsync(Uri baseUrl, string userName, string password)
    {
        var (status, body) = await PasswordGrantAsync(baseUrl, userName, password);
        if (status != 200)
            throw new InvalidOperationException($"password grant for {userName} at {baseUrl} failed: {status} {body}");
        return body!["access_token"]!.GetValue<string>();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri url, string token, object? json = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (json is not null)
            request.Content = JsonContent.Create(json);
        return await Http.SendAsync(request);
    }

    private static string? JwtHeader(string jwt, string name)
    {
        var part = jwt.Split('.')[0];
        var padded = part.Replace('-', '+').Replace('_', '/').PadRight(part.Length + (4 - part.Length % 4) % 4, '=');
        return JsonNode.Parse(Convert.FromBase64String(padded))?[name]?.ToString();
    }

    // ------------------------------------------------------------------ row comparison

    /// <summary>Same canonical form DbDiff uses so SQL Server and Postgres row values can be compared directly.</summary>
    private static string Canonical(object? value) => value switch
    {
        null or DBNull => "<null>",
        DateTime dt => Microseconds.Round(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => Microseconds.Round(dto.UtcDateTime).ToString("O", CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    private async Task<List<string>> CommonColumnsAsync(string table)
    {
        var sqlColumns = (await fx.SqlRowsAsync($"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{table}'"))
            .Select(r => (string)r["COLUMN_NAME"]!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pgColumns = (await fx.PgRowsAsync($"SELECT column_name FROM information_schema.columns WHERE table_name = '{table}'"))
            .Select(r => (string)r["column_name"]!).ToList();
        return pgColumns.Where(sqlColumns.Contains).OrderBy(c => c, StringComparer.Ordinal).ToList();
    }

    /// <summary>Asserts the row identified by <paramref name="where"/> exists on both sides with identical common columns.</summary>
    private async Task AssertRowIdenticalAsync(string table, string keyColumn, string keyValue)
    {
        var columns = await CommonColumnsAsync(table);
        var sqlRows = await fx.SqlRowsAsync($"SELECT {string.Join(", ", columns.Select(c => $"[{c}]"))} FROM [{table}] WHERE [{keyColumn}] = '{keyValue.Replace("'", "''")}'");
        var pgRows = await fx.PgRowsAsync($"SELECT {string.Join(", ", columns.Select(c => $"\"{c}\""))} FROM \"{table}\" WHERE \"{keyColumn}\" = '{keyValue.Replace("'", "''")}'");
        Assert.True(sqlRows.Count == 1, $"{table}[{keyColumn}={keyValue}] expected in monolith, found {sqlRows.Count}");
        Assert.True(pgRows.Count == 1, $"{table}[{keyColumn}={keyValue}] expected in identitydb, found {pgRows.Count}");

        foreach (var column in columns)
        {
            var left = Canonical(sqlRows[0][column]);
            var right = Canonical(pgRows[0][column]);
            Assert.True(left == right, $"{table}[{keyColumn}={keyValue}].{column}: monolith={left} identitydb={right}");
        }
    }

    private async Task<Dictionary<string, long>> CountsAsync()
    {
        var result = new Dictionary<string, long>();
        foreach (var table in SyncTables.All)
            result[table.Name] = await fx.SqlCountAsync(table.Name) * 1_000_000 + await fx.PgCountAsync(table.Name);
        return result;
    }

    private async Task<long> OutboxCountAsync(bool unpublishedOnly = false) =>
        await fx.PgScalarAsync<long>(unpublishedOnly
            ? "SELECT COUNT(*) FROM \"IdentityOutbox\" WHERE \"PublishedAtUtc\" IS NULL"
            : "SELECT COUNT(*) FROM \"IdentityOutbox\"");

    private async Task<long> MaxOutboxIdAsync() =>
        await fx.PgScalarAsync<long?>("SELECT MAX(\"Id\") FROM \"IdentityOutbox\"") ?? 0;
}
