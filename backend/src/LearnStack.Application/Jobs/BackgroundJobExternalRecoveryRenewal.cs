using System.Data.Common;
using System.Diagnostics;

namespace LearnStack.Application.Jobs;

/// <summary>
/// Closed evidence of one acknowledged, cleaned SQL renewal. It cannot bind a
/// source/run or issue another recovery; only an already bound Run can use it.
/// </summary>
public sealed class BackgroundJobExternalRecoveryRenewal
{
    private readonly BackgroundJobRunGate.RuntimeStop _runtime;
    private readonly Guid _job, _tenant, _attempt, _admission, _recovery;
    private readonly Guid? _organization;
    private readonly string _definition, _queue, _capacity;
    private readonly DateTimeOffset _due, _admitted, _deadline;
    private readonly int _ordinal;
    private readonly long _generation;

    private BackgroundJobExternalRecoveryRenewal(DbDataReader row, BackgroundJobRunGate.RuntimeStop runtime, long observedAt)
    {
        if (row.FieldCount != 18 || row.GetString(4) != "external" || row.GetString(17) != "normal"
            || observedAt <= 0 || observedAt > Stopwatch.GetTimestamp())
            throw new InvalidOperationException("Recovery renewal requires its exact returned row and source observation.");
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _job = row.GetGuid(0);
        _tenant = row.GetGuid(1);
        _organization = row.IsDBNull(2) ? null : row.GetGuid(2);
        _definition = row.GetString(3);
        _queue = row.GetString(5);
        _capacity = row.GetString(6);
        _due = row.GetFieldValue<DateTimeOffset>(7);
        _attempt = row.GetGuid(8);
        _ordinal = row.GetInt32(9);
        _admitted = row.GetFieldValue<DateTimeOffset>(10);
        _deadline = row.GetFieldValue<DateTimeOffset>(11);
        _admission = row.GetGuid(12);
        _generation = row.GetInt64(13);
        _recovery = row.GetGuid(14);
        OwnershipUntil = row.GetFieldValue<DateTimeOffset>(15);
        DbNow = row.GetFieldValue<DateTimeOffset>(16);
        ObservedAt = observedAt;
        if (OwnershipUntil <= DbNow || OwnershipUntil - DbNow > TimeSpan.FromMinutes(10))
            throw new InvalidOperationException("Recovery renewal returned an invalid lease interval.");
    }

    internal long ObservedAt { get; }
    internal DateTimeOffset OwnershipUntil { get; }
    internal DateTimeOffset DbNow { get; }
    internal bool HasLiveWindow => Stopwatch.GetElapsedTime(ObservedAt) < OwnershipUntil - DbNow;

    internal bool Matches(BackgroundJobExternalRecoveryReceipt receipt, BackgroundJobRunGate.RuntimeStop runtime) =>
        ReferenceEquals(runtime, _runtime) && receipt.Purpose == BackgroundJobExternalRecoveryPurpose.Reconcile
        && _job == receipt.JobId.Value && _tenant == receipt.TenantId.Value
        && _organization == receipt.OrganizationId?.Value && _definition == receipt.DefinitionKey
        && _queue == receipt.Queue && _capacity == receipt.CapacityGroup && _due == receipt.OriginalDueAt
        && _attempt == receipt.AttemptId.Value && _ordinal == receipt.NormalAttemptOrdinal
        && _admitted == receipt.AdmittedAt && _deadline == receipt.Deadline && _admission == receipt.AdmissionToken
        && _generation == receipt.WakeGeneration && _recovery == receipt.RecoveryToken;

    internal static BackgroundJobExternalRecoveryRenewal Capture(DbDataReader row,
        BackgroundJobRunGate.RuntimeStop runtime, long observedAt) => new(row, runtime, observedAt);
}

public sealed partial class BackgroundJobCapacityRecoveryReceipt
{
#pragma warning disable CA1034 // Reuse the same one confined minting subclass.
    public abstract partial class MintingSource
    {
        protected sealed class ExternalRecoveryRenewalCandidate
        {
            internal ExternalRecoveryRenewalCandidate(DbDataReader row,
                BackgroundJobRunGate.RuntimeStop runtime, long observedAt) =>
                Evidence = BackgroundJobExternalRecoveryRenewal.Capture(row, runtime, observedAt);
            internal BackgroundJobExternalRecoveryRenewal Evidence { get; }
        }

        protected static ExternalRecoveryRenewalCandidate CaptureExternalRecoveryRenewalCandidate(DbDataReader row,
            BackgroundJobRunGate.RuntimeStop runtime, long observedAt) => new(row, runtime, observedAt);
        protected static BackgroundJobExternalRecoveryRenewal PublishExternalRecoveryRenewalCandidate(
            ExternalRecoveryRenewalCandidate candidate) => candidate.Evidence;
    }
#pragma warning restore CA1034
}
