using LearnStack.Infrastructure.Persistence;
using System.Data.Common;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>Short, database-time fenced dispatcher transitions on its confined role.</summary>
public sealed class OutboxDispatchStore : EventTerminalTransition.MintingSource
{
    private const string Incomplete = "processed_at IS NULL AND dead_lettered_at IS NULL";
    private const string Normal = Incomplete + " AND terminal_reason IS NULL AND attempts < 6"
        + " AND available_after <= clock_timestamp() AND locked_by IS NULL";
    private const string Recovery = Incomplete + " AND terminal_reason IS NULL"
        + " AND (locked_by IS NOT NULL OR attempts >= 6)"
        + " AND ((locked_by IS NOT NULL AND locked_until <= clock_timestamp())"
        + " OR (locked_by IS NULL AND attempts >= 6))";
    private const string Admitted = " AND NOT (type = ANY(@pending)) AND NOT (partition_key = ANY(@blocked))";
    private const string ClaimColumns = "id, tenant_id, organization_id, locked_by, attempts, locked_until,"
        + " type, topic, partition_key, occurred_at, correlation_id, causation_id, actor_user_id, payload::text, metadata IS NULL";
    private const string Fence = "id = @id AND locked_by = @token AND locked_until > clock_timestamp()"
        + " AND " + Incomplete;

    private readonly PrivilegedDataSource _source;
    private readonly OutboxEventRegistry _registry;
    private readonly OutboxDispatchOptions _options;
    private readonly IGuidFactory _ids;
    private readonly IClock _clock;
    private readonly IServiceScopeFactory _scopes;

    public OutboxDispatchStore(PrivilegedDataSource source, OutboxEventRegistry registry,
        OutboxDispatchOptions options, IGuidFactory ids, IClock clock, IServiceScopeFactory scopes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(scopes);
        options.Validate();
        _source = source;
        _registry = registry;
        _options = options;
        _ids = ids;
        _clock = clock;
        _scopes = scopes;
    }

    public Task<OutboxDispatchClaim?> TryClaimAsync(IReadOnlyCollection<string> blockedPartitions,
        CancellationToken cancellationToken = default) =>
        AcquireAsync(OutboxClaimKind.Normal, Normal, blockedPartitions, cancellationToken);

    public Task<OutboxDispatchClaim?> TryAcquireRecoveryAsync(IReadOnlyCollection<string> blockedPartitions,
        CancellationToken cancellationToken = default) =>
        AcquireAsync(OutboxClaimKind.Recovery, Recovery, blockedPartitions, cancellationToken);

    public Task<OutboxDispatchClaim?> TryClaimTerminalAsync(IReadOnlyCollection<string> blockedPartitions,
        CancellationToken cancellationToken = default) =>
        AcquireAsync(OutboxClaimKind.Terminal, Incomplete + " AND terminal_reason IS NOT NULL"
            + " AND available_after <= clock_timestamp()"
            + " AND (locked_until IS NULL OR locked_until <= clock_timestamp())", blockedPartitions, cancellationToken);

    private async Task<OutboxDispatchClaim?> AcquireAsync(OutboxClaimKind kind, string eligibility,
        IReadOnlyCollection<string> blocked, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blocked);
        using var deadline = Deadline(cancellationToken);
        await using var connection = await _source.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            WITH candidate AS (
                SELECT id FROM outbox_messages WHERE {eligibility}{Admitted}
                ORDER BY available_after, occurred_at, id FOR UPDATE SKIP LOCKED LIMIT 1
            )
            UPDATE outbox_messages SET locked_by = @token,
                locked_until = clock_timestamp() + @lease,
                attempts = attempts + @increment
            WHERE id = (SELECT id FROM candidate)
            RETURNING {ClaimColumns}
            """, connection, transaction);
        Configure(command);
        AddAdmission(command, blocked);
        command.Parameters.AddWithValue("token", _ids.NewUuidV4().ToString("D"));
        command.Parameters.AddWithValue("lease", _options.LeaseDuration);
        command.Parameters.AddWithValue("increment", kind == OutboxClaimKind.Normal ? 1 : 0);
        OutboxDispatchClaim? result;
        await using (var reader = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false))
        {
            result = await reader.ReadAsync(deadline.Token).ConfigureAwait(false) ? ReadClaim(reader, kind) : null;
        }

        await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
        return result;
    }

    public Task<bool> CompleteAsync(OutboxDispatchClaim claim, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (claim.Kind != OutboxClaimKind.Normal)
            throw new ArgumentException("Only a normal publication claim can complete.", nameof(claim));
        return UpdateAsync(claim, "processed_at = clock_timestamp(), last_error = NULL,"
            + " locked_by = NULL, locked_until = NULL", " AND terminal_reason IS NULL", null, cancellationToken);
    }

    public Task<bool> FailAsync(OutboxDispatchClaim claim, OutboxFailureReason reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (claim.Kind == OutboxClaimKind.Terminal)
            throw new ArgumentException("Terminal requests cannot spend a publication retry.", nameof(claim));
        var descriptor = reason switch
        {
            OutboxFailureReason.PublishFailed => "publish:failed:provider",
            OutboxFailureReason.PublishTimeout => "publish:timeout:deadline",
            OutboxFailureReason.UnknownOutcome => "recovery:unknown:expired",
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };
        return UpdateAsync(claim, """
            terminal_reason = CASE WHEN attempts >= 6 THEN 'retry_exhausted' ELSE NULL END,
            available_after = clock_timestamp() + CASE attempts
                WHEN 1 THEN interval '1 second' WHEN 2 THEN interval '5 seconds'
                WHEN 3 THEN interval '30 seconds' WHEN 4 THEN interval '5 minutes'
                WHEN 5 THEN interval '1 hour' ELSE interval '0 seconds' END,
            last_error = @descriptor, locked_by = NULL, locked_until = NULL
            """, " AND terminal_reason IS NULL", descriptor, cancellationToken);
    }

    public Task<bool> RequestTerminalAsync(OutboxDispatchClaim claim, OutboxTerminalReason reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var code = TerminalCode(reason);
        return UpdateAsync(claim, "terminal_reason = @reason, available_after = clock_timestamp(),"
            + " last_error = @descriptor, locked_by = NULL, locked_until = NULL",
            " AND terminal_reason IS NULL", "terminal:" + code + ":closed", cancellationToken, code);
    }

    private async Task<bool> UpdateAsync(OutboxDispatchClaim claim, string assignments, string extra,
        string? descriptor, CancellationToken cancellationToken, string? reason = null)
    {
        using var deadline = Deadline(cancellationToken);
        await using var connection = await _source.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"UPDATE outbox_messages SET {assignments} WHERE {Fence}{extra}", connection);
        Configure(command);
        AddFence(command, claim);
        if (descriptor is not null) command.Parameters.AddWithValue("descriptor", descriptor);
        if (reason is not null) command.Parameters.AddWithValue("reason", reason);
        return await command.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false) == 1;
    }

    /// <summary>Fences, mints the actual returned-row witness, inserts MUST audit, then commits both.</summary>
    public async Task<bool> TryTerminalizeAsync(OutboxDispatchClaim claim, string validatedAttemptTraceparent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (claim.Kind != OutboxClaimKind.Terminal)
            throw new ArgumentException("Terminalization requires its own acquisition.", nameof(claim));
        using var deadline = Deadline(cancellationToken);
        await using var connection = await _source.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE outbox_messages SET dead_lettered_at = clock_timestamp(),
                locked_by = NULL, locked_until = NULL
            WHERE {Fence} AND terminal_reason IS NOT NULL
            RETURNING id, tenant_id, organization_id, terminal_reason, attempts,
                old.locked_by AS acquisition_token, correlation_id, causation_id, actor_user_id
            """, connection, transaction);
        Configure(command);
        AddFence(command, claim);
        EventTerminalTransition witness;
        await using (var reader = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(deadline.Token).ConfigureAwait(false)) return false;
            witness = FromProducerUpdate(connection, transaction, reader, validatedAttemptTraceparent);
        }

        await using var scope = _scopes.CreateAsyncScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var draft = new AuditEntryDraft
        {
            Id = AuditEntryId.From(_ids.NewUuidV7()),
            TenantId = TenantId.From(witness.TenantId),
            OrganizationId = witness.OrganizationId is { } organization ? OrganizationId.From(organization) : null,
            ActorUserId = UserId.SystemActor,
            ActorEmail = null,
            ModuleName = "platform",
            Operation = "platform.outbox.dead_letter",
            OperationType = OperationType.SecurityEvent,
            OperationClass = OperationClass.Must,
            EntityType = null,
            EntityId = null,
            Outcome = AuditOutcome.Success,
            ErrorKey = null,
            Reason = null,
            BeforeState = null,
            AfterState = null,
            Changes = null,
            CorrelationId = witness.CorrelationId,
            IpAddress = null,
            UserAgent = null,
            Timestamp = _clock.UtcNow,
            Metadata = witness.Metadata,
        };
        await audit.WriteEventTerminalAsync(draft, witness, connection, transaction, deadline.Token).ConfigureAwait(false);
        try
        {
            await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is NpgsqlException or OperationCanceledException or IOException)
        {
            // COMMIT may have reached PostgreSQL. Reconcile on a fresh bounded connection;
            // a durable terminal row already includes its audit and must never be inserted again.
            try
            {
                if (await IsDurablyTerminalAsync(claim.Id).ConfigureAwait(false)) return true;
            }
            catch (Exception reconciliationFailure)
            {
                throw new AggregateException("The outbox terminal commit outcome remains unknown.",
                    exception, reconciliationFailure);
            }
            throw;
        }
    }

    private async Task<bool> IsDurablyTerminalAsync(Guid id)
    {
        using var deadline = Deadline(CancellationToken.None);
        await using var connection = await _source.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM outbox_messages WHERE id = @id AND dead_lettered_at IS NOT NULL)", connection);
        Configure(command);
        command.Parameters.AddWithValue("id", id);
        return (bool)(await command.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false))!;
    }

    public async Task<OutboxQueueSnapshot> ReadSnapshotAsync(IReadOnlyCollection<string> blockedPartitions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blockedPartitions);
        using var deadline = Deadline(cancellationToken);
        await using var connection = await _source.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT count(*), count(*) FILTER (WHERE type = ANY(@pending)),
                COALESCE(GREATEST(0, extract(epoch FROM clock_timestamp() - min(occurred_at)
                    FILTER (WHERE {Normal}{Admitted}))), 0)::double precision
            FROM outbox_messages WHERE {Incomplete}
            """, connection);
        Configure(command);
        AddAdmission(command, blockedPartitions);
        await using var reader = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
        await reader.ReadAsync(deadline.Token).ConfigureAwait(false);
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetDouble(2));
    }

    private static OutboxDispatchClaim ReadClaim(DbDataReader row, OutboxClaimKind kind) =>
        new(row.GetGuid(0), row.GetGuid(1), row.IsDBNull(2) ? null : row.GetGuid(2),
            row.GetString(3), row.GetInt32(4), row.GetFieldValue<DateTimeOffset>(5), kind,
            row.GetString(6), row.GetString(7), row.GetString(8), row.GetFieldValue<DateTimeOffset>(9),
            row.GetString(10), row.IsDBNull(11) ? null : row.GetGuid(11),
            row.IsDBNull(12) ? null : row.GetGuid(12), row.GetString(13), row.GetBoolean(14));

    private CancellationTokenSource Deadline(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(_options.SqlTimeout);
        return source;
    }

    private void Configure(NpgsqlCommand command) =>
        command.CommandTimeout = Math.Max(1, (int)Math.Ceiling(_options.SqlTimeout.TotalSeconds));

    private void AddAdmission(NpgsqlCommand command, IReadOnlyCollection<string> blocked)
    {
        command.Parameters.AddWithValue("pending", NpgsqlDbType.Array | NpgsqlDbType.Text,
            _registry.PendingIdentities.ToArray());
        command.Parameters.AddWithValue("blocked", NpgsqlDbType.Array | NpgsqlDbType.Text, blocked.ToArray());
    }

    private static void AddFence(NpgsqlCommand command, OutboxDispatchClaim claim)
    {
        command.Parameters.AddWithValue("id", claim.Id);
        command.Parameters.AddWithValue("token", claim.Token);
    }

    private static string TerminalCode(OutboxTerminalReason reason) => reason switch
    {
        OutboxTerminalReason.UnknownType => "unknown_type",
        OutboxTerminalReason.RetiredType => "retired_type",
        OutboxTerminalReason.InvalidEnvelope => "invalid_envelope",
        OutboxTerminalReason.RetryExhausted => "retry_exhausted",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}
