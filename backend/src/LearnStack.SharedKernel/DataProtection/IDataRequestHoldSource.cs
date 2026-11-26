using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.DataProtection;

/// <summary>
/// Whether something the platform must keep — a payment dispute, a legal hold — stops a subject
/// request (<see href="../../../../docs/decisions/0106-subject-data-requests-authority-and-status.md">ADR-0106 § 7</see>).
/// </summary>
/// <remarks>
/// <para>
/// A demand-gated port: the working default is <see cref="NoHoldSource"/>, which reports no hold;
/// the owning phase is
/// <see href="../../../../docs/roadmap/phase-09-billing-integrations-analytics.md">Phase 09</see> (payments and
/// disputes), and the trigger is the first aggregate that can carry a hold. The source is consulted
/// by eligibility at creation and at execution, and again at a closure's Begin (ADR-0107 § 7).
/// </para>
/// <para>
/// A hold at creation is the refusal <c>blocked_hold</c> and writes no request.
/// </para>
/// <para>
/// <b>A closure's query concerns the whole person.</b> A closure is global: its Begin asks once, with
/// <see cref="DataRequestKind.Closure"/>, the person and the group the closure was filed in, and never asks per
/// participant group, and a closure's fan-out erasures start <c>fulfilling</c> and ask nothing. A source therefore answers
/// a <see cref="DataRequestKind.Closure"/> query for a hold on the person in any group, the named group being only where
/// the closure was filed. Begin asks it while it holds every participant tenant's fence, so a source answers without I/O
/// that could outlast those fences (ADR-0107 § 7).
/// </para>
/// </remarks>
public interface IDataRequestHoldSource
{
    Task<bool> HasHoldAsync(DataRequestHoldQuery query, CancellationToken cancellationToken);
}

/// <summary>The subject, group and kind a hold is asked about.</summary>
public sealed record DataRequestHoldQuery
{
    /// <exception cref="ArgumentException">A member is undefined or unassigned.</exception>
    public DataRequestHoldQuery(DataRequestKind kind, UserId subjectUserId, DataRequestScopeGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentException("A hold query needs a defined kind.", nameof(kind));
        }

        if (!StronglyTypedId.IsAssigned(subjectUserId) || subjectUserId == UserId.SystemActor)
        {
            throw new ArgumentException("A hold query needs a real person.", nameof(subjectUserId));
        }

        Kind = kind;
        SubjectUserId = subjectUserId;
        Group = group;
    }

    public DataRequestKind Kind { get; }
    public UserId SubjectUserId { get; }
    public DataRequestScopeGroup Group { get; }

    public override string ToString() => nameof(DataRequestHoldQuery);
}

/// <summary>
/// The working default of <see cref="IDataRequestHoldSource"/>: no aggregate can carry a hold yet,
/// so none is reported (ADR-0106 § 7; owner Phase 09, trigger the first hold-bearing aggregate).
/// </summary>
public sealed class NoHoldSource : IDataRequestHoldSource
{
    public Task<bool> HasHoldAsync(DataRequestHoldQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.FromResult(false);
    }
}
