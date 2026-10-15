using LearnStack.Application.Jobs;
using Npgsql;

namespace LearnStack.Infrastructure.Persistence;

/// <summary>Owns a lazy bypass pool without exposing an unguarded source or cached credential.</summary>
public sealed class PrivilegedDataSource(Func<NpgsqlDataSource> factory) : IDisposable, IAsyncDisposable
{
    private readonly Lazy<NpgsqlDataSource> _source = new(factory ?? throw new ArgumentNullException(nameof(factory)));
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>Whether the private pool has been built; reading opens nothing.</summary>
    public bool IsValueCreated => _source.IsValueCreated;

    /// <summary>Checks ordinary-job authority on every synchronous acquisition, including a warm pool.</summary>
    public NpgsqlConnection OpenConnection()
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        return Resolve().OpenConnection();
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
        TakeForDisposal()?.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var source = TakeForDisposal();
        if (source is not null) await source.DisposeAsync().ConfigureAwait(false);
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
            if (_disposed) return null;
            _disposed = true;
            return _source.IsValueCreated ? _source.Value : null;
        }
    }
}
