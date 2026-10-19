using Npgsql;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>Builds the shared grant-bounded pool for confined background infrastructure.</summary>
public static class OutboxDispatcherDataSource
{
    public const string ConnectionName = "OutboxDispatcher";
    public const string Key = "outbox-dispatcher";
    public const string Role = "learnstack_outbox_admin";

    /// <summary>Builds a guarded pool without opening a connection.</summary>
    public static NpgsqlDataSource Build(string? connectionString)
    {
        var parsed = ParseAndValidate(connectionString);

        try
        {
            var builder = new NpgsqlDataSourceBuilder(parsed.ConnectionString);
            builder.UsePhysicalConnectionInitializer(
                connection => RequireConfinedRole(connection, async: false).GetAwaiter().GetResult(),
                connection => RequireConfinedRole(connection, async: true));
            return builder.Build();
        }
        catch (Exception failure) when (failure is ArgumentException or FormatException or InvalidOperationException)
        {
            // Driver configuration errors may contain supplied values. Do not
            // attach them as inner exceptions or repeat any connection field.
            throw InvalidConfiguration();
        }
    }

    /// <summary>Checks configuration without building the pool or connecting.</summary>
    public static void Validate(string? connectionString) => _ = ParseAndValidate(connectionString);

    private static NpgsqlConnectionStringBuilder ParseAndValidate(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{ConnectionName} is not configured. See .env.example.");
        }

        NpgsqlConnectionStringBuilder parsed;
        try
        {
            parsed = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception failure) when (failure is ArgumentException or FormatException)
        {
            throw InvalidConfiguration();
        }

        if (!string.Equals(parsed.Username, Role, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{ConnectionName} must name the dispatcher role. See .env.example.");
        }

        // Startup GUCs are reset-safe: Npgsql's RESET ALL restores these values
        // on warm pool reuse. Idle cleanup also reaps aborted backend sessions;
        // it does not prove a response-withholding proxy released the client I/O.
        parsed.Options = "-c statement_timeout=5000 -c lock_timeout=5000 -c idle_in_transaction_session_timeout=5000";
        parsed.Timeout = 5;
        parsed.CommandTimeout = 5;
        parsed.CancellationTimeout = 1000;
        parsed.Enlist = false;
        parsed.Multiplexing = false;
        parsed.NoResetOnClose = false;
        parsed.IncludeErrorDetail = false;
        parsed.LogParameters = false;
        parsed.PersistSecurityInfo = false;

        return parsed;
    }

    private static InvalidOperationException InvalidConfiguration() => new(
        $"ConnectionStrings:{ConnectionName} is not a valid Npgsql key/value connection string. See .env.example.");

    private static async Task RequireConfinedRole(NpgsqlConnection connection, bool async)
    {
        var command = connection.CreateCommand();
        await using var ownedCommand = command.ConfigureAwait(false);
        command.CommandTimeout = 5;
        command.CommandText =
            """
            SELECT EXISTS (
                SELECT 1 FROM pg_roles role
                WHERE role.rolname = @role
                  AND session_user = @role AND current_user = @role
                  AND role.rolbypassrls
                  AND NOT role.rolsuper
                  AND NOT role.rolcreatedb
                  AND NOT role.rolcreaterole
                  AND NOT role.rolreplication
                  AND NOT EXISTS (
                      SELECT 1 FROM pg_roles reachable
                      WHERE reachable.oid <> role.oid
                        AND pg_has_role(role.oid, reachable.oid, 'MEMBER'))
                  AND NOT EXISTS (
                      SELECT 1 FROM pg_roles member
                      WHERE member.oid <> role.oid
                        -- Superusers implicitly belong to every role without a grant.
                        AND NOT member.rolsuper
                        AND pg_has_role(member.oid, role.oid, 'MEMBER')))
            """;
        command.Parameters.AddWithValue("role", Role);

        var confined = async
            ? await command.ExecuteScalarAsync().ConfigureAwait(false)
            : command.ExecuteScalar();

        if (confined is not true)
        {
            throw new InvalidOperationException(
                "The outbox dispatcher connection has an invalid role, attributes or memberships.");
        }
    }
}
