using System.Collections.Concurrent;
using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Npgsql;

namespace LearnStack.Infrastructure.MultiTenancy;

/// <summary>
/// Reads <c>platform_host_to_tenant</c> for one host, caching both answers — the
/// found one through <see cref="ICacheService"/>, the unknown one through a
/// structure capped on its own.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="NpgsqlDataSource"/>, not a module <c>DbContext</c> and not
/// <c>IUnitOfWork</c>.</b> This runs in host classification, before
/// authentication and before any transaction exists — and the shared registration
/// helper throws by design when a module context is resolved outside the ambient
/// transaction, because a context that never saw <c>SET LOCAL</c> reads zero rows
/// from every tenant-owned table.
/// <see href="../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040
/// § Who sets <c>app.tenant_id</c></see> already puts every pre-transaction reader
/// on "a short transaction of its own on its own connection"; this is that, and
/// this class is the only setter of <c>app.resolving_host</c>.
/// </para>
/// <para>
/// <b>Registered a singleton</b>, so the flight map below is process-wide. A
/// scoped registration gives every request its own map and coalesces nothing,
/// which is the whole of what the map is for.
/// </para>
/// </remarks>
public sealed class CachedHostToTenantResolver(
    ICacheService cache,
    UnknownHostCache unknownHosts,
    HostResolutionOptions options,
    Lazy<NpgsqlDataSource> dataSource) : IHostToTenantResolver
{
    private readonly ConcurrentDictionary<string, Lazy<Task<HostResolution?>>> _flights =
        new(StringComparer.Ordinal);

    private readonly ICacheService _cache = cache ?? throw new ArgumentNullException(nameof(cache));

    private readonly UnknownHostCache _unknownHosts =
        unknownHosts ?? throw new ArgumentNullException(nameof(unknownHosts));

    private readonly HostResolutionOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Lazy so that constructing the resolver builds no data source.
    /// </summary>
    /// <remarks>
    /// A platform host — the Studio entry point, <c>localhost</c> in development —
    /// is answered by configuration and never reaches a lookup, so it must cost no
    /// database work. Holding the data source directly would build it when this
    /// singleton is constructed, which is at the first classified request whatever
    /// its host, and would make a deployment that only ever serves platform hosts
    /// require a runtime credential it never uses. The build is already deferred on
    /// the other side — the composition root registers a factory, not an instance —
    /// so this keeps that deferral rather than defeating it.
    /// </remarks>
    private readonly Lazy<NpgsqlDataSource> _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <inheritdoc />
    public async Task<HostResolution?> ResolveAsync(
        string host, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        // Composed by the factory, never interpolated: CacheKey.EnsureValid is what
        // stops an unnormalized spelling creating a parallel entry, and it refuses
        // an IP literal outright.
        //
        // Total, because this is the anonymous pre-authentication path. The two
        // validators — EffectiveHost.Normalize and CacheKey.EnsureValid — agree
        // today, and the last time they did not, a trailing dot walked an IPv4
        // literal past the first and into the second, where the throw became a 500
        // and an error-tracker capture per request instead of the bodyless 404
        // this method exists to make cheap. A host this key shape refuses is an
        // unresolvable host, and saying so is the answer; letting the next
        // divergence be an incident is not.
        string key;

        try
        {
            key = CacheKey.ForHostMapping(host);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (await _cache.GetAsync<HostResolution>(key, cancellationToken) is { } cached)
        {
            return cached;
        }

        if (_unknownHosts.Contains(host))
        {
            return null;
        }

        return await ReadCoalescedAsync(host, key, cancellationToken);
    }

    /// <summary>
    /// One database round trip per host, however many callers arrive during it.
    /// </summary>
    /// <remarks>
    /// Get-then-set has no factory for <c>GetOrSetAsync</c> to coalesce, so N
    /// simultaneous first requests for one cold host would be N transactions. The
    /// flight runs on <see cref="CancellationToken.None"/>: one caller hanging up
    /// must not cancel the lookup the others are waiting on.
    /// <c>WaitAsync(cancellationToken)</c> stops only <i>this</i> caller waiting,
    /// and a caller that stops waiting never reaches the negative-cache write above
    /// — so that structure is populated only by a request that survived its own
    /// lookup.
    /// </remarks>
    private async Task<HostResolution?> ReadCoalescedAsync(
        string host, string key, CancellationToken cancellationToken)
    {
        var flight = _flights.GetOrAdd(
            host,
            static (h, state) => new Lazy<Task<HostResolution?>>(
                () => state.Self.ReadAndRetireAsync(h, state.Key)),
            (Self: this, Key: key));

        return await flight.Value.WaitAsync(cancellationToken);
    }

    private async Task<HostResolution?> ReadAndRetireAsync(string host, string key)
    {
        // Retirement is bound to the FLIGHT's termination, inside the flight's own
        // task — never to a caller's exit. Retiring in each waiter's finally lets a
        // joiner that cancels de-register a read whose transaction is still open,
        // so the next arrival opens a second one: the stampede the coalescing
        // exists to prevent, reintroduced by its own cleanup. Packet 5 convicted
        // that shape in InMemoryCacheService, which retires on the factory's
        // termination and lets a waiter's exit only decrement a count. This
        // resolver has no cancellation to propagate into the read, so it needs the
        // retirement rule and not the waiter bookkeeping. Exactly one flight is
        // registered per host at a time, so the plain TryRemove has no successor to
        // race.
        try
        {
            var resolution = await ReadAsync(host, CancellationToken.None);

            // Published inside the flight, not by each waiter. A lookup whose only
            // caller hangs up still completes — that is the point of running it on
            // CancellationToken.None — and if the write lived in the caller's tail
            // the answer would be thrown away, so the next request would pay for
            // the same round trip again. Doing it here also means the answer is
            // written once however many waiters there are, rather than once per
            // waiter.
            if (resolution is null)
            {
                _unknownHosts.Add(host);
            }
            else
            {
                await _cache.SetAsync(
                    key, resolution, _options.PositiveCache, CancellationToken.None);
            }

            return resolution;
        }
        finally
        {
            _flights.TryRemove(host, out _);
        }
    }

    private async Task<HostResolution?> ReadAsync(string host, CancellationToken cancellationToken)
    {
        // The policy on this table admits exactly the row the resolver ANNOUNCES.
        // Without the SET LOCAL the predicate is NULL and the query returns
        // nothing, so the miss path opens its own transaction: SET LOCAL outside a
        // transaction block emits "WARNING: SET LOCAL can only be used in
        // transaction blocks" and has no effect, and a session-level
        // set_config(..., false) would survive on a pooled connection into the next
        // request.
        await using var connection = await _dataSource.Value.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        // set_config(..., true) is SET LOCAL's function form and is
        // transaction-local for the same reason. It has to be this form:
        // `SET LOCAL app.resolving_host = $1` is a syntax error — PostgreSQL's SET
        // takes no bind parameter — so the parameterised spelling every other query
        // uses is unavailable here, and interpolating into SET would be an
        // injection site on the anonymous page-load path.
        await using (var announce = new NpgsqlCommand(
            "SELECT set_config('app.resolving_host', @host, true)", connection, transaction))
        {
            announce.Parameters.AddWithValue("host", host);
            await announce.ExecuteNonQueryAsync(cancellationToken);
        }

        // BOTH terms. Active (owned, verified) and publicly live are distinct
        // states — the row exists from submission onward, before DNS points
        // anywhere — and only the latter may answer an anonymous page load. Both
        // are read because ADR-0036 invalidates this cache on the transaction that
        // flips EITHER flag, which is only meaningful if both feed the answer.
        await using var read = new NpgsqlCommand(
            """
            SELECT tenant_id, organization_id
            FROM platform_host_to_tenant
            WHERE host = @host AND is_active AND is_publicly_live
            """,
            connection,
            transaction);
        read.Parameters.AddWithValue("host", host);

        var resolution = await ReadSingleAsync(read, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return resolution;
    }

    private static async Task<HostResolution?> ReadSingleAsync(
        NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new HostResolution(
            TenantId.From(reader.GetGuid(0)),
            await reader.IsDBNullAsync(1, cancellationToken)
                ? null
                : OrganizationId.From(reader.GetGuid(1)));
    }
}

/// <summary>How long a found mapping is cached.</summary>
/// <remarks>
/// Configuration rather than a literal for the same reason
/// <see cref="UnknownHostCacheOptions"/> is: this block gets copied, and a copied
/// number outlives the measurement that chose it. The L2 value is inert until the
/// Phase 11 adapter lands — <c>InMemoryCacheService</c> is L1 only — and is carried
/// so the two are chosen together rather than discovered apart.
/// </remarks>
public sealed record HostResolutionOptions
{
    public CacheOptions PositiveCache { get; init; } =
        new(L1Ttl: TimeSpan.FromMinutes(2), L2Ttl: TimeSpan.FromMinutes(15));
}
