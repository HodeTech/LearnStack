using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace LearnStack.Infrastructure.Audit;

/// <summary>
/// Applies a tenant's <c>audit_config</c> overrides to the catalogue's declared tier.
/// </summary>
/// <remarks>
/// <para>
/// <b>The loader runs on a cache miss, on its own short transaction.</b> Not on the
/// request's: at pipeline step 3 no transaction is open and <c>app.tenant_id</c> is unset,
/// and <c>audit_config</c> carries <c>ENABLE</c> + <c>FORCE</c> row security — so a read
/// on the request's connection would return <b>zero rows silently</b>, which reads exactly
/// like "this tenant has no overrides" and never trips a fail-closed <c>catch</c>. That is
/// the whole reason the loader announces the tenant itself
/// (<see href="../../../docs/decisions/0033-audit-durability-model.md">ADR-0033 § Decision,
/// and Amendment 4 § 3</see>, which settles this against the Implementation Notes clause
/// that described an out-of-band refresher no phase owns).
/// </para>
/// <para>
/// It is a query per <c>(tenant, generation)</c> rather than per request, which is what
/// "never a request-path query" was reaching for.
/// </para>
/// <para>
/// <b>Narrow only.</b> <c>is_enabled = false</c> silences a SHOULD or a MAY;
/// <c>true</c> is the baseline, which is also what an absent row means. A MUST is
/// untouched either way — the floor is re-applied after the override, so a compromised
/// tenant admin cannot switch off the detector that would catch the next cross-tenant
/// probe (<see href="../../../docs/decisions/0033-audit-durability-model.md">ADR-0033
/// Amendment 4 § 1</see>).
/// </para>
/// </remarks>
/// <param name="cache">The one cache abstraction; this projection is L1-only today.</param>
/// <param name="dataSource">
/// The application data source, built on first use.
/// <para>
/// <b><see cref="Lazy{T}"/>, for the reason the platform data source is one.</b>
/// Building the application data source needs a connection string, and a deployment
/// that serves only platform hosts legitimately has none — the composition root defers
/// the build so a request answered from <c>Tenancy:PlatformHosts</c> costs nothing
/// below it. Taking the data source eagerly here would resolve it on every request
/// that reaches the pipeline, which is every request, and would turn "no credential"
/// into a 500 on a surface that never touches the database.
/// </para>
/// </param>
/// <param name="logger">Records an override read that failed and fell back.</param>
/// <param name="tenantAccessor">Selects bounded delivery diagnostics; never supplies override authority.</param>
public sealed class AuditConfigService(
    ICacheService cache,
    Lazy<NpgsqlDataSource> dataSource,
    ILogger<AuditConfigService> logger,
    ITenantContextAccessor? tenantAccessor = null)
    : IAuditConfigService
{
    /// <summary>The module segment of this projection's cache key.</summary>
    private const string CacheModule = "audit";

    /// <summary>The logical-name segment of this projection's cache key.</summary>
    private const string CacheName = "config";

    /// <summary>
    /// How long a tenant's overrides stay cached, and therefore the whole of the
    /// staleness bound.
    /// </summary>
    /// <remarks>
    /// <b>Stated here rather than inherited.</b> This family has no eager invalidation —
    /// nothing writes <c>audit_config</c> until
    /// <see href="../../../docs/roadmap/phase-06-renderer-admin-studio.md">Phase 06</see>'s
    /// Studio editor lands with the grant beside the command that needs it — so the TTL
    /// *is* the contract a tenant sees: an override takes at most this long to take
    /// effect (<see href="../../../docs/standards/20-infrastructure-stack.md">Standards 20
    /// § ICacheService</see>). Taking <c>InMemoryCacheService</c>'s default instead would
    /// make a documented tenant-visible bound move whenever an unrelated adapter changed
    /// its own, and the default is 60 seconds, not this.
    /// </remarks>
    public static readonly TimeSpan OverrideTtl = TimeSpan.FromMinutes(5);

    /// <summary>The cache key this projection reads and writes for one tenant.</summary>
    /// <remarks>
    /// Public because the key <i>is</i> the isolation boundary and the family is a
    /// registered one — Standards 20 lists <c>{tenant_id}:audit:config</c> in the table
    /// that also allowlists the <c>cache.name</c> metric label. A test pins the spelling
    /// against that table; a private composition could drift from it silently.
    /// </remarks>
    public static string CacheKeyFor(TenantId tenantId) =>
        CacheKey.ForTenant(tenantId.Value, CacheModule, CacheName);

    /// <inheritdoc />
    public async Task<AuditClassification> ClassifyAsync(
        TenantId? tenantId, AuditCatalogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var declared = Declared(entry.OperationClass);

        // A MUST is not overridable, so there is nothing to read for one. Short-circuiting
        // here is not an optimisation: it is what makes a cache outage unable to affect
        // the floor at all, rather than merely unable to lower it.
        if (declared == AuditClassification.Must || tenantId is null)
        {
            return declared;
        }

        IReadOnlyDictionary<string, bool> overrides;

        try
        {
            overrides = await OverridesAsync(tenantId.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Falls back to the declared tier rather than rejecting. The in-process
            // catalogue carries the same MUST floor, so nothing proceeds unaudited — and
            // rejecting every request platform-wide because a cache is unavailable is a
            // worse compliance outcome than losing one tenant's narrowing.
            LogOverrideReadFailed(logger, tenantId.Value.Value,
                AsyncExecutionDiagnostics.IsTrusted(tenantAccessor?.Current) ? null : failure);

            return declared;
        }

        return overrides.TryGetValue(Key(entry), out var enabled) && !enabled
            ? AuditClassification.Off
            : declared;
    }

    private async Task<IReadOnlyDictionary<string, bool>> OverridesAsync(
        TenantId tenantId, CancellationToken cancellationToken)
    {
        var sourceOwner = JobApplicationSourceOwner.Current;
        return await cache.GetOrSetAsync(
            CacheKeyFor(tenantId),
            token => StartLoadAsync(tenantId, sourceOwner, token),
            new CacheOptions(OverrideTtl),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one tenant's overrides on a connection of this method's own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>BEGIN; SET TRANSACTION READ ONLY; SET LOCAL app.tenant_id; SELECT; COMMIT</c> —
    /// the announcement is the point, and it is why this cannot ride the request's
    /// connection, which has none at step 3. The transaction exists so the announcement is
    /// still in effect for the <c>SELECT</c> that follows it, not for atomicity:
    /// <c>set_config(…, true)</c> is transaction-local, so outside an explicit transaction it
    /// belongs to its own single-statement one and is gone before the read runs. The
    /// alternative — announcing at session level — would survive the read and then ride the
    /// pooled connection to whoever got it next. Read-only, because neither this nor any
    /// reader like it writes.
    /// </para>
    /// <para>
    /// The <c>SELECT</c> names its tenant too, bound from the trusted argument. Row
    /// security is the second layer, not the only one (Database Standards § Raw SQL): a
    /// policy regression would otherwise let another tenant's override silence an
    /// operation for this one.
    /// </para>
    /// </remarks>
    private Task<IReadOnlyDictionary<string, bool>> StartLoadAsync(
        TenantId tenantId, JobApplicationSourceOwner? sourceOwner, CancellationToken cancellationToken)
    {
        if (sourceOwner is null) return LoadAsync(tenantId, null, cancellationToken);
        var resources = sourceOwner.BeginAuditConfigLoad();
        var actual = LoadAsync(tenantId, resources, cancellationToken);
        resources.HoldActualTask(actual);
        return actual;
    }

    private async Task<IReadOnlyDictionary<string, bool>> LoadAsync(
        TenantId tenantId, JobApplicationAuxiliaryResources? resources, CancellationToken cancellationToken)
    {
        NpgsqlConnection? connection = null;
        NpgsqlTransaction? transaction = null;
        NpgsqlCommand? readOnly = null;
        NpgsqlCommand? announce = null;
        NpgsqlCommand? read = null;
        NpgsqlDataReader? reader = null;
        try
        {
            connection = dataSource.Value.CreateConnection();
            resources?.HoldConnection(connection);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            resources?.HoldTransaction(transaction);
            readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction);
            resources?.HoldCommand(readOnly);
            await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            announce = new NpgsqlCommand(
                "SELECT set_config('app.tenant_id', @tenant, true)", connection, transaction);
            resources?.HoldCommand(announce);
            announce.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            await announce.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            read = new NpgsqlCommand(
                "SELECT module, operation, is_enabled FROM audit_config "
                + "WHERE tenant_id = @tenant AND deleted_at IS NULL", connection, transaction);
            resources?.HoldCommand(read);
            read.Parameters.AddWithValue("tenant", tenantId.Value);
            reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            resources?.HoldReader(reader);
            var overrides = new Dictionary<string, bool>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                overrides[reader.GetString(0) + "|" + reader.GetString(1)] = reader.GetBoolean(2);
            if (resources is not null) await resources.DisposeReaderAsync().ConfigureAwait(false);
            else { var completedReader = reader; reader = null; await completedReader.DisposeAsync().ConfigureAwait(false); }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return overrides;
        }
        finally
        {
            if (resources is not null) await resources.CleanupAsync().ConfigureAwait(false);
            else await CleanupUnownedAsync(reader, read, announce, readOnly, transaction, connection).ConfigureAwait(false);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "All outer resources receive their cleanup attempt; the final disposal failure is rethrown.")]
    private static async ValueTask CleanupUnownedAsync(NpgsqlDataReader? reader, NpgsqlCommand? read, NpgsqlCommand? announce, NpgsqlCommand? readOnly, NpgsqlTransaction? transaction, NpgsqlConnection? connection)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        if (reader is not null)
        {
            try { await reader.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception); }
        }
        if (read is not null)
        {
            try { await read.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception); }
        }
        if (announce is not null)
        {
            try { await announce.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception); }
        }
        if (readOnly is not null)
        {
            try { await readOnly.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception); }
        }
        if (transaction is not null)
        {
            try { await transaction.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception); }
        }
        if (connection is not null)
        {
            try { await connection.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception); }
        }
        failure?.Throw();
    }

    private static string Key(AuditCatalogEntry entry) => entry.ModuleName + "|" + entry.Operation;

    /// <summary>
    /// The declared tier as a classification.
    /// </summary>
    /// <remarks>
    /// A total mapping rather than a cast: the two enums share three member names and
    /// nothing else, and a cast would silently turn a member added to one into a
    /// different member of the other.
    /// </remarks>
    private static AuditClassification Declared(OperationClass declared) => declared switch
    {
        OperationClass.Must => AuditClassification.Must,
        OperationClass.Should => AuditClassification.Should,
        OperationClass.May => AuditClassification.May,
        _ => throw new ArgumentOutOfRangeException(
            nameof(declared), declared, "The catalogue declared a tier this classifier does not know."),
    };

    private static readonly Action<ILogger, Guid, Exception?> LogOverrideReadFailed =
        LoggerMessage.Define<Guid>(
            LogLevel.Error,
            new EventId(1, nameof(LogOverrideReadFailed)),
            "Reading audit_config overrides for tenant {TenantId} failed; classification fell back to the in-process catalogue, which carries the same MUST floor. Nothing proceeds unaudited — the tenant's narrowing is not applied until a read succeeds. This line is the whole report: the audit health check answers only whether MUST rows can be written.");
}
