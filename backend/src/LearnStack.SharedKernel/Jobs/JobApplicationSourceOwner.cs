using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.SharedKernel.Jobs;

/// <summary>Closed job-origin ownership of one independent application connection.</summary>
/// <remarks>
/// The executor retains one owner across framework and business scopes. A cache
/// wait ending does not release its actual loader. No provider or work callback is
/// accepted here; the four business helpers and the confined authorization/evidence
/// lanes retain their concrete resources.
/// </remarks>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Only the actual unit/helper cleanup can release retained resources; caller disposal supplies no witness.")]
public sealed class JobApplicationSourceOwner
{
    private static readonly AsyncLocal<Binding?> Ambient = new();
    private static readonly AsyncLocal<object?> External = new();
    private readonly object _sync = new();
    private JobApplicationAuxiliaryResources? _auxiliary;
    private JobApplicationAuxiliaryResources? _authorization;
    private JobApplicationAuxiliaryResources? _evidence;
    private IUnitOfWork? _mainUnit;
    private DbConnection? _mainConnection;
    private DbTransaction? _mainTransaction;
    private bool _closed;
    private bool _cleanupFailed;
    private bool _used;
    private bool _externalRecovery;

    private JobApplicationSourceOwner() { }

    /// <summary>
    /// Closed creation seam for original execution, fresh recovery and terminal no-start cleanup,
    /// before the owning flow starts its first application read.
    /// </summary>
    public static JobApplicationSourceOwner Create() => new();

    /// <summary>A stale inherited binding remains visible and refuses new work.</summary>
    public static JobApplicationSourceOwner? Current => Ambient.Value?.Owner;

    /// <summary>Current execution lifetime, independent of temporary auxiliary-lane readiness.</summary>
    public bool IsCurrentBinding
    {
        get
        {
            lock (_sync)
                return !IsExternal && !_closed && !_cleanupFailed
                    && Ambient.Value is { IsOpen: true } binding && ReferenceEquals(binding.Owner, this);
        }
    }

    /// <summary>Denial remains visible in inherited tasks after their parent's scope restores.</summary>
    public static bool IsExternal => External.Value is not null;

    public static void RefuseExternalEntry()
    {
        if (IsExternal) throw Refused();
    }

    /// <summary>Denial only; neither an application source nor executable authority is created.</summary>
    public static IDisposable EnterExternal()
    {
        RefuseExternalEntry();
        Current?.RequireFreshScopeReady();
        var marker = new object();
        External.Value = marker;
        return new ExternalBinding(marker);
    }

    /// <summary>Binds this same owner separately around framework and business work.</summary>
    public IDisposable Enter()
    {
        RefuseExternalEntry();
        lock (_sync)
        {
            if (_closed || Ambient.Value is not null) throw Refused();
            var binding = new Binding(this);
            Ambient.Value = binding;
            return binding;
        }
    }

    /// <summary>Actual resources and loader task have both positively finished.</summary>
    public bool CleanupConfirmed
    {
        get
        {
            lock (_sync)
            {
                _auxiliary?.TryRelease();
                _authorization?.TryRelease();
                _evidence?.TryRelease();
                return !_cleanupFailed && _auxiliary is null && _authorization is null && _evidence is null && _mainUnit is null
                    && _mainConnection is null && _mainTransaction is null;
            }
        }
    }

    /// <summary>Refuses activation, another physical acquisition or commit while cleanup is unresolved.</summary>
    public void RequireBusinessReady()
    {
        RefuseExternalEntry();
        lock (_sync)
        {
            RequireBinding();
            _auxiliary?.TryRelease();
            _authorization?.TryRelease();
            _evidence?.TryRelease();
            if (_closed || _cleanupFailed || _auxiliary is not null || _authorization is not null || _evidence is not null) throw Refused();
        }
    }

    public void RequireFreshScopeReady()
    {
        lock (_sync)
        {
            RequireBusinessReady();
            if (_mainUnit is not null) throw Refused();
        }
    }

    public void RequireOwnedMainUnit(IUnitOfWork unit)
    {
        lock (_sync)
        {
            RequireBusinessReady();
            if (!ReferenceEquals(_mainUnit, unit)) throw Refused();
        }
    }

    /// <summary>The PostgreSQL unit claims its sole slot before creating/opening a connection.</summary>
    public void BindMainUnit(IUnitOfWork unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        lock (_sync)
        {
            RequireBusinessReady();
            if (_mainUnit is not null && !ReferenceEquals(_mainUnit, unit)) throw Refused();
            _mainUnit = unit;
            _used = true;
        }
    }

    public void HoldMainConnection(IUnitOfWork unit, DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        lock (_sync)
        {
            if (!ReferenceEquals(_mainUnit, unit) || _mainConnection is not null) throw Refused();
            _mainConnection = connection;
        }
    }

    public void HoldMainTransaction(IUnitOfWork unit, DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        lock (_sync)
        {
            if (!ReferenceEquals(_mainUnit, unit) || _mainTransaction is not null) throw Refused();
            _mainTransaction = transaction;
        }
    }

    public void ReportMainTransactionCleanup(IUnitOfWork unit, DbTransaction transaction, bool succeeded)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_mainUnit, unit) || !ReferenceEquals(_mainTransaction, transaction)) throw Refused();
            if (succeeded) _mainTransaction = null;
            else _cleanupFailed = true;
        }
    }

    public void ReportMainCleanup(IUnitOfWork unit, bool succeeded)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_mainUnit, unit)) throw Refused();
            if (!succeeded || _mainTransaction is not null) _cleanupFailed = true;
            if (!_cleanupFailed)
            {
                _mainUnit = null;
                _mainConnection = null;
            }
        }
    }

    /// <summary>Host/token edge readers cannot acquire another connection from an ordinary job flow.</summary>
    public static void RefuseUnrelatedRead()
    {
        if (IsExternal || Ambient.Value is not null) throw Refused();
    }

    /// <summary>Stops issuance without treating retained work as completed cleanup.</summary>
    public void CloseIssuance()
    {
        lock (_sync) _closed = true;
    }

    /// <summary>Joins the original auxiliary loader before the executor freezes its cleanup result.</summary>
    public async Task<bool> JoinAuxiliaryCleanupAsync()
    {
        JobApplicationAuxiliaryResources? auxiliary;
        JobApplicationAuxiliaryResources? authorization;
        JobApplicationAuxiliaryResources? evidence;
        lock (_sync) { auxiliary = _auxiliary; authorization = _authorization; evidence = _evidence; }
        if (auxiliary is not null) await auxiliary.JoinActualTaskAsync().ConfigureAwait(false);
        if (authorization is not null) await authorization.JoinActualTaskAsync().ConfigureAwait(false);
        if (evidence is not null) await evidence.JoinActualTaskAsync().ConfigureAwait(false);
        return CleanupConfirmed;
    }

    public JobApplicationAuxiliaryResources BeginAuditConfigLoad() => BeginAuxiliary();
    public JobApplicationAuxiliaryResources BeginStandaloneAudit() => BeginAuxiliary();
    public JobApplicationAuxiliaryResources BeginTenantFlagsLoad() => BeginAuxiliary();
    public JobApplicationAuxiliaryResources BeginKillswitchLoad() => BeginAuxiliary();

    /// <summary>The globally bounded authorization source retains this independent lane; the main owner may stay open.</summary>
    public JobApplicationAuxiliaryResources BeginAuthorizationRead() => BeginConfinedLane(authorization: true);

    /// <summary>The globally bounded evidence source retains this independent lane until actual completion.</summary>
    public JobApplicationAuxiliaryResources BeginAuthorizationEvidence() => BeginConfinedLane(authorization: false);

    /// <summary>Marks one genuinely new source before recovery's first physical read.</summary>
    public void BindExternalRecovery()
    {
        lock (_sync)
        {
            RequireFreshScopeReady();
            if (_used || _externalRecovery) throw Refused();
            _externalRecovery = true;
        }
    }

    private JobApplicationAuxiliaryResources BeginAuxiliary()
    {
        RefuseExternalEntry();
        lock (_sync)
        {
            RequireBinding();
            _auxiliary?.TryRelease();
            _authorization?.TryRelease();
            _evidence?.TryRelease();
            if (_closed || _cleanupFailed || _auxiliary is not null || _authorization is not null || _evidence is not null) throw Refused();
            _used = true;
            return _auxiliary = new JobApplicationAuxiliaryResources(this);
        }
    }

    private JobApplicationAuxiliaryResources BeginConfinedLane(bool authorization)
    {
        RefuseExternalEntry();
        lock (_sync)
        {
            RequireBinding();
            _auxiliary?.TryRelease();
            _authorization?.TryRelease();
            _evidence?.TryRelease();
            if (_closed || _cleanupFailed || _auxiliary is not null || _authorization is not null || _evidence is not null)
                throw Refused();
            _used = true;
            var resources = new JobApplicationAuxiliaryResources(this);
            if (authorization) _authorization = resources;
            else _evidence = resources;
            return resources;
        }
    }

    internal void ReportCleanupFailure(JobApplicationAuxiliaryResources auxiliary)
    {
        lock (_sync)
        {
            if (!OwnsAuxiliary(auxiliary)) throw Refused();
            _cleanupFailed = true;
        }
    }

    internal void Release(JobApplicationAuxiliaryResources auxiliary)
    {
        lock (_sync)
        {
            if (auxiliary.IsReleased) return;
            if (!OwnsAuxiliary(auxiliary)) throw Refused();
            if (ReferenceEquals(_auxiliary, auxiliary)) _auxiliary = null;
            else if (ReferenceEquals(_authorization, auxiliary)) _authorization = null;
            else _evidence = null;
            auxiliary.MarkReleased();
        }
    }

    private bool OwnsAuxiliary(JobApplicationAuxiliaryResources resources) =>
        ReferenceEquals(_auxiliary, resources) || ReferenceEquals(_authorization, resources) || ReferenceEquals(_evidence, resources);

    private void RequireBinding()
    {
        if (Ambient.Value is not { IsOpen: true } binding || !ReferenceEquals(binding.Owner, this))
            throw Refused();
    }

    private static InvalidOperationException Refused() =>
        new("The job application-source owner refuses another acquisition or activation.");

    private sealed class Binding(JobApplicationSourceOwner owner) : IDisposable
    {
        private int _closed;
        internal JobApplicationSourceOwner Owner { get; } = owner;
        internal bool IsOpen => Volatile.Read(ref _closed) == 0;

        public void Dispose()
        {
            Interlocked.Exchange(ref _closed, 1);
            if (ReferenceEquals(Ambient.Value, this)) Ambient.Value = null;
        }
    }

    private sealed class ExternalBinding(object marker) : IDisposable
    {
        public void Dispose()
        {
            if (ReferenceEquals(External.Value, marker)) External.Value = null;
        }
    }
}

/// <summary>The concrete resources of a closed business helper or confined authorization/evidence lane.</summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Actual cleanup is explicit and one-shot; disposing a returned caller handle cannot prove task completion.")]
public sealed class JobApplicationAuxiliaryResources
{
    private readonly object _sync = new();
    private readonly JobApplicationSourceOwner _owner;
    private DbConnection? _connection;
    private DbTransaction? _transaction;
    private readonly List<DbCommand> _commands = new(3);
    private DbDataReader? _reader;
    private Task? _actualTask;
    private bool _readerDisposalAttempted;
    private bool _cleanupAttempted;
    private bool _cleanupConfirmed;
    private bool _readerCleanupFailed;
    private bool _released;

    internal JobApplicationAuxiliaryResources(JobApplicationSourceOwner owner) => _owner = owner;

    public void HoldConnection(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (_connection is not null || _cleanupAttempted) throw InvalidOwnership();
        _connection = connection;
    }

    public void HoldTransaction(DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (_transaction is not null || _cleanupAttempted) throw InvalidOwnership();
        _transaction = transaction;
    }

    public void HoldCommand(DbCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_commands.Count == 3 || _cleanupAttempted) throw InvalidOwnership();
        _commands.Add(command);
    }

    public void HoldReader(DbDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (_reader is not null || _cleanupAttempted) throw InvalidOwnership();
        _reader = reader;
    }

    /// <summary>Retains the helper itself, including a cache factory abandoned by its waiter.</summary>
    public void HoldActualTask(Task actualTask)
    {
        ArgumentNullException.ThrowIfNull(actualTask);
        lock (_sync)
        {
            if (_actualTask is not null) throw InvalidOwnership();
            _actualTask = actualTask;
        }
        _ = actualTask.ContinueWith(static (_, state) =>
            ((JobApplicationAuxiliaryResources)state!).TryRelease(), this,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Concrete caller-owned command/reader cleanup failed; later disposal cannot replace that witness.</summary>
    public void MarkCleanupFailed()
    {
        _readerCleanupFailed = true;
        _owner.ReportCleanupFailure(this);
    }

    /// <summary>A reader must finish before its helper issues COMMIT.</summary>
    public async ValueTask DisposeReaderAsync()
    {
        if (_reader is null || _readerDisposalAttempted) throw InvalidOwnership();
        _readerDisposalAttempted = true;
        try { await _reader.DisposeAsync().ConfigureAwait(false); }
        catch
        {
            _readerCleanupFailed = true;
            _owner.ReportCleanupFailure(this);
            throw;
        }
    }

    /// <summary>Attempts every actual disposal; a failed attempt is never retried as evidence.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Every concrete resource must receive its actual cleanup attempt before the failure is rethrown.")]
    public async ValueTask CleanupAsync()
    {
        if (_cleanupAttempted) throw InvalidOwnership();
        _cleanupAttempted = true;
        ExceptionDispatchInfo? failure = null;
        if (_reader is not null && !_readerDisposalAttempted)
        {
            try { await DisposeReaderAsync().ConfigureAwait(false); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        }
        for (var index = _commands.Count - 1; index >= 0; index--)
        {
            try { await _commands[index].DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { failure ??= ExceptionDispatchInfo.Capture(exception); }
        }
        if (_transaction is not null)
        {
            try { await _transaction.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { failure ??= ExceptionDispatchInfo.Capture(exception); }
        }
        if (_connection is not null)
        {
            try { await _connection.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { failure ??= ExceptionDispatchInfo.Capture(exception); }
        }
        if (failure is not null) _owner.ReportCleanupFailure(this);
        lock (_sync) _cleanupConfirmed = failure is null && !_readerCleanupFailed;
        TryRelease();
        failure?.Throw();
    }

    internal void TryRelease()
    {
        lock (_sync)
        {
            if (_released || !_cleanupConfirmed || _actualTask?.IsCompleted != true) return;
        }
        _owner.Release(this);
    }

    internal async Task JoinActualTaskAsync()
    {
        Task? actual;
        lock (_sync) actual = _actualTask;
        if (actual is null) return;
        try { await actual.ConfigureAwait(false); }
        catch (Exception failure) when (failure.GetType() != typeof(OutOfMemoryException)
            && failure.GetType() != typeof(StackOverflowException))
        {
            // A query failure is not a resource-cleanup failure. CleanupAsync
            // supplies the independent positive witness, including after timeout.
        }
    }

    internal bool IsReleased => Volatile.Read(ref _released);
    internal void MarkReleased() => Volatile.Write(ref _released, true);

    private static InvalidOperationException InvalidOwnership() =>
        new("A job auxiliary operation retains each concrete resource and actual task once.");
}
