using System.Collections.Concurrent;
using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Infrastructure.Caching;

/// <summary>
/// The default <see cref="ICacheService"/>: correct for one process, and — unlike
/// the idempotency store next door — not silently wrong for two.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not shared between instances.</b> Two application instances each
/// hold their own map, so a value written by one is not visible to the other and
/// a <see cref="RemoveAsync"/> on one does not evict the other's copy. That is a
/// <i>staleness</i> bound, not a correctness bug: a cache miss is never an error,
/// and the source of truth is unaffected. The Valkey-backed adapter lands on its
/// <see href="../../../../docs/decisions/0035-demand-gated-infrastructure.md">ADR-0035</see>
/// trigger — more than one application instance running concurrently — and until
/// then a second instance costs cache hit rate rather than correctness.
/// </para>
/// <para>
/// <b>Why this evicts freely and <c>InMemoryIdempotencyStore</c> does not.</b>
/// The two look like the same shape and carry opposite rules. An idempotency
/// record is a promise for the length of its window, so dropping one lets an
/// operation run twice; a cache entry promises nothing, so dropping one costs a
/// round trip. Capacity here is eviction, and there it is admission — the same
/// bound in the same kind of dictionary, decided the other way, because the
/// contracts differ.
/// </para>
/// </remarks>
public sealed class InMemoryCacheService(IClock clock) : ICacheService
{
    /// <summary>
    /// The TTL an entry gets when the caller names none — Standards 20's
    /// hot-path default.
    /// </summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How often the map is swept for expired entries. Reclamation only: an
    /// expired entry is never returned, whether or not a sweep has run.
    /// </summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The most entries held at once. A cache with no bound is an
    /// out-of-memory condition waiting for a caller with an unbounded key space.
    /// </summary>
    public const int MaxEntries = 10_000;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// One factory run per key, however many callers miss at once.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _inFlight =
        new(StringComparer.Ordinal);

    private long _lastSweepTicks;

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        CacheKey.EnsureValid(key);

        var now = clock.UtcNow;
        Sweep(now);

        if (_entries.TryGetValue(key, out var entry) && entry.IsFresh(now))
        {
            return Task.FromResult((T?)entry.Value);
        }

        return Task.FromResult<T?>(default);
    }

    public async Task<T> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        CacheOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CacheKey.EnsureValid(key);
        ArgumentNullException.ThrowIfNull(factory);

        var now = clock.UtcNow;
        Sweep(now);

        if (_entries.TryGetValue(key, out var hit) && hit.IsFresh(now))
        {
            return (T)hit.Value!;
        }

        // Lazy with ExecutionAndPublication, not a bare GetOrAdd: the value
        // factory of a ConcurrentDictionary may run more than once under
        // contention, and running the caller's factory twice is the stampede
        // this method exists to prevent.
        var flight = _inFlight.GetOrAdd(
            key,
            _ => new Lazy<Task<object?>>(
                async () => await factory(cancellationToken).ConfigureAwait(false),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            var produced = (T)(await flight.Value.ConfigureAwait(false))!;
            Store(key, produced, options, clock.UtcNow);
            return produced;
        }
        finally
        {
            // Value-comparing, so a later flight started by another caller is
            // not removed by this one's cleanup.
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<object?>>>(key, flight));
        }
    }

    public Task SetAsync<T>(
        string key,
        T value,
        CacheOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CacheKey.EnsureValid(key);

        var now = clock.UtcNow;
        Sweep(now);
        Store(key, value, options, now);

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        CacheKey.EnsureValid(key);

        _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    private void Store<T>(string key, T value, CacheOptions? options, DateTimeOffset now)
    {
        // L2Ttl is read and ignored: there is no second layer here. Carrying it
        // means a caller written today does not change when the Valkey adapter
        // gives it a meaning.
        var ttl = options?.L1Ttl ?? DefaultTtl;

        _entries[key] = new Entry(value, now + ttl, now);
    }

    /// <summary>
    /// Drops expired entries, and — only when the map is over its bound — the
    /// oldest live ones. At most once per <see cref="SweepInterval"/>.
    /// </summary>
    private void Sweep(DateTimeOffset now)
    {
        var ticks = now.UtcTicks;
        var last = Interlocked.Read(ref _lastSweepTicks);

        // A clock that steps backwards would otherwise wedge the sweep until
        // real time caught up.
        if (ticks >= last && ticks - last < SweepInterval.Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastSweepTicks, ticks, last) != last)
        {
            return;
        }

        var live = 0;

        foreach (var pair in _entries)
        {
            if (pair.Value.IsFresh(now))
            {
                live++;
                continue;
            }

            // Value-comparing: between the enumerator observing an expired entry
            // and this line, another thread may have written a fresh one at the
            // same key, and removing by key alone would drop that instead.
            _entries.TryRemove(pair);
        }

        if (live <= MaxEntries)
        {
            return;
        }

        // Oldest first. Evicting a live entry is allowed here — a miss costs a
        // round trip — which is exactly what makes this bound simpler than the
        // idempotency store's.
        foreach (var pair in _entries
                     .OrderBy(pair => pair.Value.WrittenAt)
                     .Take(live - MaxEntries))
        {
            _entries.TryRemove(pair);
        }
    }

    private sealed record Entry(object? Value, DateTimeOffset ExpiresAt, DateTimeOffset WrittenAt)
    {
        public bool IsFresh(DateTimeOffset now) => now < ExpiresAt;
    }
}
