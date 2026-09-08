using System.Data;
using System.Data.Common;
using System.Text.Json;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
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
public sealed class PostgresAuditStore(
    IAuditStateCapture capture,
    NpgsqlDataSource dataSource,
    ILogger<PostgresAuditStore> logger)
    : IAuditStore
{
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

        var pending = capture.Intents
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

                Bind(command, Compose(intent, AuditOutcome.Success, intent.DeclaredAt));

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

        capture.MarkWrittenInTransaction();
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
            LogDurableDuplicate(logger, entry.Operation, duplicate);
        }
        catch (DbException failure)
        {
            throw new AuditWriteFailedException(
                $"A MUST-class audit row for {entry.Operation} could not be written "
                + "standalone.",
                failure);
        }
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
            LogBestEffortLost(logger, entry.Operation, failure);
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
        await using var connection = await dataSource
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

    /// <summary>
    /// Turns one intent plus the request's captured changes into the row to insert.
    /// </summary>
    /// <remarks>
    /// The merge is not hypothetical: <c>ProvisionTenantCommand</c> saves three times and
    /// captures <c>Tenant</c> twice, so picking one capture arbitrarily records half of
    /// what happened. <c>before_state</c> is the <b>earliest</b> matching capture's and
    /// <c>after_state</c> the <b>latest</b>, with <c>changes</c> their concatenation in
    /// capture order (<see href="../../../docs/decisions/0044-audit-write-path.md">ADR-0044
    /// Amendment 5 § 2</see>).
    /// </remarks>
    private AuditEntryDraft Compose(
        AuditIntent intent, AuditOutcome outcome, DateTimeOffset timestamp)
    {
        var matching = intent.EntityType is null
            ? []
            : capture.Changes
                .Where(change => string.Equals(
                    change.EntityType, intent.EntityType.Name, StringComparison.Ordinal))
                .ToList();

        var fields = matching.SelectMany(change => change.Fields).ToList();

        return new AuditEntryDraft
        {
            Id = intent.Id,
            TenantId = intent.TenantId,
            OrganizationId = intent.OrganizationId,
            ActorUserId = null,
            ActorEmail = null,
            ModuleName = intent.ModuleName,
            Operation = intent.Operation,
            OperationType = intent.OperationType,
            OperationClass = intent.OperationClass,
            EntityType = intent.EntityType?.Name,
            EntityId = matching.Count == 0 ? null : matching[0].EntityId,
            Outcome = outcome,
            ErrorKey = null,
            Reason = null,
            // The EARLIEST capture's before and the LATEST capture's after, nulls
            // included. Skipping nulls looks like tidying and is not: the interceptor sets
            // BeforeJson to null to say the entity did not exist and AfterJson to null to
            // say it no longer does, so those two are the only captures that carry that
            // meaning. Walking past the first would give a `create` row a complete prior
            // state — the tenant as it stood immediately after its own INSERT — and a
            // reviewer diffing before to after would read a creation as an update. On an
            // append-only table that reading is permanent.
            BeforeState = matching.Count == 0 ? null : matching[0].BeforeJson,
            AfterState = matching.Count == 0 ? null : matching[^1].AfterJson,
            Changes = fields.Count == 0 ? null : SerialiseChanges(fields),
            CorrelationId = null,
            IpAddress = null,
            UserAgent = null,
            Timestamp = timestamp,
            Metadata = null,
        };
    }

    /// <summary>
    /// The <c>changes</c> column: a JSON <b>array</b> of <c>{ path, before, after }</c>,
    /// single-entity and multi-entity alike.
    /// </summary>
    /// <remarks>
    /// Composed as text rather than serialised from objects, because each slot already
    /// holds JSON text — re-serialising would quote a document into one long escaped
    /// string. ADR-0016's polymorphic object-or-array shape is withdrawn: two readers
    /// parse this column, and a shape that changes with the row's arity is a shape each
    /// of them gets wrong once (ADR-0044 § 7).
    /// </remarks>
    private static string SerialiseChanges(IReadOnlyList<CapturedFieldChange> fields)
    {
        var entries = fields.Select(field =>
            "{\"path\":" + JsonSerializer.Serialize(field.Path)
            + ",\"before\":" + (field.BeforeJson ?? "null")
            + ",\"after\":" + (field.AfterJson ?? "null") + "}");

        return AuditJson.CapArray("[" + string.Join(',', entries) + "]");
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

    private static readonly Action<ILogger, string, Exception?> LogBestEffortLost =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(2, nameof(LogBestEffortLost)),
            "A SHOULD/MAY-class audit row for {Operation} was lost. The accepted loss is written down in the module's coverage matrix; the operation itself is unaffected.");
}
