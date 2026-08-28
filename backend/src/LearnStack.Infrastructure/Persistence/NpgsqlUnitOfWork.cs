using System.Data.Common;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Npgsql;

namespace LearnStack.Infrastructure.Persistence;

/// <summary>
/// The PostgreSQL <see cref="IUnitOfWork"/>: one connection from the application
/// data source per scope, and the transaction on it.
/// </summary>
/// <remarks>
/// <para>
/// Registered <b>scoped</b>. It is the sole owner of the connection — every
/// module <c>DbContext</c> is built against it with
/// <c>contextOwnsConnection: false</c>, so disposing a context does not return
/// the connection to the pool underneath its siblings. Disposal order is
/// transaction, then connection, and disposing with a live transaction rolls it
/// back
/// (<see href="../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040</see>
/// § Consequences).
/// </para>
/// <para>
/// <b>Frames, not a boolean.</b> Nesting is tracked as a depth because
/// <c>TransactionBehavior</c> calls <c>CommitAsync</c> directly rather than
/// through the handle, and ADR-0040 § Nesting says a nested frame "never commits,
/// never rolls back". Only a depth counter can make that true of a bare
/// <c>CommitAsync</c>: an inner call resolves its own frame and the outermost one
/// touches the database.
/// </para>
/// </remarks>
public sealed class NpgsqlUnitOfWork(NpgsqlDataSource dataSource) : IUnitOfWork
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    private NpgsqlConnection? _connection;
    private DbTransaction? _transaction;
    private int _depth;
    private bool _rollbackOnly;
    private bool _disposed;

    public DbConnection Connection
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_connection is null)
            {
                // Synchronous, and reached only by a caller that touches the
                // connection before opening a transaction. The ordinary path is
                // BeginTransactionAsync, which opens it asynchronously first.
                _connection = _dataSource.CreateConnection();
                _connection.Open();
            }

            return _connection;
        }
    }

    public DbTransaction? Transaction => _transaction;

    public bool HasActiveTransaction => _transaction is not null;

    public async Task<IUnitOfWorkScope> BeginTransactionAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_transaction is null)
        {
            _connection ??= _dataSource.CreateConnection();

            if (_connection.State != System.Data.ConnectionState.Open)
            {
                await _connection.OpenAsync(cancellationToken);
            }

            _transaction = await _connection.BeginTransactionAsync(cancellationToken);
            _rollbackOnly = false;
        }

        return new Frame(this, ++_depth);
    }

    public async Task SetTenantContextAsync(
        ITenantContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_transaction is null)
        {
            throw new InvalidOperationException(
                "SetTenantContextAsync requires an open transaction: set_config(..., true) is "
                + "transaction-local, so outside one the value is discarded before the query it "
                + "protects runs. Call BeginTransactionAsync first.");
        }

        if (_depth > 1)
        {
            // A joiner. Re-issuing would let an inner frame retarget the outer
            // frame's tenant — the same connection, the same transaction, a
            // different tenant for every statement after it.
            return;
        }

        // Written even when the context is unresolved, and written as the empty
        // string. The policies read NULLIF(current_setting(..., true), '')::uuid,
        // so '' is NULL is fail-closed — and setting it explicitly is what makes
        // a value left behind on a pooled connection by anything session-scoped
        // unreachable, rather than merely unlikely.
        await ExecuteAsync(
            "SELECT set_config('app.tenant_id', @tenant, true), "
            + "set_config('app.organization_id', @organization, true)",
            cancellationToken,
            ("tenant", context.IsResolved ? context.TenantId.ToString() : string.Empty),
            ("organization",
                context.IsResolved && context.OrganizationId is { } organization
                    ? organization.ToString()
                    : string.Empty));
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureFrameOpen();

        if (--_depth > 0)
        {
            // A joiner's commit is not a commit.
            return;
        }

        if (_rollbackOnly)
        {
            await RollbackCoreAsync();

            throw new InvalidOperationException(
                "The ambient transaction is marked rollback-only and has been rolled back. "
                + "An inner frame failed, so committing here would commit a partial unit.");
        }

        var transaction = _transaction!;
        _transaction = null;
        await transaction.CommitAsync(cancellationToken);
        await transaction.DisposeAsync();
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureFrameOpen();

        _rollbackOnly = true;

        if (--_depth > 0)
        {
            return;
        }

        await RollbackCoreAsync();
    }

    public void MarkRollbackOnly()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _rollbackOnly = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // A scope that ended with a live transaction has failed: committing here
        // would commit work nobody claimed was finished.
        if (_transaction is not null)
        {
            await RollbackCoreAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    private async Task RollbackCoreAsync()
    {
        var transaction = _transaction;
        _transaction = null;
        _depth = 0;

        if (transaction is null)
        {
            return;
        }

        // CancellationToken.None: a rollback is the cleanup path, and cancelling
        // it would leave the transaction open on a connection about to go back to
        // the pool.
        await transaction.RollbackAsync(CancellationToken.None);
        await transaction.DisposeAsync();
    }

    private void EnsureFrameOpen()
    {
        if (_depth == 0 || _transaction is null)
        {
            throw new InvalidOperationException(
                "No transaction frame is open. CommitAsync and RollbackAsync resolve a frame "
                + "opened by BeginTransactionAsync.");
        }
    }

    private async Task ExecuteAsync(
        string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = _connection!.CreateCommand();
        command.CommandText = sql;
        command.Transaction = (NpgsqlTransaction?)_transaction;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>One frame of the ambient transaction.</summary>
    private sealed class Frame(NpgsqlUnitOfWork unitOfWork, int depth) : IUnitOfWorkScope
    {
        private bool _resolved;

        public bool IsOwner => depth == 1;

        public Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            if (_resolved || unitOfWork._depth < depth)
            {
                // Already resolved, here or by an explicit CommitAsync.
                return Task.CompletedTask;
            }

            _resolved = true;
            return unitOfWork.CommitAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (_resolved || unitOfWork._disposed || unitOfWork._depth < depth)
            {
                return;
            }

            _resolved = true;
            await unitOfWork.RollbackAsync(CancellationToken.None);
        }
    }
}
