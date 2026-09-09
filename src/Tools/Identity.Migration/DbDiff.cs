using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Identity.Migration;

/// <summary>
/// Compares every synced table between the monolith and identitydb: row counts, missing/extra PKs and
/// per-row content hashes of the common columns. Exit code 0 only when both sides are identical.
/// </summary>
public class DbDiff(IConfiguration configuration)
{
    public async Task<int> RunAsync()
    {
        var source = configuration.GetConnectionString("Monolith") ?? throw new InvalidOperationException("ConnectionStrings:Monolith missing");
        var target = configuration.GetConnectionString("Identity") ?? throw new InvalidOperationException("ConnectionStrings:Identity missing");

        await using var sql = new SqlConnection(source);
        await using var pg = new NpgsqlConnection(target);
        await sql.OpenAsync();
        await pg.OpenAsync();

        var differences = 0;
        foreach (var (table, key) in SyncTables.All)
        {
            var left = await ReadAsync(new SqlCommand($"SELECT * FROM [dbo].[{table}]", sql), key);
            var right = await ReadAsync(new NpgsqlCommand($"SELECT * FROM \"{table}\"", pg), key);

            var commonColumns = left.Columns.Intersect(right.Columns).Order(StringComparer.Ordinal).ToArray();
            var leftHashes = left.Rows.ToDictionary(r => r.Key, r => Hash(r.Value, commonColumns));
            var rightHashes = right.Rows.ToDictionary(r => r.Key, r => Hash(r.Value, commonColumns));

            var missing = leftHashes.Keys.Except(rightHashes.Keys).ToList();
            var extra = rightHashes.Keys.Except(leftHashes.Keys).ToList();
            var changed = leftHashes.Keys.Intersect(rightHashes.Keys).Where(k => leftHashes[k] != rightHashes[k]).ToList();

            var tableDiffs = missing.Count + extra.Count + changed.Count;
            differences += tableDiffs;

            Console.WriteLine($"{table,-26} monolith={left.Rows.Count,6} identitydb={right.Rows.Count,6} " +
                              $"missing={missing.Count} extra={extra.Count} changed={changed.Count} {(tableDiffs == 0 ? "OK" : "DIFF")}");

            foreach (var k in missing.Take(5)) Console.WriteLine($"    missing in identitydb: {k}");
            foreach (var k in extra.Take(5)) Console.WriteLine($"    only in identitydb:    {k}");
            foreach (var k in changed.Take(5))
            {
                var cols = commonColumns.Where(c => left.Rows[k][c] != right.Rows[k][c])
                    .Select(c => $"{c} ({Trunc(left.Rows[k][c])} | {Trunc(right.Rows[k][c])})");
                Console.WriteLine($"    content differs:       {k}: {string.Join(", ", cols)}");
            }
        }

        Console.WriteLine(differences == 0 ? "Databases are in sync." : $"{differences} row differences found.");
        return differences == 0 ? 0 : 2;
    }

    private static async Task<(HashSet<string> Columns, Dictionary<string, Dictionary<string, string>> Rows)> ReadAsync(DbCommand command, string[] key)
    {
        await using (command)
        {
            await using var reader = await command.ExecuteReaderAsync();
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToHashSet(StringComparer.Ordinal);
            var rows = new Dictionary<string, Dictionary<string, string>>();

            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var column in columns)
                    row[column] = Normalize(reader[column]);

                rows[string.Join("|", key.Select(k => row[k]))] = row;
            }

            return (columns, rows);
        }
    }

    private static string Trunc(string value) => value.Length <= 40 ? value : value[..37] + "...";

    /// <summary>
    /// Canonical textual form so SQL Server and Postgres values compare equal. Timestamps are rounded to
    /// microseconds: SQL Server datetime2 keeps 100ns ticks, Postgres only microseconds.
    /// </summary>
    private static string Normalize(object value) => value switch
    {
        DBNull => "<null>",
        DateTime dt => Microseconds.Round(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => Microseconds.Round(dto.UtcDateTime).ToString("O", CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    private static string Hash(Dictionary<string, string> row, string[] columns)
    {
        var sb = new StringBuilder();
        foreach (var column in columns)
            sb.Append(column).Append('=').Append(row[column]).Append('\u001f');

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
