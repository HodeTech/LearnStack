using System.Collections.Concurrent;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Infrastructure.MultiTenancy;

/// <summary>
/// Remembers hosts that resolved to nothing, in a structure capped on its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separately capped is the requirement, not an optimisation.</b>
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036</see>
/// asks for unknown hosts to be "negative-cached in a separately capped structure
/// so a flood cannot evict real mappings", and the shared <c>ICacheService</c>
/// cannot satisfy that on two counts: it is one process-wide pool trimmed
/// oldest-first across every family, so unknown hosts would age out real ones; and
/// a stored <c>null</c> never reads back as a hit there — deliberately, pinned by
/// <c>An_Explicitly_Stored_Null_Reads_Back_As_A_Miss</c> — so it would occupy a
/// slot and answer nothing. Routing negatives through it buys eviction and no
/// cache.
/// </para>
/// <para>
/// <b>What it protects.</b> Every miss costs one PostgreSQL transaction on an
/// anonymous, pre-authentication path. Without a negative cache, a flood of novel
/// hostnames is a database round trip each. The anonymous rate limiter bounds one
/// peer; it does not bound a distributed flood, which is why the structure exists
/// as well as the limiter.
/// </para>
/// <para>
/// <b>Eviction is oldest-first down to a low-water mark, and entries expire.</b>
/// Bounded so a flood cannot grow it without limit; expiring so a host that becomes
/// live is not denied for the life of the process. <b><see cref="Forget"/> is called by
/// the host-mapping writer</b> as of Packet 7 — <c>MapHostToTenantCommandHandler</c>,
/// through <c>IHostResolutionInvalidator</c> — so the TTL is the backstop rather than the
/// whole of it. The Hub-side custom-domain lifecycle in
/// [Phase 02c](../../../../docs/roadmap/phase-02c-hub-foundation.md) is the second
/// caller, for the activation half this packet does not write.
/// A trim sweeps the lapsed entries on the way past,
/// because nothing else does: a read only drops the one entry it looked at, so the
/// map otherwise ratchets to its cap and stays there for the life of the process.
/// </para>
/// </remarks>
public sealed class UnknownHostCache(IClock clock, UnknownHostCacheOptions options)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen =
        new(StringComparer.Ordinal);

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    private readonly UnknownHostCacheOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>How many hosts are currently remembered. For tests and diagnostics.</summary>
    public int Count => _seen.Count;

    /// <summary>
    /// <c>true</c> when <paramref name="host"/> is known to resolve to nothing and
    /// that answer has not yet expired.
    /// </summary>
    public bool Contains(string host)
    {
        if (!_seen.TryGetValue(host, out var recordedAt))
        {
            return false;
        }

        if (_clock.UtcNow - recordedAt < _options.Ttl)
        {
            return true;
        }

        // Expired. Removed on read rather than by a sweep: the read is the only
        // moment the answer matters, and a background sweeper would be a timer
        // per process for a map bounded at a few thousand entries.
        _seen.TryRemove(new KeyValuePair<string, DateTimeOffset>(host, recordedAt));
        return false;
    }

    /// <summary>Records that <paramref name="host"/> resolved to nothing.</summary>
    public void Add(string host)
    {
        _seen[host] = _clock.UtcNow;

        if (_seen.Count > _options.MaxEntries)
        {
            Trim();
        }
    }

    /// <summary>
    /// Forgets <paramref name="host"/>, so the next request re-reads the table.
    /// </summary>
    /// <remarks>
    /// <b>Called by the host-mapping writer</b> — <c>MapHostToTenantCommandHandler</c>,
    /// through <see cref="IHostResolutionInvalidator"/>, as of Packet 7. Until that writer
    /// existed the TTL was the whole mechanism and a host activated inside it kept its 404
    /// for the rest of the window; ADR-0036 asks for the window to be closed on the
    /// transaction that flips either flag instead. The Hub-side custom-domain lifecycle in
    /// [Phase 02c](../../../../docs/roadmap/phase-02c-hub-foundation.md) is the second
    /// caller, for the activation half this packet does not write.
    /// </remarks>
    public void Forget(string host) => _seen.TryRemove(host, out _);


    private void Trim()
    {
        var now = _clock.UtcNow;

        // ToArray() takes every bucket lock and hands back a stable copy.
        // Enumerating the dictionary directly does not: LINQ buffers through
        // ICollection.CopyTo after a stale Count read, and under a concurrent Add
        // that TEARS — a concurrent insert throws ArgumentException, a concurrent
        // removal leaves a default slot whose null key makes TryRemove throw
        // ArgumentNullException. Measured on the shipped shape: eight threads
        // adding at the cap threw on 33% of adds. An earlier comment here called
        // the race benign on the theory that the worst case was overshooting the
        // cap; the worst case was throwing, out of an unguarded call on the
        // anonymous page-load path, where it became a 500 instead of the bodyless
        // 404 this structure exists to make cheap — and, because only an unknown
        // host reaches Add, a positive host-existence oracle.
        var snapshot = _seen.ToArray();

        // Reclaim the lapsed entries in the pass already being paid for. Contains
        // only drops the one entry it happened to read, so a map that filled
        // slowly is mostly expired by now, and this often makes the sort below
        // unnecessary.
        foreach (var pair in snapshot)
        {
            if (now - pair.Value >= _options.Ttl)
            {
                _seen.TryRemove(pair);
            }
        }

        // A low-water mark rather than the cap itself, matching
        // InMemoryCacheService. Trimming back to exactly the cap leaves the map
        // one add from overflowing, so every subsequent novel host pays for
        // another full sort.
        var target = Math.Max(1, _options.MaxEntries * 9 / 10);
        var excess = _seen.Count - target;

        if (excess <= 0)
        {
            return;
        }

        // TryRemove(KeyValuePair) rather than by key: it compares the value too,
        // so an entry re-added with a fresher timestamp between the snapshot and
        // here is left alone.
        foreach (var entry in _seen.ToArray().OrderBy(entry => entry.Value).Take(excess))
        {
            _seen.TryRemove(entry);
        }
    }
}

/// <summary>
/// The cap and the lifetime of a negative answer.
/// </summary>
/// <remarks>
/// A named options record rather than inline literals, so the block that reads them
/// carries a reference and not a number — that block gets copied, and a copied
/// number outlives the measurement that chose it. <b>Not bound to
/// <c>IConfiguration</c>:</b> nothing validates these, and an operator who set the
/// cap to zero or the TTL to a day would get a structure that either caches
/// nothing or denies an activated host for a day, with no failure to see. The
/// defaults are deliberately modest — this blunts a flood, it is not a store.
/// </remarks>
public sealed record UnknownHostCacheOptions
{
    /// <summary>Hosts remembered before the oldest are dropped. Default 10 000.</summary>
    public int MaxEntries { get; init; } = 10_000;

    /// <summary>How long a negative answer stands. Default two minutes.</summary>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromMinutes(2);
}
