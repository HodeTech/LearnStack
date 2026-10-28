using System.Data.Common;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.SharedKernel.Audit;

/// <summary>The central creation intent's immutable target and PostgreSQL audit key.</summary>
public sealed class GlobalIdentityCreationReference
{
    public const string Operation = "identity.user.create";
    private readonly IGlobalIdentityUnitOfWork _unit;
    private readonly DbConnection _connection;
    private readonly DbTransaction _transaction;
    private readonly GlobalIdentityContextAnnouncement _announcement;

    private GlobalIdentityCreationReference(IGlobalIdentityUnitOfWork unit,
        GlobalIdentityContextAnnouncement announcement, UserId targetUserId, AuditEntryId auditId,
        DateTimeOffset timestamp)
    {
        if (!targetUserId.IsInitialized() || targetUserId.Value.Version != 7 || targetUserId == UserId.SystemActor
            || !auditId.IsInitialized() || auditId.Value.Version != 7
            || announcement.Descriptor.Kind is not (GlobalIdentityOwnerKind.Bootstrap or GlobalIdentityOwnerKind.SeedAdoption)
            || (announcement.Descriptor.SeedActor is { } seed && seed != targetUserId)
            || timestamp <= DateTimeOffset.MinValue || timestamp >= DateTimeOffset.MaxValue)
            throw new ArgumentException("Global identity creation attribution is invalid.");
        _unit = unit;
        _connection = unit.Connection;
        _transaction = unit.Transaction ?? throw new InvalidOperationException("Global creation requires its live owner.");
        _announcement = announcement;
        TargetUserId = targetUserId;
        AuditId = auditId;
        Timestamp = NormalizeTimestamp(timestamp);
        RequireLive(unit);
    }

    public UserId TargetUserId { get; }
    public AuditEntryId AuditId { get; }
    public DateTimeOffset Timestamp { get; }
    public GlobalIdentityOwnerDescriptor Descriptor => _announcement.Descriptor;
    public IdentityAuthorityId AuthorityId => Descriptor.Subject.AuthorityId;
    public IdentitySubjectKey SubjectKey => Descriptor.Subject.SubjectKey;
    public string Source => Descriptor.Source;
    public UserId? ActorUserId => Descriptor.Kind == GlobalIdentityOwnerKind.SeedAdoption ? UserId.SystemActor : null;

    public void RequireSubject(IdentityAuthorityId authorityId, IdentitySubjectKey subjectKey)
    {
        if (authorityId != AuthorityId || !SubjectKey.Equals(subjectKey))
            throw new InvalidOperationException("The creation reference belongs to another identity subject.");
    }

    public void RequireSubject(GlobalIdentitySubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        RequireSubject(subject.AuthorityId, subject.SubjectKey);
        if (!string.Equals(subject.Issuer, Descriptor.Subject.Issuer, StringComparison.Ordinal)
            || !string.Equals(subject.Subject, Descriptor.Subject.Subject, StringComparison.Ordinal))
            throw new InvalidOperationException("The creation reference belongs to another identity subject.");
    }

    public void RequireLive(IUnitOfWork unit)
    {
        if (!ReferenceEquals(unit, _unit) || !unit.HasActiveTransaction || unit.IsRollbackOnly
            || unit.Mode != TransactionMode.ReadWrite || !ReferenceEquals(unit.Connection, _connection)
            || !ReferenceEquals(unit.Transaction, _transaction) || _announcement.Generation <= 0
            || _unit.GetGlobalIdentityAnnouncement(_transaction) != _announcement)
            throw new InvalidOperationException("The creation reference no longer belongs to its live owner.");
    }

    public void RequireIntent(AuditIntent intent)
    {
        if (!ReferenceEquals(intent.GlobalIdentity, this) || intent.Id != AuditId || intent.DeclaredAt != Timestamp
            || intent.TenantId != TenantId.PlatformSentinel || intent.OrganizationId is not null
            || intent.ActorUserId != ActorUserId || intent.ModuleName != "identity" || intent.Operation != Operation
            || intent.OperationClass != OperationClass.Must || intent.OperationType != OperationType.SecurityEvent
            || intent.EntityType != Descriptor.EntityType || intent.SubjectId != TargetUserId.Value.ToString("D")
            || intent.CausationId is not null || intent.CausalActorUserId is not null
            || intent.JobId is not null || intent.ParentJobId is not null)
            throw new ArgumentException("The global identity intent differs from its captured creation reference.");
    }

    public static DateTimeOffset NormalizeTimestamp(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    public DateTimeOffset ReconciliationTimestamp(DateTimeOffset now)
    {
        var normalized = NormalizeTimestamp(now);
        return normalized == Timestamp ? normalized.AddTicks(10) : normalized;
    }

    public override string ToString() => nameof(GlobalIdentityCreationReference);

#pragma warning disable CA1034 // Only the censused common audit source may mint the privately constructed reference.
    public abstract class MintingSource
#pragma warning restore CA1034
    {
        protected static GlobalIdentityCreationReference Mint(IGlobalIdentityUnitOfWork unit,
            GlobalIdentityContextAnnouncement announcement, UserId targetUserId, AuditEntryId auditId,
            DateTimeOffset timestamp) => new(unit, announcement, targetUserId, auditId, timestamp);
    }
}
