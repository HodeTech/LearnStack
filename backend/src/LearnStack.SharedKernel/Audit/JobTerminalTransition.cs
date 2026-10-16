using System.Buffers;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Audit;

/// <summary>The actual returned facts of one terminal job UPDATE on its live transaction.</summary>
public sealed class JobTerminalTransition
{
    private readonly DbConnection _connection;
    private readonly DbTransaction _transaction;

    private JobTerminalTransition(DbConnection connection, DbTransaction transaction,
        DbDataReader returnedRow, string validatedAttemptTraceparent)
    {
        EnsureLiveTransaction(connection, transaction);
        if (!ActivityContext.TryParse(validatedAttemptTraceparent, null, out var trace)
            || validatedAttemptTraceparent != string.Concat("00-", trace.TraceId.ToHexString(), "-", trace.SpanId.ToHexString(),
                "-", ((byte)trace.TraceFlags).ToString("x2", CultureInfo.InvariantCulture))
            || returnedRow.FieldCount != 11)
            throw new ArgumentException("A terminal job requires its complete returned row and canonical attempt trace.");

        _connection = connection;
        _transaction = transaction;
        JobId = returnedRow.GetGuid(0);
        TenantId = returnedRow.GetGuid(1);
        OrganizationId = returnedRow.IsDBNull(2) ? null : returnedRow.GetGuid(2);
        DefinitionKey = returnedRow.GetString(3);
        Queue = returnedRow.GetString(4);
        WakeGeneration = returnedRow.GetInt64(5);
        AttemptId = returnedRow.IsDBNull(6) ? null : returnedRow.GetGuid(6);
        NormalAttempts = returnedRow.GetInt32(7);
        Reason = returnedRow.GetString(8);
        DeadLetteredAt = returnedRow.GetFieldValue<DateTimeOffset>(9);
        AcquisitionToken = returnedRow.GetGuid(10);
        CorrelationId = validatedAttemptTraceparent;
        if (JobId.Version != 7 || TenantId == Guid.Empty || TenantId == Identifiers.TenantId.PlatformSentinel.Value
            || OrganizationId == Guid.Empty || AcquisitionToken == Guid.Empty || WakeGeneration < 1
            || (AttemptId is { } attempt && attempt.Version != 7)
            || NormalAttempts is < 0 or > 6 || ((NormalAttempts == 0) != (AttemptId is null))
            || Reason is not ("retired_definition" or "invalid_envelope" or "permanent_failure" or "retry_exhausted")
            || (Reason == "retry_exhausted" && NormalAttempts != 6) || (Reason == "permanent_failure" && NormalAttempts == 0)
            || !Regex.IsMatch(DefinitionKey, @"\A[a-z][a-z0-9]*\.[a-z][a-z0-9_]*\.v[1-9][0-9]*\z",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) || DefinitionKey.Length > 150
            || !Regex.IsMatch(Queue, @"\A[a-z][a-z0-9_]{0,49}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            throw new ArgumentException("The terminal job UPDATE returned invalid bound facts.");
        Metadata = ComposeMetadata();
    }

    public Guid JobId { get; }
    public Guid TenantId { get; }
    public Guid? OrganizationId { get; }
    public string DefinitionKey { get; }
    public string Queue { get; }
    public long WakeGeneration { get; }
    public Guid? AttemptId { get; }
    public int NormalAttempts { get; }
    public string Reason { get; }
    public DateTimeOffset DeadLetteredAt { get; }
    public Guid AcquisitionToken { get; }
    public string CorrelationId { get; }
    public string Metadata { get; }

    /// <summary>Rejects substitution or reuse after the bound transaction finishes.</summary>
    public void EnsureMatches(AuditEntryDraft entry, DbConnection connection, DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(entry);
        EnsureLiveTransaction(connection, transaction);
        if (!ReferenceEquals(_connection, connection) || !ReferenceEquals(_transaction, transaction)
            || !entry.Id.IsInitialized() || entry.Id.Value.Version != 7
            || !entry.TenantId.IsInitialized() || entry.TenantId.Value != TenantId
            || (entry.OrganizationId is { } organization && !organization.IsInitialized())
            || entry.OrganizationId?.Value != OrganizationId || entry.Metadata != Metadata || entry.CorrelationId != CorrelationId
            || entry.ModuleName != "platform" || entry.Operation != "platform.job.dead_letter"
            || entry.OperationClass != OperationClass.Must || entry.OperationType != OperationType.SecurityEvent
            || entry.ActorUserId is not { } actor || !actor.IsInitialized() || actor != UserId.SystemActor
            || entry.ActorEmail is not null || entry.Outcome != AuditOutcome.Success || entry.ErrorKey is not null
            || entry.Reason is not null || entry.EntityType is not null || entry.EntityId is not null
            || entry.BeforeState is not null || entry.AfterState is not null || entry.Changes is not null
            || entry.IpAddress is not null || entry.UserAgent is not null || entry.Timestamp != DeadLetteredAt)
            throw new ArgumentException("The job terminal audit does not match its live returned transition.", nameof(entry));
    }

#pragma warning disable CA1034 // Private construction with one closed production minting capability.
    public abstract class MintingSource
#pragma warning restore CA1034
    {
        protected MintingSource() { }
        protected static JobTerminalTransition FromBypassUpdate(DbConnection connection, DbTransaction transaction,
            DbDataReader returnedRow, string validatedAttemptTraceparent)
        {
            ArgumentNullException.ThrowIfNull(returnedRow);
            return new(connection, transaction, returnedRow, validatedAttemptTraceparent);
        }
    }

    private static void EnsureLiveTransaction(DbConnection connection, DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (connection.State != ConnectionState.Open || !ReferenceEquals(transaction.Connection, connection))
            throw new ArgumentException("A job terminal transition requires its caller's live transaction.");
    }

    private string ComposeMetadata()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("job_id", JobId);
            writer.WriteString("definition", DefinitionKey);
            writer.WriteString("queue", Queue);
            writer.WriteNumber("wake_generation", WakeGeneration);
            if (AttemptId is { } attempt) writer.WriteString("attempt_id", attempt);
            writer.WriteNumber("normal_attempts", NormalAttempts);
            writer.WriteString("reason", Reason);
            writer.WriteString("acquisition_token", AcquisitionToken);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
