using System.Buffers;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Audit;

/// <summary>The immutable result of one fenced terminal UPDATE in its live transaction.</summary>
/// <remarks>
/// Construction is private; the protected minting seam is confined to the producer
/// transition store by the production architecture census. It grants no friend-assembly
/// access to other SharedKernel authority. Subscriber minting belongs to P02b-3.
/// </remarks>
public sealed class EventTerminalTransition
{
    private readonly DbConnection _connection;
    private readonly DbTransaction _transaction;

    private EventTerminalTransition(
        DbConnection connection,
        DbTransaction transaction,
        DbDataReader returnedRow,
        string validatedAttemptTraceparent)
    {
        EnsureLiveTransaction(connection, transaction);
        if (!TryCanonicalTraceparent(validatedAttemptTraceparent, out var attempt)
            || !string.Equals(attempt, validatedAttemptTraceparent, StringComparison.Ordinal))
        {
            throw new ArgumentException("The terminal attempt requires a canonical W3C traceparent.");
        }

        if (returnedRow.FieldCount != 9)
        {
            throw new ArgumentException("The terminal UPDATE must return its complete bound row.");
        }

        _connection = connection;
        _transaction = transaction;
        EventId = returnedRow.GetGuid(0);
        TenantId = returnedRow.GetGuid(1);
        OrganizationId = returnedRow.IsDBNull(2) ? null : returnedRow.GetGuid(2);
        Reason = returnedRow.GetString(3);
        Attempts = returnedRow.GetInt32(4);
        AcquisitionToken = returnedRow.GetString(5);

        if (EventId == Guid.Empty || TenantId == Guid.Empty
            || TenantId == Identifiers.TenantId.PlatformSentinel.Value
            || OrganizationId == Guid.Empty || Attempts < 0
            || Reason is not ("unknown_type" or "retired_type" or "invalid_envelope" or "retry_exhausted")
            || !Guid.TryParseExact(AcquisitionToken, "D", out var token)
            || token == Guid.Empty
            || !string.Equals(AcquisitionToken, token.ToString("D"), StringComparison.Ordinal))
        {
            throw new ArgumentException("The terminal UPDATE returned invalid transition identity.");
        }

        Guid? causationId = returnedRow.IsDBNull(7) ? null : returnedRow.GetGuid(7);
        Guid? causalActorUserId = returnedRow.IsDBNull(8) ? null : returnedRow.GetGuid(8);
        CorrelationId = TryCanonicalTraceparent(
            returnedRow.IsDBNull(6) ? null : returnedRow.GetString(6), out var original)
            && causationId != Guid.Empty && causalActorUserId != Guid.Empty
            ? original : attempt;
        Metadata = ComposeMetadata(causationId, causalActorUserId);
    }

    public Guid EventId { get; }
    public Guid TenantId { get; }
    public Guid? OrganizationId { get; }
    public string Reason { get; }
    public int Attempts { get; }
    public string AcquisitionToken { get; }
    public string CorrelationId { get; }
    public string Metadata { get; }
    public string Kind { get; } = "producer";
    public string? SubscriptionId { get; }

    /// <summary>Refuses a draft or transaction that does not describe this exact transition.</summary>
    public void EnsureMatches(
        AuditEntryDraft entry, DbConnection connection, DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        EnsureLiveTransaction(connection, transaction);

        if (!ReferenceEquals(_connection, connection) || !ReferenceEquals(_transaction, transaction)
            || !entry.Id.IsInitialized() || entry.Id.Value == Guid.Empty
            || !entry.TenantId.IsInitialized() || entry.TenantId.Value != TenantId
            || (entry.OrganizationId is { } organization && !organization.IsInitialized())
            || entry.OrganizationId?.Value != OrganizationId
            || !string.Equals(entry.Metadata, Metadata, StringComparison.Ordinal)
            || !string.Equals(entry.CorrelationId, CorrelationId, StringComparison.Ordinal)
            || entry.ActorUserId is not { } actor || !actor.IsInitialized() || actor != UserId.SystemActor
            || entry.ActorEmail is not null
            || entry.Outcome != AuditOutcome.Success || entry.ErrorKey is not null
            || entry.Reason is not null || entry.EntityType is not null || entry.EntityId is not null
            || entry.BeforeState is not null || entry.AfterState is not null || entry.Changes is not null
            || entry.IpAddress is not null || entry.UserAgent is not null)
        {
            throw new ArgumentException("The terminal audit draft does not match its live transition.", nameof(entry));
        }
    }

    /// <summary>The only construction capability; its production subtype and call site are closed.</summary>
#pragma warning disable CA1034 // Nesting permits private witness construction without SharedKernel friend assemblies.
    public abstract class MintingSource
#pragma warning restore CA1034
    {
        protected MintingSource() { }

        /// <summary>Reads the actual nine-column result of the successful fenced producer UPDATE.</summary>
        protected static EventTerminalTransition FromProducerUpdate(
            DbConnection connection,
            DbTransaction transaction,
            DbDataReader returnedRow,
            string validatedAttemptTraceparent)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(transaction);
            ArgumentNullException.ThrowIfNull(returnedRow);
            return new EventTerminalTransition(connection, transaction, returnedRow, validatedAttemptTraceparent);
        }
    }

    private static void EnsureLiveTransaction(DbConnection connection, DbTransaction transaction)
    {
        if (connection.State != ConnectionState.Open || !ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException("A terminal transition requires the caller's live transaction.");
        }
    }

    private string ComposeMetadata(Guid? causationId, Guid? causalActorUserId)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("event_id", EventId);
            writer.WriteString("reason", Reason);
            writer.WriteNumber("attempts", Attempts);
            if (causationId is { } causation && causation != Guid.Empty)
                writer.WriteString("causation_id", causation);
            if (causalActorUserId is { } actor && actor != Guid.Empty)
                writer.WriteString("causal_actor_user_id", actor);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static bool TryCanonicalTraceparent(string? value, out string canonical)
    {
        if (ActivityContext.TryParse(value, traceState: null, out var context))
        {
            canonical = string.Concat("00-", context.TraceId.ToHexString(), "-", context.SpanId.ToHexString(),
                "-", ((byte)context.TraceFlags).ToString("x2", CultureInfo.InvariantCulture));
            return true;
        }

        canonical = string.Empty;
        return false;
    }
}
