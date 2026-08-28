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
/// module <c>DbContext</c> is built against it, so disposing a context does not
/// return the connection to the pool underneath its siblings. Disposal order is
/// transaction, then connection, and disposing with a live transaction rolls it
/// back
/// (<see href="../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040</see>
/// § Consequences).
/// </para>
/// <para>
/// <b>Frames, not a boolean.</b> Nesting is a depth because a joiner's terminal
/// call must resolve its own frame and nothing else, and because
/// <see cref="IUnitOfWork.CommitAsync"/> takes no argument. The frame-blind form
/// is kept for a caller with no handle; <see cref="IUnitOfWorkScope"/> is the
/// guarded one, and it is what <c>TransactionBehavior</c> uses.
/// </para>
/// <para>
/// <b>Three rules that only look like details.</b> A commit disposes its
/// transaction in a <c>finally</c>, so a faulted <c>COMMIT</c> still leaves a
/// clean unit. A rollback on a unit with nothing to resolve is a no-op, because
/// rollback is cleanup and cleanup must never throw over the exception it is
/// cleaning up after — measured, the strict form replaced every commit-time
/// exception with "no transaction frame is open". And
/// <see cref="MarkRollbackOnly"/> is sticky for the life of the unit rather than
/// of the transaction, because the interface says "irreversible" and a poison
/// that a later <c>BEGIN</c> clears is not.
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
    private bool _commitRequested;
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

        if (_rollbackOnly)
        {
            throw new InvalidOperationException(
                "This unit of work is marked rollback-only and cannot open a transaction. "
                + "The mark is irreversible for the life of the unit; a caller that needs a "
                + "fresh transaction takes a fresh scope, which is the model ADR-0040 decides.");
        }

        if (_transaction is null)
        {
            _connection ??= _dataSource.CreateConnection();

            if (_connection.State != System.Data.ConnectionState.Open)
            {
                await _connection.OpenAsync(cancellationToken);
            }

            _transaction = await _connection.BeginTransactionAsync(cancellationToken);
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

    public Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureFrameOpen();

        return CommitFrameAsync(cancellationToken);
    }

    public Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // No EnsureFrameOpen. A rollback is the cleanup path: on a unit a faulted
        // commit already resolved there is nothing to roll back, and throwing
        // here would replace the caller's real exception with a complaint about
        // bookkeeping.
        return _depth == 0 || _transaction is null
            ? Task.CompletedTask
            : RollbackFrameAsync();
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
        var swallowedCommit = _commitRequested && _transaction is not null;

        try
        {
            if (_transaction is not null)
            {
                await RollbackCoreAsync();
            }
        }
        finally
        {
            // In a finally: a rollback that throws — a connection already broken
            // by the failure being cleaned up after is the ordinary way — must not
            // strand the connection outside the pool for the rest of the process.
            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }
        }

        if (swallowedCommit)
        {
            // A commit was asked for and the transaction outlived it, which means
            // a frame opened below the committer was never resolved and the
            // frame-blind CommitAsync resolved that one instead. The caller has
            // already been told it succeeded. Throwing from disposal is the last
            // remaining place to say otherwise, and a silent no-op on the success
            // path is the worse outcome. IUnitOfWorkScope.CompleteAsync catches
            // this at the terminal call instead, which is why TransactionBehavior
            // uses it.
            throw new InvalidOperationException(
                "A commit was requested but the transaction was still open at disposal, so it "
                + "was rolled back after the caller had been told it succeeded. A nested "
                + "BeginTransactionAsync frame was never resolved — resolve frames through the "
                + "IUnitOfWorkScope handle, which refuses to complete out of order.");
        }
    }

    private async Task CommitFrameAsync(CancellationToken cancellationToken)
    {
        _commitRequested = true;

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

        try
        {
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            // In a finally, so a faulted COMMIT still leaves the connection clean.
            // Deliberately NOT rolled back: a COMMIT that threw leaves the
            // server-side outcome genuinely unknown, and ADR-0033 calls that state
            // Indeterminate rather than failed.
            await transaction.DisposeAsync();
        }
    }

    private async Task RollbackFrameAsync()
    {
        if (--_depth > 0)
        {
            // A joiner declining its own frame. It does NOT mark the unit:
            // ADR-0040 § Nesting reserves that for an exception or an explicit
            // MarkRollbackOnly, because an inner Result.Fail the outer handler
            // absorbs is the outer handler's to decide about.
            return;
        }

        _rollbackOnly = true;
        await RollbackCoreAsync();
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

        try
        {
            // CancellationToken.None: a rollback is the cleanup path, and
            // cancelling it would leave the transaction open on a connection
            // about to go back to the pool.
            await transaction.RollbackAsync(CancellationToken.None);
        }
        finally
        {
            await transaction.DisposeAsync();
        }
    }

    private void EnsureFrameOpen()
    {
        if (_depth == 0 || _transaction is null)
        {
            throw new InvalidOperationException(
                "No transaction frame is open. CommitAsync resolves a frame opened by "
                + "BeginTransactionAsync.");
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
    /// <remarks>
    /// It knows its own depth, which is what the frame-blind
    /// <see cref="IUnitOfWork.CommitAsync"/> cannot: resolving while a frame
    /// opened after this one is still open would commit nothing and report
    /// success.
    /// </remarks>
    private sealed class Frame(NpgsqlUnitOfWork unitOfWork, int depth) : IUnitOfWorkScope
    {
        private bool _resolved;

        public bool IsOwner => depth == 1;

        public Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            if (AlreadyResolved())
            {
                if (unitOfWork._rollbackOnly)
                {
                    // The frame is gone because something failed it — a direct
                    // RollbackAsync, or a collapse — not because it committed.
                    // Returning quietly here would report success for a unit that
                    // wrote nothing, which is the one outcome worse than throwing.
                    throw new InvalidOperationException(
                        "This frame was already resolved by a rollback; it cannot complete. "
                        + "The unit is marked rollback-only and nothing it wrote was committed.");
                }

                return Task.CompletedTask;
            }

            EnsureInnermost();
            _resolved = true;
            return unitOfWork.CommitAsync(cancellationToken);
        }

        public Task FailAsync(CancellationToken cancellationToken = default)
        {
            if (AlreadyResolved())
            {
                return Task.CompletedTask;
            }

            _resolved = true;

            if (unitOfWork._depth > depth)
            {
                // Frames above this one leaked. On the failure path that collapses
                // rather than throws: everything opened after a frame that failed
                // has failed too, and raising here would replace the caller's real
                // exception with one about bookkeeping. CompleteAsync is where the
                // same condition is loud, because there it would otherwise be
                // reported as success.
                unitOfWork.MarkRollbackOnly();
                return unitOfWork.RollbackCoreAsync();
            }

            return unitOfWork.RollbackAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() =>
            // Through FailAsync, not the frame-blind RollbackAsync: a frame that
            // ends unresolved has failed, and it has failed in exactly the way
            // FailAsync already handles — including the leaked-frames collapse.
            // Calling RollbackAsync here instead decremented the shared depth by
            // one and left the transaction open, so a frame opened later joined an
            // abandoned transaction and reported a success that never committed.
            new(FailAsync(CancellationToken.None));

        private bool AlreadyResolved() =>
            _resolved || unitOfWork._disposed || unitOfWork._depth < depth;

        private void EnsureInnermost()
        {
            if (unitOfWork._depth > depth)
            {
                throw new InvalidOperationException(
                    $"Frame {depth} cannot complete while frame {unitOfWork._depth} is still open. "
                    + "Frames resolve innermost-first; completing out of order would resolve "
                    + "someone else's frame, commit nothing, and report success.");
            }
        }
    }
}
