using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.Infrastructure.Persistence;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace LearnStack.Infrastructure.MultiTenancy;

/// <summary>
/// Opens a <c>learnstack_platform</c> connection, on its own, for one admitted operator act.
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
/// is also why this is not a ninth out-of-band setter. The second because nothing calls
/// this path read-only.
/// </para>
/// <para>
/// <b>Admission before entry.</b> The caller holds a <see cref="PlatformAdmission"/> that
/// <see cref="IPlatformOperatorGate"/> minted after evaluating a grant, so a refused operator
/// never reaches a connection (ADR-0036, ADR-0101 § 7). Entry no longer asks a gate; the
/// admission is the proof. An act that needs only the app or outbox role never enters here.
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
/// rather than injected.
/// </para>
/// <para>
/// <b>The handle is a facade.</b> It exposes no connection or transaction, because a
/// reference captured before the commit would run in autocommit on a connection that sees
/// every tenant. Completion and abandonment close the connection.
/// </para>
/// </remarks>
public sealed class PlatformAdminScope(
    [FromKeyedServices(PlatformAdminScope.PlatformDataSourceKey)] PrivilegedDataSource dataSource,
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

    private readonly PrivilegedDataSource _dataSource =
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
    /// the concrete <see cref="PlatformAdminScope"/> would otherwise get the bare defaults
    /// and log <c>&lt;unknown&gt;</c> at <c>&lt;unknown&gt;:0</c>.
    /// </remarks>
    public async Task<IPlatformAdminScopeHandle> EnterAsync(
        PlatformAdmission admission,
        CancellationToken cancellationToken = default,
        [CallerMemberName] string? callerMember = null,
        [CallerFilePath] string? callerFile = null,
        [CallerLineNumber] int callerLine = 0)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (!PlatformOperations.IsRegistered(admission.Operation) || !admission.Operation.NeedsPlatformRole)
        {
            throw new ArgumentException(
                "The admission is for an operation that does not use the platform role.", nameof(admission));
        }

        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();

        // 0. The admission is short lived and single use. A stale one, or one already spent,
        //    never reaches a connection: the grant was evaluated for a moment and an act, and
        //    a kept or replayed proof would turn that into a standing capability.
        if ((_clock.UtcNow - admission.IssuedAt).Duration() > PlatformAdmission.MaximumAge)
        {
            throw new InvalidOperationException(
                "The platform admission is stale. An act asks the gate again rather than reusing a proof.");
        }

        if (!admission.TryConsume())
        {
            throw new InvalidOperationException(
                "The platform admission was already used to enter a scope. One admission opens one scope.");
        }

        // 1. The catalogue's own declarations, before the connection. They fix the tier and
        //    the operation type, so the rows and `docs/modules/tenancy/audit.md` cannot say
        //    different things, and an absent declaration is refused here rather than papered
        //    over with a hard-coded MUST: a deployment whose catalogue lost either slug must
        //    not enter a cross-tenant scope unrecorded.
        var declaredEntry = Declared(EnterOperation);
        var declaredResult = Declared(admission.Operation.AuditSlug);
        var declaredIntent = admission.Operation.AttemptAuditSlug is { } attemptSlug ? Declared(attemptSlug) : null;

        // 2. The credential. Opening through the guarded facade here is where an absent
        //    ConnectionStrings:PlatformAdmin surfaces — on entry, at the first call, with
        //    a message naming the key rather than a container error naming a type.
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        NpgsqlTransaction? transaction = null;

        try
        {
            // 3. The row, BEFORE the privileged work can begin and in a transaction of its
            //    OWN that commits first — so an operation that later fails, throws, or is
            //    simply abandoned is still on the record (ADR-0044 § 10). Reading one's own
            //    transaction back is not durability. learnstack_app cannot write under the
            //    platform sentinel at all, which is why the row cannot ride the request's
            //    connection. A failure here throws out of EnterAsync and the catch below
            //    disposes the connection: an unrecordable entry is a refused entry.
            var entry = ComposeEntry(admission, declaredEntry, callerMember, callerFile, callerLine);

            await RecordAsync(entry, connection, cancellationToken);

            // 4. Only now the transaction the caller works in. A row whose entry then failed
            //    to begin one records a permitted, recorded entry that handed nothing out —
            //    the honest side of the trade: the other side is a handed-out BYPASSRLS
            //    connection with no durable record behind it.
            transaction = await connection.BeginTransactionAsync(cancellationToken);

            // 5. And the log line as well, which the row does not replace operationally.
            LogEntered(
                _logger,
                admission.Operation.ReasonSlug,
                callerMember ?? "<unknown>",
                ShortPath(callerFile),
                callerLine,
                entry.Id.Value,
                null);

            return new Handle(
                connection,
                transaction,
                (result, current, token) => WriteResultAsync(admission, declaredResult, result, connection, current, token),
                declaredIntent is null
                    ? null
                    : (result, current, token) => WriteIntentAsync(admission, declaredIntent, result, connection, current, token),
                RedactsAny(admission.Operation)
                    ? (plan, current, token) => RedactAsync(admission, plan, connection, current, token)
                    : null,
                plan => MayRedact(admission.Operation, plan),
                () => PlatformOperatorMetrics.Result(admission.Operation),
                _logger);
        }
        catch
        {
            // The work transaction exists here only if something after its BEGIN threw, and
            // its disposal is not independently observable: disposing the connection rolls
            // back whatever is open on it. The connection disposal below is the one the
            // refused-entry case kills, and it is in the same catch because an entry that
            // threw must not keep the one connection that sees every tenant.
            try
            {
                if (transaction is not null)
                {
                    await transaction.DisposeAsync();
                }
            }
            finally
            {
                await connection.DisposeAsync();
            }

            throw;
        }
    }

    private AuditCatalogEntry Declared(string slug) =>
        _catalog.TryGetOffPath(slug, out var declared)
            ? declared
            : throw new AuditWriteFailedException(
                $"'{slug}' is not in the audit catalogue, so a cross-tenant act cannot be recorded "
                + "and must not happen. It is declared off-path by the Tenancy source (ADR-0044 § 10).");

    /// <summary>
    /// Writes the entry row on the scope's connection, in a transaction of its own, and
    /// commits it.
    /// </summary>
    /// <remarks>
    /// Disposing the recording transaction uncommitted rolls it back, so a write that fails
    /// leaves nothing behind — and nothing has been handed out, because the work transaction
    /// is begun only after this returns.
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

    /// <summary>
    /// Writes the act's MUST result row on the work transaction, through the witness-bound
    /// method (ADR-0101 § 10).
    /// </summary>
    private async Task WriteResultAsync(
        PlatformAdmission admission,
        AuditCatalogEntry declared,
        PlatformResult result,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var draft = ComposeOperatorRow(admission, declared, result, AuditOutcome.Success);

        await using var services = _scopeFactory.CreateAsyncScope();
        await services.ServiceProvider
            .GetRequiredService<IAuditStore>()
            .WritePlatformOperatorResultAsync(draft, admission, connection, transaction, cancellationToken);
    }

    /// <summary>
    /// Writes the act's MUST intent row on the work transaction, through the witness-bound method; the handle
    /// commits it before the effect starts (ADR-0099 Amendment 11).
    /// </summary>
    private async Task WriteIntentAsync(
        PlatformAdmission admission,
        AuditCatalogEntry declared,
        PlatformResult intent,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var draft = ComposeOperatorRow(admission, declared, intent, AuditOutcome.Indeterminate);

        await using var services = _scopeFactory.CreateAsyncScope();
        await services.ServiceProvider
            .GetRequiredService<IAuditStore>()
            .WritePlatformOperatorIntentAsync(draft, admission, connection, transaction, cancellationToken);
    }

    /// <summary>
    /// The one operation that may redact each pass (ADR-0108 § 2): an erasure's under its privileged step,
    /// <c>complete_erasure</c>, and a closure's tenant and sentinel passes under the closure's Finalize (ADR-0107 § 7).
    /// Neither redacts the other's plan, and no other operation redacts anything.
    /// </summary>
    private static PlatformOperation? Redactor(AuditRedactionPass pass) => pass switch
    {
        AuditRedactionPass.Erasure => PlatformOperations.ErasureComplete,
        AuditRedactionPass.ClosureTenant or AuditRedactionPass.ClosureSentinel => PlatformOperations.ClosureFinalize,
        _ => null,
    };

    /// <summary>Whether <paramref name="operation"/>'s scope may redact <paramref name="plan"/>.</summary>
    private static bool MayRedact(PlatformOperation operation, AuditRedactionPlan plan) =>
        ReferenceEquals(Redactor(plan.Pass), operation);

    /// <summary>Whether <paramref name="operation"/> may redact any plan, and so whether its handle is given the executor at all.</summary>
    private static bool RedactsAny(PlatformOperation operation) =>
        Enum.GetValues<AuditRedactionPass>().Any(pass => ReferenceEquals(Redactor(pass), operation));

    /// <summary>
    /// Runs the one Audit-owned executor on the scope's connection and current work transaction (ADR-0108 § 2).
    /// </summary>
    /// <remarks>
    /// Resolved per call from a fresh service scope, as the audit store is, so this assembly names only the
    /// SharedKernel port and never the Audit infrastructure that implements it. A deployment that registers no executor
    /// cannot redact and must not complete an erasure or a closure, so its absence is refused by name rather than
    /// treated as nothing to redact.
    /// </remarks>
    private async Task<AuditRedactionReport> RedactAsync(
        PlatformAdmission admission,
        AuditRedactionPlan plan,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var services = _scopeFactory.CreateAsyncScope();
        var redactor = services.ServiceProvider.GetService<IAuditRedactor>()
            ?? throw new InvalidOperationException(
                "No IAuditRedactor is registered, so the audit log cannot be redacted and the act must not complete.");
        return await redactor.RedactAsync(plan, admission, connection, transaction, cancellationToken);
    }

    private AuditEntryDraft ComposeOperatorRow(
        PlatformAdmission admission, AuditCatalogEntry declared, PlatformResult result, AuditOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!StronglyTypedId.IsAssigned(result.TenantId))
        {
            throw new ArgumentException("A result row needs an assigned tenant.", nameof(result));
        }

        // A caller's state is checked here, as a programming error, before it can reach the database
        // and come back as a write failure the endpoint would report as an audit outage.
        RequireJsonState(result.BeforeState, nameof(result.BeforeState));
        RequireJsonState(result.AfterState, nameof(result.AfterState));

        // The ticket stays out of a row a tenant can read: it appears only under the sentinel.
        var isSentinel = result.TenantId == TenantId.PlatformSentinel;
        return new AuditEntryDraft
        {
            Id = AuditEntryId.From(_guidFactory.NewUuidV7()),
            TenantId = result.TenantId,
            OrganizationId = null,
            ActorUserId = null,
            ActorEmail = null,
            ModuleName = declared.ModuleName,
            Operation = declared.Operation,
            OperationType = declared.OperationType,
            OperationClass = declared.OperationClass,
            EntityType = result.EntityType ?? declared.EntityType?.Name,
            EntityId = result.EntityId,
            Outcome = outcome,
            ErrorKey = null,
            Reason = admission.Operation.ReasonSlug,
            BeforeState = result.BeforeState,
            AfterState = result.AfterState,
            Changes = null,
            CorrelationId = admission.Operator.CorrelationId,
            IpAddress = null,
            UserAgent = null,
            Timestamp = _clock.UtcNow,
            Metadata = OperatorMetadata(admission, includeTicket: isSentinel, provenance: null),
        };
    }

    private const int MaximumStateBytes = 32 * 1024;

    private static void RequireJsonState(string? state, string name)
    {
        if (state is null)
        {
            return;
        }

        if (System.Text.Encoding.UTF8.GetByteCount(state) > MaximumStateBytes)
        {
            throw new ArgumentException($"The {name} of a result row is larger than {MaximumStateBytes} bytes.", name);
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(state);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new ArgumentException($"The {name} of a result row is not well-formed JSON.", name);
        }
    }

    /// <summary>
    /// The row one entry writes: the sentinel tenant, the catalogue's tier, the operator and
    /// the caller's provenance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The platform-scope class of ADR-0044 § 1.</b> It is a platform-scope operation
    /// with no resolvable tenant, written standalone on the scope's own platform-role
    /// connection — the one place the sentinel is legal, every other announcement site
    /// refusing it.
    /// </para>
    /// <para>
    /// <b>No actor user id, and the operator in metadata.</b> An operator is not a
    /// <c>users</c> row (ADR-0101 § 9), so the actor column stays NULL and
    /// <c>metadata.operator</c> carries the issuer, the subject digest, the kind, the
    /// accepted <c>acr</c> and the executor. Compiler-supplied provenance lands in
    /// <c>metadata</c> rather than in <c>reason</c>: the reason is a closed slug that a
    /// query groups by.
    /// </para>
    /// </remarks>
    private AuditEntryDraft ComposeEntry(
        PlatformAdmission admission,
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
            Reason = admission.Operation.ReasonSlug,
            BeforeState = null,
            AfterState = null,
            Changes = null,
            CorrelationId = admission.Operator.CorrelationId,
            IpAddress = null,
            UserAgent = null,
            Timestamp = _clock.UtcNow,
            Metadata = OperatorMetadata(admission, includeTicket: true, Provenance(callerMember, callerFile, callerLine)),
        };

    private static string OperatorMetadata(PlatformAdmission admission, bool includeTicket, string? provenance) =>
        "{"
        + (provenance is null ? string.Empty : "\"caller\":" + provenance + ",")
        + "\"operator\":"
        + PlatformOperatorAttribution.From(admission).ToOperatorJson(includeTicket ? admission.Ticket : null)
        + "}";

    private static string Provenance(string? callerMember, string? callerFile, int callerLine) =>
        "{"
        + "\"member\":" + AuditJson.Quote(callerMember ?? "<unknown>")
        + ",\"file\":" + AuditJson.Quote(ShortPath(callerFile))
        + ",\"line\":" + callerLine.ToString(CultureInfo.InvariantCulture)
        + "}";

    private static string ShortPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "<unknown>";
        }

        var segments = path.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length <= 2 ? string.Join('/', segments) : string.Join('/', segments[^2..]);
    }

    private static readonly Action<ILogger, string, string, string, int, Guid, Exception?> LogEntered =
        LoggerMessage.Define<string, string, string, int, Guid>(
            LogLevel.Warning,
            new EventId(7001, nameof(LogEntered)),
            "Platform-admin scope entered: {Reason} (from {Member} at {File}:{Line}). "
            + "Cross-tenant access under learnstack_platform, audit row {AuditEntryId}.");

    /// <summary>
    /// The bounded facade over one work transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One statement, a DML verb.</b> A statement must start with <c>SELECT</c>,
    /// <c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c> or <c>WITH</c> and contain no further
    /// statement, so a caller cannot send <c>COMMIT</c>, <c>SET</c> or <c>BEGIN</c> through the
    /// facade and end or reshape the transaction the result row is bound to. This is a guard
    /// against a mistake by trusted code, not a sandbox: the architecture rules decide who may
    /// hold a handle at all.
    /// </para>
    /// <para>
    /// <b>A result row, and no failed statement, before the commit.</b> A state change that
    /// commits without its MUST row is the failure this scope exists to prevent, and a
    /// statement that threw leaves PostgreSQL's transaction aborted, so a later commit would
    /// silently roll back. A read-only scope has nothing to commit and ends by disposal.
    /// </para>
    /// <para>
    /// <b>The result row is last.</b> A statement after it could change state the row does not
    /// describe, so the facade refuses one. No statement may name <c>audit_log</c> either: the
    /// platform role holds audit maintenance rights for retention, and a scope that rewrote the
    /// record of its own act would defeat it. The one way to the log is the redaction, which only
    /// an operation ADR-0108 sanctions is handed, for the plans it sanctions, through the Audit-owned
    /// executor.
    /// </para>
    /// <para>
    /// <b>One owner.</b> A handle is used by the single call that entered it, on one logical
    /// thread; it is not safe for concurrent use.
    /// </para>
    /// </remarks>
    private sealed class Handle(
        NpgsqlConnection connection,
        NpgsqlTransaction firstTransaction,
        Func<PlatformResult, NpgsqlTransaction, CancellationToken, Task> writeResult,
        Func<PlatformResult, NpgsqlTransaction, CancellationToken, Task>? writeIntent,
        Func<AuditRedactionPlan, NpgsqlTransaction, CancellationToken, Task<AuditRedactionReport>>? redact,
        Func<AuditRedactionPlan, bool> mayRedact,
        Action onCommitted,
        ILogger logger)
        : IPlatformAdminScopeHandle
    {
        private static readonly string[] StatementVerbs = ["SELECT", "INSERT", "UPDATE", "DELETE", "WITH"];

        // Replaced once, at the intent checkpoint: the first transaction commits the intent row alone, and the work
        // that follows runs in a fresh one on the same connection.
        private NpgsqlTransaction _transaction = firstTransaction;
        private bool _resolved;
        private bool _disposed;
        private bool _faulted;
        private bool _intentWritten;
        private bool _resultWritten;

        public async Task<int> ExecuteAsync(
            string sql, IReadOnlyList<PlatformSqlParameter>? parameters = null, CancellationToken cancellationToken = default)
        {
            EnsureUsable();
            EnsureStatementsAllowed();
            await using var command = Build(sql, parameters);
            try
            {
                return await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }

        public async Task<IReadOnlyList<T>> QueryAsync<T>(
            string sql,
            IReadOnlyList<PlatformSqlParameter>? parameters,
            Func<DbDataReader, T> map,
            CancellationToken cancellationToken = default)
        {
            EnsureUsable();
            EnsureStatementsAllowed();
            ArgumentNullException.ThrowIfNull(map);
            await using var command = Build(sql, parameters);
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var rows = new List<T>();
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows.Add(map(reader));
                }

                return rows;
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }

        public async Task WriteIntentAsync(PlatformResult intent, CancellationToken cancellationToken = default)
        {
            EnsureUsable();
            if (writeIntent is null)
            {
                throw new InvalidOperationException("This operation declares no intent checkpoint.");
            }

            if (_intentWritten || _resultWritten)
            {
                throw new InvalidOperationException(
                    "An intent row is written once, before the result row.");
            }

            try
            {
                await writeIntent(intent, _transaction, cancellationToken);
            }
            catch (Exception failure) when (failure is not ArgumentException)
            {
                _faulted = true;
                throw;
            }

            try
            {
                // The row commits alone and before the effect: an effect that later fails, or a process that dies,
                // leaves it standing. A commit whose outcome is unknown stops the act all the same, because the
                // effect must not start on an intent nobody can prove was recorded.
                await _transaction.CommitAsync(cancellationToken);
            }
            catch (Exception failure) when (failure is not (OutOfMemoryException or AccessViolationException))
            {
                // Any failure of the commit, a connection already broken included, stops the effect: it must not start on
                // an intent nobody can prove was recorded (ADR-0044 Amendment 18).
                _faulted = true;
                throw new AuditWriteFailedException(
                    "The platform operator intent row could not be committed, so the effect must not start.", failure);
            }

            try
            {
                await _transaction.DisposeAsync();
                _transaction = await connection.BeginTransactionAsync(cancellationToken);
            }
            catch
            {
                _faulted = true;
                throw;
            }

            _intentWritten = true;
        }

        public async Task<AuditRedactionReport> RedactAuditAsync(
            AuditRedactionPlan plan, CancellationToken cancellationToken = default)
        {
            EnsureUsable();
            EnsureStatementsAllowed();
            ArgumentNullException.ThrowIfNull(plan);
            if (redact is null || !mayRedact(plan))
            {
                // Refused before anything runs, so the scope stays usable: an operation that redacts nothing, or a plan
                // of the other redacting operation's kind.
                throw new InvalidOperationException(
                    "This platform scope's operation may not redact this plan's audit rows (ADR-0108 § 2).");
            }

            try
            {
                // The current transaction, read at the call: an intent checkpoint replaces it, so the redaction commits or
                // rolls back with what follows it on this one, the intent row of a closure's Finalize that runs it
                // before the checkpoint (ADR-0108 § 8), or else the work and the result row.
                return await redact(plan, _transaction, cancellationToken);
            }
            catch (Exception failure) when (failure is not ArgumentException)
            {
                // A failed statement leaves the transaction aborted, and a missing executor means the act cannot
                // finish: either way the scope can only be abandoned, so nothing of it commits.
                _faulted = true;
                throw;
            }
        }

        public async Task WriteResultAsync(PlatformResult result, CancellationToken cancellationToken = default)
        {
            EnsureUsable();
            if (writeIntent is not null && !_intentWritten)
            {
                throw new InvalidOperationException(
                    "An operation with an intent checkpoint writes its intent row before its result row.");
            }

            try
            {
                await writeResult(result, _transaction, cancellationToken);
            }
            catch (Exception failure) when (failure is not ArgumentException)
            {
                // An argument the scope refused before it wrote anything leaves the transaction intact;
                // anything else may have left it aborted.
                _faulted = true;
                throw;
            }

            _resultWritten = true;
        }

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            EnsureUsable();
            if (!_resultWritten)
            {
                throw new InvalidOperationException(
                    "A platform scope commits only after its MUST result row is written. An act with "
                    + "nothing to record ends by disposal, which rolls back.");
            }

            _resolved = true;
            await _transaction.CommitAsync(cancellationToken);
            onCommitted();

            // The connection closes at completion, not at disposal: nothing may run on a
            // connection that sees every tenant once its transaction is over.
            await CloseAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (!_resolved)
                {
                    try
                    {
                        await _transaction.RollbackAsync();
                    }
                    catch (Exception failure) when (failure is InvalidOperationException or NpgsqlException)
                    {
                        LogRollbackFailed(logger, failure);
                    }
                }
            }
            finally
            {
                await CloseAsync();
            }
        }

        private async ValueTask CloseAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                await _transaction.DisposeAsync();
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }

        private NpgsqlCommand Build(string sql, IReadOnlyList<PlatformSqlParameter>? parameters)
        {
            RequireOneStatement(sql);
            var command = new NpgsqlCommand(sql, connection, _transaction);
            if (parameters is not null)
            {
                foreach (var parameter in parameters)
                {
                    command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
                }
            }

            return command;
        }

        private static void RequireOneStatement(string sql)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sql);
            var text = sql.AsSpan().Trim();
            if (text.EndsWith(";"))
            {
                text = text[..^1].TrimEnd();
            }

            var verbEnd = text.IndexOfAny(" \t\r\n(");
            var verb = (verbEnd < 0 ? text : text[..verbEnd]).ToString();
            if (text.Contains(';') || !StatementVerbs.Contains(verb, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "A platform scope statement is a single SELECT, INSERT, UPDATE, DELETE or WITH statement.",
                    nameof(sql));
            }

            if (text.Contains("audit_log", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("A platform scope statement cannot name the audit log.", nameof(sql));
            }
        }

        private void EnsureStatementsAllowed()
        {
            if (_resultWritten)
            {
                throw new InvalidOperationException(
                    "A platform scope's result row is its last write. A statement after it could change "
                    + "state the row does not describe; run it before the row.");
            }
        }

        private void EnsureUsable()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_resolved)
            {
                throw new InvalidOperationException(
                    "This platform-admin scope has been resolved and is no longer usable. "
                    + "A caller needing further cross-tenant work takes a fresh scope: the "
                    + "connection bypasses Row Level Security and is closed at completion.");
            }

            if (_faulted)
            {
                throw new InvalidOperationException(
                    "A statement of this platform-admin scope failed, so its transaction can only be abandoned. "
                    + "Dispose the scope; the work rolls back.");
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
