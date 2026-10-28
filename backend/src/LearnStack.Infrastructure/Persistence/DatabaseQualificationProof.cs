using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using LearnStack.SharedKernel.Jobs;
using Npgsql;

namespace LearnStack.Infrastructure.Persistence;

/// <summary>A bounded startup-only witness that auxiliary lanes reach the live business database.</summary>
/// <remarks>
/// The two-int advisory key space is distinct from every bigint business fence.
/// No schema, grant, tenant announcement, business lock or row mutation is involved.
/// The actual business transaction keeps its unpredictable marker until qualification finishes.
/// </remarks>
public sealed class DatabaseQualificationProof : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly object _gate = new();
    private readonly NpgsqlTransaction _transaction;
    private readonly CancellationTokenSource _deadline;
    private readonly int _first;
    private readonly int _second;
    private readonly int _backendPid;
    private int _closed;
    private Task? _disposal;

    private DatabaseQualificationProof(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationTokenSource deadline, int first, int second, int backendPid)
    {
        _connection = connection; _transaction = transaction; _deadline = deadline;
        _first = first; _second = second;
        _backendPid = backendPid;
    }

    public CancellationToken DeadlineToken => _deadline.Token;

    public static async Task<DatabaseQualificationProof> OpenAsync(NpgsqlDataSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        JobApplicationSourceOwner.RefuseUnrelatedRead();
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        NpgsqlConnection? connection = null;
        NpgsqlTransaction? transaction = null;
        try
        {
            connection = await source.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
            await ApplicationDataSource.RefuseBypassRoleAsync(connection, deadline.Token).ConfigureAwait(false);
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, deadline.Token).ConfigureAwait(false);
            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
                await readOnly.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false);
            int backendPid;
            await using (var identity = new NpgsqlCommand("SELECT current_user, pg_backend_pid()", connection, transaction))
            await using (var reader = await identity.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(deadline.Token).ConfigureAwait(false)
                    || reader.GetString(0) != ApplicationDataSource.RuntimeRole)
                    throw new InvalidOperationException("Business database qualification requires the guarded application role.");
                // PgBouncer's protocol cancellation PID identifies its client;
                // only SQL identifies the server that owns this transaction.
                backendPid = reader.GetInt32(1);
            }
            var bytes = RandomNumberGenerator.GetBytes(8);
            var first = BinaryPrimitives.ReadInt32BigEndian(bytes);
            var second = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(4));
            await using (var marker = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@first, @second)", connection, transaction))
            {
                marker.Parameters.AddWithValue("first", first);
                marker.Parameters.AddWithValue("second", second);
                await marker.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false);
            }
            return new(connection, transaction, deadline, first, second, backendPid);
        }
        catch
        {
            try { if (transaction is not null) await transaction.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                try { if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false); }
                finally { deadline.Dispose(); }
            }
            throw;
        }
    }

    internal void RequireActive()
    {
        _deadline.Token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _closed) != 0 || _connection.State != ConnectionState.Open
            || !ReferenceEquals(_transaction.Connection, _connection))
            throw new InvalidOperationException("Business database qualification proof is no longer active.");
    }

    internal static void Bind(NpgsqlCommand command, DatabaseQualificationProof? proof)
    {
        proof?.RequireActive();
        command.Parameters.AddWithValue("has_business_proof", proof is not null);
        command.Parameters.AddWithValue("business_pid", proof?._backendPid ?? 0);
        command.Parameters.AddWithValue("business_first", proof is null ? 0L : unchecked((uint)proof._first));
        command.Parameters.AddWithValue("business_second", proof is null ? 0L : unchecked((uint)proof._second));
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            Volatile.Write(ref _closed, 1);
            return new ValueTask(_disposal ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        try { await _transaction.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            try { await _connection.DisposeAsync().ConfigureAwait(false); }
            finally { _deadline.Dispose(); }
        }
    }
}
