using LearnStack.Application.Jobs;
using Npgsql;

namespace LearnStack.Infrastructure.Persistence;

/// <summary>Owns a lazy bypass pool without exposing an unguarded source or cached credential.</summary>
public sealed class PrivilegedDataSource(Func<NpgsqlDataSource> factory) : IDisposable, IAsyncDisposable
{
    private readonly Lazy<NpgsqlDataSource> _source = new(factory ?? throw new ArgumentNullException(nameof(factory)));
    private readonly object _gate = new();
    private readonly TaskCompletionSource _disposalRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _positiveCompletion;
    private Task? _retentionObserver;
    private Exception? _retentionFailure;
    private Exception? _disposalFailure;
    private bool _disposeRequested;
    private bool _retentionRegistered;
    private bool _positiveCompletionConfirmed;
    private bool _disposed;
    private bool _disposalComplete;

    /// <summary>Whether the private pool has been built; reading opens nothing.</summary>
    public bool IsValueCreated => _source.IsValueCreated;

    internal bool HasUnconfirmedCleanup
    {
        get { lock (_gate) return _retentionFailure is not null || _disposalFailure is not null; }
    }

    internal bool IsDisposalComplete
    {
        get { lock (_gate) return _disposalComplete; }
    }

    /// <summary>Binds the sole closed runtime witness before any disposal request.</summary>
    internal void RetainBackgroundJobsUntil(Task positiveCompletion)
    {
        ArgumentNullException.ThrowIfNull(positiveCompletion);
        lock (_gate)
        {
            if (_retentionRegistered)
                throw new InvalidOperationException("The background-job source lifetime is already bound.");
            ObjectDisposedException.ThrowIf(_disposeRequested || _disposed, this);
            _retentionRegistered = true;
            _positiveCompletion = positiveCompletion;
            if (ExecutionContext.IsFlowSuppressed()) _retentionObserver = ObserveRetentionAsync();
            else
            {
                using (ExecutionContext.SuppressFlow()) _retentionObserver = ObserveRetentionAsync();
            }
        }
    }

    /// <summary>Checks ordinary-job authority on every synchronous acquisition, including a warm pool.</summary>
    public NpgsqlConnection OpenConnection()
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        // The stock factory and schema bootstrap have synchronous contracts.
        // Join the driver's actual async DNS/socket path: its sync buffers can
        // round a positive sub-millisecond remainder to an infinite socket wait.
        // No detached task, substitute timeout or authority check after Resolve.
        return Resolve().OpenConnectionAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Checks ordinary-job authority before credential construction, cancellation or pool reuse.</summary>
    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        return Resolve().OpenConnectionAsync(cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var source = TakeForDisposal();
        if (source is null) return;
        try
        {
            source.Dispose();
            lock (_gate) _disposalComplete = true;
        }
        catch (Exception failure)
        {
            lock (_gate) _disposalFailure = failure;
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var source = TakeForDisposal();
        if (source is not null) await DisposeSourceAsync(source).ConfigureAwait(false);
    }

    private NpgsqlDataSource Resolve()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _source.Value;
        }
    }

    private NpgsqlDataSource? TakeForDisposal()
    {
        lock (_gate)
        {
            _disposeRequested = true;
            if (_retentionRegistered)
            {
                // A container's disposal request is not a join. The sole observer
                // owns actual disposal, and admitted completion can still Resolve.
                _disposalRequested.TrySetResult();
                return null;
            }
            return TakeSourceUnderGate();
        }
    }

    private NpgsqlDataSource? TakeSourceUnderGate()
    {
        if (_disposed) return null;
        _disposed = true;
        if (_source.IsValueCreated) return _source.Value;
        _disposalComplete = true;
        return null;
    }

    private async Task ObserveRetentionAsync()
    {
        try
        {
            await _positiveCompletion!.ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            // A returned fault/cancellation supplies no positive release witness.
            lock (_gate) _retentionFailure = failure;
            return;
        }

        lock (_gate) _positiveCompletionConfirmed = true;
        await _disposalRequested.Task.ConfigureAwait(false);
        NpgsqlDataSource? source;
        lock (_gate)
        {
            if (!_positiveCompletionConfirmed || !_disposeRequested) return;
            source = TakeSourceUnderGate();
        }
        if (source is null) return;
        try
        {
            await DisposeSourceAsync(source).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // DisposeSourceAsync records the failure. The lazy retains the source;
            // repeated Dispose cannot relabel the driver's later no-op as cleanup.
        }
    }

    private async ValueTask DisposeSourceAsync(NpgsqlDataSource source)
    {
        try
        {
            await source.DisposeAsync().ConfigureAwait(false);
            lock (_gate) _disposalComplete = true;
        }
        catch (Exception failure)
        {
            lock (_gate) _disposalFailure = failure;
            throw;
        }
    }
}
