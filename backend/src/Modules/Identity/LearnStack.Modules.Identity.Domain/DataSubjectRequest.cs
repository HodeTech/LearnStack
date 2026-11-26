using System.Collections.Frozen;
using System.Collections.Immutable;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Identity.Domain;

/// <summary>
/// One subject data request — an export, an erasure or a global account closure — bound to one scope
/// group, with the closed status machine of
/// <see href="../../../../../docs/decisions/0106-subject-data-requests-authority-and-status.md">ADR-0106 § 8</see>.
/// </summary>
/// <remarks>
/// <para>
/// <b>An immutable snapshot.</b> The kind, origin, subject, requester, group, frozen Membership
/// identifiers and execution time are set by a factory and never change, so removing the Membership
/// strands neither pending work nor its status. The database guard refuses the same changes.
/// </para>
/// <para>
/// <b>The transitions are the only mutators.</b> Each is one of ADR-0106 § 8's arrows and returns a
/// <c>business_rule_violation</c> for any other; nothing here throws for an expected refusal. Who may
/// take a transition — the requester, the subject, a job, an operator — is the calling operation's
/// authorization, not this aggregate's. The privileged arrows (an erasure's
/// <c>awaiting_privileged_step → completed</c>, a closure's Begin and Finalize) are modelled here and
/// executed by the operator acts of ADR-0107 § 7 and § 8, which own their database witnesses.
/// </para>
/// <para>
/// <b>Every terminal status frees the group's one active slot</b>, which the partial unique index on
/// the non-terminal rows enforces; a terminal request accepts no further change.
/// </para>
/// </remarks>
[TenantOwned]
[OrganizationScoped]
public sealed class DataSubjectRequest : AuditableEntity<DataSubjectRequestId>, IAggregateRoot<DataSubjectRequestId>, IOrganizationScoped
{
    private DataSubjectRequest() { }

    public TenantId TenantId { get; private set; }
    public OrganizationId? OrganizationId { get; private set; }
    public DataRequestKind Kind { get; private set; }
    public DataRequestOrigin Origin { get; private set; }
    public UserId SubjectUserId { get; private set; }

    /// <summary>The person who filed the request; <see langword="null"/> for a closure's fan-out erasure.</summary>
    public UserId? RequesterUserId { get; private set; }

    public DataRequestStatus Status { get; private set; }

    /// <summary>The frozen Membership identifiers of the subject in the group, as canonical JSON.</summary>
    public string MembershipSnapshotJson { get; private set; } = "[]";

    /// <summary>When a scheduled erasure or closure may execute; the end of its cancellable window.</summary>
    public DateTimeOffset? ExecuteAt { get; private set; }

    /// <summary>Participant key to completion instant, as canonical JSON (ADR-0107 § 1).</summary>
    public string ProgressJson { get; private set; } = DataRequestDocuments.EmptyObject;

    /// <summary>Retained-reference kind to count, as canonical JSON (ADR-0107 § 6).</summary>
    public string ResultJson { get; private set; } = DataRequestDocuments.EmptyObject;

    /// <summary>The latest refusal recorded against the request, shown on its receipt.</summary>
    public DataRequestRefusal? LastRefusal { get; private set; }

    public DataRequestFailureReason? FailureReason { get; private set; }

    /// <summary>When the request completed; <c>completed</c> means every step, the privileged one included.</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    public DataRequestScopeGroup Group => new(TenantId, OrganizationId);

    public ImmutableArray<MembershipId> SnapshotMembershipIds => DataRequestDocuments.ReadMemberships(MembershipSnapshotJson);

    public FrozenDictionary<string, DateTimeOffset> Progress => DataRequestDocuments.ReadProgress(ProgressJson);

    public FrozenDictionary<RetainedReferenceKind, long> RetainedReport => DataRequestDocuments.ReadResult(ResultJson);

    public bool IsTerminal => DataRequestCodes.IsTerminal(Status);

    /// <summary>The person files a request for themselves: an export, an erasure or a closure (ADR-0106 § 3).</summary>
    public static Result<DataSubjectRequest> FileForSelf(DataSubjectRequestId id, DataRequestKind kind, UserId subject,
        DataRequestScopeGroup group, IReadOnlyCollection<MembershipId> memberships, DateTimeOffset at) =>
        Create(id, kind, DataRequestOrigin.Self, subject, subject, group, memberships, at, subject);

    /// <summary>
    /// A holder of <c>identity.membership.admin</c> files an export or an erasure for a subject in the
    /// group; an administrator never creates a closure (ADR-0106 § 3).
    /// </summary>
    public static Result<DataSubjectRequest> FileForSubject(DataSubjectRequestId id, DataRequestKind kind, UserId administrator,
        UserId subject, DataRequestScopeGroup group, IReadOnlyCollection<MembershipId> memberships, DateTimeOffset at) =>
        kind == DataRequestKind.Closure || administrator == subject
            ? Result<DataSubjectRequest>.Fail(IdentityFailures.BusinessRuleError())
            : Create(id, kind, DataRequestOrigin.Administrator, subject, administrator, group, memberships, at, administrator);

    /// <summary>
    /// A closure's Begin creates one group's erasure: <c>fulfilling</c> at once, with no window and no
    /// cancellation, attributed to the system actor (ADR-0107 § 7).
    /// </summary>
    public static Result<DataSubjectRequest> FanOutErasure(DataSubjectRequestId id, UserId subject,
        DataRequestScopeGroup group, IReadOnlyCollection<MembershipId> memberships, DateTimeOffset at) =>
        Create(id, DataRequestKind.Erasure, DataRequestOrigin.ClosureFanout, subject, null, group, memberships, at, UserId.SystemActor);

    /// <summary><c>requested → fulfilling</c> for an export; <c>scheduled → fulfilling</c> for an erasure or a closure once its window has passed.</summary>
    public Result<None> StartFulfilling(DateTimeOffset at, UserId actor)
    {
        var admitted = (Kind, Status) switch
        {
            (DataRequestKind.Export, DataRequestStatus.Requested) => true,
            (DataRequestKind.Erasure or DataRequestKind.Closure, DataRequestStatus.Scheduled) => ExecuteAt is { } due && at >= due,
            _ => false,
        };
        return Transition(admitted, DataRequestStatus.Fulfilling, at, actor);
    }

    /// <summary><c>fulfilling → awaiting_privileged_step</c>: the tenant-side erasure is done and an operator owes the redaction.</summary>
    public Result<None> AwaitPrivilegedStep(DateTimeOffset at, UserId actor) => Transition(
        Kind == DataRequestKind.Erasure && Status == DataRequestStatus.Fulfilling, DataRequestStatus.AwaitingPrivilegedStep, at, actor);

    /// <summary>
    /// <c>fulfilling → completed</c> for an export or a closure, <c>awaiting_privileged_step → completed</c> for an
    /// erasure; records the complete retained report.
    /// </summary>
    public Result<None> Complete(IReadOnlyCollection<RetainedReferenceCount> retained, DateTimeOffset at, UserId actor)
    {
        ArgumentNullException.ThrowIfNull(retained);
        var report = DataRequestDocuments.WriteResult(retained);
        var admitted = report is not null && (Kind, Status) switch
        {
            (DataRequestKind.Export or DataRequestKind.Closure, DataRequestStatus.Fulfilling) => true,
            (DataRequestKind.Erasure, DataRequestStatus.AwaitingPrivilegedStep) => true,
            _ => false,
        };
        if (!ValidChange(admitted, at, actor))
        {
            return IdentityFailures.BusinessRule();
        }

        MarkUpdated(at, actor);
        Status = DataRequestStatus.Completed;
        CompletedAt = at;
        ResultJson = report!;
        return Result<None>.Ok(None.Value);
    }

    /// <summary><c>fulfilling → failed</c> for an export or an erasure, with its closed reason.</summary>
    public Result<None> Fail(DataRequestFailureReason reason, DateTimeOffset at, UserId actor)
    {
        var admitted = Enum.IsDefined(reason) && Status == DataRequestStatus.Fulfilling
            && (Kind == DataRequestKind.Export
                || (Kind == DataRequestKind.Erasure && reason != DataRequestFailureReason.ExportTooLarge));
        if (!ValidChange(admitted, at, actor))
        {
            return IdentityFailures.BusinessRule();
        }

        MarkUpdated(at, actor);
        Status = DataRequestStatus.Failed;
        FailureReason = reason;
        return Result<None>.Ok(None.Value);
    }

    /// <summary><c>fulfilling → blocked</c>: an erasure failed its re-check at execution (ADR-0106 § 7).</summary>
    public Result<None> Block(DataRequestRefusal refusal, DateTimeOffset at, UserId actor)
    {
        var admitted = Kind == DataRequestKind.Erasure && Status == DataRequestStatus.Fulfilling
            && refusal is DataRequestRefusal.BlockedHold or DataRequestRefusal.BlockedLastAdministrator;
        if (!ValidChange(admitted, at, actor))
        {
            return IdentityFailures.BusinessRule();
        }

        MarkUpdated(at, actor);
        Status = DataRequestStatus.Blocked;
        LastRefusal = refusal;
        return Result<None>.Ok(None.Value);
    }

    /// <summary>
    /// <c>requested → cancelled</c> for an export; <c>scheduled → cancelled</c> for an erasure before its
    /// execution time and for a closure until its Begin commits (ADR-0106 § 11).
    /// </summary>
    public Result<None> Cancel(DateTimeOffset at, UserId actor)
    {
        var admitted = (Kind, Status) switch
        {
            (DataRequestKind.Export, DataRequestStatus.Requested) => true,
            (DataRequestKind.Erasure, DataRequestStatus.Scheduled) => ExecuteAt is { } due && at < due,
            (DataRequestKind.Closure, DataRequestStatus.Scheduled) => true,
            _ => false,
        };
        return Transition(admitted, DataRequestStatus.Cancelled, at, actor);
    }

    /// <summary>
    /// Records a refusal that leaves a scheduled request in place, so the receipt shows the person
    /// what to act on (ADR-0106 § 7); repeating the same refusal changes nothing.
    /// </summary>
    public Result<None> RecordRefusal(DataRequestRefusal refusal, DateTimeOffset at, UserId actor)
    {
        if (Enum.IsDefined(refusal) && Status == DataRequestStatus.Scheduled && LastRefusal == refusal)
        {
            return Result<None>.Ok(None.Value);
        }

        if (!ValidChange(Enum.IsDefined(refusal) && Status == DataRequestStatus.Scheduled, at, actor))
        {
            return IdentityFailures.BusinessRule();
        }

        MarkUpdated(at, actor);
        LastRefusal = refusal;
        return Result<None>.Ok(None.Value);
    }

    /// <summary>
    /// Records that one participant finished, so a resumed job skips it (ADR-0107 § 1); recording a
    /// participant again changes nothing.
    /// </summary>
    public Result<None> RecordParticipantCompleted(string participantKey, DateTimeOffset at, UserId actor)
    {
        if (Status != DataRequestStatus.Fulfilling || !DataRequestLimits.IsParticipantKey(participantKey))
        {
            return IdentityFailures.BusinessRule();
        }

        var progress = Progress;
        if (progress.ContainsKey(participantKey))
        {
            return Result<None>.Ok(None.Value);
        }

        var recorded = DataRequestDocuments.WriteProgress(
            new Dictionary<string, DateTimeOffset>(progress, StringComparer.Ordinal) { [participantKey] = at });
        if (!ValidChange(recorded is not null, at, actor))
        {
            return IdentityFailures.BusinessRule();
        }

        MarkUpdated(at, actor);
        ProgressJson = recorded!;
        return Result<None>.Ok(None.Value);
    }

    /// <summary>
    /// Records the tenant side's retained report while the request is fulfilling, before the privileged
    /// step adds what it retains (ADR-0107 § 6, ADR-0108 § 6).
    /// </summary>
    public Result<None> RecordRetained(IReadOnlyCollection<RetainedReferenceCount> retained, DateTimeOffset at, UserId actor)
    {
        ArgumentNullException.ThrowIfNull(retained);
        var report = DataRequestDocuments.WriteResult(retained);
        if (!ValidChange(report is not null && Status == DataRequestStatus.Fulfilling, at, actor))
        {
            return IdentityFailures.BusinessRule();
        }

        if (string.Equals(report, ResultJson, StringComparison.Ordinal))
        {
            return Result<None>.Ok(None.Value);
        }

        MarkUpdated(at, actor);
        ResultJson = report!;
        return Result<None>.Ok(None.Value);
    }

    private static Result<DataSubjectRequest> Create(DataSubjectRequestId id, DataRequestKind kind, DataRequestOrigin origin,
        UserId subject, UserId? requester, DataRequestScopeGroup group, IReadOnlyCollection<MembershipId> memberships,
        DateTimeOffset at, UserId actor)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(memberships);
        var snapshot = DataRequestDocuments.WriteMemberships(memberships);
        if (!StronglyTypedId.IsAssigned(id) || id.Value.Version != 7 || !Enum.IsDefined(kind)
            || !ValidPerson(subject) || requester is { } person && !ValidPerson(person)
            || snapshot is null || !ValidInstant(at) || at > DateTimeOffset.MaxValue - DataRequestLimits.CancellationWindow)
        {
            return Result<DataSubjectRequest>.Fail(IdentityFailures.BusinessRuleError());
        }

        var scheduled = kind != DataRequestKind.Export && origin != DataRequestOrigin.ClosureFanout;
        var request = new DataSubjectRequest
        {
            Id = id,
            TenantId = group.TenantId,
            OrganizationId = group.OrganizationId,
            Kind = kind,
            Origin = origin,
            SubjectUserId = subject,
            RequesterUserId = requester,
            MembershipSnapshotJson = snapshot,
            Status = kind == DataRequestKind.Export ? DataRequestStatus.Requested
                : scheduled ? DataRequestStatus.Scheduled : DataRequestStatus.Fulfilling,
            ExecuteAt = scheduled ? at + DataRequestLimits.CancellationWindow : null,
        };
        request.MarkCreated(at, actor);
        return Result<DataSubjectRequest>.Ok(request);
    }

    private Result<None> Transition(bool admitted, DataRequestStatus next, DateTimeOffset at, UserId actor)
    {
        if (!ValidChange(admitted, at, actor))
        {
            return IdentityFailures.BusinessRule();
        }

        MarkUpdated(at, actor);
        Status = next;
        return Result<None>.Ok(None.Value);
    }

    private bool ValidChange(bool admitted, DateTimeOffset at, UserId actor) =>
        admitted && !IsTerminal && !IsDeleted && Version != long.MaxValue
        && StronglyTypedId.IsAssigned(actor) && ValidInstant(at) && at >= (UpdatedAt ?? CreatedAt);

    private static bool ValidPerson(UserId user) =>
        StronglyTypedId.IsAssigned(user) && user.Value.Version == 7 && user != UserId.SystemActor;

    private static bool ValidInstant(DateTimeOffset at) => at != default && at.Offset == TimeSpan.Zero;
}
