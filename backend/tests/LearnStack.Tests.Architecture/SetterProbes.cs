using Npgsql;

namespace LearnStack.Tests.Architecture.Probes;

/// <summary>
/// The shapes an out-of-band setter can take, for
/// <c>The_Setter_Scan_Can_Actually_Fail</c>. Every one is <c>async</c>, because a real reader
/// is: an async method's statements live in a compiler-generated state machine, which is where
/// a walk over the declaring method alone loses them. Never called.
/// </summary>
internal static class SetterProbes
{
    /// <summary>Opens read-only first, as every shipped reader does.</summary>
    public static async Task GuardsItsAnnouncementAsync(NpgsqlConnection connection)
    {
        await Run(connection, "SET TRANSACTION READ ONLY").ConfigureAwait(false);
        await Run(connection, "SELECT set_config('app.tenant_id', @tenant, true)").ConfigureAwait(false);
    }

    /// <summary>Announces with no statement at all.</summary>
    public static async Task AnnouncesWithNoStatementAsync(NpgsqlConnection connection) =>
        await Run(connection, "SELECT set_config('app.tenant_id', @tenant, true)").ConfigureAwait(false);

    /// <summary>Issues the statement, but after the announcement it was meant to bind.</summary>
    public static async Task AnnouncesBeforeTheStatementAsync(NpgsqlConnection connection)
    {
        await Run(connection, "SELECT set_config('app.tenant_id', @tenant, true)").ConfigureAwait(false);
        await Run(connection, "SET TRANSACTION READ ONLY").ConfigureAwait(false);
    }

    /// <summary>Opens two transactions and guards only the first.</summary>
    public static async Task AnnouncesTwiceUnderOneStatementAsync(NpgsqlConnection connection)
    {
        await Run(connection, "SET TRANSACTION READ ONLY").ConfigureAwait(false);
        await Run(connection, "SELECT set_config('app.tenant_id', @tenant, true)").ConfigureAwait(false);
        await Run(connection, "SET LOCAL app.organization_id = @organization").ConfigureAwait(false);
    }

    /// <summary>Names the statement in a message and issues nothing.</summary>
    public static async Task NamesTheStatementInAMessageAsync(NpgsqlConnection connection)
    {
        await Run(connection, "SELECT set_config('app.tenant_id', @tenant, true)").ConfigureAwait(false);

        throw new InvalidOperationException(
            "This transaction was opened with SET TRANSACTION READ ONLY before the announcement.");
    }

    private static Task Run(NpgsqlConnection connection, string sql)
    {
        using var command = new NpgsqlCommand(sql, connection);
        return Task.CompletedTask;
    }
}
