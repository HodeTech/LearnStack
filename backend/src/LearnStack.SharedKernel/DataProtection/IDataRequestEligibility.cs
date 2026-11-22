using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.DataProtection;

/// <summary>
/// The closed decision whether a data request may be created, or may still execute
/// (<see href="../../../../docs/decisions/0106-subject-data-requests-authority-and-status.md">ADR-0106 § 7</see>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Asked twice.</b> At <see cref="DataRequestEligibilityStage.Creation"/> a refusal is a response
/// and writes no request; at <see cref="DataRequestEligibilityStage.Execution"/> a refusal ends an
/// erasure <c>blocked</c> with its code, or leaves a closure <c>scheduled</c> with its
/// <c>last_refusal</c>.
/// </para>
/// <para>
/// <b>The default implementation checks structure only, and nothing else:</b>
/// </para>
/// <list type="number">
/// <item>the subject holds a Membership of any status in the group (a Revoked tombstone counts),
/// otherwise <see cref="DataRequestIneligibility.NoMembership"/>;</item>
/// <item>at creation, no non-terminal request of the same kind, subject and group exists, otherwise
/// <see cref="DataRequestIneligibility.RequestAlreadyActive"/> — answered 409
/// <c>business_rule_violation</c> with <c>lockey_request_already_active</c>, and enforced
/// independently by the partial unique index on the non-terminal rows so two concurrent creations
/// cannot both pass; at execution the request being executed is that row and is not counted;</item>
/// <item>an <see cref="DataRequestKind.Erasure"/> is refused when the subject is the only eligible
/// tenant-wide administrator (<see cref="DataRequestIneligibility.BlockedLastAdministrator"/>);</item>
/// <item>the registered <see cref="IDataRequestHoldSource"/> is consulted, and a hold refuses with
/// <see cref="DataRequestIneligibility.BlockedHold"/>.</item>
/// </list>
/// <para>
/// The decision is read inside the tenant's ambient context as <c>learnstack_app</c>; it grants no
/// authority and is not the caller's authorization, which the operation policy decides first.
/// </para>
/// </remarks>
public interface IDataRequestEligibility
{
    Task<DataRequestEligibilityDecision> EvaluateAsync(DataRequestEligibilityQuery query, CancellationToken cancellationToken);
}

/// <summary>When eligibility is asked.</summary>
public enum DataRequestEligibilityStage
{
    Creation = 1,
    Execution = 2,
}

/// <summary>The closed reasons a request is not eligible.</summary>
public enum DataRequestIneligibility
{
    NoMembership = 1,
    RequestAlreadyActive = 2,
    BlockedLastAdministrator = 3,
    BlockedHold = 4,
}

/// <summary>What eligibility is asked about.</summary>
public sealed record DataRequestEligibilityQuery
{
    /// <exception cref="ArgumentException">A member is undefined or unassigned, or an execution query names no request.</exception>
    public DataRequestEligibilityQuery(DataRequestKind kind, DataRequestOrigin origin, UserId subjectUserId,
        DataRequestScopeGroup group, DataRequestEligibilityStage stage, Guid? requestId = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(origin) || !Enum.IsDefined(stage))
        {
            throw new ArgumentException("An eligibility query needs a defined kind, origin and stage.", nameof(kind));
        }

        if (!StronglyTypedId.IsAssigned(subjectUserId) || subjectUserId == UserId.SystemActor)
        {
            throw new ArgumentException("An eligibility query needs a real person.", nameof(subjectUserId));
        }

        if ((stage == DataRequestEligibilityStage.Execution) != requestId.HasValue || requestId == Guid.Empty)
        {
            throw new ArgumentException("An execution query names its request and a creation query names none.", nameof(requestId));
        }

        Kind = kind;
        Origin = origin;
        SubjectUserId = subjectUserId;
        Group = group;
        Stage = stage;
        RequestId = requestId;
    }

    public DataRequestKind Kind { get; }
    public DataRequestOrigin Origin { get; }
    public UserId SubjectUserId { get; }
    public DataRequestScopeGroup Group { get; }
    public DataRequestEligibilityStage Stage { get; }

    /// <summary>The request being executed; absent at creation.</summary>
    public Guid? RequestId { get; }

    public override string ToString() => nameof(DataRequestEligibilityQuery);
}

/// <summary>An eligibility answer: eligible, or one closed reason.</summary>
public sealed record DataRequestEligibilityDecision
{
    private DataRequestEligibilityDecision(DataRequestIneligibility? reason) => Reason = reason;

    public static DataRequestEligibilityDecision Eligible { get; } = new((DataRequestIneligibility?)null);

    /// <exception cref="ArgumentOutOfRangeException">The reason is undefined.</exception>
    public static DataRequestEligibilityDecision Refused(DataRequestIneligibility reason) => Enum.IsDefined(reason)
        ? new(reason)
        : throw new ArgumentOutOfRangeException(nameof(reason), "The reason is not a member of the closed vocabulary.");

    public DataRequestIneligibility? Reason { get; }

    public bool IsEligible => Reason is null;

    /// <summary>
    /// The refusal a request records, for the two reasons that are refusals rather than structural
    /// answers; <see langword="null"/> otherwise.
    /// </summary>
    public DataRequestRefusal? Refusal => Reason switch
    {
        DataRequestIneligibility.BlockedHold => DataRequestRefusal.BlockedHold,
        DataRequestIneligibility.BlockedLastAdministrator => DataRequestRefusal.BlockedLastAdministrator,
        _ => null,
    };
}
