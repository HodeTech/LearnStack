using System.Data.Common;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Application.Jobs;

/// <summary>Exact immutable envelope facts, read and validated using the app role.</summary>
public sealed record BackgroundJobEnvelope(BackgroundJobId JobId, TenantId TenantId,
    OrganizationId? OrganizationId, string DefinitionKey, string Profile, string Queue, string? CapacityGroup,
    string Parameters, DateTimeOffset OriginalDueAt, string Traceparent, UserId? CausalActor,
    Guid? IncomingEventId, BackgroundJobId? ParentJobId, BackgroundJobRecipientId? RecipientId);

public sealed record ReadBackgroundJobEnvelopeQuery(BackgroundJobRoutingClaim Claim)
    : IRequest<Result<BackgroundJobEnvelope>>;

/// <summary>The exact short transaction that records one normal admission.</summary>
public sealed class AdmitBackgroundJobAttemptCommand(BackgroundJobRoutingClaim claim, BackgroundJobEnvelope envelope)
    : IRequest<Result<None>>
{
    private IUnitOfWork? _unit;
    private TenantContextAnnouncement? _announcement;
    private DbTransaction? _transaction;
    private BackgroundJobAttemptId? _attempt;
    private int _ordinal;
    private DateTimeOffset _admittedAt;
    private DateTimeOffset _deadline;
    private DateTimeOffset _ownershipUntil;
    private BackgroundJobStartPublication? _publication;
    private JobApplicationSourceOwner? _sourceOwner;

    public BackgroundJobRoutingClaim Claim { get; } = claim ?? throw new ArgumentNullException(nameof(claim));
    public BackgroundJobEnvelope Envelope { get; } = envelope ?? throw new ArgumentNullException(nameof(envelope));
    public ConfirmedStartReceipt? Receipt { get; private set; }
    public bool CommitStarted { get; private set; }
    public bool CapacityDeferred { get; private set; }

    internal void BindOwner(IUnitOfWork unit, bool owner)
    {
        BackgroundJobExecutionAdmission.RequireStart(this);
        if (!owner || _unit is not null || unit.Mode != TransactionMode.ReadWrite)
            throw new InvalidOperationException("Job admission must own a fresh writable transaction.");
        _unit = unit;
        _sourceOwner = JobApplicationSourceOwner.Current
            ?? throw new InvalidOperationException("Job admission requires its application-source owner.");
        _transaction = unit.Transaction;
        _announcement = unit.GetTenantContextAnnouncement(unit.Transaction)
            ?? throw new InvalidOperationException("Job admission requires a live announcement.");
        if (_transaction is null || _announcement.Generation <= 0 || _announcement.TenantId != Claim.TenantId
            || _announcement.OrganizationId != Claim.OrganizationId)
            throw new InvalidOperationException("Job admission requires its exact routing announcement.");
    }

    /// <summary>Called only after the app-role INSERT and summary UPDATE both succeeded.</summary>
    public void RecordAttempt(IUnitOfWork unit, BackgroundJobAttemptId attempt, int ordinal,
        DateTimeOffset admittedAt, DateTimeOffset deadline, DateTimeOffset ownershipUntil)
    {
        BackgroundJobExecutionAdmission.RequireStart(this);
        if (!ReferenceEquals(_unit, unit) || !ReferenceEquals(_transaction, unit.Transaction)
            || _announcement != unit.GetTenantContextAnnouncement(unit.Transaction)
            || unit.IsRollbackOnly || CapacityDeferred || _attempt is not null || !attempt.IsInitialized() || attempt.Value.Version != 7
            || ordinal is < 1 or > 6 || admittedAt >= deadline || ownershipUntil > deadline
            || ownershipUntil <= admittedAt)
            throw new InvalidOperationException("The attempt does not belong to this admission owner.");
        _attempt = attempt;
        _ordinal = ordinal;
        _admittedAt = admittedAt;
        _deadline = deadline;
        _ownershipUntil = ownershipUntil;
    }

    /// <summary>Records a grouped refusal before any normal attempt or reservation is issued.</summary>
    public void RecordCapacityDeferred(IUnitOfWork unit)
    {
        BackgroundJobExecutionAdmission.RequireStart(this);
        if (!ReferenceEquals(_unit, unit) || _transaction is null || !ReferenceEquals(_transaction, unit.Transaction)
            || !ReferenceEquals(_sourceOwner, JobApplicationSourceOwner.Current) || !unit.HasActiveTransaction
            || _announcement is not { Generation: > 0 } || _announcement != unit.GetTenantContextAnnouncement(_transaction)
            || _announcement.TenantId != Claim.TenantId || _announcement.OrganizationId != Claim.OrganizationId
            || unit.Mode != TransactionMode.ReadWrite || unit.IsRollbackOnly || Envelope.CapacityGroup is null
            || CapacityDeferred || _attempt is not null || Receipt is not null || CommitStarted)
            throw new InvalidOperationException("Capacity deferral requires its unissued admission owner.");
        _sourceOwner!.RequireOwnedMainUnit(unit);
        CapacityDeferred = true;
    }

    internal void BindPublication(BackgroundJobStartPublication publication)
    {
        if (_publication is not null || _unit is not null || CommitStarted || Receipt is not null)
            throw new InvalidOperationException("Publication must bind before admission begins.");
        _publication = publication;
    }

    internal void MarkCommitting(IUnitOfWork unit)
    {
        if (!ReferenceEquals(_unit, unit) || !ReferenceEquals(_transaction, unit.Transaction)
            || _attempt is null || unit.IsRollbackOnly
            || _announcement != unit.GetTenantContextAnnouncement(unit.Transaction))
            throw new InvalidOperationException("Only the live recorded admission may commit.");
        CommitStarted = true;
    }

    internal void RecordCommitted(IUnitOfWork unit)
    {
        if (!ReferenceEquals(_unit, unit) || !CommitStarted || _announcement is null || _attempt is not { } attempt || Receipt is not null)
            throw new InvalidOperationException("An acknowledged admission requires its exact recorded attempt.");
        Receipt = ConfirmedStartReceipt.FromAcknowledged(this, attempt, _ordinal, _admittedAt, _deadline, _ownershipUntil);
        _publication?.Publish(this, Receipt);
    }
}

/// <summary>A single-use business activation token; unknown COMMIT never creates one.</summary>
public sealed class ConfirmedStartReceipt
{
    private int _consumed;
    private ConfirmedStartReceipt(AdmitBackgroundJobAttemptCommand command, BackgroundJobAttemptId attempt,
        BackgroundJobExecutionStage stage, int normalAttemptOrdinal, DateTimeOffset admittedAt,
        DateTimeOffset deadline, DateTimeOffset ownershipUntil, JobApplicationSourceOwner sourceOwner)
    {
        Claim = command.Claim;
        Envelope = command.Envelope;
        AttemptId = attempt;
        Stage = stage;
        NormalAttemptOrdinal = normalAttemptOrdinal;
        AdmittedAt = admittedAt;
        Deadline = deadline;
        OwnershipUntil = ownershipUntil;
        SourceOwner = sourceOwner;
    }
    public BackgroundJobRoutingClaim Claim { get; }
    public BackgroundJobEnvelope Envelope { get; }
    public BackgroundJobAttemptId AttemptId { get; }
    public BackgroundJobExecutionStage Stage { get; }
    public int NormalAttemptOrdinal { get; }
    public DateTimeOffset AdmittedAt { get; }
    public DateTimeOffset Deadline { get; }
    public DateTimeOffset OwnershipUntil { get; }
    public JobApplicationSourceOwner SourceOwner { get; }
    internal static ConfirmedStartReceipt FromAcknowledged(AdmitBackgroundJobAttemptCommand command,
        BackgroundJobAttemptId attempt, int ordinal, DateTimeOffset admittedAt, DateTimeOffset deadline,
        DateTimeOffset ownershipUntil) => new(command, attempt, BackgroundJobExecutionStage.Transactional,
            ordinal, admittedAt, deadline, ownershipUntil, JobApplicationSourceOwner.Current
                ?? throw new InvalidOperationException("Admission must retain its application-source owner."));
    internal void Consume()
    {
        if (Interlocked.Exchange(ref _consumed, 1) != 0)
            throw new InvalidOperationException("A confirmed start receipt can activate only once.");
    }
}

public static class BackgroundJobFrameworkRequests
{
    public static IReadOnlyList<BackgroundJobRequestDescriptor> All { get; } = Array.AsReadOnly(new[]
    {
        new BackgroundJobRequestDescriptor(typeof(ReadBackgroundJobEnvelopeQuery), BackgroundJobRequestClassification.FrameworkReadOnly, null),
        new BackgroundJobRequestDescriptor(typeof(AdmitBackgroundJobAttemptCommand), BackgroundJobRequestClassification.FrameworkReadWrite, null),
        new BackgroundJobRequestDescriptor(typeof(RecordBackgroundJobCapacityCleanupCommand), BackgroundJobRequestClassification.FrameworkReadWrite, null),
    });
}

public sealed class BackgroundJobAuditCatalogSource : IAuditCatalogSource
{
    public string ModuleName => "backgroundjobs";
    public void Describe(IAuditCatalogBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Off<ReadBackgroundJobEnvelopeQuery>();
        builder.Off<AdmitBackgroundJobAttemptCommand>();
        builder.Off<RecordBackgroundJobCapacityCleanupCommand>();
    }
}
