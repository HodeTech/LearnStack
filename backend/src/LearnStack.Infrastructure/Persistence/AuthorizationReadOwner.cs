using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Npgsql;

namespace LearnStack.Infrastructure.Persistence;

/// <summary>One isolated committed-only candidate read and its retained actual cleanup.</summary>
public sealed class AuthorizationReadOwner : IAsyncDisposable
{
    private readonly IdentityAuthorizationReadSource _source;
    private readonly NpgsqlDataSource _dataSource;
    private readonly AuthorizationCandidate? _candidate;
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationToken _deadlineToken;
    private readonly long _generation;
    private readonly JobApplicationAuxiliaryResources? _jobResources;
    private readonly TaskCompletionSource _actualCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private NpgsqlConnection? _connection;
    private NpgsqlTransaction? _transaction;
    private TenantContextAnnouncement? _announcement;
    private Task? _cleanup;
    private bool _terminal;
    private bool _committed;
    private bool _cleanupFailed;

    internal AuthorizationReadOwner(IdentityAuthorizationReadSource source, NpgsqlDataSource dataSource,
        AuthorizationCandidate? candidate, CancellationTokenSource deadline, long generation,
        JobApplicationAuxiliaryResources? jobResources)
    {
        _source = source;
        _dataSource = dataSource;
        _candidate = candidate;
        _deadline = deadline;
        _deadlineToken = deadline.Token;
        _generation = generation;
        _jobResources = jobResources;
        Unit = new ConfinedUnit(this);
        _jobResources?.HoldActualTask(_actualCompletion.Task);
        // A retained failure may have no remaining caller. Observe it without turning it into success.
        _ = _actualCompletion.Task.ContinueWith(static task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public IUnitOfWork Unit { get; }
    public CancellationToken DeadlineToken => _deadlineToken;
    internal Task ActualCompletion => _actualCompletion.Task;

    /// <summary>Records a concrete command/reader disposal failure; elapsed time or SQL failure is not this witness.</summary>
    public void MarkCleanupFailed()
    {
        _cleanupFailed = true;
        _announcement = null;
        _jobResources?.MarkCleanupFailed();
    }

    public async Task CompleteAsync()
    {
        EnsureLive();
        _terminal = true;
        _announcement = null;
        DeadlineToken.ThrowIfCancellationRequested();
        await _transaction!.CommitAsync(DeadlineToken).ConfigureAwait(false);
        _committed = true;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _terminal = true;
            _announcement = null;
            return new ValueTask(_cleanup ??= CleanupAsync());
        }
    }

    internal async Task OpenAsync()
    {
        DeadlineToken.ThrowIfCancellationRequested();
        _connection = _dataSource.CreateConnection();
        _jobResources?.HoldConnection(_connection);
        await _connection.OpenAsync(DeadlineToken).ConfigureAwait(false);
        await ApplicationDataSource.RefuseBypassRoleAsync(_connection, DeadlineToken).ConfigureAwait(false);
        _transaction = await _connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, DeadlineToken).ConfigureAwait(false);
        _jobResources?.HoldTransaction(_transaction);
        await ExecuteSetupAsync("SET TRANSACTION READ ONLY").ConfigureAwait(false);
        if (_candidate is null) return;
        if (!_candidate.IsActive) throw new InvalidOperationException("Authorization candidate owner is closed.");
        await ExecuteSetupAsync("""
            SELECT set_config('app.tenant_id', @tenant, true),
                   set_config('app.organization_id', @organization, true),
                   set_config('app.scope', '', true), set_config('app.resolving_host', '', true),
                   set_config('app.identity_authority_id', '', true), set_config('app.identity_subject_key', '', true)
            """).ConfigureAwait(false);
        DeadlineToken.ThrowIfCancellationRequested();
        if (!_candidate.IsActive) throw new InvalidOperationException("Authorization candidate owner is closed.");
        _announcement = new TenantContextAnnouncement(_generation, _candidate.TenantId, _candidate.OrganizationId);
    }

    internal async Task<string> QualifyAsync(IdentityAuthorityId authorityId, string issuer, DatabaseQualificationProof? businessProof)
    {
        var command = new NpgsqlCommand("""
            SELECT current_user, current_database(), authority_id, issuer,
                   NOT @has_business_proof OR EXISTS (
                       SELECT 1 FROM pg_catalog.pg_locks
                       WHERE locktype = 'advisory' AND granted AND mode = 'ExclusiveLock'
                         AND database = (SELECT oid FROM pg_catalog.pg_database WHERE datname = current_database())
                         AND pid = @business_pid AND objsubid = 2
                         AND classid::bigint = @business_first AND objid::bigint = @business_second)
            FROM public.identity_authority WHERE id = 1
            """, _connection, _transaction);
        DbDataReader? reader = null;
        try
        {
            DatabaseQualificationProof.Bind(command, businessProof);
            reader = await command.ExecuteReaderAsync(DeadlineToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(DeadlineToken).ConfigureAwait(false)
                || reader.GetString(0) != ApplicationDataSource.RuntimeRole
                || string.IsNullOrEmpty(reader.GetString(1))
                || reader.GetGuid(2) != authorityId.Value
                || !string.Equals(reader.GetString(3), issuer, StringComparison.Ordinal) || !reader.GetBoolean(4))
                throw new InvalidOperationException("Identity authorization startup qualification is unavailable or does not match.");
            businessProof?.RequireActive();
            return reader.GetString(1);
        }
        finally { await DisposeStatementAsync(reader, command).ConfigureAwait(false); }
    }

    private async Task ExecuteSetupAsync(string statement)
    {
        var command = new NpgsqlCommand(statement, _connection, _transaction);
        if (_candidate is not null && statement.StartsWith("SELECT", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("tenant", _candidate.TenantId.Value.ToString("D"));
            command.Parameters.AddWithValue("organization", _candidate.OrganizationId?.Value.ToString("D") ?? string.Empty);
        }
        try { await command.ExecuteNonQueryAsync(DeadlineToken).ConfigureAwait(false); }
        finally { await DisposeStatementAsync(null, command).ConfigureAwait(false); }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Every concrete resource receives its cleanup attempt; any failure retains the owner.")]
    private async Task DisposeStatementAsync(DbDataReader? reader, DbCommand command)
    {
        ExceptionDispatchInfo? failure = null;
        if (reader is not null)
        {
            try { await reader.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        }
        try { await command.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { failure ??= ExceptionDispatchInfo.Capture(exception); }
        if (failure is not null) MarkCleanupFailed();
        failure?.Throw();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "All actual cleanup is attempted and observed independently of the deadline and query outcome.")]
    private async Task CleanupAsync()
    {
        ExceptionDispatchInfo? failure = null;
        if (_jobResources is not null)
        {
            try { await _jobResources.CleanupAsync().ConfigureAwait(false); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        }
        else
        {
            if (_transaction is not null)
            {
                try { await _transaction.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
            }
            if (_connection is not null)
            {
                try { await _connection.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { failure ??= ExceptionDispatchInfo.Capture(exception); }
            }
        }
        _deadline.Dispose();
        if (_cleanupFailed || failure is not null)
        {
            var exception = failure?.SourceException ?? new InvalidOperationException("Authorization resource cleanup is unconfirmed.");
            _actualCompletion.TrySetException(exception);
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
        _transaction = null;
        _connection = null;
        _source.Release(this);
        _actualCompletion.TrySetResult();
    }

    private void EnsureLive()
    {
        DeadlineToken.ThrowIfCancellationRequested();
        JobApplicationSourceOwner.RefuseExternalEntry();
        if (_terminal || _cleanupFailed || _transaction is null || (_candidate is not null && !_candidate.IsActive))
            throw new InvalidOperationException("The authorization read owner is no longer live.");
    }

    /// <summary>Read-only evidence of this child; it cannot open, join, retarget or resolve any unit.</summary>
    private sealed class ConfinedUnit(AuthorizationReadOwner owner) : IUnitOfWork
    {
        public DbConnection Connection { get { owner.EnsureLive(); return owner._connection!; } }
        public DbTransaction? Transaction => owner._terminal || owner._cleanupFailed || owner.DeadlineToken.IsCancellationRequested
            || owner._candidate?.IsActive == false ? null : owner._transaction;
        public bool HasActiveTransaction => Transaction is not null;
        public TransactionMode? Mode => HasActiveTransaction ? TransactionMode.ReadOnly : null;
        public bool IsRollbackOnly => owner._cleanupFailed || owner.DeadlineToken.IsCancellationRequested
            || (owner._terminal && !owner._committed);
        public bool IsTenantContextIssuedOn(DbTransaction? transaction) => GetTenantContextAnnouncement(transaction) is not null;
        public TenantContextAnnouncement? GetTenantContextAnnouncement(DbTransaction? transaction) =>
            transaction is not null && ReferenceEquals(transaction, Transaction) && owner._candidate?.IsActive == true
                ? owner._announcement : null;
        public Task<IUnitOfWorkScope> BeginTransactionAsync(TransactionMode mode, CancellationToken cancellationToken = default) => throw Refused();
        public Task<IUnitOfWorkScope> BeginTransactionAsync(CancellationToken cancellationToken = default) => throw Refused();
        public Task SetTenantContextAsync(ITenantContext context, CancellationToken cancellationToken = default) => throw Refused();
        public Task SetProvisioningTenantContextAsync(TenantId tenantId, CancellationToken cancellationToken = default) => throw Refused();
        public Task CommitAsync(CancellationToken cancellationToken = default) => throw Refused();
        public Task RollbackAsync(CancellationToken cancellationToken = default) => throw Refused();
        public void MarkRollbackOnly() => throw Refused();
        public ValueTask DisposeAsync() => throw Refused();
        private static InvalidOperationException Refused() => new("Only the confined authorization owner controls this child unit.");
    }
}
