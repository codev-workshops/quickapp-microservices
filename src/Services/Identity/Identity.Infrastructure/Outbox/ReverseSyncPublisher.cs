using Identity.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Data;
using System.Text.Json;

namespace Identity.Infrastructure.Outbox;

public class ReverseSyncOptions
{
    public const string SectionName = "ReverseSync";

    /// <summary>When false the outbox is still written but never drained (keeps rollback data available).</summary>
    public bool Enabled { get; set; }
    public string? MonolithConnectionString { get; set; }
    public int PollIntervalMs { get; set; } = 1000;
    public int BatchSize { get; set; } = 200;
    public int MaxAttempts { get; set; } = 25;
}

/// <summary>
/// Drains <see cref="IdentityOutboxMessage"/> rows in commit order and replays them into the
/// monolith SQL Server database as idempotent MERGE / DELETE statements keyed on the primary key,
/// so the monolith Identity + OpenIddict tables stay current while Identity.API is authoritative.
/// </summary>
public class ReverseSyncPublisher(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<ReverseSyncOptions> options,
    ILogger<ReverseSyncPublisher> logger) : BackgroundService
{
    private readonly Dictionary<string, TableSchema> schemaCache = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var current = options.CurrentValue;

            if (!current.Enabled || string.IsNullOrWhiteSpace(current.MonolithConnectionString))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(current.PollIntervalMs, 250)), stoppingToken);
                continue;
            }

            try
            {
                var processed = await DrainOnceAsync(current, stoppingToken);
                if (processed == 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(current.PollIntervalMs), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reverse sync batch failed; retrying after {Delay}ms", current.PollIntervalMs);
                await Task.Delay(TimeSpan.FromMilliseconds(current.PollIntervalMs), stoppingToken);
            }
        }
    }

    /// <summary>Publishes one batch. Exposed so the migration CLI can drain synchronously before rollback.</summary>
    public async Task<int> DrainOnceAsync(ReverseSyncOptions current, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        var pending = await db.OutboxMessages
            .Where(m => m.PublishedAtUtc == null)
            .OrderBy(m => m.Id)
            .Take(current.BatchSize)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
            return 0;

        await using var sql = new SqlConnection(current.MonolithConnectionString);
        await sql.OpenAsync(cancellationToken);

        var processed = 0;
        foreach (var message in pending)
        {
            try
            {
                await ApplyAsync(sql, message, cancellationToken);
                message.PublishedAtUtc = DateTime.UtcNow;
                message.LastError = null;
                processed++;
            }
            catch (Exception ex)
            {
                message.Attempts++;
                message.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                logger.LogError(ex, "Reverse sync failed for outbox {Id} ({Table} {Op}), attempt {Attempts}",
                    message.Id, message.TableName, message.Operation, message.Attempts);

                if (message.Attempts >= current.MaxAttempts)
                {
                    logger.LogCritical("Outbox {Id} exceeded {Max} attempts and is being skipped; manual reconciliation required",
                        message.Id, current.MaxAttempts);
                    message.PublishedAtUtc = DateTime.UtcNow;
                }
                else
                {
                    // Preserve commit ordering: stop the batch at the first failure.
                    break;
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        return processed;
    }

    private async Task ApplyAsync(SqlConnection sql, IdentityOutboxMessage message, CancellationToken ct)
    {
        var schema = await GetSchemaAsync(sql, message.TableName, ct);
        var key = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(message.KeyJson)!;

        if (message.Operation == OutboxOperation.Delete)
        {
            var where = string.Join(" AND ", key.Keys.Select(k => $"[{k}] = @k_{k}"));
            await using var cmd = new SqlCommand($"DELETE FROM [dbo].[{schema.Name}] WHERE {where}", sql);
            foreach (var (column, value) in key)
                cmd.Parameters.Add(CreateParameter($"@k_{column}", schema.Columns[column], value));
            await cmd.ExecuteNonQueryAsync(ct);
            return;
        }

        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(message.PayloadJson!)!;
        var columns = payload.Keys.Where(schema.Columns.ContainsKey).ToList();
        var nonKey = columns.Except(key.Keys, StringComparer.OrdinalIgnoreCase).ToList();

        var identityInsert = schema.IdentityColumns.Count > 0;
        var merge = $"""
            {(identityInsert ? $"SET IDENTITY_INSERT [dbo].[{schema.Name}] ON;" : "")}
            MERGE [dbo].[{schema.Name}] WITH (HOLDLOCK) AS target
            USING (SELECT {string.Join(", ", columns.Select(c => $"@p_{c} AS [{c}]"))}) AS source
            ON {string.Join(" AND ", key.Keys.Select(k => $"target.[{k}] = source.[{k}]"))}
            {(nonKey.Count > 0 ? $"WHEN MATCHED THEN UPDATE SET {string.Join(", ", nonKey.Select(c => $"target.[{c}] = source.[{c}]"))}" : "")}
            WHEN NOT MATCHED THEN INSERT ({string.Join(", ", columns.Select(c => $"[{c}]"))})
                VALUES ({string.Join(", ", columns.Select(c => $"source.[{c}]"))});
            {(identityInsert ? $"SET IDENTITY_INSERT [dbo].[{schema.Name}] OFF;" : "")}
            """;

        await using var mergeCmd = new SqlCommand(merge, sql);
        foreach (var column in columns)
            mergeCmd.Parameters.Add(CreateParameter($"@p_{column}", schema.Columns[column], payload[column]));
        await mergeCmd.ExecuteNonQueryAsync(ct);
    }

    private static SqlParameter CreateParameter(string name, string sqlType, JsonElement value)
    {
        var parameter = new SqlParameter(name, ConvertValue(sqlType, value) ?? DBNull.Value);
        if (sqlType is "datetime2") parameter.SqlDbType = SqlDbType.DateTime2;
        else if (sqlType is "datetimeoffset") parameter.SqlDbType = SqlDbType.DateTimeOffset;
        else if (sqlType is "nvarchar") parameter.SqlDbType = SqlDbType.NVarChar;
        else if (sqlType is "bit") parameter.SqlDbType = SqlDbType.Bit;
        else if (sqlType is "int") parameter.SqlDbType = SqlDbType.Int;
        else if (sqlType is "bigint") parameter.SqlDbType = SqlDbType.BigInt;
        else if (sqlType is "uniqueidentifier") parameter.SqlDbType = SqlDbType.UniqueIdentifier;
        return parameter;
    }

    private static object? ConvertValue(string sqlType, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        return sqlType switch
        {
            "datetime2" or "datetime" => DateTime.Parse(value.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind),
            "datetimeoffset" => DateTimeOffset.Parse(value.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind),
            "bit" => value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.Number && value.GetInt32() != 0),
            "int" => value.GetInt32(),
            "bigint" => value.GetInt64(),
            "uniqueidentifier" => Guid.Parse(value.GetString()!),
            _ => value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
        };
    }

    private async Task<TableSchema> GetSchemaAsync(SqlConnection sql, string table, CancellationToken ct)
    {
        if (schemaCache.TryGetValue(table, out var cached))
            return cached;

        const string query = """
            SELECT c.COLUMN_NAME, c.DATA_TYPE,
                   COLUMNPROPERTY(OBJECT_ID(c.TABLE_SCHEMA + '.' + c.TABLE_NAME), c.COLUMN_NAME, 'IsIdentity') AS IsIdentity
            FROM INFORMATION_SCHEMA.COLUMNS c
            WHERE c.TABLE_SCHEMA = 'dbo' AND c.TABLE_NAME = @table
            """;

        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var identityColumns = new List<string>();

        await using var cmd = new SqlCommand(query, sql);
        cmd.Parameters.AddWithValue("@table", table);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            columns[name] = reader.GetString(1);
            if (!reader.IsDBNull(2) && reader.GetInt32(2) == 1)
                identityColumns.Add(name);
        }

        if (columns.Count == 0)
            throw new InvalidOperationException($"Table dbo.{table} does not exist in the monolith database.");

        var schema = new TableSchema(table, columns, identityColumns);
        schemaCache[table] = schema;
        return schema;
    }

    private sealed record TableSchema(string Name, Dictionary<string, string> Columns, List<string> IdentityColumns);
}
