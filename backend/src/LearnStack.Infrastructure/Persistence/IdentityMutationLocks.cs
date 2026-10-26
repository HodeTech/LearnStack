using System.Data;
using System.Data.Common;
using System.Diagnostics;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using Npgsql;

namespace LearnStack.Infrastructure.Persistence;

/// <summary>One immutable plan on one announced physical transaction; never a session lock owner.</summary>
public sealed class IdentityMutationLocks(IUnitOfWork unit) : IIdentityMutationLocks
{
    private IdentityMutationLockPlan? _plan;
    private DbConnection? _connection;
    private DbTransaction? _transaction;
    private long _generation;
    private bool _attempted, _faultRecorded;

    public async Task AcquireAsync(IdentityMutationLockPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (_attempted || !IsAdmitted(plan)) Refuse();
        _attempted = true;
        _connection = unit.Connection;
        _transaction = unit.Transaction;
        _generation = unit.GetTenantContextAnnouncement(_transaction)!.Generation;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            // The driver's cached isolation property cannot observe an explicit
            // SET TRANSACTION made earlier on the same physical owner.
            await using (var check = new NpgsqlCommand("""
                SELECT current_setting('transaction_isolation') = 'read committed'
                    AND current_setting('transaction_read_only') = 'off'
                """, (NpgsqlConnection)_connection, (NpgsqlTransaction)_transaction!) { CommandTimeout = 10 })
                if (await check.ExecuteScalarAsync(deadline.Token) is not true) Refuse();
            await ExecuteAsync("SET LOCAL lock_timeout = '2s'", null, deadline.Token);
            foreach (var user in plan.UserIds)
                await AcquireOneAsync(IdentityLockKind.User, IdentityAdvisoryLockKey.ForUser(user), false, deadline.Token);
            await AcquireOneAsync(IdentityLockKind.Tenant, IdentityAdvisoryLockKey.ForTenant(plan.TenantId),
                plan.TenantMode == IdentityTenantLockMode.Shared, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            _plan = plan;
            RequireCurrent(plan);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            unit.MarkRollbackOnly();
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        {
            unit.MarkRollbackOnly();
            throw new IdentityMutationLockException();
        }
    }

    public void RequireCurrent(IdentityMutationLockPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!ReferenceEquals(_plan, plan) || !IsAdmitted(plan)
            || !ReferenceEquals(_connection, unit.Connection) || !ReferenceEquals(_transaction, unit.Transaction)
            || unit.GetTenantContextAnnouncement(unit.Transaction)?.Generation != _generation)
            Refuse();
    }

    private bool IsAdmitted(IdentityMutationLockPlan plan) => unit.HasActiveTransaction
        && !unit.IsRollbackOnly && unit.Mode == TransactionMode.ReadWrite
        && unit is not IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }
        && unit.Transaction is { IsolationLevel: IsolationLevel.ReadCommitted }
        && unit.GetTenantContextAnnouncement(unit.Transaction) is { Generation: > 0 } announcement
        && announcement.TenantId == plan.TenantId;

    private void Refuse()
    {
        if (!_faultRecorded)
        {
            _faultRecorded = true;
            IdentityLockObservation.PlanFault(IdentityLockKind.Tenant);
        }
        if (unit.HasActiveTransaction) unit.MarkRollbackOnly();
        throw new IdentityMutationLockException();
    }

    private async Task AcquireOneAsync(IdentityLockKind kind, long key, bool shared, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await ExecuteAsync(shared ? "SELECT pg_catalog.pg_advisory_xact_lock_shared(@key)"
                : "SELECT pg_catalog.pg_advisory_xact_lock(@key)", key, cancellationToken);
            IdentityLockObservation.Complete(kind, started);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        {
            IdentityLockObservation.Complete(kind, started, failure);
            throw;
        }
    }

    private async Task ExecuteAsync(string sql, long? key, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)_connection!, (NpgsqlTransaction)_transaction!)
        { CommandTimeout = 10 };
        if (key is { } value) command.Parameters.AddWithValue("key", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
