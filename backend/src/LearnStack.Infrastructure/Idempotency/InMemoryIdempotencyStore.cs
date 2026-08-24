using System.Collections.Concurrent;
using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Infrastructure.Idempotency;

/// <summary>
/// The default <see cref="IIdempotencyStore"/>: correct for one instance,
/// wrong for two.
/// </summary>
/// <remarks>
/// <para>
/// <b>This does not survive a restart and is not shared between instances.</b>
/// Two application instances behind a load balancer each hold their own map, so
/// a retry that lands on the other one runs the operation a second time — which
/// is precisely what an idempotency key exists to prevent. Per
/// <see href="../../../../docs/decisions/0037-idempotency-key-contract.md">ADR-0037</see>
/// that is acceptable only while there is one instance and no endpoint yet
/// requires the header; the durable implementation lands with the schema in
/// <see href="../../../../docs/roadmap/phase-02a-kernel-tenancy.md">Packet 6</see>,
/// and Standards 04's "required for payment operations" list has no member
/// before then.
/// </para>
/// <para>
/// The same limitation is why <c>ICacheService</c> exists as a port and why
/// <c>RemoveByPrefixAsync</c> was removed from it in
/// <see href="../../../../docs/decisions/0014-adopt-dapr.md">ADR-0014 Amendment 2</see>:
/// an instance-local structure cannot honour a contract phrased as if it were
/// shared. Saying so here keeps the next reader from mistaking this for a
/// finished component.
/// </para>
/// </remarks>
public sealed class InMemoryIdempotencyStore(IClock clock) : IIdempotencyStore
{
    /// <summary>Standards 04 § Idempotency: "stores (idempotency_key, response) for 24 hours".</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>
    /// How long a claim may stay in flight before another request may take it.
    /// A process that dies mid-operation would otherwise hold the key for the
    /// full retention window.
    /// </summary>
    /// <remarks>
    /// This is a <b>lease</b>, and the distinction matters: when it expires the
    /// store stops treating the first attempt as the owner, but it cannot stop
    /// that attempt from still running. ADR-0037 states the resulting guarantee
    /// as at-most-once <i>while a claim is live</i> rather than as exactly-once.
    /// </remarks>
    public static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often the map is swept for expired entries. Reclamation only —
    /// correctness never waits for it, because an expired entry is taken over
    /// by the next claim on that key whether or not a sweep has run. Throttled
    /// because the sweep snapshots the whole map, and doing that on every
    /// request turns an O(1) claim into an O(n) one.
    /// </summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The most keys held at once, across every tenant. Bounded because the key
    /// is client-chosen: unbounded, a caller with a fresh key per request is an
    /// out-of-memory condition rather than a rate-limited one.
    /// </summary>
    public const int MaxEntries = 10_000;

    /// <summary>
    /// The most keys one tenant may hold. Without it the global ceiling is a
    /// shared resource with no owner: one tenant minting keys in a loop would
    /// crowd out every other tenant.
    /// </summary>
    public const int MaxEntriesPerTenant = 1_000;

    private readonly ConcurrentDictionary<(Guid Tenant, string Key), Entry> _entries = new();

    private long _lastSweepTicks;
    private Census _census = Census.Empty;

    public Task<IdempotencyClaimResult> TryClaimAsync(
        Guid tenantId, string key, string fingerprint, CancellationToken cancellationToken)
    {
        Guard(tenantId, key);
        ArgumentNullException.ThrowIfNull(fingerprint);

        var now = clock.UtcNow;
        Sweep(now);

        var composite = (tenantId, key);

        // Admission, not eviction. A record that has not expired is a promise
        // for the rest of its window, and dropping one to make room for a new
        // key would let the operation it describes run a second time — the
        // capacity control quietly cancelling the guarantee it exists to
        // protect. Refusing a NEW key costs the caller a retry and costs the
        // guarantee nothing. An existing key is always served, so a client
        // holding one is never locked out by another's flood.
        if (!_entries.ContainsKey(composite) && _census.IsFull(tenantId))
        {
            return Result(new IdempotencyClaimResult(
                IdempotencyClaim.CapacityExhausted, Guid.Empty, null));
        }

        // Reference identity decides who won, not the timestamp. IClock can
        // return the same instant to two callers in one tick, and comparing
        // ClaimedAt would then hand both of them the claim — the one outcome
        // this type exists to prevent.
        var mine = Entry.Claimed(now, fingerprint);
        var entry = _entries.AddOrUpdate(
            composite,
            mine,
            (_, existing) => existing.IsUsable(now) ? existing : mine);

        if (ReferenceEquals(entry, mine))
        {
            return Result(new IdempotencyClaimResult(IdempotencyClaim.Acquired, mine.Token, null));
        }

        // The key is held by a live entry. Whether replaying it is the right
        // answer depends on whether it answers the same question.
        if (!string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return Result(new IdempotencyClaimResult(IdempotencyClaim.Mismatched, Guid.Empty, null));
        }

        return Result(entry.State switch
        {
            EntryState.Completed => new IdempotencyClaimResult(
                IdempotencyClaim.Completed, Guid.Empty, entry.Response),
            EntryState.Unreplayable => new IdempotencyClaimResult(
                IdempotencyClaim.Unreplayable, Guid.Empty, null),
            _ => new IdempotencyClaimResult(IdempotencyClaim.InFlight, Guid.Empty, null),
        });
    }

    public Task<bool> CompleteAsync(
        Guid tenantId,
        string key,
        Guid token,
        IdempotentResponse? response,
        CancellationToken cancellationToken)
    {
        Guard(tenantId, key);

        // Fenced. An attempt that overran the claim timeout no longer owns the
        // key, and writing its response here would overwrite the record of the
        // attempt that replaced it — the newer answer replaced by the older one,
        // silently, for the rest of the retention window.
        // TryGetValue then TryUpdate is a compare-and-swap, not a read-then-write:
        // the update only lands if the entry is still byte-for-byte the one that
        // was read, so a sweep or a successor that intervened makes this a no-op
        // rather than a clobber. Losing that race means the lease expired while
        // the operation ran, which the caller is told about rather than left to
        // assume.
        if (_entries.TryGetValue((tenantId, key), out var current) && current.Token == token)
        {
            return Task.FromResult(_entries.TryUpdate(
                (tenantId, key),
                current with
                {
                    ClaimedAt = clock.UtcNow,
                    State = response is null ? EntryState.Unreplayable : EntryState.Completed,
                    Response = response,
                },
                current));
        }

        return Task.FromResult(false);
    }

    public Task<bool> AbandonAsync(
        Guid tenantId, string key, Guid token, CancellationToken cancellationToken)
    {
        Guard(tenantId, key);

        // Fenced for the mirror-image reason: a timed-out attempt releasing
        // "its" key would delete the successor's claim, and two requests would
        // then run the operation concurrently.
        if (_entries.TryGetValue((tenantId, key), out var current) && current.Token == token)
        {
            return Task.FromResult(
                _entries.TryRemove(new KeyValuePair<(Guid, string), Entry>((tenantId, key), current)));
        }

        return Task.FromResult(false);
    }

    private static void Guard(Guid tenantId, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        // The tenant is the key space. An empty one is not a tenant — it is a
        // call site that forgot to scope, and accepting it would build exactly
        // the flat space this store's contract exists to prevent.
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException(
                "An idempotency key is scoped to a tenant; Guid.Empty is not one.", nameof(tenantId));
        }
    }

    private static Task<IdempotencyClaimResult> Result(IdempotencyClaimResult result) =>
        Task.FromResult(result);

    /// <summary>
    /// Drops expired entries and recounts what is left, at most once per
    /// <see cref="SweepInterval"/>.
    /// </summary>
    /// <remarks>
    /// Expiry is the <b>only</b> reason an entry leaves this map. Nothing here
    /// evicts a live claim or an unexpired record.
    /// </remarks>
    private void Sweep(DateTimeOffset now)
    {
        var ticks = now.UtcTicks;
        var last = Interlocked.Read(ref _lastSweepTicks);

        // A clock that steps backwards (an NTP correction) would otherwise wedge
        // the sweep until real time caught up, so a backwards step sweeps
        // immediately rather than waiting out the difference.
        if (ticks >= last && ticks - last < SweepInterval.Ticks)
        {
            return;
        }

        // One sweeper per interval; the losers of this exchange skip it rather
        // than queue behind it, because a sweep reclaims memory and never
        // decides an outcome.
        if (Interlocked.CompareExchange(ref _lastSweepTicks, ticks, last) != last)
        {
            return;
        }

        var counts = new Dictionary<Guid, int>();
        var total = 0;

        foreach (var pair in _entries)
        {
            if (!pair.Value.IsUsable(now))
            {
                // Value-comparing, and that is the whole point. Removing by key
                // alone deletes whatever is at that key NOW, which — between the
                // enumerator observing an expired entry and this line running —
                // may be a live claim another thread just acquired. The next
                // caller would then find the key absent and run the operation a
                // second time, concurrently with the first.
                _entries.TryRemove(pair);
                continue;
            }

            counts[pair.Key.Tenant] = counts.GetValueOrDefault(pair.Key.Tenant) + 1;
            total++;
        }

        _census = new Census(counts, total);
    }

    /// <summary>
    /// What the last sweep counted.
    /// </summary>
    /// <remarks>
    /// Admission reads this rather than the live map because counting a
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> takes every one of its
    /// locks, and doing that on each claim is the O(n) cost the sweep throttle
    /// exists to avoid. It is therefore up to one <see cref="SweepInterval"/>
    /// stale, so a tenant can overshoot its allowance by one interval's worth of
    /// claims — a soft ceiling, which is what a ceiling that must never evict a
    /// live record has to be.
    /// </remarks>
    private sealed record Census(IReadOnlyDictionary<Guid, int> PerTenant, int Total)
    {
        public static readonly Census Empty = new(new Dictionary<Guid, int>(), 0);

        public bool IsFull(Guid tenantId) =>
            Total >= MaxEntries || PerTenant.GetValueOrDefault(tenantId) >= MaxEntriesPerTenant;
    }

    private enum EntryState
    {
        /// <summary>An attempt holds the key and has not finished.</summary>
        Claimed,

        /// <summary>An attempt finished and its response can be replayed.</summary>
        Completed,

        /// <summary>An attempt finished and its response was not retained.</summary>
        Unreplayable,
    }

    private sealed record Entry(
        DateTimeOffset ClaimedAt,
        Guid Token,
        string Fingerprint,
        EntryState State,
        IdempotentResponse? Response)
    {
        /// <summary>
        /// A fresh claim. The token is <see cref="Guid.NewGuid"/> rather than
        /// <c>IGuidFactory.NewUuidV7</c> because it is not an identifier of
        /// anything — it never reaches a database, an index, or a caller. It is a
        /// nonce two attempts must not share, and uniqueness is the only property
        /// asked of it.
        /// </summary>
        public static Entry Claimed(DateTimeOffset now, string fingerprint) =>
            new(now, Guid.NewGuid(), fingerprint, EntryState.Claimed, null);

        /// <summary>
        /// A finished outcome lives for the retention window; an unfinished
        /// claim only for the claim timeout, so a process that died mid-flight
        /// does not hold the key for a day.
        /// </summary>
        public bool IsUsable(DateTimeOffset now) =>
            State == EntryState.Claimed
                ? now - ClaimedAt < ClaimTimeout
                : now - ClaimedAt < Retention;
    }
}
