using System.Data.Common;
using Npgsql;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The catalogue sweeps and session-context helpers the schema cases share.
/// </summary>
/// <remarks>
/// Shared because both suites run against the same applied schema and must ask
/// the same question of it. A second copy of the catalogue query is a second
/// place for a table to go missing from, which is the failure these queries exist
/// to prevent.
/// </remarks>
internal static class SchemaQueries
{
    /// <summary>
    /// Every ordinary table in schema <c>public</c>, minus EF's history tables.
    /// </summary>
    /// <remarks>
    /// The only names written down anywhere in these sweeps, and they are written
    /// down because they are the tables that are <b>not</b> part of the schema
    /// under test: each carries <c>MigrationId</c> and <c>ProductVersion</c>, both
    /// PascalCase, and no row security by design.
    /// </remarks>
    public const string TableOids =
        """
        SELECT oid FROM pg_class
        WHERE relnamespace = 'public'::regnamespace AND relkind = 'r'
          AND relname NOT LIKE '\_\_ef%'
        """;

    public const string TableNames =
        """
        SELECT relname FROM pg_class
        WHERE relnamespace = 'public'::regnamespace AND relkind = 'r'
          AND relname NOT LIKE '\_\_ef%'
        ORDER BY relname
        """;

    public static async Task<Dictionary<string, long>> CountEveryTableAsync(
        DbConnection connection,
        DbTransaction? transaction)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var table in await ReadStringsAsync(connection, TableNames, transaction))
        {
            // The table name is a pg_class.relname read from the same connection
            // moments earlier, not caller input; there is no bind parameter for an
            // identifier, and the quoting is what makes the interpolation safe.
            await using var command = new NpgsqlCommand(
                $"SELECT count(*) FROM {Quote(table)}",
                (NpgsqlConnection)connection, (NpgsqlTransaction?)transaction);

            counts[table] = (long)(await command.ExecuteScalarAsync())!;
        }

        return counts;
    }

    public static async Task<List<string>> ReadStringsAsync(
        DbConnection connection,
        string sql,
        DbTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(
            sql, (NpgsqlConnection)connection, (NpgsqlTransaction?)transaction);

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    public static async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(
            sql, (NpgsqlConnection)connection, (NpgsqlTransaction?)transaction);

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    public static Task SetTenantAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid tenantId)
        => SetSettingAsync(connection, transaction, "app.tenant_id", tenantId.ToString());

    public static async Task SetSettingAsync(
        DbConnection connection,
        DbTransaction transaction,
        string name,
        string value)
    {
        // set_config(..., true) rather than SET LOCAL: PostgreSQL's SET takes no
        // bind parameter — `SET LOCAL x = $1` is a syntax error, measured — and the
        // third argument `true` is what makes it transaction-local. The transaction
        // is passed explicitly because that locality is the whole point: a setting
        // applied to the wrong transaction is applied to nothing.
        await using var command = new NpgsqlCommand(
            "SELECT set_config(@name, @value, true)",
            (NpgsqlConnection)connection,
            (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("value", value);
        await command.ExecuteNonQueryAsync();
    }

    private static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
