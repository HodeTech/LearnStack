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

    /// <summary>
    /// What a trim evicts down to, rather than back to <see cref="MaxEntries"/>.
    /// </summary>
    /// <remarks>
    /// Without this gap the steady state of an unbounded key space — the exact
    /// workload the ceiling exists for — is a trim on <b>every</b> write, each one
    /// evicting a single entry. Measured on that version: 0.26 ms and 281 KB of
    /// garbage per write, because evicting one entry copied and sorted all ten
    /// thousand. Evicting a tenth of the map at once pays that cost once per
    /// thousand writes instead of once per write.
    /// </remarks>
    public const int TrimTarget = MaxEntries * 9 / 10;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// One factory run per (key, requested type), however many callers miss at
    /// once. Keyed by type as well as key because a flight hands its result to
    /// every joiner: two callers asking for the same key as different <c>T</c>
    /// would otherwise share one run, and the loser would receive the winner's
    /// payload — its own factory never invoked at all.
    /// </summary>
    private readonly ConcurrentDictionary<(string Key, Type Type), Flight> _inFlight = new();

    private long _lastSweepTicks;

    /// <summary>
    /// Monotonic insertion counter. Eviction orders by this rather than by
    /// <c>WrittenAt</c>, because a burst shares one instant: with a frozen or
    /// coarse clock every entry carries the same timestamp and "oldest first"
    /// silently becomes "an arbitrary one first".
    /// </summary>
    private long _sequence;

    /// <summary>1 while a trim is running. A field, for Interlocked.</summary>
    private int _trimming;

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

    /// <summary>
    /// How many factory runs are registered as in flight. A diagnostic, for the
    /// same reason and with the same caveat as <see cref="Count"/>.
    /// </summary>
    /// <remarks>
    /// This map is the other structure that could grow without bound, and the
    /// one whose cleanup is subtlest: it is unregistered when the flight ends,
    /// not when a caller stops waiting for it.
    /// </remarks>
    public int InFlightCount => _inFlight.Count;

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        CacheKey.EnsureValid(key);

        var now = clock.UtcNow;
        Sweep(now);

        // `is T` rather than a cast: a key holding some other type is a caller
        // bug, and answering it with a miss lets the caller read the source of
        // truth instead of taking an InvalidCastException out of a component
        // whose contract is that a miss is never an error.
        if (_entries.TryGetValue(key, out var entry) && entry.IsFresh(now) && entry.Value is T hit)
        {
            return Task.FromResult<T?>(hit);
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

        if (_entries.TryGetValue(key, out var hit) && hit.IsFresh(now) && hit.Value is T cached)
        {
            return cached;
        }

        // Lazy with ExecutionAndPublication, not a bare GetOrAdd value factory:
        // a ConcurrentDictionary's value factory may run more than once under
        // contention, and running the caller's factory twice is the stampede
        // this method exists to prevent. The Lazy is built before the GetOrAdd
        // and passed as a VALUE, so the loser of a creation race simply
        // discards an object whose .Value was never touched — no factory run.
        //
        // The flight runs on CancellationToken.None, NOT on this caller's
        // token. Measured: with the caller's token, one client pressing refresh
        // cancelled the shared factory and every other request waiting on that
        // key died with it — as a 499, which this host treats as "the client
        // hung up" and therefore writes no body, captures no error and records
        // no span. A request that did nothing wrong failed invisibly.
        var mine = new Flight(new Lazy<Task<object?>>(
            async () => await factory(CancellationToken.None).ConfigureAwait(false),
            LazyThreadSafetyMode.ExecutionAndPublication));

        var registration = (key, typeof(T));

        // A flight that a write has ALREADY superseded must not be joined. It is
        // only stopped from storing, so a caller whose GetOrSetAsync begins
        // strictly after RemoveAsync returned would otherwise miss _entries —
        // the Remove emptied it — join the doomed flight, and be answered with
        // the value the invalidation existed to kill, its own factory never run.
        // Its callers keep their own reference and still get their result; they
        // were already in flight when the write landed, which is an ordinary
        // race. Arriving afterwards is not.
        Flight flight;
        while (true)
        {
            flight = _inFlight.GetOrAdd(registration, mine);
            if (ReferenceEquals(flight, mine) || !flight.Superseded)
            {
                break;
            }

            _inFlight.TryRemove(new KeyValuePair<(string, Type), Flight>(registration, flight));
        }

        // Registered before anything can observe the count, so the completion
        // continuation below never sees a zero that is about to become one.
        Interlocked.Increment(ref flight.Waiters);

        try
        {
            if (ReferenceEquals(flight, mine))
            {
                // Covers the case the `finally` cannot: every caller abandoned
                // before the factory finished, so no `finally` runs again to
                // notice the flight is done.
                _ = flight.Task.Value.ContinueWith(
                    completed =>
                    {
                        // Touching Exception marks the fault observed. Without
                        // it, a factory that faults after every caller has
                        // abandoned its flight — the correlated failure, since a
                        // dependency being down is exactly when clients
                        // disconnect — leaves the task unobserved, and
                        // TaskScheduler.UnobservedTaskException fires once per
                        // key with no request, no span and no correlation id
                        // attached to it.
                        _ = completed.Exception;
                        Retire(registration, flight);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            // Each caller observes its OWN token while waiting, so a joiner can
            // abandon a slow flight without ending it for anybody else.
            var produced = (T)(await flight.Task.Value.WaitAsync(cancellationToken)
                .ConfigureAwait(false))!;

            // A Set or a Remove landing while the factory ran marks the flight
            // superseded. Storing anyway would resurrect a value the caller
            // already replaced or deleted — eager invalidation silently lost for
            // a full TTL, which is the one thing a cache must not do quietly.
            if (!flight.Superseded)
            {
                Store(key, produced, options, clock.UtcNow);

                // Re-checked after the write, because the check above and the
                // write are two steps rather than one: a write landing between
                // them would otherwise be overwritten by this stale result.
                // Evicting is the safe resolution — the next reader takes a miss
                // and goes to the source of truth, and a miss is never an error,
                // whereas a stale value presented as fresh is.
                if (flight.Superseded)
                {
                    _entries.TryRemove(key, out _);
                }
            }

            return produced;
        }
        finally
        {
            // Unregistered when the last caller is DONE, not when the factory
            // finishes. Measured on two earlier versions, each of which fixed
            // one half and broke the other:
            //
            //   - Unregistering on the caller's exit meant a joiner that
            //     cancelled removed the shared registration while the factory
            //     was still running, so the next arrival started a second
            //     concurrent run — the stampede this method exists to prevent,
            //     reintroduced by its own cleanup.
            //   - Unregistering on the factory's completion instead meant the
            //     flight was already gone by the time the caller stored, so
            //     `Supersede` had nothing left to mark and a write landing in
            //     that window was silently overwritten.
            //
            // The registration is what `Supersede` reaches, so it has to outlive
            // the store, and it has to outlive every other caller's store too.
            //
            // Retiring turns only on the caller count, NOT on the factory having
            // finished. An earlier version also required IsCompleted, which meant
            // a factory that never completes — no deadline exists anywhere, since
            // the flight deliberately runs on CancellationToken.None so one
            // caller cannot cancel it for the rest — left its registration in
            // place forever. `_inFlight` has no ceiling, and worse, every later
            // caller JOINED that dead flight: the key never ran a factory again
            // for the life of the process, once per generic instantiation. With
            // no callers left there is nothing to stampede, so a fresh arrival
            // starting its own flight is right.
            if (Interlocked.Decrement(ref flight.Waiters) == 0)
            {
                Retire(registration, flight);
            }
        }
    }

    /// <summary>
    /// Unregisters a flight once it has finished and no caller is still using
    /// it. Value-comparing, so a later flight for the same key is never removed
    /// by an earlier one's cleanup.
    /// </summary>
    private void Retire((string Key, Type Type) registration, Flight flight)
    {
        if (Volatile.Read(ref flight.Waiters) != 0)
        {
            return;
        }

        _inFlight.TryRemove(new KeyValuePair<(string, Type), Flight>(registration, flight));
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
        Supersede(key);
        Store(key, value, options, now);

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        CacheKey.EnsureValid(key);

        Supersede(key);
        _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Marks every in-flight factory for <paramref name="key"/> as superseded,
    /// so none of them writes over the change that just landed.
    /// </summary>
    /// <remarks>
    /// The flag lives on the flight and dies with it. An earlier version kept a
    /// per-key version counter in a dictionary of its own, which was never
    /// swept: measured at 50,000 distinct keys, <c>_entries</c> held its 10,000
    /// ceiling while that map held all 50,000 — an unbounded structure behind a
    /// bounded one, reachable by ordinary per-entity keys rather than by misuse.
    /// </remarks>
    private void Supersede(string key)
    {
        foreach (var pair in _inFlight)
        {
            if (string.Equals(pair.Key.Key, key, StringComparison.Ordinal))
            {
                pair.Value.Superseded = true;
            }
        }
    }

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
        // One trimmer at a time. Concurrent writers all cross the ceiling
        // together, and without this each of them snapshots and sorts the whole
        // map to do work the first one is already doing.
        if (Interlocked.CompareExchange(ref _trimming, 1, 0) != 0)
        {
            return;
        }

        try
        {
            // ToArray(), NOT LINQ over `_entries` directly. Measured: an
            // `_entries.OrderBy(...)` buffers the LIVE dictionary through
            // ICollection.CopyTo after reading Count, and those two steps are not
            // atomic — if the map grew in between, CopyTo throws
            // ArgumentException; if it shrank, the tail of the buffer keeps
            // default(KeyValuePair) whose Value is null and the sort key
            // dereferences it. Both escaped Trim into SetAsync and
            // GetOrSetAsync, so a component whose contract says it may no-op at
            // any time was instead failing the caller's request: with two
            // concurrent writers at the ceiling, 4.1% of ordinary writes threw;
            // with four, 15.5%. ToArray takes every bucket lock and hands back a
            // consistent snapshot — measured at 0 failures over the same probe.
            var snapshot = _entries.ToArray();
            var live = new List<KeyValuePair<string, Entry>>(snapshot.Length);

            foreach (var pair in snapshot)
            {
                if (pair.Value.IsFresh(now))
                {
                    live.Add(pair);
                }
                else
                {
                    // Value-comparing: between the snapshot and this line another
                    // thread may have written a fresh entry at the same key.
                    _entries.TryRemove(pair);
                }
            }

            var excess = live.Count - TrimTarget;
            if (excess <= 0)
            {
                return;
            }

            live.Sort(static (left, right) => left.Value.Sequence.CompareTo(right.Value.Sequence));

            for (var i = 0; i < excess; i++)
            {
                _entries.TryRemove(live[i]);
            }
        }
        finally
        {
            Volatile.Write(ref _trimming, 0);
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

    /// <summary>One shared factory run, and whether a write has superseded it.</summary>
    private sealed class Flight(Lazy<Task<object?>> task)
    {
        public Lazy<Task<object?>> Task { get; } = task;

        public volatile bool Superseded;

        /// <summary>Callers still using this flight. A field, for Interlocked.</summary>
        public int Waiters;
    }
}
