using System.Buffers;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Platform;

namespace LearnStack.SharedKernel.Audit;

/// <summary>
/// The immutable result of one operator's fenced reset of a dead-lettered row, in its live transaction
/// (ADR-0103 § 5).
/// </summary>
/// <remarks>
/// <para>
/// Construction is private and its protected minting seam is confined to the two dispatcher stores by the
/// production architecture census, as the terminal witness's is. It is a separate witness because the terminal one
/// demands the system actor, a null reason and fixed metadata, so it cannot carry an operator: this one binds the
/// row to the admitted operation, the closed reason, the admitted operator's attribution and the fence the
/// returned row showed before and after. It names the real event tenant and carries no ticket, because a tenant can
/// read the row.
/// </para>
/// </remarks>
public sealed class OperatorRecoveryTransition
{
    private static readonly Regex SubscriptionGrammar = new(
        @"\A[a-z][a-z0-9]*\.[a-z][a-z0-9-]*\.v[1-9][0-9]*\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private readonly DbConnection _connection;
    private readonly DbTransaction _transaction;
    private readonly PlatformAdmission _admission;

    private OperatorRecoveryTransition(
        DbConnection connection, DbTransaction transaction, DbDataReader returnedRow, PlatformAdmission admission, bool replay)
    {
        if (connection.State != ConnectionState.Open || !ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException("A recovery transition requires the caller's live transaction.");
        }

        var expected = replay ? PlatformOperations.DeliveryReplay : PlatformOperations.DeliveryReset;
        if (!ReferenceEquals(admission.Operation, expected) || string.IsNullOrEmpty(admission.Operator.CorrelationId))
        {
            throw new ArgumentException("The admission is not for this recovery operation.");
        }

        if (returnedRow.FieldCount != (replay ? 8 : 7))
        {
            throw new ArgumentException("The recovery UPDATE must return its complete bound row.");
        }

        _connection = connection;
        _transaction = transaction;
        _admission = admission;
        Kind = replay ? "replay" : "reset";
        EventId = returnedRow.GetGuid(0);
        TenantId = returnedRow.GetGuid(1);
        OrganizationId = returnedRow.IsDBNull(2) ? null : returnedRow.GetGuid(2);
        var next = 3;
        SubscriptionId = replay ? returnedRow.GetString(next++) : null;
        BeforeCount = returnedRow.GetInt32(next++);
        BeforeDeadLetteredAt = returnedRow.GetFieldValue<DateTimeOffset>(next++);
        BeforeReason = returnedRow.GetString(next++);
        AfterCount = returnedRow.GetInt32(next);

        if (EventId == Guid.Empty || TenantId == Guid.Empty
            || TenantId == Identifiers.TenantId.PlatformSentinel.Value || OrganizationId == Guid.Empty
            || BeforeCount < 0 || AfterCount < 0
            || (replay
                ? SubscriptionId is null || !SubscriptionGrammar.IsMatch(SubscriptionId)
                    || BeforeReason is not ("unknown_type" or "retired_type" or "retry_exhausted" or "invalid_subscription")
                    || AfterCount != BeforeCount + 1
                : BeforeReason != "retry_exhausted" || AfterCount != 0))
        {
            throw new ArgumentException("The recovery UPDATE returned invalid transition identity.");
        }

        CorrelationId = admission.Operator.CorrelationId;
        Metadata = ComposeMetadata();
    }

    /// <summary>Either <c>replay</c> (a subscriber delivery) or <c>reset</c> (a producer row).</summary>
    public string Kind { get; }

    /// <summary>The event identity: the outbox row id, which a delivery keeps.</summary>
    public Guid EventId { get; }

    /// <summary>The real tenant the stored row names.</summary>
    public Guid TenantId { get; }

    /// <summary>The organization the stored row names, when it names one.</summary>
    public Guid? OrganizationId { get; }

    /// <summary>The subscription of a replayed delivery, and null for a reset.</summary>
    public string? SubscriptionId { get; }

    /// <summary>The fence's count before: <c>replay_count</c> of a delivery or <c>attempts</c> of a producer row.</summary>
    public int BeforeCount { get; }

    /// <summary>The fence's timestamp before.</summary>
    public DateTimeOffset BeforeDeadLetteredAt { get; }

    /// <summary>The terminal reason the row carried before.</summary>
    public string BeforeReason { get; }

    /// <summary>The fence's count after.</summary>
    public int AfterCount { get; }

    /// <summary>The admitted correlation id the row carries.</summary>
    public string CorrelationId { get; }

    /// <summary>The exact <c>metadata</c> text the row must carry.</summary>
    public string Metadata { get; }

    /// <summary>Refuses a draft or transaction that does not describe this exact transition.</summary>
    public void EnsureMatches(AuditEntryDraft entry, DbConnection connection, DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (connection.State != ConnectionState.Open || !ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException("A recovery transition requires the caller's live transaction.", nameof(entry));
        }

        if (!ReferenceEquals(_connection, connection) || !ReferenceEquals(_transaction, transaction)
            || !entry.Id.IsInitialized() || entry.Id.Value == Guid.Empty
            || !entry.TenantId.IsInitialized() || entry.TenantId.Value != TenantId
            || (entry.OrganizationId is { } organization && !organization.IsInitialized())
            || entry.OrganizationId?.Value != OrganizationId
            || !string.Equals(entry.Operation, _admission.Operation.AuditSlug, StringComparison.Ordinal)
            || !string.Equals(entry.Reason, _admission.Operation.ReasonSlug, StringComparison.Ordinal)
            || !string.Equals(entry.Metadata, Metadata, StringComparison.Ordinal)
            || !string.Equals(entry.CorrelationId, CorrelationId, StringComparison.Ordinal)
            || entry.ActorUserId is not null || entry.ActorEmail is not null
            || entry.Outcome != AuditOutcome.Success || entry.ErrorKey is not null
            || entry.OperationClass != OperationClass.Must || entry.OperationType != OperationType.SecurityEvent
            || entry.EntityType is not null || entry.EntityId is not null
            || entry.BeforeState is not null || entry.AfterState is not null || entry.Changes is not null
            || entry.IpAddress is not null || entry.UserAgent is not null)
        {
            throw new ArgumentException("The recovery audit draft does not match its live transition.", nameof(entry));
        }
    }

    /// <summary>The only construction capability; its production subtypes and call sites are closed.</summary>
#pragma warning disable CA1034 // Nesting permits private witness construction without SharedKernel friend assemblies.
    public abstract class MintingSource
#pragma warning restore CA1034
    {
        /// <summary>Confines subtyping to the dispatcher stores' own nested classes.</summary>
        protected MintingSource() { }

        /// <summary>Reads the actual eight-column result of the successful fenced subscriber reset.</summary>
        protected static OperatorRecoveryTransition FromReplayUpdate(
            DbConnection connection, DbTransaction transaction, DbDataReader returnedRow, PlatformAdmission admission)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(transaction);
            ArgumentNullException.ThrowIfNull(returnedRow);
            ArgumentNullException.ThrowIfNull(admission);
            return new OperatorRecoveryTransition(connection, transaction, returnedRow, admission, replay: true);
        }

        /// <summary>Reads the actual seven-column result of the successful fenced producer reset.</summary>
        protected static OperatorRecoveryTransition FromResetUpdate(
            DbConnection connection, DbTransaction transaction, DbDataReader returnedRow, PlatformAdmission admission)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(transaction);
            ArgumentNullException.ThrowIfNull(returnedRow);
            ArgumentNullException.ThrowIfNull(admission);
            return new OperatorRecoveryTransition(connection, transaction, returnedRow, admission, replay: false);
        }
    }

    private string ComposeMetadata()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("operator");
            writer.WriteRawValue(PlatformOperatorAttribution.From(_admission).ToOperatorJson());
            writer.WriteString("event_id", EventId);
            if (SubscriptionId is not null)
            {
                writer.WriteString("subscription_id", SubscriptionId);
            }

            var count = Kind == "replay" ? "replay_count" : "attempts";
            writer.WriteStartObject("fence");
            writer.WriteStartObject("before");
            writer.WriteNumber(count, BeforeCount);
            writer.WriteString("dead_lettered_at", BeforeDeadLetteredAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("terminal_reason", BeforeReason);
            writer.WriteEndObject();
            writer.WriteStartObject("after");
            writer.WriteNumber(count, AfterCount);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
