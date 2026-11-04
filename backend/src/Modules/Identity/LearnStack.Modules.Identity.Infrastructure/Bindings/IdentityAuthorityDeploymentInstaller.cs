using Npgsql;

namespace LearnStack.Modules.Identity.Infrastructure.Bindings;

/// <summary>Explicit deployment-only anchor installation on the caller's migration connection; never registered in API DI.</summary>
public static class IdentityAuthorityDeploymentInstaller
{
    public static async Task InstallAsync(NpgsqlConnection connection, IdentityAuthorityRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(registration);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var role = new NpgsqlCommand("SELECT current_user", connection, transaction))
                if (await role.ExecuteScalarAsync(cancellationToken) is not "learnstack_migration") throw Refused();
            await using (var tableLock = new NpgsqlCommand(
                "LOCK TABLE public.identity_authority IN SHARE ROW EXCLUSIVE MODE", connection, transaction))
                await tableLock.ExecuteNonQueryAsync(cancellationToken);
            var exists = false;
            await using (var read = new NpgsqlCommand(
                "SELECT authority_id, issuer FROM public.identity_authority WHERE id = 1", connection, transaction))
            await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
            {
                exists = await reader.ReadAsync(cancellationToken);
                if (exists && (reader.GetGuid(0) != registration.AuthorityId.Value
                    || !string.Equals(reader.GetString(1), registration.Issuer, StringComparison.Ordinal))) throw Refused();
            }
            if (!exists)
            {
                await using var insert = new NpgsqlCommand(
                    "INSERT INTO public.identity_authority (id, authority_id, issuer) VALUES (1, @authority, @issuer)",
                    connection, transaction);
                insert.Parameters.AddWithValue("authority", registration.AuthorityId.Value);
                insert.Parameters.AddWithValue("issuer", registration.Issuer);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await InstallCreationCapacityAsync(connection, transaction, registration, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (NpgsqlException)
        {
            throw Refused();
        }
    }

    private static async Task InstallCreationCapacityAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, IdentityAuthorityRegistration registration, CancellationToken cancellationToken)
    {
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO public.identity_creation_capacity
                (authority_id,minute_window,day_window,allocated_users)
            SELECT @authority,
                date_trunc('minute',clock_timestamp() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC',
                date_trunc('day',clock_timestamp() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC',
                (SELECT count(*) FROM public.users WHERE authority_id=@authority)
            ON CONFLICT (authority_id) DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("authority", registration.AuthorityId.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var lockedCount = new NpgsqlCommand("""
            SELECT allocated_users FROM public.identity_creation_capacity
                WHERE authority_id=@authority FOR UPDATE
            """, connection, transaction);
        lockedCount.Parameters.AddWithValue("authority", registration.AuthorityId.Value);
        if (await lockedCount.ExecuteScalarAsync(cancellationToken) is not long allocated) throw Refused();
        // Separate post-wait statement: the count and every User INSERT share the
        // locked control row. An existing counter is qualified, never reset.
        await using var users = new NpgsqlCommand("SELECT count(*) FROM public.users WHERE authority_id=@authority",
            connection, transaction);
        users.Parameters.AddWithValue("authority", registration.AuthorityId.Value);
        if (await users.ExecuteScalarAsync(cancellationToken) is not long actual || actual != allocated) throw Refused();
    }

    private static InvalidOperationException Refused() => new("Identity authority deployment installation was refused.");
}
