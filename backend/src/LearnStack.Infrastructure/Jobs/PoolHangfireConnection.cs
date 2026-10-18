using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Preserves the concrete extended connection API and isolates the three server lifecycle calls.</summary>
internal sealed class PoolHangfireConnection(JobStorage provider, PoolHangfireLifecycle lifecycle, bool readOnly,
    PoolHangfireStorage? contract = null)
    : JobStorageConnection
{
    private readonly JobStorage _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    private readonly PoolHangfireLifecycle _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    private readonly object _gate = new();
    private JobStorageConnection? _connection;
    private bool _disposed;

    public override IWriteOnlyTransaction CreateWriteTransaction() => Connection.CreateWriteTransaction();
    public override IDisposable AcquireDistributedLock(string resource, TimeSpan timeout) => Connection.AcquireDistributedLock(resource, timeout);
    public override string CreateExpiredJob(Job job, IDictionary<string, string> parameters, DateTime createdAt, TimeSpan expireIn) =>
        Connection.CreateExpiredJob(job, parameters, createdAt, expireIn);
    public override IFetchedJob FetchNextJob(string[] queues, CancellationToken cancellationToken) => Connection.FetchNextJob(queues, cancellationToken);
    public override void SetJobParameter(string id, string name, string value) => Connection.SetJobParameter(id, name, value);
    public override string GetJobParameter(string id, string name) => Connection.GetJobParameter(id, name);
    public override JobData GetJobData(string jobId) => Connection.GetJobData(jobId);
    public override StateData GetStateData(string jobId) => Connection.GetStateData(jobId);
    public override int RemoveTimedOutServers(TimeSpan timeOut) => Connection.RemoveTimedOutServers(timeOut);
    public override HashSet<string> GetAllItemsFromSet(string key) => Connection.GetAllItemsFromSet(key);
    public override string GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore) =>
        Connection.GetFirstByLowestScoreFromSet(key, fromScore, toScore);
    public override List<string> GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore, int count) =>
        Connection.GetFirstByLowestScoreFromSet(key, fromScore, toScore, count);
    public override long GetSetCount(string key) => Connection.GetSetCount(key);
    public override long GetSetCount(IEnumerable<string> keys, int limit) => Connection.GetSetCount(keys, limit);
    public override bool GetSetContains(string key, string value) => Connection.GetSetContains(key, value);
    public override List<string> GetRangeFromSet(string key, int startingFrom, int endingAt) => Connection.GetRangeFromSet(key, startingFrom, endingAt);
    public override TimeSpan GetSetTtl(string key) => Connection.GetSetTtl(key);
    public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs) => Connection.SetRangeInHash(key, keyValuePairs);
    public override Dictionary<string, string> GetAllEntriesFromHash(string key) => Connection.GetAllEntriesFromHash(key);
    public override string GetValueFromHash(string key, string name) => Connection.GetValueFromHash(key, name);
    public override long GetHashCount(string key) => Connection.GetHashCount(key);
    public override TimeSpan GetHashTtl(string key) => Connection.GetHashTtl(key);
    public override long GetListCount(string key) => Connection.GetListCount(key);
    public override List<string> GetAllItemsFromList(string key) => Connection.GetAllItemsFromList(key);
    public override List<string> GetRangeFromList(string key, int startingFrom, int endingAt) => Connection.GetRangeFromList(key, startingFrom, endingAt);
    public override TimeSpan GetListTtl(string key) => Connection.GetListTtl(key);
    public override long GetCounter(string key) => Connection.GetCounter(key);
    public override DateTime GetUtcDateTime() => Connection.GetUtcDateTime();

    public override void AnnounceServer(string serverId, ServerContext context) =>
        _lifecycle.AnnounceServer(CreateOperation(PoolHangfireLifecycleMethod.Announce, serverId, context));
    public override void Heartbeat(string serverId) =>
        _lifecycle.Heartbeat(CreateOperation(PoolHangfireLifecycleMethod.Heartbeat, serverId));
    public override void RemoveServer(string serverId) =>
        _lifecycle.RemoveServer(CreateOperation(PoolHangfireLifecycleMethod.Remove, serverId));

    public override void Dispose()
    {
        try
        {
            JobStorageConnection? connection;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                connection = _connection;
            }
            // Lifecycle operations own their separate connection through actual disposal.
            // A failure there is retained by the lifecycle owner, never retried here.
            connection?.Dispose();
            GC.SuppressFinalize(this);
        }
        finally { base.Dispose(); }
    }

    private PoolHangfireLifecycleOperation CreateOperation(PoolHangfireLifecycleMethod method,
        string serverId, ServerContext? context = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new(_provider, readOnly, method, serverId, context, contract);
        }
    }

    private JobStorageConnection Connection
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                contract?.ValidateContract();
                if (_connection is not null) return _connection;
                var connection = readOnly ? _provider.GetReadOnlyConnection() : _provider.GetConnection();
                if (connection is JobStorageConnection extended) return _connection = extended;
                connection.Dispose();
                throw new InvalidOperationException("The provider no longer exposes the pinned extended connection contract.");
            }
        }
    }
}

/// <summary>
/// Closed generation-owner capability, not a service or a module execution port.
/// Implementations own retry/stop fences and retain operations whose disposal fails.
/// </summary>
internal abstract class PoolHangfireLifecycle
{
    internal abstract void AnnounceServer(PoolHangfireLifecycleOperation operation);
    internal abstract void Heartbeat(PoolHangfireLifecycleOperation operation);
    internal abstract void RemoveServer(PoolHangfireLifecycleOperation operation);
}

internal enum PoolHangfireLifecycleMethod { Announce, Heartbeat, Remove }
internal enum PoolHangfireLifecyclePhase { Created, Acquiring, Invoking, Disposing, Returned }

internal sealed record PoolHangfireLifecycleResult(Exception? Failure, Exception? DisposalFailure)
{
    internal bool Succeeded => Failure is null && DisposalFailure is null;
}

/// <summary>An exact stock lifecycle call; no arbitrary callback or raw connection leaves this capability.</summary>
internal sealed class PoolHangfireLifecycleOperation(JobStorage provider, bool readOnly,
    PoolHangfireLifecycleMethod method, string serverId, ServerContext? context, PoolHangfireStorage? contract = null)
{
    private readonly object _gate = new();
    private IStorageConnection? _retainedConnection;
    private bool _executing;
    private bool _attempted;
    private bool _succeeded;
    private int _phase;
    private int _attempts;

    internal PoolHangfireLifecycleMethod Method => method;
    // Observation only: Returned does not attest successful disposal or child join.
    internal PoolHangfireLifecyclePhase Phase => (PoolHangfireLifecyclePhase)Volatile.Read(ref _phase);
    internal int Attempts => Volatile.Read(ref _attempts);

    internal bool RetainsConnection
    {
        get { lock (_gate) return _retainedConnection is not null; }
    }

    internal PoolHangfireLifecycleResult ExecuteAttempt()
    {
        lock (_gate)
        {
            if (_executing || _succeeded || _retainedConnection is not null
                || _attempted && method != PoolHangfireLifecycleMethod.Heartbeat)
                throw new InvalidOperationException("The lifecycle operation cannot start another attempt.");
            _executing = true;
            _attempted = true;
            Interlocked.Increment(ref _attempts);
        }

        IStorageConnection? connection = null;
        Exception? failure = null;
        Exception? disposalFailure = null;
        var invoked = false;
        try
        {
            Volatile.Write(ref _phase, (int)PoolHangfireLifecyclePhase.Acquiring);
            contract?.ValidateContract();
            connection = readOnly ? provider.GetReadOnlyConnection() : provider.GetConnection();
            Volatile.Write(ref _phase, (int)PoolHangfireLifecyclePhase.Invoking);
            switch (method)
            {
                case PoolHangfireLifecycleMethod.Announce: connection.AnnounceServer(serverId, context!); break;
                case PoolHangfireLifecycleMethod.Heartbeat: connection.Heartbeat(serverId); break;
                case PoolHangfireLifecycleMethod.Remove: connection.RemoveServer(serverId); break;
                default: throw new InvalidOperationException("The lifecycle method is not supported.");
            }
            invoked = true;
        }
        catch (Exception exception) when (IsCatchable(exception))
        {
            failure = exception;
        }
        finally
        {
            try
            {
                Volatile.Write(ref _phase, (int)PoolHangfireLifecyclePhase.Disposing);
                connection?.Dispose();
            }
            catch (Exception exception) when (IsCatchable(exception))
            {
                disposalFailure = exception;
                lock (_gate) _retainedConnection = connection;
            }
            finally
            {
                lock (_gate)
                {
                    _succeeded = invoked && failure is null && disposalFailure is null;
                    _executing = false;
                    Volatile.Write(ref _phase, (int)PoolHangfireLifecyclePhase.Returned);
                }
            }
        }
        return new(failure, disposalFailure);
    }

    private static bool IsCatchable(Exception exception) =>
        exception.GetType() != typeof(OutOfMemoryException) && exception.GetType() != typeof(StackOverflowException);
}
