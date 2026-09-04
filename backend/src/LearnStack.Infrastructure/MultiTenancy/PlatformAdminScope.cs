using System.Data.Common;
using System.Runtime.CompilerServices;
using LearnStack.SharedKernel.Tenancy;
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
/// </remarks>
public sealed class PlatformAdminScope(
    IPlatformAdminGate gate,
    [FromKeyedServices(PlatformAdminScope.PlatformDataSourceKey)] Lazy<NpgsqlDataSource> dataSource,
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

    private readonly IPlatformAdminGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    private readonly Lazy<NpgsqlDataSource> _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

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
        // The order below is load-bearing, because Packet 9 inherits this call site and
        // writes its SecurityEvent row where the log line sits.
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        // 1. The gate, before anything opens. ADR-0036 asks for the permission to be
        //    "checked before the scope opens", and a check after the connection exists
        //    would already have spent a BYPASSRLS connection on a refused caller.
        if (!await _gate.IsPermittedAsync(reason, cancellationToken))
        {
            throw new PlatformAdminScopeDeniedException(reason);
        }

        // 2. The credential. Touching Value here is where an absent
        //    ConnectionStrings:PlatformAdmin surfaces — on entry, at the first call, with
        //    a message naming the key rather than a container error naming a type.
        var connection = await _dataSource.Value.OpenConnectionAsync(cancellationToken);

        try
        {
            // 3. Recorded between the open and the transaction. Not on dispose and not
            //    after the body: Packet 9's row must be written on this connection
            //    BEFORE the operation runs, so that an operation which then fails is
            //    still recorded, and this is the position it takes over.
            LogEntered(_logger, reason, callerMember ?? "<unknown>", ShortPath(callerFile), callerLine, null);

            var transaction = await connection.BeginTransactionAsync(cancellationToken);
            return new Handle(connection, transaction, _logger);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

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
    // filtering at Information must still see it. The reason and the call site and
    // nothing else — deliberately no tenant id: TenantId leaves the platform sentinel's
    // value unfixed, and Packet 9 chooses it with the schema that stores it, because a
    // log line is not a one-way door and an identifier minted for a table that does not
    // exist yet is.
    private static readonly Action<ILogger, string, string, string, int, Exception?> LogEntered =
        LoggerMessage.Define<string, string, string, int>(
            LogLevel.Warning,
            new EventId(7001, nameof(LogEntered)),
            "Platform-admin scope entered: {Reason} (from {Member} at {File}:{Line}). "
            + "Cross-tenant access under learnstack_platform.");

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
