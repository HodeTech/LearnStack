using Hangfire.PostgreSql;
using LearnStack.Infrastructure.Persistence;
using Npgsql;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Every vendor acquisition crosses the existing authority and physical-role guards.</summary>
public sealed class HangfireConnectionFactory(PrivilegedDataSource source, BackgroundJobRuntimeGate? gate = null) : IConnectionFactory
{
    private readonly PrivilegedDataSource _source = source ?? throw new ArgumentNullException(nameof(source));

    public NpgsqlConnection GetOrCreateConnection()
    {
        if (gate?.TryAdmitStorage() == false)
            throw new InvalidOperationException("The job storage acquisition gate is closed.");
        PoolHangfireLifecycleOperation.ObservePhysicalOpening();
        // Keep the stock synchronous factory contract, but use the driver's
        // asynchronous DNS/socket deadline path. Its synchronous buffers can
        // round a positive sub-millisecond remainder to an infinite socket wait.
        // Await the actual acquisition; no detached task or substitute timeout.
        try { return _source.OpenConnectionAsync().AsTask().GetAwaiter().GetResult(); }
        finally { PoolHangfireLifecycleOperation.ObservePhysicalOpened(); }
    }
}
