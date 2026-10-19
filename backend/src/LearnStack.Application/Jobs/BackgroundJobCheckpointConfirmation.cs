using System.Data.Common;
using System.Diagnostics;

namespace LearnStack.Application.Jobs;

public enum BackgroundJobCheckpointHistoryDisposition { Committed, Absent, OtherDurableBranch, Unresolved }

/// <summary>Exact historical evidence; never a physical COMMIT acknowledgement or a new invocation.</summary>
public sealed class BackgroundJobCheckpointConfirmation
{
    private readonly ConfirmedStartReceipt? _receipt;
    private readonly RecordBackgroundJobArmCommand? _arm;
    private readonly RecordBackgroundJobOutcomeCommand? _outcome;
    private readonly long _observedAt;

    internal BackgroundJobCheckpointConfirmation(ConfirmedStartReceipt? receipt, BackgroundJobCommitGate gate,
        RecordBackgroundJobArmCommand? arm, RecordBackgroundJobOutcomeCommand? outcome,
        BackgroundJobCheckpointHistoryFacts facts, long observedAt)
    {
        _receipt = receipt;
        _arm = arm;
        _outcome = outcome;
        Gate = gate;
        Facts = facts;
        _observedAt = observedAt;
    }

    public BackgroundJobCommitGate Gate { get; }
    public BackgroundJobCheckpointHistoryFacts Facts { get; }
    public bool Matches(ConfirmedStartReceipt receipt, BackgroundJobCommitGate gate) =>
        ReferenceEquals(_receipt, receipt) && ReferenceEquals(Gate, gate);
    public bool Matches(RecordBackgroundJobArmCommand arm) =>
        ReferenceEquals(_arm, arm) && ReferenceEquals(Gate, arm.Gate);
    public bool Matches(RecordBackgroundJobOutcomeCommand outcome) =>
        ReferenceEquals(_outcome, outcome) && ReferenceEquals(Gate, outcome.Gate);

    /// <summary>Historical intent alone cannot renew a stale original invocation.</summary>
    public bool AllowsOriginalExecute(RecordBackgroundJobArmCommand arm) =>
        Matches(arm) && AllowsOriginalExecute(arm.Gate);

    internal bool AllowsOriginalExecute(BackgroundJobCommitGate gate)
    {
        if (_arm is not { } arm || gate.IsClosed || !ReferenceEquals(Gate, gate) || !ReferenceEquals(arm.Gate, gate)) return false;
        var receipt = arm.Receipt;
        if (Facts.CurrentAttemptId != receipt.AttemptId.Value || Facts.AdmissionToken != receipt.Claim.Token
            || Facts.CurrentOwnershipToken != receipt.Claim.Token || Facts.CurrentOwnershipKind != "execution"
            || Facts.CurrentWakeGeneration != receipt.Claim.WakeGeneration || Facts.NormalAttempts != receipt.NormalAttemptOrdinal
            || Facts.CurrentAttemptDeadline != receipt.Deadline || Facts.Phase != "executing"
            || Facts.CurrentOwnershipUntil is not { } until || Facts.Deadline != receipt.Deadline
            || Facts.ProcessedAt is not null || Facts.DeadLetteredAt is not null || Facts.TerminalReason is not null
            || Facts.JobSelectedNormalAttemptId is not null || Facts.FinalizationStatus is not null || Facts.FinalizationReason is not null
            || Facts.BusinessFinalizationIncomplete || Facts.ExecuteIntentAt is null || Facts.NoStartAt is not null
            || Facts.OutcomeKind is not null || Facts.ReleasedAt is not null || Facts.ReleaseReason is not null
            || Facts.ReservationAttemptId != receipt.AttemptId.Value || Facts.PlanFingerprint != arm.PlanFingerprint
            || Facts.AttemptPlanFingerprint != arm.PlanFingerprint || Facts.OperationId != arm.OperationId
            || Facts.AttemptOperationId != arm.OperationId) return false;
        var remaining = (until < receipt.Deadline ? until : receipt.Deadline) - Facts.DbNow;
        // The timestamp precedes the fresh SQL, so reader/rollback/cleanup time
        // and waiting for the final run lock can only shorten this observation.
        return _observedAt > 0 && remaining > TimeSpan.Zero && remaining <= TimeSpan.FromMinutes(10)
            && Stopwatch.GetElapsedTime(_observedAt) < remaining;
    }
}

/// <summary>Copied status-only projection. Possession of these facts conveys no permission.</summary>
public sealed class BackgroundJobCheckpointHistoryFacts
{
    internal BackgroundJobCheckpointHistoryFacts(DbDataReader row)
    {
        if (row.FieldCount != 48) throw new InvalidOperationException("Checkpoint history requires its exact status projection.");
        JobId = row.GetGuid(0);
        TenantId = row.GetGuid(1);
        OrganizationId = Id(row, 2);
        DefinitionKey = row.GetString(3);
        Profile = row.GetString(4);
        Queue = row.GetString(5);
        CapacityGroup = row.GetString(6);
        OriginalDueAt = row.GetFieldValue<DateTimeOffset>(7);
        AttemptId = row.GetGuid(8);
        Stage = row.GetString(9);
        Ordinal = row.GetInt32(10);
        AdmissionToken = row.GetGuid(11);
        AdmittedAt = row.GetFieldValue<DateTimeOffset>(12);
        Deadline = row.GetFieldValue<DateTimeOffset>(13);
        SelectedNormalAttemptId = Id(row, 14);
        PlanFingerprint = Text(row, 15);
        OperationId = Id(row, 16);
        PreparedAt = Time(row, 17);
        AttemptPlanFingerprint = Text(row, 18);
        AttemptOperationId = Id(row, 19);
        ExecuteIntentAt = Time(row, 20);
        OutcomeKind = Text(row, 21);
        OutcomeAt = Time(row, 22);
        FailurePolicy = Text(row, 23);
        QuiescenceId = Id(row, 24);
        QuiescenceKind = Text(row, 25);
        QuiescentAt = Time(row, 26);
        NoStartAt = Time(row, 27);
        ReservationAttemptId = Id(row, 28);
        ReleasedAt = Time(row, 29);
        ReleaseReason = Text(row, 30);
        JobSelectedNormalAttemptId = Id(row, 31);
        FinalizationStatus = Text(row, 32);
        FinalizationReason = Text(row, 33);
        CurrentAttemptId = Id(row, 34);
        NormalAttempts = row.GetInt32(35);
        FinalizeAttempts = row.GetInt32(36);
        Phase = row.GetString(37);
        ProcessedAt = Time(row, 38);
        DeadLetteredAt = Time(row, 39);
        TerminalReason = Text(row, 40);
        BusinessFinalizationIncomplete = row.GetBoolean(41);
        CurrentWakeGeneration = row.GetInt64(42);
        CurrentOwnershipToken = Id(row, 43);
        CurrentOwnershipKind = Text(row, 44);
        CurrentOwnershipUntil = Time(row, 45);
        CurrentAttemptDeadline = Time(row, 46);
        DbNow = row.GetFieldValue<DateTimeOffset>(47);
    }

    public Guid JobId { get; }
    public Guid TenantId { get; }
    public Guid? OrganizationId { get; }
    public string DefinitionKey { get; }
    public string Profile { get; }
    public string Queue { get; }
    public string CapacityGroup { get; }
    public DateTimeOffset OriginalDueAt { get; }
    public Guid AttemptId { get; }
    public string Stage { get; }
    public int Ordinal { get; }
    public Guid AdmissionToken { get; }
    public DateTimeOffset AdmittedAt { get; }
    public DateTimeOffset Deadline { get; }
    public Guid? SelectedNormalAttemptId { get; }
    public string? PlanFingerprint { get; }
    public Guid? OperationId { get; }
    public DateTimeOffset? PreparedAt { get; }
    public string? AttemptPlanFingerprint { get; }
    public Guid? AttemptOperationId { get; }
    public DateTimeOffset? ExecuteIntentAt { get; }
    public string? OutcomeKind { get; }
    public DateTimeOffset? OutcomeAt { get; }
    public string? FailurePolicy { get; }
    public Guid? QuiescenceId { get; }
    public string? QuiescenceKind { get; }
    public DateTimeOffset? QuiescentAt { get; }
    public DateTimeOffset? NoStartAt { get; }
    public Guid? ReservationAttemptId { get; }
    public DateTimeOffset? ReleasedAt { get; }
    public string? ReleaseReason { get; }
    public Guid? JobSelectedNormalAttemptId { get; }
    public string? FinalizationStatus { get; }
    public string? FinalizationReason { get; }
    public Guid? CurrentAttemptId { get; }
    public int NormalAttempts { get; }
    public int FinalizeAttempts { get; }
    public string Phase { get; }
    public DateTimeOffset? ProcessedAt { get; }
    public DateTimeOffset? DeadLetteredAt { get; }
    public string? TerminalReason { get; }
    public bool BusinessFinalizationIncomplete { get; }
    public long CurrentWakeGeneration { get; }
    public Guid? CurrentOwnershipToken { get; }
    public string? CurrentOwnershipKind { get; }
    public DateTimeOffset? CurrentOwnershipUntil { get; }
    public DateTimeOffset? CurrentAttemptDeadline { get; }
    public DateTimeOffset DbNow { get; }

    private static Guid? Id(DbDataReader row, int column) => row.IsDBNull(column) ? null : row.GetGuid(column);
    private static string? Text(DbDataReader row, int column) => row.IsDBNull(column) ? null : row.GetString(column);
    private static DateTimeOffset? Time(DbDataReader row, int column) => row.IsDBNull(column) ? null : row.GetFieldValue<DateTimeOffset>(column);
}

public sealed partial class BackgroundJobCapacityRecoveryReceipt
{
#pragma warning disable CA1034 // Extend the existing single confined minter; no new subclass surface.
    public abstract partial class MintingSource
    {
        protected sealed class CheckpointHistoryCandidate
        {
            internal CheckpointHistoryCandidate(ConfirmedStartReceipt? receipt, BackgroundJobCommitGate gate,
                RecordBackgroundJobArmCommand? arm, RecordBackgroundJobOutcomeCommand? outcome, long observedAt, DbDataReader row)
            {
                if (observedAt <= 0 || observedAt > Stopwatch.GetTimestamp())
                    throw new InvalidOperationException("Checkpoint observation must precede its fresh SQL read.");
                Receipt = receipt;
                Gate = gate;
                Arm = arm;
                Outcome = outcome;
                Facts = new(row);
                ObservedAt = observedAt;
            }

            internal ConfirmedStartReceipt? Receipt { get; }
            internal BackgroundJobCommitGate Gate { get; }
            internal RecordBackgroundJobArmCommand? Arm { get; }
            internal RecordBackgroundJobOutcomeCommand? Outcome { get; }
            public BackgroundJobCheckpointHistoryFacts Facts { get; }
            internal long ObservedAt { get; }
        }

        protected static CheckpointHistoryCandidate CaptureCheckpointHistory(ConfirmedStartReceipt receipt,
            BackgroundJobCommitGate gate, long observedAt, DbDataReader row) => new(receipt, gate, null, null, observedAt, row);
        protected static CheckpointHistoryCandidate CaptureCheckpointHistory(RecordBackgroundJobArmCommand arm,
            long observedAt, DbDataReader row) => new(null, arm.Gate, arm, null, observedAt, row);
        protected static CheckpointHistoryCandidate CaptureCheckpointHistory(RecordBackgroundJobOutcomeCommand outcome,
            long observedAt, DbDataReader row) => new(null, outcome.Gate, null, outcome, observedAt, row);
        protected static BackgroundJobCheckpointHistoryFacts CaptureSelectedNormalHistory(DbDataReader row) => new(row);

        protected static BackgroundJobCheckpointConfirmation PublishCheckpointHistory(CheckpointHistoryCandidate candidate)
        {
            var confirmation = new BackgroundJobCheckpointConfirmation(candidate.Receipt, candidate.Gate,
                candidate.Arm, candidate.Outcome, candidate.Facts, candidate.ObservedAt);
            candidate.Gate.RecordConfirmation(confirmation);
            return confirmation;
        }
    }
#pragma warning restore CA1034
}
