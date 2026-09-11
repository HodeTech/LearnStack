using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace LearnStack.Infrastructure.MultiTenancy;

/// <summary>
/// Opens a <c>learnstack_platform</c> connection, on its own, for one bounded operation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stateless and singleton.</b> Every <c>EnterAsync</c> returns an independent handle
/// owning its own connection and transaction, so two concurrent or nested entries share
/// nothing. Per-entry state on the singleton would put two callers on one
/// <c>BYPASSRLS</c> connection, which is the hazard <c>IUnitOfWork</c> already documents
/// for the ambient one — worse here, because the connection sees every tenant.
/// </para>
/// <para>
/// <b>It never joins the ambient unit of work.</b> No <c>BeginTransactionAsync</c> on
/// <c>IUnitOfWork</c>, no <c>Database.UseTransaction</c>, no <c>SetTenantContextAsync</c>.
/// The whole point is a second connection under a different role; enlisting would put
/// the bypass on the request's own connection and leave it there.
/// </para>
/// <para>
/// <b>No <c>set_config('app.tenant_id', …)</c>, and no <c>SET TRANSACTION READ ONLY</c>.</b>
/// The first because there is no policy to announce to — the role bypasses them — which
/// is also why this is not an eighth out-of-band setter. The second because nothing calls
/// this path read-only: both named consumers, GDPR redaction and the retention purge,
/// write.
/// </para>
/// <para>
/// <b>Entry is audited, and refusing to record it refuses the entry.</b> Every
/// <c>EnterAsync</c> writes one <c>platform.admin_scope.enter</c> row on the scope's own
/// connection, in a transaction of its own that <b>commits before</b> the transaction the
/// caller works in begins — so an operation that later fails, throws or is abandoned is
/// still on the record
/// (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 § 10</see>).
/// The scope is a <b>singleton</b> and <c>IAuditStore</c> is <b>scoped</b>, which is why
/// the store is resolved from a fresh <see cref="IServiceScopeFactory"/> scope per entry
/// rather than injected: this path is entered from background work as readily as from a
/// request, so there is not always an ambient scope to capture — and capturing one on a
/// singleton would hand every later entry the first caller's.
/// </para>
/// </remarks>
public sealed class PlatformAdminScope(
    IPlatformAdminGate gate,
    [FromKeyedServices(PlatformAdminScope.PlatformDataSourceKey)] Lazy<NpgsqlDataSource> dataSource,
    IAuditCatalog catalog,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IGuidFactory guidFactory,
    ILogger<PlatformAdminScope> logger)
    : IPlatformAdminScope
{
    /// <summary>The DI key the platform data source is registered under.</summary>
    /// <remarks>
    /// Public because the key is not the capability — <c>GetKeyedServices</c> with
    /// <c>KeyedService.AnyKey</c> reaches a keyed registration whatever the key is
    /// spelled, so hiding it buys nothing a reader can rely on.
    /// <c>Platform_DataSource_Resolved_Only_By_PlatformAdminScope</c> is the boundary.
    /// </remarks>
    public const string PlatformDataSourceKey = "PlatformAdmin";

    /// <summary>The slug every entry records, declared off-path by the Tenancy source.</summary>
    /// <remarks>
    /// Its module segment is <c>platform</c> because the scope belongs to no module's
    /// request path, while the matrix row lives in <c>docs/modules/tenancy/audit.md</c>
    /// because that is where a reader looks for it (ADR-0044 Amendment 3 § 1).
    /// </remarks>
    public const string EnterOperation = "platform.admin_scope.enter";

    private readonly IPlatformAdminGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    private readonly Lazy<NpgsqlDataSource> _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    private readonly IAuditCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    private readonly IServiceScopeFactory _scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    private readonly IGuidFactory _guidFactory =
        guidFactory ?? throw new ArgumentNullException(nameof(guidFactory));

    private readonly ILogger<PlatformAdminScope> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    /// <remarks>
    /// The <c>[Caller*]</c> attributes are restated here and not left to the interface.
    /// C# fills them in from the <b>static type of the receiver</b>, so a caller holding
    /// the concrete <see cref="PlatformAdminScope"/> — every test that constructs it
    /// directly, and anything resolving it by implementation type — would otherwise get
    /// the bare defaults and log <c>&lt;unknown&gt;</c> at <c>&lt;unknown&gt;:0</c>.
    /// Losing the provenance silently is exactly what this record exists to prevent.
    /// </remarks>
    public async Task<IPlatformAdminScopeHandle> EnterAsync(
        string reason,
        CancellationToken cancellationToken = default,
        [CallerMemberName] string? callerMember = null,
        [CallerFilePath] string? callerFile = null,
        [CallerLineNumber] int callerLine = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        // 1. The gate, before anything opens. ADR-0036 asks for the permission to be
        //    "checked before the scope opens", and a check after the connection exists
        //    would already have spent a BYPASSRLS connection on a refused caller.
        if (!await _gate.IsPermittedAsync(reason, cancellationToken))
        {
            throw new PlatformAdminScopeDeniedException(reason);
        }

        // 2. The catalogue's own declaration, before the connection. It is what fixes the
        //    tier and the operation type, so the row and `docs/modules/tenancy/audit.md`
        //    cannot say different things — and an absent declaration is refused here
        //    rather than papered over with a hard-coded MUST, because a deployment whose
        //    catalogue lost this slug must not enter a cross-tenant scope unrecorded.
        if (!_catalog.TryGetOffPath(EnterOperation, out var declared))
        {
            throw new AuditWriteFailedException(
                $"'{EnterOperation}' is not in the audit catalogue, so entry into a "
                + "cross-tenant scope cannot be recorded and must not happen. It is "
                + "declared off-path by the Tenancy source (ADR-0044 § 10).");
        }

        // 3. The credential. Touching Value here is where an absent
        //    ConnectionStrings:PlatformAdmin surfaces — on entry, at the first call, with
        //    a message naming the key rather than a container error naming a type.
        var connection = await _dataSource.Value.OpenConnectionAsync(cancellationToken);
        NpgsqlTransaction? transaction = null;

        try
        {
            // 4. The row, BEFORE the privileged work can begin and in a transaction of its
            //    OWN that commits first — so an operation that later fails, throws, or is
            //    simply abandoned is still on the record, which is the sentence ADR-0044
            //    § 10 writes. It rode the business transaction until the review of this
            //    packet: a caller that read across every tenant on the handle and then threw
            //    took the only record of that read down with its rollback. Reading one's own
            //    transaction back is not durability.
            //
            //    learnstack_app cannot write under the platform sentinel at all, which is
            //    why the row cannot ride the request's connection (ADR-0044 § 10). A failure
            //    here throws out of EnterAsync and the catch below disposes the connection:
            //    an unrecordable entry is a refused entry, and no business transaction has
            //    been begun for it.
            var entry = Compose(reason, declared, callerMember, callerFile, callerLine);

            await RecordAsync(entry, connection, cancellationToken);

            // 5. Only now the transaction the caller works in. A row whose entry then failed
            //    to begin one records a permitted, recorded entry that handed nothing out —
            //    the rarer and the honest side of the trade: the other side is a handed-out
            //    BYPASSRLS connection with no durable record behind it.
            transaction = await connection.BeginTransactionAsync(cancellationToken);

            // 6. And the log line as well, which the row does not replace operationally.
            //    The row is the durable record a compliance reviewer reads afterwards;
            //    this is the line an operator alerting at Warning sees while it is
            //    happening. It carries the row's id, so the two are one another's index.
            LogEntered(
                _logger,
                reason,
                callerMember ?? "<unknown>",
                ShortPath(callerFile),
                callerLine,
                entry.Id.Value,
                null);

            return new Handle(connection, transaction, _logger);
        }
        catch
        {
            // The business transaction exists here only if something after its BEGIN threw
            // — the log line, in practice never — and its disposal is NOT independently
            // observable, which is better said than implied: disposing the connection rolls
            // back and clears whatever transaction is open on it. What it buys is not
            // depending on that, because the rollback-on-close is Npgsql's own behaviour
            // rather than anything DbTransaction promises. The connection disposal below is
            // the one the refused-entry case kills, and it is in the same catch because an
            // entry that threw must not keep the one connection that sees every tenant.
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }

            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The row one entry writes: the sentinel tenant, the catalogue's tier, and the
    /// caller's provenance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The only class of row that carries <see cref="TenantId.PlatformSentinel"/>.</b>
    /// It is a platform-scope operation with no resolvable tenant, and it is written
    /// standalone on the scope's own platform-role connection — which is the one place the
    /// sentinel is legal, every other announcement site refusing it (ADR-0044 § 1, § 10).
    /// </para>
    /// <para>
    /// <b>No actor and no correlation id, and both are deliberate.</b> There is no
    /// principal in the process — authentication is Phase 02b — so the caller is known
    /// only through the compiler-supplied provenance, which lands in <c>metadata</c>
    /// rather than being flattened into <c>reason</c>: the reason is an operator-authored
    /// slug that a query groups by, and appending a file and a line to it would make every
    /// group of one. The correlation id is scoped state, and this scope is entered from
    /// background work as readily as from a request.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Writes the entry row on the scope's connection, in a transaction of its own, and
    /// commits it.
    /// </summary>
    /// <remarks>
    /// Disposing the recording transaction uncommitted rolls it back, so a write that fails
    /// leaves nothing behind — and nothing has been handed out, because the business
    /// transaction is begun only after this returns.
    /// </remarks>
    private async Task RecordAsync(
        AuditEntryDraft entry, NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var recording = await connection.BeginTransactionAsync(cancellationToken);

        await using (var services = _scopeFactory.CreateAsyncScope())
        {
            await services.ServiceProvider
                .GetRequiredService<IAuditStore>()
                .WritePlatformScopeAsync(entry, connection, recording, cancellationToken);
        }

        await recording.CommitAsync(cancellationToken);
    }

    private AuditEntryDraft Compose(
        string reason,
        AuditCatalogEntry declared,
        string? callerMember,
        string? callerFile,
        int callerLine) =>
        new()
        {
            Id = AuditEntryId.From(_guidFactory.NewUuidV7()),
            TenantId = TenantId.PlatformSentinel,
            OrganizationId = null,
            ActorUserId = null,
            ActorEmail = null,
            ModuleName = declared.ModuleName,
            Operation = declared.Operation,
            OperationType = declared.OperationType,
            OperationClass = declared.OperationClass,
            EntityType = declared.EntityType?.Name,
            EntityId = null,
            Outcome = AuditOutcome.Success,
            ErrorKey = null,
            Reason = reason,
            BeforeState = null,
            AfterState = null,
            Changes = null,
            CorrelationId = null,
            IpAddress = null,
            UserAgent = null,
            Timestamp = _clock.UtcNow,
            Metadata = Provenance(callerMember, callerFile, callerLine),
        };

    /// <summary>The calling site, as the row's <c>metadata</c> document.</summary>
    private static string Provenance(string? callerMember, string? callerFile, int callerLine) =>
        AuditJson.CapObject(
            "{\"caller\":{"
            + "\"member\":" + AuditJson.Quote(callerMember ?? "<unknown>")
            + ",\"file\":" + AuditJson.Quote(ShortPath(callerFile))
            + ",\"line\":" + callerLine.ToString(CultureInfo.InvariantCulture)
            + "}}");

    /// <summary>
    /// The last two segments of a compile-time path.
    /// </summary>
    /// <remarks>
    /// <c>CallerFilePath</c> is the absolute path on the machine that compiled the
    /// assembly, so logging it whole puts a build-agent directory layout into every
    /// forwarded line and tells a reader nothing the file name does not.
    /// </remarks>
    private static string ShortPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "<unknown>";
        }

        var segments = path.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length <= 2 ? string.Join('/', segments) : string.Join('/', segments[^2..]);
    }

    // Warning, because a cross-tenant bypass is not an ordinary event and an operator
    // filtering at Information must still see it. Still no tenant id: every one of these
    // rows carries TenantId.PlatformSentinel, so logging it would be a constant.
    //
    // The audit entry id is what the line gained in Packet 9. The row is the durable
    // record and this is the real-time signal, and without the id an operator alerting on
    // the line has no key to find the row it is about — which is the whole of what the
    // two are worth together.
    private static readonly Action<ILogger, string, string, string, int, Guid, Exception?> LogEntered =
        LoggerMessage.Define<string, string, string, int, Guid>(
            LogLevel.Warning,
            new EventId(7001, nameof(LogEntered)),
            "Platform-admin scope entered: {Reason} (from {Member} at {File}:{Line}). "
            + "Cross-tenant access under learnstack_platform, audit row {AuditEntryId}.");

    /// <summary>One entry's connection and transaction.</summary>
    private sealed class Handle(
        NpgsqlConnection connection, DbTransaction transaction, ILogger logger)
        : IPlatformAdminScopeHandle
    {
        private bool _resolved;
        private bool _disposed;

        public DbConnection Connection
        {
            get
            {
                EnsureUsable();
                return connection;
            }
        }

        public DbTransaction Transaction
        {
            get
            {
                EnsureUsable();
                return transaction;
            }
        }

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            EnsureUsable();

            // Marked resolved BEFORE the await. Two things depend on the ordering and
            // only one of them is observable, which is worth saying rather than
            // implying. A COMMIT that faults at the server —
            // a deferred constraint, a serialization failure — leaves the outcome
            // genuinely unknown, and ADR-0033 calls that state Indeterminate rather
            // than failed. Setting the flag afterwards would leave it false, so
            // disposal would issue ROLLBACK on a transaction that is already over,
            // which throws, which skips both disposals, which strands a BYPASSRLS
            // connection outside the pool for the life of the process — measured, and
            // it also replaces the caller's real PostgresException with a bookkeeping
            // one. NpgsqlUnitOfWork nulls its transaction before the same await for the
            // same reason.
            //
            // The catch in DisposeAsync makes that leak impossible on its own, so with
            // both present this ordering is not independently observable — no test here
            // fails if it is reverted, and none pretends to. What it still buys is the
            // semantics: a faulted commit is Indeterminate, and attempting to undo an
            // outcome nobody knows is a different claim from declining to.
            _resolved = true;
            await transaction.CommitAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                // Only an ABANDONED frame is rolled back. A frame that ended without
                // resolving has failed, and leaving its writes for a later decision is
                // how a partial cross-tenant mutation ships. A faulted commit is not
                // that case and is deliberately left alone.
                //
                // The explicit rollback stays rather than relying on disposal: this
                // field is a DbTransaction, whose base DisposeAsync delegates to an
                // empty Dispose(bool). Rolling back on dispose is NpgsqlTransaction's
                // own override, so dropping the line would make a cross-tenant rollback
                // depend on the runtime type behind a base-class reference.
                if (!_resolved)
                {
                    try
                    {
                        await transaction.RollbackAsync();
                    }
                    catch (Exception failure) when (failure is InvalidOperationException or NpgsqlException)
                    {
                        // A connection already broken by the failure being cleaned up
                        // after is the ordinary way here. Logged and swallowed, because
                        // throwing from disposal replaces whatever the caller was
                        // actually failing on.
                        LogRollbackFailed(logger, failure);
                    }
                }

                await transaction.DisposeAsync();
            }
            finally
            {
                // In a finally. Nothing above may strand the one connection in this
                // process that sees every tenant.
                await connection.DisposeAsync();
            }
        }

        /// <summary>
        /// Refuses a handle that is disposed or already resolved.
        /// </summary>
        /// <remarks>
        /// <b>Resolved is terminal, and fencing <c>Connection</c> is the half that
        /// matters.</b> Measured: after a successful commit the connection is still
        /// open, so a statement issued on it runs in <i>autocommit</i> — on a
        /// <c>BYPASSRLS</c> connection, with no transaction to undo it. A write there
        /// survives <c>DisposeAsync</c>, which is the exact opposite of what this
        /// type's contract promises. Fencing only <c>Transaction</c> would leave that
        /// hole open.
        /// </remarks>
        private void EnsureUsable()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_resolved)
            {
                throw new InvalidOperationException(
                    "This platform-admin scope has been resolved and is no longer usable. "
                    + "A caller needing further cross-tenant work takes a fresh scope: the "
                    + "connection bypasses Row Level Security, and a statement issued after "
                    + "the commit runs in autocommit, so disposal cannot roll it back.");
            }
        }
    }

    private static readonly Action<ILogger, Exception?> LogRollbackFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(7002, nameof(LogRollbackFailed)),
            "Platform-admin scope could not roll back an abandoned transaction. The "
            + "connection is still returned to the pool; the server ends the transaction "
            + "when it closes.");
}
