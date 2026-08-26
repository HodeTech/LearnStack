namespace LearnStack.SharedKernel.Caching;

/// <summary>
/// The one cache abstraction, per
/// <see href="../../../../docs/decisions/0038-cross-cutting-port-and-event-contracts.md">ADR-0038</see>.
/// Modules never inject a cache client — no
/// <c>IConnectionMultiplexer</c>, no <c>IDistributedCache</c>, no
/// <c>IMemoryCache</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no <c>RemoveByPrefixAsync</c>.</b> ADR-0038 excludes it: the only
/// implementable form iterated a process-local key set, so
/// keys written by another instance were never evicted — a name promising a
/// global effect while delivering a local one. A key family that must invalidate
/// a set it cannot enumerate uses the **generation-key** pattern instead, which
/// is a caller-side convention rather than a member here: a durable counter
/// bumped inside the business transaction and embedded in the key template, so a
/// write makes every stale key unreachable at once without deleting any of them
/// (<see href="../../../../docs/architecture/32-tenant-customization-model.md">architecture/32
/// § 8.2</see>).
/// </para>
/// <para>
/// <b>A cache miss is never an error.</b> Every method here is allowed to be a
/// no-op — an implementation may evict at any moment for any reason, and a caller
/// that treats a miss as a failure has built a dependency on a component whose
/// contract is "sometimes". Correctness lives in the source of truth; this only
/// makes reading it cheaper.
/// </para>
/// </remarks>
public interface ICacheService
{
    /// <summary>Reads a cached value, or <c>default</c> when there is none.</summary>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a cached value, producing and storing it on a miss.
    /// </summary>
    /// <remarks>
    /// An implementation is expected to run <paramref name="factory"/> <b>once</b>
    /// for concurrent misses on one key. The factory is the expensive side — a
    /// database round trip, a Hub call — and a cache that lets N simultaneous
    /// misses each run it turns a cold key into a stampede against the very
    /// dependency it exists to spare.
    /// </remarks>
    Task<T> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        CacheOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Stores a value.</summary>
    Task SetAsync<T>(
        string key,
        T value,
        CacheOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Drops one key. Dropping a key that is not there is not an error.</summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}
