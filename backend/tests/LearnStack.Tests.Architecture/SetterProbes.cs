using Npgsql;

namespace LearnStack.Tests.Architecture.Probes;

/// <summary>
/// The shapes an out-of-band setter can take, for
/// <c>The_Setter_Scan_Can_Actually_Fail</c>. Every one is <c>async</c>, because a real reader
/// is: an async method's statements live in a compiler-generated state machine, which is where
/// a walk over the declaring method alone loses them. Never called.
/// </summary>
/// <remarks>
/// Each probe builds and runs its own commands rather than delegating to a helper, because the
/// guard asks whether the statement was <b>executed</b> before the announcement and it reads one
/// method's instructions. A helper executing on the probe's behalf would move the call out of
/// sight — and it would also stop these probes resembling the readers they stand for, which
/// issue their statements inline.
/// </remarks>
internal static class SetterProbes
{
    private const string ReadOnly = "SET TRANSACTION READ ONLY";

    private const string Announcement = "SELECT set_config('app.tenant_id', @tenant, true)";

    /// <summary>Opens read-only first, as every shipped reader does.</summary>
    public static async Task GuardsItsAnnouncementAsync(NpgsqlConnection connection)
    {
        await using (var readOnly = new NpgsqlCommand(ReadOnly, connection))
        {
            await readOnly.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using var announce = new NpgsqlCommand(Announcement, connection);
        await announce.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>Announces with no statement at all.</summary>
    public static async Task AnnouncesWithNoStatementAsync(NpgsqlConnection connection)
    {
        await using var announce = new NpgsqlCommand(Announcement, connection);
        await announce.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>Issues the statement, but after the announcement it was meant to bind.</summary>
    public static async Task AnnouncesBeforeTheStatementAsync(NpgsqlConnection connection)
    {
        await using (var announce = new NpgsqlCommand(Announcement, connection))
        {
            await announce.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using var readOnly = new NpgsqlCommand(ReadOnly, connection);
        await readOnly.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>Opens two transactions and guards only the first.</summary>
    public static async Task AnnouncesTwiceUnderOneStatementAsync(NpgsqlConnection connection)
    {
        await using (var readOnly = new NpgsqlCommand(ReadOnly, connection))
        {
            await readOnly.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using (var announce = new NpgsqlCommand(Announcement, connection))
        {
            await announce.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using var second = new NpgsqlCommand(
            "SET LOCAL app.organization_id = @organization", connection);
        await second.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>Names the statement in a message and issues nothing.</summary>
    public static async Task NamesTheStatementInAMessageAsync(NpgsqlConnection connection)
    {
        await using (var announce = new NpgsqlCommand(Announcement, connection))
        {
            await announce.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            "This transaction was opened with SET TRANSACTION READ ONLY before the announcement.");
    }

    /// <summary>
    /// Builds the read-only command and never runs it — what deleting one line looks like.
    /// </summary>
    /// <remarks>
    /// The reason this probe exists. An earlier version of the guard set its flag on the string
    /// alone, so removing the execution left the literal, the constructor and the guard's answer
    /// untouched while PostgreSQL reported <c>transaction_read_only=off</c>. A loaded statement
    /// is not an issued one.
    /// </remarks>
    public static async Task ConstructsWithoutExecutingAsync(NpgsqlConnection connection)
    {
        await using (var readOnly = new NpgsqlCommand(ReadOnly, connection))
        {
            _ = readOnly.CommandText;
        }

        await using var announce = new NpgsqlCommand(Announcement, connection);
        await announce.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
