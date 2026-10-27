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
            await transaction.CommitAsync(cancellationToken);
        }
        catch (NpgsqlException)
        {
            throw Refused();
        }
    }

    private static InvalidOperationException Refused() => new("Identity authority deployment installation was refused.");
}
