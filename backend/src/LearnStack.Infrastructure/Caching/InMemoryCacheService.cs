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
    /// <remarks>
    /// Enforced on every write that adds a key, <b>not</b> inside the throttled
    /// sweep. Measured on the first version, which trimmed only during a sweep:
    /// a burst of 60,000 writes inside one <see cref="SweepInterval"/> left
    /// 60,000 entries against this ceiling of 10,000, because the sweep is
    /// throttled by clock time and a burst does not advance the clock. The test
    /// that covered the bound advanced the clock one second per write, which is
    /// the one schedule under which the old code held — a guard and a test that
    /// agreed with each other and not with reality.
    /// </remarks>
    public const int MaxEntries = 10_000;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// One factory run per key, however many callers miss at once.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _inFlight =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Per-key write counter. A <see cref="GetOrSetAsync{T}"/> flight reads it
    /// before running its factory and refuses to store a result the caller has
    /// since superseded.
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _versions = new(StringComparer.Ordinal);

    private long _lastSweepTicks;

    /// <summary>
    /// Monotonic insertion counter. Eviction orders by this rather than by
    /// <c>WrittenAt</c>, because a burst shares one instant: with a frozen or
    /// coarse clock every entry carries the same timestamp and "oldest first"
    /// silently becomes "an arbitrary one first".
    /// </summary>
    private long _sequence;

    /// <summary>
    /// How many entries the map currently holds, expired-but-unreclaimed ones
    /// included.
    /// </summary>
    /// <remarks>
    /// A diagnostic on this class, deliberately <b>not</b> on
    /// <see cref="ICacheService"/>: a caller that branches on the size of a cache
    /// has made a component whose contract is "sometimes" into one it depends on.
    /// It exists so the two bounds this class claims — the ceiling and the
    /// reclamation of expired entries — can be asserted directly rather than
    /// inferred from which keys happen to survive an eviction. The first version
    /// of the bound test inferred, and agreed with a ceiling that was holding
    /// 60,000 entries against 10,000.
    /// </remarks>
    public int Count => _entries.Count;

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

        // The version is read BEFORE the factory runs. If a Set or a Remove
        // lands while it is running, storing the result would resurrect a value
        // the caller already replaced or deleted — eager invalidation silently
        // lost for a full TTL, which is the one thing a cache must not do
        // quietly.
        var versionAtStart = VersionOf(key);

        // Lazy with ExecutionAndPublication, not a bare GetOrAdd: the value
        // factory of a ConcurrentDictionary may run more than once under
        // contention, and running the caller's factory twice is the stampede
        // this method exists to prevent.
        //
        // The flight runs on CancellationToken.None, NOT on this caller's
        // token. Measured: with the caller's token, one client pressing refresh
        // cancelled the shared factory and every other request waiting on that
        // key died with it — as a 499, which this host treats as "the client
        // hung up" and therefore writes no body, captures no error and records
        // no span. A request that did nothing wrong failed invisibly.
        var flight = _inFlight.GetOrAdd(
            key,
            _ => new Lazy<Task<object?>>(
                async () => await factory(CancellationToken.None).ConfigureAwait(false),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            // Each caller observes its OWN token while waiting, so a joiner can
            // abandon a slow flight without affecting the others.
            var produced = (T)(await flight.Value.WaitAsync(cancellationToken)
                .ConfigureAwait(false))!;

            if (VersionOf(key) == versionAtStart)
            {
                Store(key, produced, options, clock.UtcNow);
            }

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
        Bump(key);
        Store(key, value, options, now);

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        CacheKey.EnsureValid(key);

        Bump(key);
        _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    private long VersionOf(string key) => _versions.TryGetValue(key, out var v) ? v : 0;

    private void Bump(string key) => _versions.AddOrUpdate(key, 1, (_, v) => v + 1);

    private void Store<T>(string key, T value, CacheOptions? options, DateTimeOffset now)
    {
        // L2Ttl is read and ignored: there is no second layer here. Carrying it
        // means a caller written today does not change when the Valkey adapter
        // gives it a meaning.
        var ttl = options?.L1Ttl ?? DefaultTtl;
        var entry = new Entry(value, now + ttl, Interlocked.Increment(ref _sequence));

        // TryAdd first, so a write that GROWS the map is distinguishable from
        // one that replaces an entry. Only the former can cross the ceiling, so
        // only the former pays for checking it.
        if (!_entries.TryAdd(key, entry))
        {
            _entries[key] = entry;
            return;
        }

        if (_entries.Count > MaxEntries)
        {
            Trim(now);
        }
    }

    /// <summary>
    /// Evicts down to <see cref="MaxEntries"/>, expired entries first and then
    /// the oldest live ones.
    /// </summary>
    /// <remarks>
    /// Evicting a live entry is allowed here — a miss costs a round trip — which
    /// is what makes this bound simpler than <c>InMemoryIdempotencyStore</c>'s,
    /// where an entry is a promise and eviction would let an operation run twice.
    /// </remarks>
    private void Trim(DateTimeOffset now)
    {
        foreach (var pair in _entries)
        {
            if (!pair.Value.IsFresh(now))
            {
                // Value-comparing: between the enumerator observing an expired
                // entry and this line, another thread may have written a fresh
                // one at the same key.
                _entries.TryRemove(pair);
            }
        }

        var excess = _entries.Count - MaxEntries;
        if (excess <= 0)
        {
            return;
        }

        foreach (var pair in _entries.OrderBy(pair => pair.Value.Sequence).Take(excess))
        {
            _entries.TryRemove(pair);
        }
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

        foreach (var pair in _entries)
        {
            if (pair.Value.IsFresh(now))
            {
                continue;
            }

            // Value-comparing, for the same reason Trim's pass is.
            _entries.TryRemove(pair);
        }
    }

    private sealed record Entry(object? Value, DateTimeOffset ExpiresAt, long Sequence)
    {
        public bool IsFresh(DateTimeOffset now) => now < ExpiresAt;
    }
}
