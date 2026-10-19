using System.Data.Common;
using System.Diagnostics;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Application.Jobs;

/// <summary>The immutable purpose derived from the acknowledged stored phase and terminal reason.</summary>
public enum BackgroundJobExternalRecoveryPurpose { Reconcile, TerminalNoStart }

/// <summary>
/// Acknowledged fresh recovery authority, separate from the original counted
/// admission. It can never construct or consume a business-root receipt.
/// </summary>
public sealed class BackgroundJobExternalRecoveryReceipt
{
    private readonly object _sync = new();
    private readonly BackgroundJobRunGate.RuntimeStop _runtime;
    private BackgroundJobExternalRecoveryBinding? _binding;

    private BackgroundJobExternalRecoveryReceipt(DbDataReader row, BackgroundJobRunGate.RuntimeStop runtime, long observedAt)
    {
        if (row.FieldCount != 20 || observedAt <= 0 || observedAt > Stopwatch.GetTimestamp()) throw Refused();
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ObservedAt = observedAt;
        JobId = BackgroundJobId.From(row.GetGuid(0));
        TenantId = TenantId.From(row.GetGuid(1));
        OrganizationId = row.IsDBNull(2) ? null : LearnStack.SharedKernel.Identifiers.OrganizationId.From(row.GetGuid(2));
        DefinitionKey = row.GetString(3);
        Queue = row.GetString(5);
        CapacityGroup = row.GetString(6);
        OriginalDueAt = row.GetFieldValue<DateTimeOffset>(7);
        AttemptId = BackgroundJobAttemptId.From(row.GetGuid(8));
        NormalAttemptOrdinal = row.GetInt32(9);
        AdmittedAt = row.GetFieldValue<DateTimeOffset>(10);
        Deadline = row.GetFieldValue<DateTimeOffset>(11);
        AdmissionToken = row.GetGuid(12);
        WakeGeneration = row.GetInt64(13);
        RecoveryToken = row.GetGuid(14);
        OwnershipUntil = row.GetFieldValue<DateTimeOffset>(15);
        DbNow = row.GetFieldValue<DateTimeOffset>(16);
        TerminalReason = row.IsDBNull(19) ? null : row.GetString(19);
        Purpose = (row.GetString(18), TerminalReason) switch
        {
            ("reconciling", null) => BackgroundJobExternalRecoveryPurpose.Reconcile,
            ("terminal_pending", "permanent_failure" or "retired_definition" or "invalid_envelope") =>
                BackgroundJobExternalRecoveryPurpose.TerminalNoStart,
            ("terminal_pending", "retry_exhausted") when NormalAttemptOrdinal == 6 => BackgroundJobExternalRecoveryPurpose.TerminalNoStart,
            _ => throw Refused(),
        };
        if (JobId.Value.Version != 7 || AttemptId.Value.Version != 7 || TenantId.Value == Guid.Empty
            || TenantId == LearnStack.SharedKernel.Identifiers.TenantId.PlatformSentinel || OrganizationId?.Value == Guid.Empty
            || row.GetString(4) != "external" || row.GetString(17) != "normal"
            || string.IsNullOrWhiteSpace(DefinitionKey) || DefinitionKey.Length > 150
            || string.IsNullOrWhiteSpace(Queue) || Queue.Length > 50
            || string.IsNullOrWhiteSpace(CapacityGroup) || CapacityGroup.Length > 150
            || NormalAttemptOrdinal is < 1 or > 6 || AdmittedAt > DbNow || OriginalDueAt > AdmittedAt
            || Deadline <= AdmittedAt || Deadline - AdmittedAt > TimeSpan.FromHours(24)
            || AdmissionToken == Guid.Empty || RecoveryToken == Guid.Empty || RecoveryToken == AdmissionToken
            || WakeGeneration < 1 || OwnershipUntil <= DbNow || OwnershipUntil - DbNow > TimeSpan.FromMinutes(10))
            throw Refused();
        // The original deadline may have expired. Only the fresh recovery lease
        // limits this new authority; it never authorizes another Execute.
    }

    public BackgroundJobId JobId { get; }
    public TenantId TenantId { get; }
    public OrganizationId? OrganizationId { get; }
    public string DefinitionKey { get; }
    public string Queue { get; }
    public string CapacityGroup { get; }
    public DateTimeOffset OriginalDueAt { get; }
    public BackgroundJobAttemptId AttemptId { get; }
    public int NormalAttemptOrdinal { get; }
    public DateTimeOffset AdmittedAt { get; }
    public DateTimeOffset Deadline { get; }
    public Guid AdmissionToken { get; }
    public long WakeGeneration { get; }
    public Guid RecoveryToken { get; }
    public DateTimeOffset OwnershipUntil { get; }
    public DateTimeOffset DbNow { get; }
    public BackgroundJobExternalRecoveryPurpose Purpose { get; }
    public string? TerminalReason { get; }
    internal long ObservedAt { get; }

    public BackgroundJobExternalRecoveryBinding Bind(BackgroundJobRunGate run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var source = JobApplicationSourceOwner.Current ?? throw Refused();
        lock (_sync)
        {
            if (_binding is not null || run.IsClosed) throw Refused();
            var binding = new BackgroundJobExternalRecoveryBinding(this, source, run);
            // This rejects a cleaned but previously used original source as well
            // as another recovery. A fresh probe cannot clean an old invocation.
            source.BindExternalRecovery();
            run.BindExternalRecovery(binding, _runtime);
            return _binding = binding;
        }
    }

    internal static BackgroundJobExternalRecoveryReceipt Capture(DbDataReader row,
        BackgroundJobRunGate.RuntimeStop runtime, long observedAt) => new(row, runtime, observedAt);

    internal bool IsBoundTo(BackgroundJobExternalRecoveryBinding binding)
    {
        lock (_sync) return ReferenceEquals(_binding, binding);
    }

    private static InvalidOperationException Refused() =>
        new("External recovery requires its exact acknowledged projection and fresh binding.");
}

/// <summary>Once-bound fresh source/run; no admission ACK or module-root activation.</summary>
public sealed class BackgroundJobExternalRecoveryBinding
{
    private BackgroundJobExternalRecoveryRenewal? _renewal;
    internal BackgroundJobExternalRecoveryBinding(BackgroundJobExternalRecoveryReceipt receipt,
        JobApplicationSourceOwner source, BackgroundJobRunGate run)
    {
        Receipt = receipt;
        SourceOwner = source;
        Run = run;
    }

    public BackgroundJobExternalRecoveryReceipt Receipt { get; }
    public JobApplicationSourceOwner SourceOwner { get; }
    public BackgroundJobRunGate Run { get; }

    // The SQL source samples before its fresh projection. Binding, COMMIT,
    // resource cleanup and adapter construction all spend this same interval.
    // Only Run-locked admission reads it; no receipt lock is acquired in reverse.
    internal bool HasLiveLeaseWindow => _renewal?.HasLiveWindow
        ?? Stopwatch.GetElapsedTime(Receipt.ObservedAt) < Receipt.OwnershipUntil - Receipt.DbNow;

    // Caller holds this binding's Run lock. Neither accepting a genuine renewal
    // nor replaying its evidence changes the immutable acquisition receipt.
    internal bool TryAcceptRenewal(BackgroundJobExternalRecoveryRenewal renewal,
        BackgroundJobRunGate.RuntimeStop runtime)
    {
        if (!renewal.Matches(Receipt, runtime)
            || renewal.ObservedAt <= (_renewal?.ObservedAt ?? Receipt.ObservedAt)
            || renewal.OwnershipUntil <= (_renewal?.OwnershipUntil ?? Receipt.OwnershipUntil)) return false;
        if (!HasLiveLeaseWindow || !renewal.HasLiveWindow) return false;
        _renewal = renewal;
        return true;
    }
}

public sealed partial class BackgroundJobCapacityRecoveryReceipt
{
#pragma warning disable CA1034 // Reuse the existing single confined minting subclass.
    public abstract partial class MintingSource
    {
        protected sealed class ExternalRecoveryCandidate
        {
            internal ExternalRecoveryCandidate(DbDataReader row, BackgroundJobRunGate.RuntimeStop runtime, long observedAt) =>
                Receipt = BackgroundJobExternalRecoveryReceipt.Capture(row, runtime, observedAt);
            internal BackgroundJobExternalRecoveryReceipt Receipt { get; }
        }

        protected static ExternalRecoveryCandidate CaptureExternalRecoveryCandidate(DbDataReader row,
            BackgroundJobRunGate.RuntimeStop runtime, long observedAt) => new(row, runtime, observedAt);
        protected static BackgroundJobExternalRecoveryReceipt PublishExternalRecoveryCandidate(ExternalRecoveryCandidate candidate) =>
            candidate.Receipt;
    }
#pragma warning restore CA1034
}
