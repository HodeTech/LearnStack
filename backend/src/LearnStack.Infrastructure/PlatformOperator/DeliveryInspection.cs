using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LearnStack.Infrastructure.Messaging;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Infrastructure.PlatformOperator;

/// <summary>
/// The metadata-only operator inspection of dead-lettered and pending-terminal outbox rows and deliveries and of
/// quarantined jobs (ADR-0103 § 1).
/// </summary>
/// <remarks>
/// <para>
/// <b>The database bounds the projection.</b> Each page runs in one entered platform scope, on the platform role,
/// whose column grants exclude every envelope, parameter, plan, outcome and causal column: a statement that named
/// one would be refused by PostgreSQL, so the projection does not rest on this file's discipline. The scope's entry
/// row, naming the inspection operation, is the audit; the scope reads and changes nothing, so it writes no result
/// row and ends by disposal.
/// </para>
/// <para>
/// <b>Cursors are keyset positions.</b> A cursor names the resource, the state and the last row's ordering key, and
/// carries no authority: it can move a page within what the operator may already read.
/// </para>
/// </remarks>
public sealed class DeliveryInspection(IPlatformAdminScope scope, OutboxEventRegistry registry) : IDeliveryInspection
{
    private const int MaximumLimit = CursorPagination.MaxLimit;

    // The dispatcher writes a closed vocabulary of lowercase descriptors (for example publish:failed:provider). The
    // database bounds only the byte length of last_error, so the projection shows a value of that shape and nothing
    // else: free text could carry markup, and a page is rendered by a client the operator trusts.
    private static readonly Regex DescriptorShape = new(
        @"\A[a-z][a-z0-9_.:-]{0,63}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private const string OutboxColumns = "id, tenant_id, organization_id, occurred_at, type, attempts, last_error, "
        + "available_after, locked_until, terminal_reason, dead_lettered_at, processed_at";

    private const string DeliveryColumns = "tenant_id, subscription_id, event_id, organization_id, occurred_at, type, attempts, "
        + "last_error, available_after, locked_until, terminal_reason, dead_lettered_at, replay_count, replayed_at, processed_at";

    private static (string Where, string Order) Shape(DeliveryInspectionState state) =>
        state == DeliveryInspectionState.DeadLettered
            ? ("dead_lettered_at IS NOT NULL AND processed_at IS NULL", "dead_lettered_at")
            : ("processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NOT NULL", "available_after");

    /// <summary>
    /// The statement of an outbox page. It is exposed so that a test can ask PostgreSQL for the plan of the text the
    /// inspection really runs: the dead-lettered scan must use <c>ix_outbox_messages_dead_lettered</c> in key order.
    /// </summary>
    internal static string OutboxSql(DeliveryInspectionState state, bool afterCursor)
    {
        var (where, order) = Shape(state);
        return $"SELECT {OutboxColumns} FROM outbox_messages WHERE {where}"
            + (afterCursor ? $" AND ({order}, id) > (@after, @key)" : string.Empty)
            + $" ORDER BY {order}, id LIMIT @size";
    }

    /// <summary>The statement of a delivery page, exposed for the same reason as <see cref="OutboxSql"/>.</summary>
    internal static string DeliverySql(DeliveryInspectionState state, bool afterCursor)
    {
        var (where, order) = Shape(state);
        return $"SELECT {DeliveryColumns} FROM event_deliveries WHERE {where}"
            + (afterCursor ? $" AND ({order}, tenant_id, subscription_id, event_id) > (@after, @tenant, @subscription, @event)" : string.Empty)
            + $" ORDER BY {order}, tenant_id, subscription_id, event_id LIMIT @size";
    }

    /// <inheritdoc />
    public Task<InspectionPage<OutboxMessageInspection>?> InspectOutboxAsync(
        PlatformAdmission admission, DeliveryInspectionState state, string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        var byDeadLetter = state == DeliveryInspectionState.DeadLettered;
        return PageAsync(
            admission, "outbox." + state, cursor, limit, cancellationToken,
            (after, key, size) => new Statement(
                OutboxSql(state, after is not null),
                [.. KeyParameters(after, key, Guid.TryParse(key, out var id) ? id : Guid.Empty, size)]),
            reader => MapOutbox(reader),
            row => (byDeadLetter ? row.DeadLetteredAt!.Value : row.AvailableAfter).UtcTicks,
            row => row.EventId.ToString("D"),
            key => Guid.TryParse(key, out _));
    }

    /// <inheritdoc />
    public Task<InspectionPage<EventDeliveryInspection>?> InspectDeliveriesAsync(
        PlatformAdmission admission, DeliveryInspectionState state, string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        var byDeadLetter = state == DeliveryInspectionState.DeadLettered;
        return PageAsync(
            admission, "delivery." + state, cursor, limit, cancellationToken,
            (after, key, size) =>
            {
                var parts = key?.Split('|');
                var parameters = KeyParameters(after, null, Guid.Empty, size);
                if (parts is { Length: 3 } && Guid.TryParse(parts[0], out var tenant) && Guid.TryParse(parts[2], out var eventId))
                {
                    parameters.Add(new PlatformSqlParameter("tenant", tenant));
                    parameters.Add(new PlatformSqlParameter("subscription", parts[1]));
                    parameters.Add(new PlatformSqlParameter("event", eventId));
                }

                return new Statement(DeliverySql(state, after is not null), parameters);
            },
            reader => MapDelivery(reader),
            row => (byDeadLetter ? row.DeadLetteredAt!.Value : row.AvailableAfter).UtcTicks,
            row => $"{row.TenantId:D}|{row.SubscriptionId}|{row.EventId:D}",
            key => key.Split('|') is { Length: 3 } parts && Guid.TryParse(parts[0], out _) && Guid.TryParse(parts[2], out _)
                && OutboxEventRegistry.ValidSubscriptionId(parts[1]));
    }

    /// <inheritdoc />
    public Task<InspectionPage<QuarantinedJobInspection>?> InspectJobsAsync(
        PlatformAdmission admission, string? cursor, int limit, CancellationToken cancellationToken = default) =>
        PageAsync(
            admission, "job.quarantined", cursor, limit, cancellationToken,
            (after, key, size) => new Statement(
                """
                SELECT j.id, j.tenant_id, j.organization_id, j.definition_key, j.profile, j.queue, j.capacity_group,
                    j.created_at, j.phase, j.normal_attempts, j.finalize_attempts, j.quarantine_reason, j.last_error,
                    j.ownership_until, j.probe_after, j.unsuccessful_probes, j.finalization_status,
                    j.business_finalization_incomplete,
                    (SELECT count(*) FROM background_job_reservations r
                        WHERE r.tenant_id = j.tenant_id AND r.job_id = j.id AND r.released_at IS NULL),
                    (SELECT count(*) FROM background_job_reservations r
                        WHERE r.tenant_id = j.tenant_id AND r.capacity_group = j.capacity_group AND r.released_at IS NULL)
                FROM background_jobs j WHERE j.phase = 'quarantined'
                """
                + (after is null ? string.Empty : " AND (j.created_at, j.id) > (@after, @key)")
                + " ORDER BY j.created_at, j.id LIMIT @size",
                [.. KeyParameters(after, key, Guid.TryParse(key, out var id) ? id : Guid.Empty, size)]),
            reader => MapJob(reader),
            row => row.CreatedAt.UtcTicks,
            row => row.JobId.ToString("D"),
            key => Guid.TryParse(key, out _));

    private async Task<InspectionPage<T>?> PageAsync<T>(
        PlatformAdmission admission,
        string scopeKey,
        string? cursor,
        int limit,
        CancellationToken cancellationToken,
        Func<DateTimeOffset?, string?, int, Statement> statement,
        Func<DbDataReader, T> map,
        Func<T, long> ticksOf,
        Func<T, string> keyOf,
        Func<string, bool> keyIsValid)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (!ReferenceEquals(admission.Operation, PlatformOperations.DeliveryInspect))
        {
            throw new ArgumentException("The admission is not for a delivery inspection.", nameof(admission));
        }

        var size = Math.Clamp(limit, 1, MaximumLimit);
        DateTimeOffset? after = null;
        string? key = null;
        if (cursor is not null)
        {
            if (!TryDecode(cursor, scopeKey, out var ticks, out key) || !keyIsValid(key))
            {
                return null;
            }

            after = new DateTimeOffset(ticks, TimeSpan.Zero);
        }

        IReadOnlyList<T> rows;
        try
        {
            await using var handle = await scope.EnterAsync(admission, cancellationToken).ConfigureAwait(false);
            var built = statement(after, key, size + 1);
            rows = await handle.QueryAsync(built.Sql, built.Parameters, map, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (OperatorRecoveryFaults.IsTransientDatabaseFault(failure, cancellationToken))
        {
            // A read is safe to repeat; the cause is for the route's log, never for the response. A refused
            // credential or a missing grant is not transient and surfaces as the defect it is.
            throw new OperatorRecoveryUnavailableException(cause: failure);
        }

        var items = rows.Take(size).ToList();
        var next = rows.Count > size && items.Count > 0
            ? Encode(scopeKey, ticksOf(items[^1]), keyOf(items[^1]))
            : null;
        return new InspectionPage<T>(items, next);
    }

    private static List<PlatformSqlParameter> KeyParameters(DateTimeOffset? after, string? key, Guid id, int size)
    {
        var parameters = new List<PlatformSqlParameter> { new("size", size) };
        if (after is { } at)
        {
            parameters.Add(new PlatformSqlParameter("after", at));
            if (key is not null)
            {
                parameters.Add(new PlatformSqlParameter("key", id));
            }
        }

        return parameters;
    }

    private OutboxMessageInspection MapOutbox(DbDataReader reader)
    {
        var type = reader.GetString(4);
        var reason = reader.IsDBNull(9) ? null : reader.GetString(9);
        var lockedUntil = reader.IsDBNull(8) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(8);
        var deadLettered = reader.IsDBNull(10) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(10);
        var disposition = registry.Resolve(type);
        return new OutboxMessageInspection(
            reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), disposition.EventTypeLabel,
            reader.GetFieldValue<DateTimeOffset>(3), reader.GetInt32(5), Shown(reader.IsDBNull(6) ? null : reader.GetString(6)),
            reader.GetFieldValue<DateTimeOffset>(7), lockedUntil, reason, deadLettered,
            Actionable: deadLettered is not null && reason == "retry_exhausted" && reader.IsDBNull(11) && lockedUntil is null
                && disposition.Disposition is not (OutboxEventDisposition.Retired or OutboxEventDisposition.Unknown));
    }

    private EventDeliveryInspection MapDelivery(DbDataReader reader)
    {
        var type = reader.GetString(5);
        var subscription = reader.GetString(1);
        var reason = reader.IsDBNull(10) ? null : reader.GetString(10);
        var lockedUntil = reader.IsDBNull(9) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(9);
        var deadLettered = reader.IsDBNull(11) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(11);
        return new EventDeliveryInspection(
            reader.GetGuid(0), subscription, reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetGuid(3),
            registry.Resolve(type).EventTypeLabel, reader.GetFieldValue<DateTimeOffset>(4), reader.GetInt32(6),
            Shown(reader.IsDBNull(7) ? null : reader.GetString(7)), reader.GetFieldValue<DateTimeOffset>(8), lockedUntil, reason,
            deadLettered, reader.GetInt32(12), reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
            Actionable: deadLettered is not null && reader.IsDBNull(14) && lockedUntil is null && reason != "invalid_envelope"
                && registry.IsActiveOrdinarySubscription(type, subscription));
    }

    private static string? Shown(string? error) =>
        error is null ? null : DescriptorShape.IsMatch(error) ? error : "unrecognized";

    private static QuarantinedJobInspection MapJob(DbDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetFieldValue<DateTimeOffset>(7), reader.GetString(8), reader.GetInt32(9), reader.GetInt32(10),
        reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
        reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14), reader.GetInt32(15),
        reader.IsDBNull(16) ? null : reader.GetString(16), reader.GetBoolean(17),
        checked((int)reader.GetInt64(18)), checked((int)reader.GetInt64(19)));

    private static string Encode(string scopeKey, long ticks, string key) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('~', "v1", scopeKey, ticks.ToString(CultureInfo.InvariantCulture), key)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecode(string cursor, string scopeKey, out long ticks, out string key)
    {
        ticks = 0;
        key = string.Empty;
        if (cursor.Length is 0 or > 512)
        {
            return false;
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split('~', 4);
            if (parts.Length != 4 || parts[0] != "v1" || parts[1] != scopeKey
                || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out ticks)
                || ticks < 0 || ticks > DateTimeOffset.MaxValue.Ticks)
            {
                return false;
            }

            key = parts[3];
            return key.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed record Statement(string Sql, IReadOnlyList<PlatformSqlParameter> Parameters);
}
