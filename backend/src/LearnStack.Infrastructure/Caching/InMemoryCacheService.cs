using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Infrastructure.Caching;

/// <summary>
/// The process-local <see cref="ICacheService"/> implementation.
/// </summary>
/// <remarks>
/// <para>
/// A second process has its own map, so cross-instance freshness requires the
/// Valkey-backed adapter gated by ADR-0035. Correctness remains in the source of
/// truth: this cache may evict at any time and a miss is never an error.
/// </para>
/// <para>
/// Concurrent misses for one key and requested type share one factory flight.
/// Caller cancellation only stops that caller waiting. When the last waiter
/// leaves, a service-owned token cancels the factory, but the flight remains
/// registered until the factory actually terminates; a replacement can therefore
/// never overlap abandoned work for the same registration.
/// </para>
/// </remarks>
public sealed class InMemoryCacheService : ICacheService
{
    public const string MeterName = "learnstack.cache";
    public const string HitCounterName = "learnstack_cache_hit_total";
    public const string MissCounterName = "learnstack_cache_miss_total";
    public const string StoreCounterName = "learnstack_cache_store_total";
    public const string CoalescedCounterName = "learnstack_cache_coalesced_total";
    public const string EvictionCounterName = "learnstack_cache_eviction_total";
    public const string FactoryDurationName = "learnstack_cache_factory_duration_seconds";

    /// <summary>The default in-process lifetime.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);

    /// <summary>How often expired entries are reclaimed.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The longest one cache factory may run. Provider calls are required to be
    /// bounded below this value by Standards 15; this is the final service-owned
    /// backstop and is deliberately independent of any one caller's token.
    /// </summary>
    public static readonly TimeSpan FactoryTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The hard maximum number of stored entries.</summary>
    public const int MaxEntries = 10_000;

    /// <summary>The low-water mark capacity trimming targets.</summary>
    public const int TrimTarget = MaxEntries * 9 / 10;

    private const int KeyGateCount = 256;

    private readonly IClock _clock;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Key, Type Type), Flight> _inFlight = new();
    private readonly object[] _keyGates = Enumerable.Range(0, KeyGateCount)
        .Select(static _ => new object())
        .ToArray();
    private readonly object _capacityGate = new();
    private readonly Counter<long> _hits;
    private readonly Counter<long> _misses;
    private readonly Counter<long> _stores;
    private readonly Counter<long> _coalesced;
    private readonly Counter<long> _evictions;
    private readonly Histogram<double> _factoryDuration;

    private long _lastSweepTicks;
    private long _sequence;

    public InMemoryCacheService(IClock clock, IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(meterFactory);

        _clock = clock;
        var meter = meterFactory.Create(new MeterOptions(MeterName));
        _hits = meter.CreateCounter<long>(HitCounterName);
        _misses = meter.CreateCounter<long>(MissCounterName);
        _stores = meter.CreateCounter<long>(StoreCounterName);
        _coalesced = meter.CreateCounter<long>(CoalescedCounterName);
        _evictions = meter.CreateCounter<long>(EvictionCounterName);
        _factoryDuration = meter.CreateHistogram<double>(FactoryDurationName, unit: "s");
    }

    /// <summary>Stored entries, including expired entries awaiting a sweep.</summary>
    public int Count => _entries.Count;

    /// <summary>Factory flights that have not yet reached a terminal state.</summary>
    public int InFlightCount => _inFlight.Count;

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        CacheKey.EnsureValid(key);
        cancellationToken.ThrowIfCancellationRequested();

        var now = _clock.UtcNow;
        Sweep(now);

        if (TryRead(key, now, out T? value))
        {
            _hits.Add(1, CacheNameTag(key));
            return Task.FromResult(value);
        }

        _misses.Add(1, CacheNameTag(key));
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
        cancellationToken.ThrowIfCancellationRequested();

        var now = _clock.UtcNow;
        var ttl = ValidateOptions(options, now);
        Sweep(now);

        var registration = (key, typeof(T));
        var recordedMiss = false;

        while (true)
        {
            Flight flight;
            var owner = false;
            var waitForTerminal = false;
            var keyGate = KeyGate(key);

            // The miss check and flight publication share the same bounded key
            // gate as Set/Remove. There is no interval in which an explicit
            // write can finish after a miss but before the flight is visible to
            // supersede.
            lock (keyGate)
            {
                now = _clock.UtcNow;
                if (TryRead(key, now, out T? cached))
                {
                    if (!recordedMiss)
                    {
                        _hits.Add(1, CacheNameTag(key));
                    }

                    return cached!;
                }

                if (!recordedMiss)
                {
                    _misses.Add(1, CacheNameTag(key));
                    recordedMiss = true;
                }

                if (_inFlight.TryGetValue(registration, out flight!))
                {
                    if (flight.Abandoned || flight.Superseded)
                    {
                        waitForTerminal = true;
                    }
                    else
                    {
                        // Acquired while holding the same gate retirement and
                        // abandonment use. A published flight therefore never
                        // exists with an owner count of zero.
                        flight.Waiters++;
                        _coalesced.Add(1, CacheNameTag(key));
                    }
                }
                else
                {
                    flight = new Flight(FactoryTimeout) { Waiters = 1 };
                    _inFlight[registration] = flight;
                    owner = true;
                }
            }

            if (waitForTerminal)
            {
                await WaitForTerminalThenRetryAsync(flight, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (owner)
            {
                _ = RunFactoryAsync(registration, flight, key, factory, ttl);
            }

            try
            {
                return (T)(await flight.Completion.WaitAsync(cancellationToken)
                    .ConfigureAwait(false))!;
            }
            finally
            {
                ReleaseWaiter(registration, flight);
            }
        }
    }

    public Task SetAsync<T>(
        string key,
        T value,
        CacheOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CacheKey.EnsureValid(key);
        cancellationToken.ThrowIfCancellationRequested();

        var now = _clock.UtcNow;
        var ttl = ValidateOptions(options, now);
        Sweep(now);

        lock (KeyGate(key))
        {
            SupersedeUnderKeyGate(key);
            Store(key, value, ttl, now);
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        CacheKey.EnsureValid(key);
        cancellationToken.ThrowIfCancellationRequested();

        lock (KeyGate(key))
        {
            SupersedeUnderKeyGate(key);
            if (_entries.TryRemove(key, out _))
            {
                _evictions.Add(1, CacheNameAndReasonTags(key, "explicit"));
            }
        }

        return Task.CompletedTask;
    }

    private async Task RunFactoryAsync<T>(
        (string Key, Type Type) registration,
        Flight flight,
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan ttl)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = "success";

        try
        {
            var produced = await factory(flight.FactoryToken).ConfigureAwait(false);

            lock (KeyGate(key))
            {
                if (!flight.Superseded && !flight.Abandoned)
                {
                    Store(key, produced, ttl, _clock.UtcNow);
                }

                _inFlight.TryRemove(
                    new KeyValuePair<(string Key, Type Type), Flight>(registration, flight));
            }

            flight.TrySetResult(produced);
        }
        catch (OperationCanceledException) when (flight.FactoryToken.IsCancellationRequested)
        {
            outcome = "cancelled";
            RetireTerminalFlight(registration, flight, key);
            flight.TrySetCanceled();
        }
        catch (Exception exception)
        {
            outcome = "faulted";
            RetireTerminalFlight(registration, flight, key);
            flight.TrySetException(exception);
        }
        finally
        {
            _factoryDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds,
                CacheNameAndOutcomeTags(key, outcome));
            flight.Dispose();
        }
    }

    private void RetireTerminalFlight(
        (string Key, Type Type) registration, Flight flight, string key)
    {
        lock (KeyGate(key))
        {
            _inFlight.TryRemove(
                new KeyValuePair<(string Key, Type Type), Flight>(registration, flight));
        }
    }

    private void ReleaseWaiter((string Key, Type Type) registration, Flight flight)
    {
        lock (KeyGate(registration.Key))
        {
            flight.Waiters--;
            if (flight.Waiters == 0 && !flight.Completion.IsCompleted)
            {
                flight.Abandoned = true;
                flight.CancelFactory();
            }
        }
    }

    private static async Task WaitForTerminalThenRetryAsync(
        Flight flight, CancellationToken cancellationToken)
    {
        try
        {
            await flight.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The caller did not join this superseded/abandoned result. It only
            // waits for terminality so its replacement cannot overlap; the next
            // loop iteration performs the fresh read/factory attempt.
        }
    }

    private bool TryRead<T>(string key, DateTimeOffset now, out T? value)
    {
        if (_entries.TryGetValue(key, out var entry)
            && entry.IsFresh(now)
            && entry.Value is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    private void SupersedeUnderKeyGate(string key)
    {
        foreach (var pair in _inFlight)
        {
            if (string.Equals(pair.Key.Key, key, StringComparison.Ordinal))
            {
                pair.Value.Superseded = true;
            }
        }
    }

    private void Store<T>(string key, T value, TimeSpan ttl, DateTimeOffset now)
    {
        lock (_capacityGate)
        {
            var isNew = !_entries.ContainsKey(key);
            _entries[key] = new Entry(value, now + ttl, Interlocked.Increment(ref _sequence));
            _stores.Add(1, CacheNameTag(key));

            if (isNew && _entries.Count > MaxEntries)
            {
                Trim(now);
            }
        }
    }

    /// <summary>
    /// Evicts down to <see cref="TrimTarget"/>, removing expired entries before
    /// the oldest live entries.
    /// </summary>
    private void Trim(DateTimeOffset now)
    {
        // Store serializes admission through _capacityGate, so this snapshot
        // cannot be invalidated by a concurrent replacement. Remove may shrink
        // it, which only reduces the work required.
        var snapshot = _entries.ToArray();
        foreach (var pair in snapshot)
        {
            if (!pair.Value.IsFresh(now) && _entries.TryRemove(pair.Key, out _))
            {
                _evictions.Add(1, CacheNameAndReasonTags(pair.Key, "expired"));
            }
        }

        var excess = _entries.Count - TrimTarget;
        if (excess <= 0)
        {
            return;
        }

        var live = _entries.ToArray();
        Array.Sort(
            live,
            static (left, right) => left.Value.Sequence.CompareTo(right.Value.Sequence));

        for (var index = 0; index < live.Length && excess > 0; index++)
        {
            if (_entries.TryRemove(live[index].Key, out _))
            {
                excess--;
                _evictions.Add(1, CacheNameAndReasonTags(live[index].Key, "capacity"));
            }
        }
    }

    /// <summary>
    /// Removes expired entries only. Capacity eviction is owned by
    /// <see cref="Trim"/> on admission.
    /// </summary>
    private void Sweep(DateTimeOffset now)
    {
        var ticks = now.UtcTicks;
        var last = Interlocked.Read(ref _lastSweepTicks);

        if (ticks >= last && ticks - last < SweepInterval.Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastSweepTicks, ticks, last) != last)
        {
            return;
        }

        lock (_capacityGate)
        {
            foreach (var pair in _entries)
            {
                if (!pair.Value.IsFresh(now) && _entries.TryRemove(pair))
                {
                    _evictions.Add(1, CacheNameAndReasonTags(pair.Key, "expired"));
                }
            }
        }
    }

    private static TimeSpan ValidateOptions(CacheOptions? options, DateTimeOffset now)
    {
        var l1 = options?.L1Ttl ?? DefaultTtl;
        ValidateTtl(l1, now, nameof(CacheOptions.L1Ttl));

        if (options?.L2Ttl is { } l2)
        {
            ValidateTtl(l2, now, nameof(CacheOptions.L2Ttl));
        }

        return l1;
    }

    private static void ValidateTtl(TimeSpan ttl, DateTimeOffset now, string parameterName)
    {
        if (ttl <= TimeSpan.Zero || ttl > DateTimeOffset.MaxValue - now)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                ttl,
                "A cache TTL must be positive and representable from the current instant.");
        }
    }

    private object KeyGate(string key) =>
        _keyGates[(StringComparer.Ordinal.GetHashCode(key) & int.MaxValue) % KeyGateCount];

    private static KeyValuePair<string, object?> CacheNameTag(string key) =>
        new("cache.name", CacheName(key));

    private static KeyValuePair<string, object?>[] CacheNameAndReasonTags(
        string key, string reason) =>
        [CacheNameTag(key), new("reason", reason)];

    private static KeyValuePair<string, object?>[] CacheNameAndOutcomeTags(
        string key, string outcome) =>
        [CacheNameTag(key), new("outcome", outcome)];

    private static string CacheName(string key)
    {
        var segments = key.Split(CacheKey.Separator);
        if (segments[0].Equals(CacheKey.PlatformTenant, StringComparison.Ordinal))
        {
            return "hub:host-map";
        }

        var moduleIndex = segments.Length >= 4 && Guid.TryParse(segments[1], out _) ? 2 : 1;
        return (segments[moduleIndex], segments[moduleIndex + 1]) switch
        {
            ("hub", "entitlement") => "hub:entitlement",
            ("identity", "permissions") => "identity:permissions",
            ("tenancy", "feature-flags") => "tenancy:feature-flags",
            ("tenancy", "settings") => "tenancy:settings",
            _ => "other",
        };
    }

    private sealed record Entry(object? Value, DateTimeOffset ExpiresAt, long Sequence)
    {
        public bool IsFresh(DateTimeOffset now) => now < ExpiresAt;
    }

    private sealed class Flight : IDisposable
    {
        private readonly CancellationTokenSource _factoryCancellation = new();
        private readonly TaskCompletionSource<object?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Flight(TimeSpan timeout)
        {
            _factoryCancellation.CancelAfter(timeout);

            // A fault can arrive after every caller has left. Observe it here so
            // it never surfaces later as an uncorrelated process-wide event.
            _ = _completion.Task.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public Task<object?> Completion => _completion.Task;
        public CancellationToken FactoryToken => _factoryCancellation.Token;
        public int Waiters { get; set; }
        public bool Abandoned { get; set; }
        public bool Superseded { get; set; }

        public void CancelFactory() => _factoryCancellation.Cancel();
        public void TrySetResult(object? value) => _completion.TrySetResult(value);
        public void TrySetCanceled() => _completion.TrySetCanceled(_factoryCancellation.Token);
        public void TrySetException(Exception exception) => _completion.TrySetException(exception);
        public void Dispose() => _factoryCancellation.Dispose();
    }
}
