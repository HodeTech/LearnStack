using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Platform;

/// <summary>One scope group a closure reaches: a tenant and one organization, or the tenant-wide group.</summary>
/// <param name="TenantId">The participant tenant.</param>
/// <param name="OrganizationId">The organization, or <see langword="null"/> for the tenant-wide group.</param>
public sealed record ClosureParticipantGroup(TenantId TenantId, OrganizationId? OrganizationId);

/// <summary>What the fan-out did for one participant group.</summary>
public enum ClosureGroupOutcome
{
    /// <summary>A fan-out erasure request was created and its job enqueued.</summary>
    Started = 1,

    /// <summary>An erasure the group already had in flight, or already awaiting its operator step, was adopted.</summary>
    Adopted = 2,

    /// <summary>
    /// An erasure the person or an administrator filed is past its window and not yet run: the group is not ready and
    /// a repeated Begin picks it up.
    /// </summary>
    Waiting = 3,

    /// <summary>The group's frame failed; nothing is recorded for it and a repeated Begin retries it.</summary>
    Failed = 4,
}

/// <summary>The fan-out's answer for one participant group.</summary>
/// <param name="Group">The group.</param>
/// <param name="Outcome">What happened there.</param>
public sealed record ClosureGroupReport(ClosureParticipantGroup Group, ClosureGroupOutcome Outcome);

/// <summary>The person and the participant groups a closure's Begin hands to the fan-out.</summary>
/// <param name="SubjectUserId">The person whose account is closing; their User is already <c>Closing</c>.</param>
/// <param name="Groups">The groups that allow ordinary work, in a stable order; unserved groups are not listed.</param>
public sealed record ClosureFanOutRequest(UserId SubjectUserId, IReadOnlyList<ClosureParticipantGroup> Groups);

/// <summary>
/// Creates, in each participant group's own transaction, the fan-out erasure of a closing person and enqueues its job
/// (<see href="../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 7</see>,
/// <c>fan_out_closure</c>).
/// </summary>
/// <remarks>
/// <para>
/// It runs after Begin's transaction has committed and released its fences: each group is one pipeline act under the
/// operator's identity, announced for that tenant and organization, which takes the person's User fence and the tenant
/// fence itself. It mints one admission of <c>fan_out_closure</c> per group from the operator witness it is given, so
/// the gate evaluates the grant on every act. A group that fails is reported, never thrown: Begin is repeated until
/// every group is started or adopted.
/// </para>
/// <para>
/// It is idempotent: a repeat adopts what an earlier call started and starts what it lacked.
/// </para>
/// </remarks>
public interface IClosureFanOut
{
    /// <summary>Fans the closure out to every group of <paramref name="request"/> and reports each group's outcome.</summary>
    Task<IReadOnlyList<ClosureGroupReport>> FanOutAsync(
        PlatformOperatorWitness operatorWitness,
        string? ticket,
        ClosureFanOutRequest request,
        CancellationToken cancellationToken = default);
}
