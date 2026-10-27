using LearnStack.Infrastructure.Persistence;
using System.Data;
using Npgsql;

namespace LearnStack.Modules.Identity.Infrastructure.Bindings;

/// <summary>Explicit startup qualification; request-time binding access only consumes its immutable result.</summary>
public sealed class IdentityAuthorityQualification(
    Lazy<NpgsqlDataSource> dataSource, IdentityAuthorityRegistration? registration)
{
    private readonly object _gate = new();
    private Task? _qualification;
    private IdentityAuthorityRegistration? _current;

    public IdentityAuthorityRegistration? Current => Volatile.Read(ref _current);

    public string? QualifiedDatabaseName { get; private set; }

    public Task<DatabaseQualificationProof> OpenDatabaseQualificationProofAsync(CancellationToken cancellationToken = default)
    {
        if (Current is null) throw Unavailable();
        return DatabaseQualificationProof.OpenAsync(dataSource.Value, cancellationToken);
    }

    public Task QualifyAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) return _qualification ??= QualifyCoreAsync(cancellationToken);
    }

    private async Task QualifyCoreAsync(CancellationToken cancellationToken)
    {
        if (registration is null) throw Unavailable();
        try
        {
            await using var connection = await dataSource.Value.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
                await readOnly.ExecuteNonQueryAsync(cancellationToken);
            string database;
            await using (var command = new NpgsqlCommand(
                "SELECT current_user, authority_id, issuer, current_database() FROM public.identity_authority WHERE id = 1", connection, transaction))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken)
                    || reader.GetString(0) != "learnstack_app"
                    || reader.GetGuid(1) != registration.AuthorityId.Value
                    || !string.Equals(reader.GetString(2), registration.Issuer, StringComparison.Ordinal))
                    throw Unavailable();
                database = reader.GetString(3);
            }
            await transaction.CommitAsync(cancellationToken);
            QualifiedDatabaseName = database;
            Volatile.Write(ref _current, registration);
        }
        catch (NpgsqlException)
        {
            throw Unavailable();
        }
        catch (TimeoutException)
        {
            throw Unavailable();
        }
    }

    private static InvalidOperationException Unavailable() =>
        new("Identity authority startup qualification is unavailable or does not match deployment configuration.");
}
