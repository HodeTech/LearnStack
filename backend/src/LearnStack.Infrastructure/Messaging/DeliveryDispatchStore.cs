using LearnStack.Infrastructure.Persistence;
using LearnStack.Application.Jobs;
using System.Data.Common;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>Short, database-time fenced dispatcher transitions on its confined role.</summary>
public sealed class DeliveryDispatchStore : EventTerminalTransition.MintingSource
{
    private const string Incomplete = "processed_at IS NULL AND dead_lettered_at IS NULL";
    private const string Normal = Incomplete + " AND terminal_reason IS NULL AND attempts < 6"
        + " AND available_after <= clock_timestamp() AND locked_by IS NULL";
    private const string Recovery = Incomplete + " AND terminal_reason IS NULL"
        + " AND (locked_by IS NOT NULL OR attempts >= 6)"
        + " AND ((locked_by IS NOT NULL AND locked_until <= clock_timestamp())"
        + " OR (locked_by IS NULL AND attempts >= 6))";
    private const string Terminal = Incomplete + " AND terminal_reason IS NOT NULL"
        + " AND available_after <= clock_timestamp()"
        + " AND (locked_until IS NULL OR locked_until <= clock_timestamp())";
    private const string Held = "EXISTS (SELECT 1 FROM unnest(@held_types, @held_subscriptions)"
        + " AS held(type, subscription_id) WHERE held.type = event_deliveries.type"
        + " AND held.subscription_id = event_deliveries.subscription_id)";
    private const string Admitted = " AND NOT (" + Held + ") AND NOT (partition_key = ANY(@blocked))";
    private const string ClaimColumns = "tenant_id, subscription_id, event_id, organization_id, locked_by,"
        + " attempts, locked_until, type, topic, partition_key, occurred_at";
    private const string Identity = "tenant_id = @tenant AND subscription_id = @subscription AND event_id = @event";
    private const string Owned = Identity + " AND locked_by = @token AND " + Incomplete;
    private const string Fence = Owned + " AND locked_until > clock_timestamp()";

    private readonly PrivilegedDataSource _source;
    private readonly OutboxEventRegistry _registry;
    private readonly DeliveryDispatchOptions _options;
    private readonly IGuidFactory _ids;
    private readonly IClock _clock;
    private readonly IServiceScopeFactory _scopes;
    private readonly object _connectionGate = new();
    // The main SQL owner and its uncertain-COMMIT reconciliation are the only
    // simultaneous source users. Slots are retained through actual disposal.
    private readonly OwnedConnection?[] _connections = new OwnedConnection?[2];
    private bool _cleanupUnconfirmed;

    /// <summary>Whether actual connection disposal failed; reading opens nothing.</summary>
    public bool HasUnconfirmedCleanup
    {
        get { lock (_connectionGate) return _cleanupUnconfirmed; }
    }

    public DeliveryDispatchStore(PrivilegedDataSource source, OutboxEventRegistry registry,
        DeliveryDispatchOptions options, IGuidFactory ids, IClock clock, IServiceScopeFactory scopes)
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

    public Task<DeliveryClaim?> TryClaimAsync(IReadOnlyCollection<string> blockedPartitions,
        CancellationToken cancellationToken = default) =>
        AcquireAsync(DeliveryClaimKind.Normal, Normal, blockedPartitions, cancellationToken);

    public Task<DeliveryClaim?> TryAcquireRecoveryAsync(IReadOnlyCollection<string> blockedPartitions,
        CancellationToken cancellationToken = default) =>
        AcquireAsync(DeliveryClaimKind.Recovery, Recovery, blockedPartitions, cancellationToken);

    public Task<DeliveryClaim?> TryClaimTerminalAsync(IReadOnlyCollection<string> blockedPartitions,
        CancellationToken cancellationToken = default) =>
        AcquireAsync(DeliveryClaimKind.Terminal, Terminal, blockedPartitions, cancellationToken);

    private async Task<DeliveryClaim?> AcquireAsync(DeliveryClaimKind kind, string eligibility,
        IReadOnlyCollection<string> blocked, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blocked);
        using var deadline = Deadline(cancellationToken);
        await using var ownedConnection = await OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        var connection = ownedConnection.Connection;
        await using var transaction = await connection.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            WITH candidate AS (
                SELECT tenant_id, subscription_id, event_id FROM event_deliveries WHERE {eligibility}{Admitted}
                ORDER BY available_after, occurred_at, event_id, tenant_id, subscription_id
                FOR UPDATE SKIP LOCKED LIMIT 1
            )
            UPDATE event_deliveries SET locked_by = @token,
                locked_until = clock_timestamp() + @lease,
                attempts = attempts + @increment
            WHERE (tenant_id, subscription_id, event_id) =
                (SELECT tenant_id, subscription_id, event_id FROM candidate)
            RETURNING {ClaimColumns}
            """, connection, transaction);
        Configure(command);
        AddAdmission(command, blocked);
        command.Parameters.AddWithValue("token", _ids.NewUuidV4().ToString("D"));
        command.Parameters.AddWithValue("lease", _options.LeaseDuration);
        command.Parameters.AddWithValue("increment", kind == DeliveryClaimKind.Normal ? 1 : 0);
        DeliveryClaim? result;
        await using (var reader = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false))
        {
            result = await reader.ReadAsync(deadline.Token).ConfigureAwait(false) ? ReadClaim(reader, kind) : null;
        }

        await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
        return result;
    }

    public Task<bool> FailAsync(DeliveryClaim claim, DeliveryFailureReason reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (claim.Kind == DeliveryClaimKind.Terminal)
            throw new ArgumentException("Terminal requests cannot spend an execution retry.", nameof(claim));
        var descriptor = reason switch
        {
            DeliveryFailureReason.ExecutionFailed => "execute:failed:handler",
            DeliveryFailureReason.ExecutionRefused => "execute:refused:catalogue",
            DeliveryFailureReason.ExecutionTimeout => "execute:timeout:deadline",
            DeliveryFailureReason.UnknownOutcome => claim.Kind == DeliveryClaimKind.Recovery
                ? "recovery:unknown:expired" : "execute:unknown:commit",
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

    public Task<bool> RequestTerminalAsync(DeliveryClaim claim, DeliveryTerminalReason reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var code = TerminalCode(reason);
        return UpdateAsync(claim, "terminal_reason = @reason, available_after = clock_timestamp(),"
            + " last_error = @descriptor, locked_by = NULL, locked_until = NULL",
            " AND terminal_reason IS NULL", "terminal:" + code + ":closed", cancellationToken, code);
    }

    private async Task<bool> UpdateAsync(DeliveryClaim claim, string assignments, string extra,
        string? descriptor, CancellationToken cancellationToken, string? reason = null)
    {
        using var deadline = Deadline(cancellationToken);
        await using var ownedConnection = await OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        var connection = ownedConnection.Connection;
        await using var transaction = await connection.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
        if (!await LockOwnedAsync(connection, transaction, claim, extra, deadline.Token).ConfigureAwait(false)) return false;
        await using var command = new NpgsqlCommand(
            $"UPDATE event_deliveries SET {assignments} WHERE {Fence}{extra}", connection, transaction);
        Configure(command);
        AddFence(command, claim);
        if (descriptor is not null) command.Parameters.AddWithValue("descriptor", descriptor);
        if (reason is not null) command.Parameters.AddWithValue("reason", reason);
        var updated = await command.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false) == 1;
        await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
        return updated;
    }

    private async Task<bool> LockOwnedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        DeliveryClaim claim, string extra, CancellationToken cancellationToken)
    {
        // A lock wait on an unchanged tuple does not re-evaluate an earlier clock
        // predicate. Acquire without time first; the following UPDATE reads fresh time.
        await using var command = new NpgsqlCommand(
            $"SELECT TRUE FROM event_deliveries WHERE {Owned}{extra} FOR UPDATE", connection, transaction);
        Configure(command);
        AddFence(command, claim);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    /// <summary>Fences, mints the actual returned-row witness, inserts MUST audit, then commits both.</summary>
    public async Task<bool> TryTerminalizeAsync(DeliveryClaim claim, string validatedAttemptTraceparent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (claim.Kind != DeliveryClaimKind.Terminal)
            throw new ArgumentException("Terminalization requires its own acquisition.", nameof(claim));
        using var deadline = Deadline(cancellationToken);
        await using var ownedConnection = await OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        var connection = ownedConnection.Connection;
        await using var transaction = await connection.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
        if (!await LockOwnedAsync(connection, transaction, claim, " AND terminal_reason IS NOT NULL",
                deadline.Token).ConfigureAwait(false)) return false;
        await using var command = new NpgsqlCommand($"""
            UPDATE event_deliveries SET dead_lettered_at = clock_timestamp(),
                locked_by = NULL, locked_until = NULL
            WHERE {Fence} AND terminal_reason IS NOT NULL
            RETURNING event_id, tenant_id, organization_id, terminal_reason, attempts,
                old.locked_by AS acquisition_token, subscription_id
            """, connection, transaction);
        Configure(command);
        AddFence(command, claim);
        EventTerminalTransition witness;
        await using (var reader = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(deadline.Token).ConfigureAwait(false)) return false;
            witness = FromSubscriberUpdate(connection, transaction, reader, validatedAttemptTraceparent);
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
            Operation = "platform.delivery.dead_letter",
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
            ownedConnection.RecordCommitFailure(exception);
            // COMMIT may have reached PostgreSQL. Reconcile on a fresh bounded connection;
            // a durable terminal row already includes its audit and must never be inserted again.
            try
            {
                if (await IsDurablyTerminalAsync(claim).ConfigureAwait(false))
                {
                    ownedConnection.ConfirmCommitOutcome();
                    return true;
                }
            }
            catch (Exception reconciliationFailure)
            {
                var failure = new AggregateException("The delivery terminal commit outcome remains unknown.",
                    exception, reconciliationFailure);
                ownedConnection.RecordCommitFailure(failure);
                throw failure;
            }
            throw;
        }
    }

    private async Task<bool> IsDurablyTerminalAsync(DeliveryClaim claim)
    {
        using var deadline = Deadline(CancellationToken.None);
        await using var ownedConnection = await OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        var connection = ownedConnection.Connection;
        await using var command = new NpgsqlCommand(
            $"SELECT EXISTS (SELECT 1 FROM event_deliveries WHERE {Identity} AND dead_lettered_at IS NOT NULL)", connection);
        Configure(command);
        AddIdentity(command, claim.TenantId, claim.SubscriptionId, claim.EventId);
        return (bool)(await command.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false))!;
    }

    public async Task<DeliveryQueueSnapshot> ReadSnapshotAsync(IReadOnlyCollection<string> blockedPartitions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blockedPartitions);
        using var deadline = Deadline(cancellationToken);
        await using var ownedConnection = await OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        var connection = ownedConnection.Connection;
        await using var command = new NpgsqlCommand($"""
            SELECT count(*), count(*) FILTER (WHERE {Held}),
                COALESCE(GREATEST(0, extract(epoch FROM clock_timestamp() - min(occurred_at)
                    FILTER (WHERE {Normal}{Admitted}))), 0)::double precision
            FROM event_deliveries WHERE {Incomplete}
            """, connection);
        Configure(command);
        AddAdmission(command, blockedPartitions);
        await using var reader = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
        await reader.ReadAsync(deadline.Token).ConfigureAwait(false);
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetDouble(2));
    }

    /// <summary>Resets exactly one completed terminal Active ordinary subscription; inbox identity is never reset.</summary>
    public async Task<bool> ReplayAsync(Guid tenant, string subscription, Guid eventId,
        CancellationToken cancellationToken = default)
    {
        if (tenant == Guid.Empty || tenant == TenantId.PlatformSentinel.Value || eventId == Guid.Empty
            || !OutboxEventRegistry.ValidSubscriptionId(subscription)) return false;
        using var deadline = Deadline(cancellationToken);
        await using var ownedConnection = await OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        var connection = ownedConnection.Connection;
        await using var transaction = await connection.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
        const string replayable = " AND processed_at IS NULL AND dead_lettered_at IS NOT NULL"
            + " AND terminal_reason IS NOT NULL AND locked_by IS NULL AND locked_until IS NULL";
        await using var rowLock = new NpgsqlCommand(
            $"SELECT type FROM event_deliveries WHERE {Identity}{replayable} FOR UPDATE", connection, transaction);
        Configure(rowLock);
        AddIdentity(rowLock, tenant, subscription, eventId);
        if (await rowLock.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false) is not string type
            || !_registry.IsActiveOrdinarySubscription(type, subscription)) return false;
        await using var reset = new NpgsqlCommand($"""
            UPDATE event_deliveries SET terminal_reason = NULL, dead_lettered_at = NULL,
                attempts = 0, last_error = NULL, locked_by = NULL, locked_until = NULL,
                available_after = clock_timestamp(), replay_count = replay_count + 1,
                replayed_at = clock_timestamp()
            WHERE {Identity}{replayable} AND type = @type
            """, connection, transaction);
        Configure(reset);
        AddIdentity(reset, tenant, subscription, eventId);
        reset.Parameters.AddWithValue("type", type);
        var updated = await reset.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false) == 1;
        await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
        return updated;
    }

    private async ValueTask<OwnedConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        OwnedConnection owned;
        ValueTask<NpgsqlConnection> opening;
        lock (_connectionGate)
        {
            if (_cleanupUnconfirmed)
                throw new InvalidOperationException("The delivery dispatcher connection cleanup remains unconfirmed.");
            var slot = _connections[0] is null ? 0 : _connections[1] is null ? 1 : -1;
            if (slot < 0)
                throw new InvalidOperationException("The delivery dispatcher SQL ownership bound was exceeded.");
            owned = new OwnedConnection(this, slot);
            _connections[slot] = owned;
            try
            {
                // Reserving the concrete owner and issuing its open are serialized
                // with the cleanup seal. No later acquisition crosses that seal.
                opening = _source.OpenConnectionAsync(cancellationToken);
            }
            catch
            {
                _connections[slot] = null;
                throw;
            }
        }

        try
        {
            owned.Attach(await opening.ConfigureAwait(false));
            return owned;
        }
        catch
        {
            // Npgsql owns failed-open unwind before returning a caller connection.
            ReleaseConnection(owned);
            throw;
        }
    }

    private void ReleaseConnection(OwnedConnection owned)
    {
        lock (_connectionGate)
        {
            if (ReferenceEquals(_connections[owned.Slot], owned))
                _connections[owned.Slot] = null;
        }
    }

    private void RetainConnection(OwnedConnection owned)
    {
        lock (_connectionGate)
        {
            _cleanupUnconfirmed = true;
            // Its pre-reserved slot keeps the exact connection and disposal task.
            // A completed fault never grants another SQL owner or a disposal retry.
            _connections[owned.Slot] = owned;
        }
    }

    private sealed class OwnedConnection(DeliveryDispatchStore owner, int slot) : IAsyncDisposable
    {
        private readonly object _disposeGate = new();
        private NpgsqlConnection? _connection;
        private Task? _cleanup;
        private Exception? _cleanupFailure;
        private Exception? _commitFailure;

        internal int Slot => slot;
        internal NpgsqlConnection Connection => _connection
            ?? throw new InvalidOperationException("The dispatcher connection has not been acquired.");

        internal void Attach(NpgsqlConnection connection) => _connection = connection;
        internal void RecordCommitFailure(Exception exception) => _commitFailure = exception;
        internal void ConfirmCommitOutcome() => _commitFailure = null;

        public ValueTask DisposeAsync()
        {
            lock (_disposeGate) return new(_cleanup ??= DisposeCoreAsync());
        }

        private async Task DisposeCoreAsync()
        {
            try
            {
                await Connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _cleanupFailure = exception;
                owner.RetainConnection(this);
                if (exception.GetType() == typeof(OutOfMemoryException)
                    || exception.GetType() == typeof(StackOverflowException)) throw;
                var failure = new InvalidOperationException(
                    "The delivery dispatcher connection cleanup remains unconfirmed.");
                if (_commitFailure is { } commitFailure)
                    throw new AggregateException("The delivery terminal commit outcome remains unknown.", commitFailure, failure);
                throw failure;
            }

            owner.ReleaseConnection(this);
        }
    }

    private static DeliveryClaim ReadClaim(DbDataReader row, DeliveryClaimKind kind) =>
        new(row.GetGuid(0), row.GetString(1), row.GetGuid(2), row.IsDBNull(3) ? null : row.GetGuid(3),
            row.GetString(4), row.GetInt32(5), row.GetFieldValue<DateTimeOffset>(6), kind,
            row.GetString(7), row.GetString(8), row.GetString(9), row.GetFieldValue<DateTimeOffset>(10));

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
        command.Parameters.AddWithValue("held_types", NpgsqlDbType.Array | NpgsqlDbType.Text,
            _registry.HeldDeliverySubscriptions.Select(pair => pair.Type).ToArray());
        command.Parameters.AddWithValue("held_subscriptions", NpgsqlDbType.Array | NpgsqlDbType.Text,
            _registry.HeldDeliverySubscriptions.Select(pair => pair.SubscriptionId).ToArray());
        command.Parameters.AddWithValue("blocked", NpgsqlDbType.Array | NpgsqlDbType.Text, blocked.ToArray());
    }

    private static void AddFence(NpgsqlCommand command, DeliveryClaim claim)
    {
        AddIdentity(command, claim.TenantId, claim.SubscriptionId, claim.EventId);
        command.Parameters.AddWithValue("token", claim.Token);
    }

    private static void AddIdentity(NpgsqlCommand command, Guid tenant, string subscription, Guid eventId)
    {
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("subscription", subscription);
        command.Parameters.AddWithValue("event", eventId);
    }

    private static string TerminalCode(DeliveryTerminalReason reason) => reason switch
    {
        DeliveryTerminalReason.UnknownType => "unknown_type",
        DeliveryTerminalReason.RetiredType => "retired_type",
        DeliveryTerminalReason.InvalidEnvelope => "invalid_envelope",
        DeliveryTerminalReason.InvalidSubscription => "invalid_subscription",
        DeliveryTerminalReason.RetryExhausted => "retry_exhausted",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}
