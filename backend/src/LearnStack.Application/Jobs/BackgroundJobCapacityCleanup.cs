using System.Data.Common;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Application.Jobs;

public enum BackgroundJobCapacityCleanupEvidence { TransactionalRollback, NeverStarted }

/// <summary>Acknowledged nonexecuting recovery authority; never an executable business receipt.</summary>
public sealed partial class BackgroundJobCapacityRecoveryReceipt
{
    private BackgroundJobCapacityRecoveryReceipt(DbDataReader row)
    {
        if (row.FieldCount != 17) throw new InvalidOperationException("Recovery requires its exact returned projection.");
        JobId = BackgroundJobId.From(row.GetGuid(0));
        TenantId = TenantId.From(row.GetGuid(1));
        OrganizationId = row.IsDBNull(2) ? null : LearnStack.SharedKernel.Identifiers.OrganizationId.From(row.GetGuid(2));
        DefinitionKey = row.GetString(3);
        Profile = row.GetString(4);
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
        if (JobId.Value.Version != 7 || AttemptId.Value.Version != 7 || TenantId.Value == Guid.Empty
            || TenantId == LearnStack.SharedKernel.Identifiers.TenantId.PlatformSentinel || OrganizationId?.Value == Guid.Empty
            || Profile != "transactional" || string.IsNullOrWhiteSpace(DefinitionKey) || string.IsNullOrWhiteSpace(Queue)
            || string.IsNullOrWhiteSpace(CapacityGroup) || NormalAttemptOrdinal is < 1 or > 6
            || Deadline <= AdmittedAt || AdmissionToken == Guid.Empty || RecoveryToken == Guid.Empty
            || RecoveryToken == AdmissionToken || WakeGeneration < 1 || OwnershipUntil <= DbNow)
            throw new InvalidOperationException("Recovery returned invalid immutable authority.");
    }

    public BackgroundJobId JobId { get; }
    public TenantId TenantId { get; }
    public OrganizationId? OrganizationId { get; }
    public string DefinitionKey { get; }
    public string Profile { get; }
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

#pragma warning disable CA1034 // Only the confined store may publish a captured candidate after ACK or locked proof.
    public abstract partial class MintingSource
    {
        protected sealed class Candidate
        {
            internal Candidate(DbDataReader row) => Receipt = new(row);
            internal BackgroundJobCapacityRecoveryReceipt Receipt { get; }
        }

        protected static Candidate CaptureCandidate(DbDataReader returnedRow) => new(returnedRow);
        protected static BackgroundJobCapacityRecoveryReceipt ConfirmCandidate(Candidate candidate) => candidate.Receipt;
    }
#pragma warning restore CA1034
}

/// <summary>One exact app-role evidence and reservation checkpoint on the existing request owner.</summary>
public sealed class RecordBackgroundJobCapacityCleanupCommand : IRequest<Result<None>>
{
    private IUnitOfWork? _unit;
    private DbTransaction? _transaction;
    private TenantContextAnnouncement? _announcement;
    private bool _recorded;

    public RecordBackgroundJobCapacityCleanupCommand(BackgroundJobCapacityRecoveryReceipt recovery,
        BackgroundJobCapacityCleanupEvidence evidence, BackgroundJobCommitGate gate)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        if (!Enum.IsDefined(evidence)) throw new ArgumentOutOfRangeException(nameof(evidence));
        Recovery = recovery;
        Evidence = evidence;
        Gate = gate is { Kind: BackgroundJobCheckpointKind.TransactionalCleanup }
            ? gate : throw new ArgumentException("Capacity cleanup requires its prebound stage.", nameof(gate));
        SourceOwner = JobApplicationSourceOwner.Current
            ?? throw new InvalidOperationException("Capacity cleanup requires its original application-source owner.");
    }

    public BackgroundJobCapacityRecoveryReceipt Recovery { get; }
    public BackgroundJobCapacityCleanupEvidence Evidence { get; }
    public JobApplicationSourceOwner SourceOwner { get; }
    public BackgroundJobCommitGate Gate { get; }
    public bool CommitStarted { get; private set; }
    public bool CommitAcknowledged { get; private set; }
    public bool KnownRollback { get; private set; }

    internal void BindOwner(IUnitOfWork unit, bool owner)
    {
        BackgroundJobExecutionAdmission.RequireCapacityCleanup(this);
        if (!owner || _unit is not null || unit.Mode != TransactionMode.ReadWrite)
            throw new InvalidOperationException("Capacity cleanup must own a fresh writable transaction.");
        _unit = unit;
        _transaction = unit.Transaction;
        _announcement = unit.GetTenantContextAnnouncement(_transaction)
            ?? throw new InvalidOperationException("Capacity cleanup requires a successful announcement.");
        RequireOwner(unit);
    }

    public void RequireOwner(IUnitOfWork unit)
    {
        BackgroundJobExecutionAdmission.RequireCapacityCleanup(this);
        SourceOwner.RequireOwnedMainUnit(unit);
        if (!ReferenceEquals(SourceOwner, JobApplicationSourceOwner.Current) || !ReferenceEquals(_unit, unit)
            || _transaction is null || !ReferenceEquals(_transaction, unit.Transaction) || !unit.HasActiveTransaction
            || unit.IsRollbackOnly || unit.Mode != TransactionMode.ReadWrite || _announcement is not { Generation: > 0 }
            || Gate.IsClosed
            || _announcement != unit.GetTenantContextAnnouncement(_transaction)
            || _announcement.TenantId != Recovery.TenantId || _announcement.OrganizationId != Recovery.OrganizationId)
            throw new InvalidOperationException("Capacity cleanup lost its exact announced physical owner.");
    }

    /// <summary>Only the exact handler records both matching evidence and reservation release.</summary>
    public void RecordCleanup(IUnitOfWork unit)
    {
        RequireOwner(unit);
        if (_recorded) throw new InvalidOperationException("Capacity cleanup records its checkpoint once.");
        _recorded = true;
    }

    internal void MarkCommitting(IUnitOfWork unit)
    {
        RequireOwner(unit);
        if (!_recorded || CommitStarted) throw new InvalidOperationException("Capacity cleanup has no recorded checkpoint.");
        if (!Gate.TryBeginCommit()) throw new OperationCanceledException("Capacity checkpoint COMMIT is closed.");
        CommitStarted = true;
    }

    internal void RecordCommitted(IUnitOfWork unit)
    {
        if (!ReferenceEquals(_unit, unit) || !CommitStarted || CommitAcknowledged)
            throw new InvalidOperationException("Capacity cleanup acknowledgement belongs to its actual owner.");
        CommitAcknowledged = true;
        Gate.RecordCommitted();
    }

    internal void RecordRolledBack()
    {
        KnownRollback = true;
        Gate.RecordRolledBack();
    }
}
