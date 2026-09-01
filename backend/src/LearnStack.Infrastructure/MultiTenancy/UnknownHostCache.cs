using System.Collections.Concurrent;
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
/// <b>Eviction is oldest-first on a bounded map, and entries expire.</b> Bounded
/// so a flood cannot grow it without limit; expiring so a host that becomes live
/// is not denied for the life of the process. The activation path also invalidates
/// explicitly — <see cref="Forget"/> — so the TTL is the backstop rather than the
/// mechanism.
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
    /// The activation path calls this. Without it a host that was guessed before it
    /// went live stays denied for the whole TTL after activation, which is the
    /// cache window ADR-0036 asks to be closed on the transaction that flips
    /// either flag.
    /// </remarks>
    public void Forget(string host) => _seen.TryRemove(host, out _);

    private void Trim()
    {
        // Oldest-first, in one pass, down to the cap. Racy by construction — a
        // concurrent Add may push it back over — and that is acceptable: the cap
        // is a bound on growth, not an invariant, and taking a lock on the
        // anonymous page-load path to make it exact would cost more than the
        // handful of extra entries it saves.
        var excess = _seen.Count - _options.MaxEntries;

        if (excess <= 0)
        {
            return;
        }

        foreach (var entry in _seen.OrderBy(entry => entry.Value).Take(excess))
        {
            _seen.TryRemove(entry);
        }
    }
}

/// <summary>
/// The cap and the lifetime of a negative answer.
/// </summary>
/// <remarks>
/// Configuration rather than literals, because the block that reads them is copied
/// and a copied number outlives the measurement that chose it. The defaults are
/// deliberately modest: the structure exists to blunt a flood, not to be a
/// long-lived store, and a host that goes live is forgotten explicitly rather than
/// waited out.
/// </remarks>
public sealed record UnknownHostCacheOptions
{
    /// <summary>Hosts remembered before the oldest are dropped. Default 10 000.</summary>
    public int MaxEntries { get; init; } = 10_000;

    /// <summary>How long a negative answer stands. Default two minutes.</summary>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromMinutes(2);
}
