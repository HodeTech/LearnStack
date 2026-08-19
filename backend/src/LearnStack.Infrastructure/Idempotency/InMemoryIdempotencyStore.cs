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
/// <c>RemoveByPrefixAsync</c> is being removed from it: an instance-local
/// structure cannot honour a contract phrased as if it were shared. Saying so
/// here keeps the next reader from mistaking this for a finished component.
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
    /// The most keys held at once. Bounded because the key is client-chosen:
    /// unbounded, a caller with a fresh key per request is an out-of-memory
    /// condition rather than a rate-limited one.
    /// </summary>
    public const int MaxEntries = 10_000;

    /// <summary>
    /// The most keys one tenant may hold. Without it the global ceiling is a
    /// shared resource with no owner: one tenant minting keys in a loop evicts
    /// every other tenant's records, and their retries then re-run operations
    /// that had already completed.
    /// </summary>
    public const int MaxEntriesPerTenant = 1_000;

    private readonly ConcurrentDictionary<(Guid Tenant, string Key), Entry> _entries = new();
    private long _lastSweepTicks;

    public Task<IdempotencyClaimResult> TryClaimAsync(
        Guid tenantId, string key, string fingerprint, CancellationToken cancellationToken)
    {
        Guard(tenantId, key);
        ArgumentNullException.ThrowIfNull(fingerprint);

        var now = clock.UtcNow;
        Sweep(now);

        // Reference identity decides who won, not the timestamp. IClock can
        // return the same instant to two callers in one tick, and comparing
        // ClaimedAt would then hand both of them the claim — the one outcome
        // this type exists to prevent.
        var mine = Entry.Claimed(now, fingerprint);
        var entry = _entries.AddOrUpdate(
            (tenantId, key),
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

        return Result(entry.Response is { } stored
            ? new IdempotencyClaimResult(IdempotencyClaim.Completed, Guid.Empty, stored)
            : new IdempotencyClaimResult(IdempotencyClaim.InFlight, Guid.Empty, null));
    }

    public Task CompleteAsync(
        Guid tenantId,
        string key,
        Guid token,
        IdempotentResponse response,
        CancellationToken cancellationToken)
    {
        Guard(tenantId, key);
        ArgumentNullException.ThrowIfNull(response);

        // Fenced. An attempt that overran the claim timeout no longer owns the
        // key, and writing its response here would overwrite the record of the
        // attempt that replaced it — the newer answer replaced by the older one,
        // silently, for the rest of the retention window.
        if (_entries.TryGetValue((tenantId, key), out var current) && current.Token == token)
        {
            _entries.TryUpdate(
                (tenantId, key),
                current with { ClaimedAt = clock.UtcNow, Response = response },
                current);
        }

        return Task.CompletedTask;
    }

    public Task AbandonAsync(
        Guid tenantId, string key, Guid token, CancellationToken cancellationToken)
    {
        Guard(tenantId, key);

        // Fenced for the mirror-image reason: a timed-out attempt releasing
        // "its" key would delete the successor's claim, and two requests would
        // then run the operation concurrently.
        if (_entries.TryGetValue((tenantId, key), out var current) && current.Token == token)
        {
            _entries.TryRemove(new KeyValuePair<(Guid, string), Entry>((tenantId, key), current));
        }

        return Task.CompletedTask;
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
    /// Drops expired entries and enforces the ceilings, at most once per
    /// <see cref="SweepInterval"/>.
    /// </summary>
    private void Sweep(DateTimeOffset now)
    {
        var ticks = now.UtcTicks;
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (ticks - last < SweepInterval.Ticks)
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
            }
        }

        EnforceCeilings();
    }

    /// <summary>
    /// Evicts completed records — and only completed records — down to the
    /// per-tenant and global ceilings, oldest first.
    /// </summary>
    /// <remarks>
    /// A live in-flight claim is never evicted. Dropping one releases a key
    /// whose operation is still running, so the next retry executes it again;
    /// that trades a bounded memory overshoot for a duplicated side effect,
    /// which is the wrong direction for a mechanism whose entire purpose is the
    /// opposite. Live claims are bounded by concurrent requests in flight, which
    /// the server bounds independently.
    /// </remarks>
    private void EnforceCeilings()
    {
        var snapshot = _entries.ToArray();
        var evicted = 0;
        var dropped = new HashSet<(Guid, string)>();

        foreach (var group in snapshot.GroupBy(pair => pair.Key.Tenant))
        {
            var excess = group.Count() - MaxEntriesPerTenant;
            if (excess <= 0)
            {
                continue;
            }

            foreach (var pair in Completed(group).Take(excess))
            {
                if (_entries.TryRemove(pair))
                {
                    dropped.Add(pair.Key);
                    evicted++;
                }
            }
        }

        var remaining = snapshot.Length - evicted;
        if (remaining <= MaxEntries)
        {
            return;
        }

        foreach (var pair in Completed(snapshot.Where(pair => !dropped.Contains(pair.Key)))
                     .Take(remaining - MaxEntries))
        {
            _entries.TryRemove(pair);
        }

        static IOrderedEnumerable<KeyValuePair<(Guid Tenant, string Key), Entry>> Completed(
            IEnumerable<KeyValuePair<(Guid Tenant, string Key), Entry>> pairs) =>
            pairs.Where(pair => pair.Value.Response is not null)
                .OrderBy(pair => pair.Value.ClaimedAt);
    }

    private sealed record Entry(
        DateTimeOffset ClaimedAt, Guid Token, string Fingerprint, IdempotentResponse? Response)
    {
        /// <summary>
        /// A fresh claim. The token is <see cref="Guid.NewGuid"/> rather than
        /// <c>IGuidFactory.NewUuidV7</c> because it is not an identifier of
        /// anything — it never reaches a database, an index, or a caller. It is a
        /// nonce two attempts must not share, and uniqueness is the only property
        /// asked of it.
        /// </summary>
        public static Entry Claimed(DateTimeOffset now, string fingerprint) =>
            new(now, Guid.NewGuid(), fingerprint, null);

        /// <summary>
        /// A completed entry lives for the retention window; an unfinished
        /// claim only for the claim timeout, so a process that died mid-flight
        /// does not hold the key for a day.
        /// </summary>
        public bool IsUsable(DateTimeOffset now) =>
            Response is null
                ? now - ClaimedAt < ClaimTimeout
                : now - ClaimedAt < Retention;
    }
}
