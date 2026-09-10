using System.Data;
using System.Data.Common;
using System.Text.Json;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Audit;

/// <summary>
/// The only thing in the solution that inserts an <c>audit_log</c> row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Parameterised SQL, never an EF entity.</b> Two reasons, and neither is
/// performance. Mapping <c>AuditEntry</c> into every module's <c>DbContext</c> would
/// need SharedKernel to reference the Audit module, which already references
/// SharedKernel; and a row written through a <c>DbContext</c> would re-enter
/// <see cref="Capture.AuditChangeTrackerInterceptor"/>, which is a capture loop no
/// exclusion list should have to be the only guard against
/// (<see href="../../../docs/decisions/0033-audit-durability-model.md">ADR-0033
/// § Implementation Notes</see>).
/// </para>
/// <para>
/// <b>It resolves no tenant.</b> Every value the row's own policy checks arrives on the
/// <see cref="AuditIntent"/> or the <see cref="AuditEntryDraft"/>, decided at pipeline
/// step 3 from the tenant context and the provisioning marker. Reading a tenant here
/// would throw on the one case the rule exists for: a provisioning command runs under an
/// <em>unresolved</em> context by construction, while its transaction has announced the
/// tenant it is creating
/// (<see href="../../../docs/decisions/0044-audit-write-path.md">ADR-0044 Amendment
/// 3 § 2</see>).
/// </para>
/// </remarks>
public sealed class PostgresAuditStore : IAuditStore
{
    /// <summary>The meter every audit-write counter hangs off.</summary>
    public const string MeterName = "LearnStack.Audit";

    /// <summary>
    /// Counts the duplicate-key outcome on a standalone re-write.
    /// </summary>
    /// <remarks>
    /// ADR-0033 § 4 and ADR-0044 § 5 both say the <c>23505</c> is "logged, counted, and
    /// swallowed", and the catch below said so too while counting nothing. The count is
    /// the half that matters operationally: each one is a business <c>COMMIT</c> whose
    /// outcome the process could not observe, so a rate that moves is a signal about the
    /// database connection rather than about any one request — and a log line nobody
    /// aggregates is not that signal.
    /// </remarks>
    public const string DurableDuplicateCounterName = "learnstack_audit_standalone_duplicates_total";

    /// <summary>
    /// Counts MUST-class standalone writes that failed outright.
    /// </summary>
    /// <remarks>
    /// The metric <see href="../../../docs/decisions/0033-audit-durability-model.md">ADR-0033
    /// Amendment 3</see> names, beside the <c>audit</c> health check and the
    /// <c>Critical</c> line. It answers a different question from the check: the check says
    /// whether the path is working <i>now</i>, and this says how often it has not — which
    /// is the one an alert threshold is written against. Labelled by operation and nothing
    /// else: the slug set is the catalogue's, so it is bounded and attacker-chosen by
    /// nobody (<see href="../../../docs/standards/10-observability.md">Observability
    /// Standards</see>).
    /// </remarks>
    public const string StandaloneWriteFailureCounterName =
        "learnstack_audit_standalone_write_failures_total";

    private readonly IAuditStateCapture _capture;
    private readonly Lazy<NpgsqlDataSource> _dataSource;
    private readonly ILogger<PostgresAuditStore> _logger;
    private readonly IAuditHealth _health;
    private readonly Counter<long> _durableDuplicates;
    private readonly Counter<long> _standaloneWriteFailures;

    public PostgresAuditStore(
        IAuditStateCapture capture,
        Lazy<NpgsqlDataSource> dataSource,
        ILogger<PostgresAuditStore> logger,
        IMeterFactory meterFactory,
        IAuditHealth health)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        _capture = capture;
        _dataSource = dataSource;
        _logger = logger;
        _health = health ?? throw new ArgumentNullException(nameof(health));

        var meter = meterFactory.Create(MeterName);
        _durableDuplicates = meter.CreateCounter<long>(DurableDuplicateCounterName);
        _standaloneWriteFailures = meter.CreateCounter<long>(StandaloneWriteFailureCounterName);
    }

    /// <summary>
    /// The one INSERT. Every write path uses it; only the connection differs.
    /// </summary>
    /// <remarks>
    /// <c>id</c> and <c>timestamp</c> are supplied rather than defaulted — the
    /// commit-in-doubt pair needs one identity across two connections and two different
    /// instants, which the column defaults cannot give it. The three <c>jsonb</c>
    /// parameters are typed explicitly; Npgsql maps a bare <c>string</c> to <c>text</c>,
    /// and PostgreSQL does not assign <c>text</c> to <c>jsonb</c>.
    /// </remarks>
    private const string InsertSql =
        """
        INSERT INTO audit_log (
            id, tenant_id, organization_id, actor_user_id, actor_email,
            module, operation, operation_type, operation_class,
            entity_type, entity_id, outcome, error_key, reason,
            before_state, after_state, changes,
            correlation_id, ip_address, user_agent, timestamp, metadata)
        VALUES (
            @id, @tenant_id, @organization_id, @actor_user_id, @actor_email,
            @module, @operation, @operation_type, @operation_class,
            @entity_type, @entity_id, @outcome, @error_key, @reason,
            @before_state, @after_state, @changes,
            @correlation_id, @ip_address, @user_agent, @timestamp, @metadata)
        """;

    /// <inheritdoc />
    public async Task WritePendingAsync(
        IUnitOfWork unitOfWork, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        var pending = _capture.Intents
            .Where(intent => intent.OperationClass == OperationClass.Must)
            .ToList();

        if (pending.Count == 0)
        {
            return;
        }

        if (unitOfWork.Transaction is null)
        {
            // Not a silent skip. This method is called from the owning frame immediately
            // before COMMIT, so no transaction here means the caller's own invariant is
            // broken — and writing the rows anywhere else would give them a durability
            // nobody asked for.
            throw new AuditWriteFailedException(
                "WritePendingAsync ran with no ambient transaction. MUST-class rows are "
                + "written on the business transaction, immediately before COMMIT "
                + "(ADR-0033); there is nothing here for them to commit with.");
        }

        try
        {
            foreach (var intent in pending)
            {
                await using var command = unitOfWork.Connection.CreateCommand();
                command.CommandText = InsertSql;
                command.Transaction = unitOfWork.Transaction;

                // The shared composer, because the reconcile writes the other rows and the two
                // must not diverge — they did, and only this path carried a snapshot.
                Bind(command, AuditDraftComposer.Compose(
                    intent, _capture.Changes, AuditOutcome.Success, intent.DeclaredAt));

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (DbException failure)
        {
            // Fail-closed, and the exception is how: TransactionBehavior's catch rolls the
            // business write back and the caller is answered 503 audit_unavailable rather
            // than committing unaudited.
            throw new AuditWriteFailedException(
                "A MUST-class audit row could not be written on the business transaction.",
                failure);
        }

        _capture.MarkWrittenInTransaction();
    }

    /// <inheritdoc />
    public async Task WriteStandaloneAsync(
        AuditEntryDraft entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            await WriteOwnTransactionAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException duplicate)
            when (duplicate.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Positive evidence that the business COMMIT landed: the in-transaction row is
            // already there under this id and timestamp, so the row this call exists to
            // rescue is durable. Logged, counted, and swallowed — it is not an audit
            // failure and must not produce audit_unavailable
            // (ADR-0044 § 5).
            // Logged AND counted, which is what the two ADRs say and what the comment
            // above used to claim on its own.
            LogDurableDuplicate(_logger, entry.Operation, duplicate);
            _durableDuplicates.Add(1, new KeyValuePair<string, object?>("operation", entry.Operation));

            // And healthy: the row is durable, which is the opposite of the state the
            // check reports. Reporting a failure here would take the deployment unhealthy
            // on the one outcome that proves the write path worked.
            _health.ReportStandaloneWriteSucceeded();

            return;
        }
        catch (DbException failure)
        {
            // The three ADR-0033 Amendment 3 assigns to this packet, at the one site that
            // can leave an operation SUCCEEDED and unrecorded: Critical, counted, and the
            // health check unhealthy. What is deferred to Phase 11 is only the act of
            // ceasing to serve past a configured unhealthy window.
            ReportFailure(entry.Operation, failure);

            throw new AuditWriteFailedException(
                $"A MUST-class audit row for {entry.Operation} could not be written "
                + "standalone.",
                failure);
        }
        catch (Exception failure) when (failure is not DbException)
        {
            // The SAME three signals, for the failures that never reach the catch above.
            // `DbException` is not the only way this write dies: the application data
            // source is built lazily, so a missing credential surfaces as an
            // InvalidOperationException from the Lazy factory, and the physical-connection
            // initializer that refuses a role able to bypass row security throws one too —
            // both from inside OpenConnectionAsync, both from inside this try.
            //
            // Before this, every one of them left the `audit` health check GREEN while no
            // row could be written at all, which is the precise question that check exists
            // to answer. Rethrown UNCHANGED rather than wrapped: each caller's exception
            // contract stays exactly as it was, and only the reporting was missing.
            ReportFailure(entry.Operation, failure);

            throw;
        }

        _health.ReportStandaloneWriteSucceeded();
    }

    /// <summary>Critical, counted, and the health check unhealthy — the three, together.</summary>
    /// <remarks>
    /// One helper because they are one event, and because two catch clauses reach it. A
    /// site that logged without counting would be an alert nobody can threshold, and one
    /// that counted without marking the check would leave a readiness surface reporting a
    /// path that cannot write.
    /// </remarks>
    private void ReportFailure(string operation, Exception failure)
    {
        LogStandaloneWriteFailed(_logger, operation, failure);
        _standaloneWriteFailures.Add(1, new KeyValuePair<string, object?>("operation", operation));
        _health.ReportStandaloneWriteFailed(operation);
    }

    /// <inheritdoc />
    public async Task WriteBestEffortAsync(
        AuditEntryDraft entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            await WriteOwnTransactionAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (DbException failure)
        {
            // The opposite posture to WriteStandaloneAsync, and the accepted loss is
            // written down in the module's coverage matrix rather than assumed here.
            LogBestEffortLost(_logger, entry.Operation, failure);
        }
    }

    /// <inheritdoc />
    public async Task WritePlatformScopeAsync(
        AuditEntryDraft entry,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        // The caller's connection and the caller's transaction, because the row runs as
        // learnstack_platform under the sentinel tenant and the request's transaction is
        // the wrong lifetime for a record that must outlive it. No announcement is issued
        // here: the platform role holds BYPASSRLS, and announcing the sentinel on a
        // request connection is what the four guards refuse (ADR-0044 § 10).
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = InsertSql;
            command.Transaction = transaction;

            Bind(command, entry);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbException failure)
        {
            throw new AuditWriteFailedException(
                "The platform-scope audit row could not be written, so the scope must not "
                + "be entered.",
                failure);
        }
    }

    /// <summary>
    /// <c>BEGIN; SET LOCAL app.tenant_id; SET LOCAL app.organization_id; INSERT; COMMIT</c>
    /// on a connection of this call's own.
    /// </summary>
    /// <remarks>
    /// <b>Both</b> session variables, from the draft. <c>audit_log</c> is org-scoped, so a
    /// row whose <c>organization_id</c> is non-null while the organization GUC is unset
    /// fails <c>WITH CHECK</c> — the unset GUC reads as the empty string, <c>NULLIF</c>
    /// makes it <c>NULL</c>, and the comparison is <c>NULL</c>, which is false. Measured,
    /// and every <c>denied</c> row for an org-scoped resource travels this path
    /// (<see href="../../../docs/decisions/0033-audit-durability-model.md">ADR-0033
    /// Amendment 2 § 5</see>).
    /// </remarks>
    private async Task WriteOwnTransactionAsync(
        AuditEntryDraft entry, CancellationToken cancellationToken)
    {
        // The FIFTH announcement site, and it needs the same refusal the other four carry.
        // TenantId's own remarks enumerate the guards — SetProvisioningTenantContextAsync,
        // TenantOwnership.EnsureRealTenant, EventTenantContext.FromEnvelope and
        // SetTenantContextAsync — because a CHECK on `tenants` cannot stop a session
        // variable from being announced. This method announces one from a draft, on a
        // learnstack_app connection, and audit_log deliberately has no foreign key to
        // `tenants` — so nothing else in the stack refuses it. Measured before the guard:
        // a draft carrying the sentinel wrote a platform-scope row through the runtime
        // role, which is precisely what IAuditStore's own contract says cannot happen.
        //
        // The sentinel's rows have one writer: WritePlatformScopeAsync, on the scope's own
        // platform-role connection (ADR-0044 § 10).
        if (entry.TenantId == TenantId.PlatformSentinel)
        {
            throw new AuditWriteFailedException(
                "A platform-scope row carries TenantId.PlatformSentinel and belongs to "
                + "WritePlatformScopeAsync, which writes it on the scope's own "
                + "platform-role connection. Announcing the sentinel on a runtime "
                + "connection is refused here as it is at every other announcement site "
                + "(ADR-0044 § 1, § 10).");
        }

        await using var connection = await _dataSource.Value
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (var announce = connection.CreateCommand())
        {
            announce.Transaction = transaction;
            announce.CommandText =
                "SELECT set_config('app.tenant_id', @tenant, true), "
                + "set_config('app.organization_id', @organization, true)";

            Add(announce, "tenant", entry.TenantId.Value.ToString());
            Add(announce, "organization", entry.OrganizationId?.Value.ToString() ?? string.Empty);

            await announce.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = InsertSql;

            Bind(command, entry);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Bind(DbCommand command, AuditEntryDraft entry)
    {
        Add(command, "id", entry.Id.Value);
        Add(command, "tenant_id", entry.TenantId.Value);
        Add(command, "organization_id", entry.OrganizationId?.Value);
        Add(command, "actor_user_id", entry.ActorUserId?.Value);
        Add(command, "actor_email", entry.ActorEmail);
        Add(command, "module", entry.ModuleName);
        Add(command, "operation", entry.Operation);
        Add(command, "operation_type", entry.OperationType.ToString());
        Add(command, "operation_class", entry.OperationClass.ToString());
        Add(command, "entity_type", entry.EntityType);
        Add(command, "entity_id", entry.EntityId);

        // Lowercase, because two Accepted ADRs write the four values that way and
        // ck_audit_log_outcome admits nothing else. The other two closed sets store the
        // C# member name unchanged, which is why only this one is transformed.
        Add(command, "outcome", entry.Outcome.ToString().ToLowerInvariant());

        Add(command, "error_key", entry.ErrorKey);
        Add(command, "reason", entry.Reason);
        AddJsonb(command, "before_state", entry.BeforeState);
        AddJsonb(command, "after_state", entry.AfterState);
        AddJsonb(command, "changes", entry.Changes);
        Add(command, "correlation_id", entry.CorrelationId);
        Add(command, "ip_address", entry.IpAddress);
        Add(command, "user_agent", entry.UserAgent);
        Add(command, "timestamp", entry.Timestamp);
        AddJsonb(command, "metadata", entry.Metadata);
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;

        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// Binds a <c>jsonb</c> parameter, typed explicitly.
    /// </summary>
    /// <remarks>
    /// Npgsql infers <c>text</c> from a <c>string</c>, and PostgreSQL will not assign
    /// <c>text</c> to <c>jsonb</c> — so an inferred parameter fails at execute time on
    /// every row carrying a snapshot, which is every row this store exists to write.
    /// </remarks>
    private static void AddJsonb(DbCommand command, string name, string? json)
    {
        var parameter = new NpgsqlParameter(name, NpgsqlDbType.Jsonb)
        {
            Value = (object?)json ?? DBNull.Value,
        };

        command.Parameters.Add(parameter);
    }

    // LoggerMessage source-generated delegates (CA1848), matching the house style in
    // TransactionBehavior and LoggingBehavior.
    private static readonly Action<ILogger, string, Exception?> LogDurableDuplicate =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogDurableDuplicate)),
            "The standalone audit re-write for {Operation} hit a duplicate key, which is positive evidence that the business COMMIT landed: the in-transaction row is durable.");

    private static readonly Action<ILogger, string, Exception?> LogStandaloneWriteFailed =
        LoggerMessage.Define<string>(
            LogLevel.Critical,
            new EventId(3, nameof(LogStandaloneWriteFailed)),
            "A MUST-class audit row for {Operation} could not be written standalone. The operation is not on the record, the audit health check is unhealthy, and it stays unhealthy until a later standalone write succeeds (ADR-0033 Amendment 1, Amendment 3).");

    private static readonly Action<ILogger, string, Exception?> LogBestEffortLost =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(2, nameof(LogBestEffortLost)),
            "A SHOULD/MAY-class audit row for {Operation} was lost. The accepted loss is written down in the module's coverage matrix; the operation itself is unaffected.");
}
