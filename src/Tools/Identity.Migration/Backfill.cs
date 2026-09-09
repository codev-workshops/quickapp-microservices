using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Data;

namespace Identity.Migration;

/// <summary>
/// Saga step 2: one-time backfill monolith (SQL Server) -> identitydb (Postgres).
/// Every row is copied column-by-column with values passed through verbatim (PasswordHash,
/// SecurityStamp, ConcurrencyStamp and the string GUID primary keys are never regenerated).
/// Writes are `INSERT ... ON CONFLICT (pk) DO UPDATE`, so re-running is safe and converges.
/// </summary>
public class Backfill(IConfiguration configuration)
{
    public async Task<int> RunAsync()
    {
        var source = configuration.GetConnectionString("Monolith") ?? throw new InvalidOperationException("ConnectionStrings:Monolith missing");
        var target = configuration.GetConnectionString("Identity") ?? throw new InvalidOperationException("ConnectionStrings:Identity missing");
        var batchSize = configuration.GetValue("Backfill:BatchSize", 500);

        await using var sql = new SqlConnection(source);
        await using var pg = new NpgsqlConnection(target);
        await sql.OpenAsync();
        await pg.OpenAsync();

        var total = 0;
        foreach (var (table, key) in SyncTables.All)
        {
            var copied = await CopyTableAsync(sql, pg, table, key, batchSize);
            total += copied;
            Console.WriteLine($"{table,-26} {copied,8} rows upserted");
        }

        foreach (var (table, column) in SyncTables.IdentityColumns)
        {
            await using var cmd = new NpgsqlCommand(
                $"""SELECT setval(pg_get_serial_sequence('"{table}"', '{column}'), COALESCE((SELECT MAX("{column}") FROM "{table}"), 0) + 1, false)""", pg);
            await cmd.ExecuteScalarAsync();
        }

        Console.WriteLine($"Backfill complete: {total} rows.");
        return 0;
    }

    private static async Task<int> CopyTableAsync(SqlConnection sql, NpgsqlConnection pg, string table, string[] key, int batchSize)
    {
        var targetColumns = await GetPostgresColumnsAsync(pg, table);

        await using var select = new SqlCommand($"SELECT * FROM [dbo].[{table}]", sql);
        await using var reader = await select.ExecuteReaderAsync();

        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(reader.GetName)
            .Where(targetColumns.Contains)
            .ToArray();

        var nonKey = columns.Except(key).ToArray();
        var insertSql = $"""
            INSERT INTO "{table}" ({string.Join(", ", columns.Select(c => $"\"{c}\""))})
            VALUES ({string.Join(", ", columns.Select((_, i) => $"${i + 1}"))})
            ON CONFLICT ({string.Join(", ", key.Select(k => $"\"{k}\""))})
            DO {(nonKey.Length == 0 ? "NOTHING" : $"UPDATE SET {string.Join(", ", nonKey.Select(c => $"\"{c}\" = EXCLUDED.\"{c}\""))}")}
            """;

        var count = 0;
        var tx = await pg.BeginTransactionAsync();
        try
        {
            while (await reader.ReadAsync())
            {
                await using var insert = new NpgsqlCommand(insertSql, pg, tx);
                foreach (var column in columns)
                    insert.Parameters.Add(new NpgsqlParameter { Value = ConvertValue(reader[column]) });

                await insert.ExecuteNonQueryAsync();

                if (++count % batchSize == 0)
                {
                    await tx.CommitAsync();
                    tx = await pg.BeginTransactionAsync();
                }
            }

            await tx.CommitAsync();
        }
        finally
        {
            await tx.DisposeAsync();
        }

        return count;
    }

    /// <summary>
    /// Strings/bools/ints pass through untouched. Only temporal types are adjusted, because Npgsql maps
    /// EF's DateTime/DateTimeOffset to `timestamp with time zone` and requires UTC kind / zero offset.
    /// </summary>
    public static object ConvertValue(object value) => value switch
    {
        DBNull => DBNull.Value,
        DateTime dt => Microseconds.Round(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
        DateTimeOffset dto => Microseconds.Round(dto.ToUniversalTime()),
        _ => value
    };

    private static async Task<HashSet<string>> GetPostgresColumnsAsync(NpgsqlConnection pg, string table)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = $1", pg);
        cmd.Parameters.Add(new NpgsqlParameter { Value = table });

        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(0));

        if (columns.Count == 0)
            throw new InvalidOperationException($"Table \"{table}\" not found in identitydb. Apply EF migrations first.");

        return columns;
    }
}
