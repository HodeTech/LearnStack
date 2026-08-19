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
/// is precisely what an idempotency key exists to prevent. That is acceptable
/// only while there is one instance and no endpoint yet requires the header;
/// the durable implementation lands with the schema in
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
    /// The most keys held at once. Bounded because the key is client-chosen:
    /// unbounded, a caller with a fresh key per request is an out-of-memory
    /// condition rather than a rate-limited one.
    /// </summary>
    public const int MaxEntries = 10_000;

    private readonly ConcurrentDictionary<(Guid Tenant, string Key), Entry> _entries = new();

    public Task<(IdempotencyClaim Claim, IdempotentResponse? Stored)> TryClaimAsync(
        Guid tenantId, string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var now = clock.UtcNow;
        Evict(now);

        // Reference identity decides who won, not the timestamp. IClock can
        // return the same instant to two callers in one tick, and comparing
        // ClaimedAt would then hand both of them the claim — the one outcome
        // this type exists to prevent.
        var mine = Entry.Claimed(now);
        var entry = _entries.AddOrUpdate(
            (tenantId, key),
            mine,
            (_, existing) => existing.IsUsable(now) ? existing : mine);

        if (ReferenceEquals(entry, mine))
        {
            return Task.FromResult<(IdempotencyClaim, IdempotentResponse?)>(
                (IdempotencyClaim.Acquired, null));
        }

        return Task.FromResult<(IdempotencyClaim, IdempotentResponse?)>(
            entry.Response is { } stored
                ? (IdempotencyClaim.Completed, stored)
                : (IdempotencyClaim.InFlight, null));
    }

    public Task CompleteAsync(
        Guid tenantId, string key, IdempotentResponse response, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(response);

        _entries[(tenantId, key)] = Entry.Completed(clock.UtcNow, response);
        return Task.CompletedTask;
    }

    public Task AbandonAsync(Guid tenantId, string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _entries.TryRemove((tenantId, key), out _);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Drops expired entries, and — only when the map is at its ceiling —
    /// the oldest live ones.
    /// </summary>
    private void Evict(DateTimeOffset now)
    {
        foreach (var (composite, entry) in _entries)
        {
            if (!entry.IsUsable(now))
            {
                _entries.TryRemove(composite, out _);
            }
        }

        if (_entries.Count <= MaxEntries)
        {
            return;
        }

        // Oldest first. Evicting a live claim can let an operation run twice,
        // which is why the ceiling is high enough that reaching it means
        // something is wrong; the alternative — refusing new keys — turns a
        // memory problem into an availability one.
        foreach (var (composite, _) in _entries
                     .OrderBy(pair => pair.Value.ClaimedAt)
                     .Take(_entries.Count - MaxEntries))
        {
            _entries.TryRemove(composite, out _);
        }
    }

    private sealed record Entry(DateTimeOffset ClaimedAt, IdempotentResponse? Response)
    {
        public static Entry Claimed(DateTimeOffset now) => new(now, null);

        public static Entry Completed(DateTimeOffset now, IdempotentResponse response) =>
            new(now, response);

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
