using Npgsql;
using LearnStack.Infrastructure.Jobs;

namespace LearnStack.Infrastructure.Persistence;

/// <summary>The API's lazy application pool, retained through actual background-job cleanup.</summary>
public sealed class ApplicationDataSourceLifetime : IDisposable, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Lazy<NpgsqlDataSource> _source;
    private readonly TaskCompletionSource _disposalRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _positiveCompletion;
    private Task? _retentionObserver;
    private bool _retentionRegistered;
    private bool _disposeRequested;
    private bool _disposed;
    private bool _disposalComplete;
    private Exception? _cleanupFailure;

    public ApplicationDataSourceLifetime(string? connectionString)
    {
        if (!string.IsNullOrWhiteSpace(connectionString)) ApplicationDataSource.Validate(connectionString);
        var parsed = new NpgsqlConnectionStringBuilder(connectionString ?? string.Empty);
        PoolSettings = new BackgroundJobApplicationPoolSettings(parsed.Pooling, parsed.MaxPoolSize);
        _source = new Lazy<NpgsqlDataSource>(() =>
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return ApplicationDataSource.Build(connectionString);
            }
        });
    }

    /// <summary>The one lazy source registered for all application-role consumers.</summary>
    public Lazy<NpgsqlDataSource> Source => _source;

    public BackgroundJobApplicationPoolSettings PoolSettings { get; }

    internal bool IsDisposalComplete { get { lock (_sync) return _disposalComplete; } }
    internal bool HasUnconfirmedCleanup { get { lock (_sync) return _cleanupFailure is not null; } }

    /// <summary>The runtime binds its positive actual completion before work or disposal is issued.</summary>
    internal void RetainBackgroundJobsUntil(Task positiveCompletion)
    {
        ArgumentNullException.ThrowIfNull(positiveCompletion);
        lock (_sync)
        {
            if (_retentionRegistered) throw new InvalidOperationException("The application job-source lifetime is already bound.");
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

    public void Dispose()
    {
        var source = TakeForDisposal();
        if (source is null) return;
        try
        {
            source.Dispose();
            lock (_sync) _disposalComplete = true;
        }
        catch (Exception failure)
        {
            lock (_sync) _cleanupFailure = failure;
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var source = TakeForDisposal();
        if (source is not null) await DisposeSourceAsync(source).ConfigureAwait(false);
    }

    private NpgsqlDataSource? TakeForDisposal()
    {
        lock (_sync)
        {
            _disposeRequested = true;
            if (_retentionRegistered)
            {
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
        try { await _positiveCompletion!.ConfigureAwait(false); }
        catch (Exception failure)
        {
            lock (_sync) _cleanupFailure = failure;
            return;
        }
        await _disposalRequested.Task.ConfigureAwait(false);
        NpgsqlDataSource? source;
        lock (_sync) source = TakeSourceUnderGate();
        if (source is null) return;
        try { await DisposeSourceAsync(source).ConfigureAwait(false); }
        catch (Exception)
        {
            // The failed actual disposal stays recorded; no later no-op supplies a witness.
        }
    }

    private async ValueTask DisposeSourceAsync(NpgsqlDataSource source)
    {
        try
        {
            await source.DisposeAsync().ConfigureAwait(false);
            lock (_sync) _disposalComplete = true;
        }
        catch (Exception failure)
        {
            lock (_sync) _cleanupFailure = failure;
            throw;
        }
    }
}
